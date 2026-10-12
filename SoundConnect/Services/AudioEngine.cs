using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundConnect.Audio;
using SoundConnect.Shared.Services;

namespace SoundConnect.Services;

/// <summary>
/// 音频通路：把指定进程的音乐和真实麦克风混成一路，写进虚拟线给队友听。
///
/// 实测经验写在这里，改代码时别踩回去：
/// 1. 虚拟线自己带 +30dB（实测 31.62x），所以送进去之前要乘 <see cref="CableCompensation"/>，否则一律削顶。
/// 2. 浏览器这类多进程程序，音频会话挂在子进程上，而子进程会被回收，所以要抓进程树根。
/// 3. 混音块必须整块整块地取。源缓冲不够一整块时要在"块边界"上补静音，绝不能在一块中间补——
///    那样源缓冲永远攒不到深度，每个块都会插一段静音，听感就是持续的卡顿。
/// 4. 输出由设备自己按实时节奏来拉（<see cref="WasapiOut"/> 直接拉混音图），不要再套一层
///    自己 sleep 的中间缓冲：那既加延迟，又会为了让缓冲到水位而在中间插静音。
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    private const double CableCompensation = 0.0316;
    private const int AlignmentBlockMs = 5;
    private const int MeterIntervalMs = 100;
    private const int LogLimit = 200;
    private const int OutputLatencyMs = 20;
    private const int MicrophoneLatencyMs = 10;
    /// <summary>源缓冲涨到这个深度就说明两边时钟漂了，直接跳到最新数据，避免延迟越积越大。</summary>
    private const int MaxSourceBufferedMs = 150;

    private readonly object sync = new();
    private readonly List<string> log = new();
    private readonly MMDeviceEnumerator enumerator = new();

    private ProcessLoopbackCapture? musicCapture;
    private WasapiCapture? microphoneCapture;
    private BufferedWaveProvider? musicBuffer;
    private BufferedWaveProvider? microphoneBuffer;
    private MixingSampleProvider? mixer;
    private VolumeSampleProvider? musicVolume;
    private VolumeSampleProvider? microphoneVolume;
    private BlockAlignedSampleProvider? musicAligned;
    private BlockAlignedSampleProvider? microphoneAligned;
    private WasapiOut? masterOutput;
    private MasterWaveProvider? masterProvider;

    private double musicGain = 1.0;
    private double microphoneGain = 1.0;
    private double musicLevel;
    private double microphoneLevel;
    private double outputLevel;
    private double outputPeak;
    private bool clipping;
    private bool warnedClipping;
    private DateTime lastMeterAt = DateTime.MinValue;
    private long musicUnderruns;
    private long microphoneUnderruns;
    private long lastMusicStarved;
    private long lastMicrophoneStarved;
    private DateTime lastUnderrunAt = DateTime.MinValue;

    public event Action? Changed;

    public bool IsRunning { get; private set; }

    public IReadOnlyList<AudioDeviceInfo> RenderDevices { get; private set; } = [];

    public IReadOnlyList<AudioDeviceInfo> CaptureDevices { get; private set; } = [];

    public AudioEngine()
    {
        Log("音频引擎已就绪");
    }

    public AudioEngineSnapshot Snapshot => new()
    {
        IsRunning = IsRunning,
        MusicLevel = musicLevel,
        MicrophoneLevel = microphoneLevel,
        OutputLevel = outputLevel,
        OutputPeak = outputPeak,
        IsClipping = clipping,
        MusicUnderruns = musicUnderruns,
        MicrophoneUnderruns = microphoneUnderruns,
        MusicGain = musicGain,
        MicrophoneGain = microphoneGain,
        Log = log.ToArray()
    };

    public Task RefreshDevicesAsync()
    {
        RenderDevices = ReadDevices(DataFlow.Render, "输出");
        CaptureDevices = ReadDevices(DataFlow.Capture, "输入");
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AudioProcessInfo>> ListAudioProcessesAsync()
    {
        var found = new Dictionary<int, AudioProcessInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var index = 0; index < sessions.Count; index++)
                {
                    var session = sessions[index];
                    var processId = (int)(uint)session.GetProcessID;
                    if (processId <= 0 || found.ContainsKey(processId)) continue;
                    string name;
                    try
                    {
                        name = System.Diagnostics.Process.GetProcessById(processId).ProcessName;
                    }
                    catch
                    {
                        continue;
                    }
                    // NAudio 2.x 把这个枚举挪进了 Interfaces 命名空间，这里不依赖具体类型，直接看名字
                    var state = session.State.ToString().EndsWith("Active", StringComparison.Ordinal)
                        ? "正在发声"
                        : "空闲";
                    found[processId] = new AudioProcessInfo(processId, name, state);
                }
            }
            catch (Exception error)
            {
                Log($"枚举 {device.FriendlyName} 的会话失败：{error.Message}");
            }
        }

        return Task.FromResult<IReadOnlyList<AudioProcessInfo>>(
            found
                .Values.OrderByDescending(item => item.State == "正在发声")
                .ThenBy(item => item.Name)
                .ToList()
        );
    }

    public async Task StartAsync(AudioStartOptions options)
    {
        await StopAsync();

        var outputDevice =
            FindDevice(DataFlow.Render, options.OutputDeviceId)
            ?? throw new InvalidOperationException("找不到送队友的输出设备");
        var deviceFormat = outputDevice.AudioClient.MixFormat;
        var mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            deviceFormat.SampleRate,
            deviceFormat.Channels
        );

        var sources = new List<ISampleProvider>();

        musicBuffer = CreateBuffer(mixFormat);
        musicAligned = new BlockAlignedSampleProvider(musicBuffer);
        musicVolume = new VolumeSampleProvider(musicAligned)
        {
            Volume = (float)musicGain
        };
        sources.Add(musicVolume);

        if (options.MusicProcessId > 0)
        {
            var process = System.Diagnostics.Process.GetProcessById(options.MusicProcessId);
            var rootId = ProcessTree.FindTreeRoot((uint)process.Id, process.ProcessName);
            if (rootId != (uint)process.Id)
            {
                Log($"PID {process.Id} 是子进程，上溯到进程树根 {rootId} 再采集");
            }
            musicCapture = new ProcessLoopbackCapture(rootId, mixFormat);
            musicCapture.DataAvailable += OnMusicData;
            musicCapture.Start();
            Log($"开始采集 {process.ProcessName}（PID {rootId}）的声音");
        }

        if (!string.IsNullOrWhiteSpace(options.MicrophoneDeviceId))
        {
            var microphoneDevice =
                FindDevice(DataFlow.Capture, options.MicrophoneDeviceId)
                ?? throw new InvalidOperationException("找不到麦克风设备");
            microphoneCapture = new WasapiCapture(microphoneDevice, true, MicrophoneLatencyMs);
            var deviceMicrophoneFormat = microphoneCapture.WaveFormat;
            var bufferFormat = ToBufferFormat(deviceMicrophoneFormat);
            microphoneBuffer = CreateBuffer(bufferFormat);

            microphoneAligned = new BlockAlignedSampleProvider(microphoneBuffer);
            ISampleProvider microphoneSource = microphoneAligned;
            if (bufferFormat.SampleRate != mixFormat.SampleRate)
            {
                microphoneSource = new WdlResamplingSampleProvider(
                    microphoneSource,
                    mixFormat.SampleRate
                );
            }
            if (bufferFormat.Channels != mixFormat.Channels)
            {
                microphoneSource = new ChannelMapperSampleProvider(
                    microphoneSource,
                    mixFormat.Channels
                );
            }
            microphoneVolume = new VolumeSampleProvider(microphoneSource)
            {
                Volume = (float)microphoneGain
            };
            sources.Add(microphoneVolume);
            microphoneCapture.DataAvailable += OnMicrophoneData;
            microphoneCapture.StartRecording();
            Log(
                $"麦克风已接入：{microphoneDevice.FriendlyName}"
                    + $"（{bufferFormat.SampleRate}Hz/{bufferFormat.Channels}ch/{bufferFormat.BitsPerSample}bit）"
            );
        }

        mixer = new MixingSampleProvider(sources) { ReadFully = true };
        masterProvider = new MasterWaveProvider(this, mixer);
        masterOutput = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, OutputLatencyMs);
        masterOutput.Init(masterProvider);
        masterOutput.Play();

        IsRunning = true;
        Log($"通路已建立：混音 → {outputDevice.FriendlyName}（已按 -30dB 补偿虚拟线增益）");
        RaiseChanged();
    }

    public Task StopAsync()
    {
        if (musicCapture is not null)
        {
            musicCapture.DataAvailable -= OnMusicData;
            musicCapture.Dispose();
            musicCapture = null;
        }
        if (microphoneCapture is not null)
        {
            microphoneCapture.DataAvailable -= OnMicrophoneData;
            microphoneCapture.StopRecording();
            microphoneCapture.Dispose();
            microphoneCapture = null;
        }

        masterOutput?.Stop();
        masterOutput?.Dispose();
        masterOutput = null;
        masterProvider = null;
        mixer = null;
        musicVolume = null;
        microphoneVolume = null;
        musicAligned = null;
        microphoneAligned = null;
        musicBuffer = null;
        microphoneBuffer = null;

        musicLevel = 0;
        microphoneLevel = 0;
        outputLevel = 0;
        outputPeak = 0;
        clipping = false;
        warnedClipping = false;
        musicUnderruns = 0;
        microphoneUnderruns = 0;
        lastMusicStarved = 0;
        lastMicrophoneStarved = 0;
        if (IsRunning)
        {
            Log("通路已停止");
        }
        IsRunning = false;
        RaiseChanged();
        return Task.CompletedTask;
    }

    public void SetGain(string channel, double volume)
    {
        var clamped = Math.Clamp(volume, 0, 10); // 上限 10x，也就是 +20dB
        if (channel == "music")
        {
            musicGain = clamped;
            if (musicVolume is not null) musicVolume.Volume = (float)clamped;
        }
        else if (channel == "mic")
        {
            microphoneGain = clamped;
            if (microphoneVolume is not null) microphoneVolume.Volume = (float)clamped;
        }
    }

    public double GetGain(string channel)
    {
        return channel == "music" ? musicGain : microphoneGain;
    }

    private void OnMusicData(byte[] chunk)
    {
        musicLevel = Level(chunk, musicBuffer?.WaveFormat) * musicGain;
        Push(musicBuffer, chunk, chunk.Length, "音乐");
    }

    private void OnMicrophoneData(object? sender, WaveInEventArgs eventArgs)
    {
        microphoneLevel =
            Level(eventArgs.Buffer, microphoneBuffer?.WaveFormat, eventArgs.BytesRecorded)
            * microphoneGain;
        Push(microphoneBuffer, eventArgs.Buffer, eventArgs.BytesRecorded, "麦克风");
    }

    private void Push(BufferedWaveProvider? buffer, byte[] data, int count, string name)
    {
        if (buffer is null) return;
        if (buffer.BufferedDuration.TotalMilliseconds > MaxSourceBufferedMs)
        {
            // 采集端和输出端是两个独立时钟，跑久了缓冲会缓慢涨上去；涨起来就等于白加延迟
            buffer.ClearBuffer();
            Log($"{name}源缓冲积压超过 {MaxSourceBufferedMs}ms，已跳到最新数据（时钟漂移）");
        }
        buffer.AddSamples(data, 0, count);
    }

    /// <summary>设备每次拉数据时都会走这里：量电平、补偿虚拟线的 +30dB、再交出去。</summary>
    private void OnMixedBlock(float[] samples, int count)
    {
        // 这里量到的就是"队友实际听到的电平"：虚拟线自带的 +30dB 正好抵消下面这次衰减
        outputLevel = Level(samples, count);
        var peak = 0.0;
        for (var index = 0; index < count; index++)
        {
            var magnitude = Math.Abs(samples[index]);
            if (magnitude > peak) peak = magnitude;
            samples[index] *= (float)CableCompensation;
        }
        outputPeak = peak;
        clipping = peak >= 0.99;
        if (clipping && !warnedClipping)
        {
            warnedClipping = true;
            Log("输出削顶了：把音乐或麦克风音量往下调一点");
        }

        if ((DateTime.UtcNow - lastMeterAt).TotalMilliseconds >= MeterIntervalMs)
        {
            lastMeterAt = DateTime.UtcNow;
            ReportUnderruns();
            RaiseChanged();
        }
    }

    /// <summary>
    /// 每秒报一次源侧的欠载次数。这个数就是"卡顿"的直接来源：
    /// 非零说明源缓冲被抽空了，那一块只能补静音；稳定在 0 才说明既没丢数据也没插静音。
    /// </summary>
    private void ReportUnderruns()
    {
        if ((DateTime.UtcNow - lastUnderrunAt).TotalMilliseconds < 1000) return;
        lastUnderrunAt = DateTime.UtcNow;

        var musicStarved = musicAligned?.StarvedCount ?? 0;
        var microphoneStarved = microphoneAligned?.StarvedCount ?? 0;
        musicUnderruns = musicStarved - lastMusicStarved;
        microphoneUnderruns = microphoneStarved - lastMicrophoneStarved;
        lastMusicStarved = musicStarved;
        lastMicrophoneStarved = microphoneStarved;

        if (musicUnderruns > 0 || microphoneUnderruns > 0)
        {
            Log($"欠载：音乐 {musicUnderruns} 次 / 麦克风 {microphoneUnderruns} 次（每秒）");
        }
    }

    private BufferedWaveProvider CreateBuffer(WaveFormat format)
    {
        return new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(300),
            ReadFully = true,
            DiscardOnBufferOverflow = true
        };
    }

    /// <summary>
    /// 采集设备给的格式里，编码可能是 Extensible（NAudio 的 ToSampleProvider 不认它），
    /// 这里换成等价的普通格式，字节布局一样，原始数据可以直接往里塞。
    /// </summary>
    private static WaveFormat ToBufferFormat(WaveFormat deviceFormat)
    {
        return ProcessLoopbackCapture.IsFloat(deviceFormat)
            ? WaveFormat.CreateIeeeFloatWaveFormat(deviceFormat.SampleRate, deviceFormat.Channels)
            : new WaveFormat(deviceFormat.SampleRate, deviceFormat.BitsPerSample, deviceFormat.Channels);
    }

    private List<AudioDeviceInfo> ReadDevices(DataFlow flow, string kind)
    {
        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            var text = $"{device.DeviceFriendlyName} {device.FriendlyName}";
            result.Add(
                new AudioDeviceInfo(
                    device.ID,
                    device.FriendlyName,
                    device.DeviceFriendlyName,
                    LooksVirtual(text),
                    kind
                )
            );
        }
        return result;
    }

    private MMDevice? FindDevice(DataFlow flow, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            if (device.ID == deviceId) return device;
        }
        return null;
    }

    private static bool LooksVirtual(string text)
    {
        return text.Contains("cable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("virtual", StringComparison.OrdinalIgnoreCase)
            || text.Contains("voicemeeter", StringComparison.OrdinalIgnoreCase)
            || text.Contains("steam streaming", StringComparison.OrdinalIgnoreCase)
            || text.Contains("网易", StringComparison.Ordinal);
    }

    private static double Level(byte[] data, WaveFormat? format, int length = 0)
    {
        var count = length > 0 ? length : data.Length;
        if (format is null || !ProcessLoopbackCapture.IsFloat(format)) return 0;
        double sum = 0;
        var samples = 0;
        for (var offset = 0; offset + 4 <= count; offset += 4)
        {
            var value = BitConverter.ToSingle(data, offset);
            sum += value * value;
            samples++;
        }
        return samples == 0 ? 0 : Math.Sqrt(sum / samples);
    }

    private static double Level(float[] data, int count)
    {
        double sum = 0;
        for (var index = 0; index < count; index++)
        {
            sum += data[index] * data[index];
        }
        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    private void Log(string message)
    {
        lock (sync)
        {
            log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (log.Count > LogLimit)
            {
                log.RemoveRange(0, log.Count - LogLimit);
            }
        }
    }

    private void RaiseChanged()
    {
        Changed?.Invoke();
    }

    /// <summary>交出去的字节流，由音频设备按自己的实时节奏来拉。</summary>
    private sealed class MasterWaveProvider : IWaveProvider
    {
        private readonly AudioEngine engine;
        private readonly ISampleProvider source;
        private float[] buffer = [];

        public MasterWaveProvider(AudioEngine engine, ISampleProvider source)
        {
            this.engine = engine;
            this.source = source;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] destination, int offset, int count)
        {
            var samples = count / 4;
            if (buffer.Length < samples)
            {
                buffer = new float[samples];
            }
            var read = source.Read(buffer, 0, samples);
            if (read <= 0) return 0;
            engine.OnMixedBlock(buffer, read);
            Buffer.BlockCopy(buffer, 0, destination, offset, read * 4);
            return read * 4;
        }
    }

    /// <summary>
    /// 整块取数：源缓冲够一整块才读，不够就整块给静音并且不消耗缓冲。
    /// 这样缓冲会自己预热到一个稳定深度，之后既不会丢数据也不会插静音。
    /// </summary>
    private sealed class BlockAlignedSampleProvider : ISampleProvider
    {
        private readonly BufferedWaveProvider buffer;
        private readonly ISampleProvider source;
        private readonly int blockSamples;
        private readonly int blockBytes;
        private readonly float[] carry;
        private int carryOffset;
        private int carryLength;

        public BlockAlignedSampleProvider(BufferedWaveProvider buffer)
        {
            this.buffer = buffer;
            source = buffer.ToSampleProvider();
            WaveFormat = source.WaveFormat;
            var framesPerBlock = Math.Max(1, WaveFormat.SampleRate * AlignmentBlockMs / 1000);
            blockSamples = framesPerBlock * WaveFormat.Channels;
            blockBytes = framesPerBlock * buffer.WaveFormat.BlockAlign;
            carry = new float[blockSamples];
        }

        public WaveFormat WaveFormat { get; }

        public long StarvedCount { get; private set; }

        public int Read(float[] output, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                if (carryOffset >= carryLength)
                {
                    if (buffer.BufferedBytes < blockBytes)
                    {
                        StarvedCount++;
                        Array.Clear(output, offset + written, count - written);
                        return count;
                    }
                    carryLength = source.Read(carry, 0, blockSamples);
                    carryOffset = 0;
                    if (carryLength <= 0)
                    {
                        Array.Clear(output, offset + written, count - written);
                        return count;
                    }
                }

                var take = Math.Min(carryLength - carryOffset, count - written);
                Array.Copy(carry, carryOffset, output, offset + written, take);
                carryOffset += take;
                written += take;
            }
            return count;
        }
    }

    /// <summary>把采样统一到目标声道数：单声道复制，多声道取前几路。</summary>
    private sealed class ChannelMapperSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly int sourceChannels;
        private float[] buffer = [];

        public ChannelMapperSampleProvider(ISampleProvider source, int targetChannels)
        {
            this.source = source;
            sourceChannels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate,
                targetChannels
            );
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] destination, int offset, int count)
        {
            var targetChannels = WaveFormat.Channels;
            var frames = count / targetChannels;
            var needed = frames * sourceChannels;
            if (buffer.Length < needed)
            {
                buffer = new float[needed];
            }

            var read = source.Read(buffer, 0, needed);
            var readFrames = read / sourceChannels;
            for (var frame = 0; frame < readFrames; frame++)
            {
                for (var channel = 0; channel < targetChannels; channel++)
                {
                    var sourceChannel =
                        sourceChannels == 1 ? 0 : Math.Min(channel, sourceChannels - 1);
                    destination[offset + frame * targetChannels + channel] =
                        buffer[frame * sourceChannels + sourceChannel];
                }
            }
            return readFrames * targetChannels;
        }
    }
}
