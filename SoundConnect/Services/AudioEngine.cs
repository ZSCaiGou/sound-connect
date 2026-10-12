using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundConnect.Audio;
using SoundConnect.Shared.Services;

namespace SoundConnect.Services;

/// <summary>
/// 音频通路：把指定进程的音乐和真实麦克风混成一路，写进虚拟线给队友听。
///
/// 两条实测经验写在这里，改代码时别踩回去：
/// 1. 虚拟线自己带 +30dB（实测 31.62x），所以送进去之前要乘 <see cref="CableCompensation"/>，否则一律削顶。
/// 2. 浏览器这类多进程程序，音频会话挂在子进程上，而子进程会被回收，所以要抓进程树根。
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    private const double CableCompensation = 0.0316;
    private const int BlockMilliseconds = 10;
    private const int MeterIntervalMs = 100;
    private const int LogLimit = 200;

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
    private BufferedWaveProvider? masterBuffer;
    private WasapiOut? masterOutput;
    private CancellationTokenSource? pumpCancellation;
    private Thread? pumpThread;

    private double musicGain = 1.0;
    private double microphoneGain = 1.0;
    private double musicLevel;
    private double microphoneLevel;
    private double outputLevel;
    private double outputPeak;
    private bool clipping;
    private bool warnedClipping;
    private DateTime lastMeterAt = DateTime.MinValue;

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
            found.Values.OrderByDescending(item => item.State == "正在发声").ThenBy(item => item.Name).ToList()
        );
    }

    public async Task StartAsync(AudioStartOptions options)
    {
        await StopAsync();

        var outputDevice =
            FindDevice(DataFlow.Render, options.OutputDeviceId)
            ?? throw new InvalidOperationException("找不到送队友的输出设备");
        var deviceFormat = outputDevice.AudioClient.MixFormat;
        // 注意：设备的混音格式是 WaveFormatExtensible，而 NAudio 的 ToSampleProvider 不认它，
        // 所以整条链路统一用普通 32 位浮点（字节布局一样，WASAPI 也接受）。
        var mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            deviceFormat.SampleRate,
            deviceFormat.Channels
        );

        musicBuffer = CreateBuffer(mixFormat);
        var sources = new List<ISampleProvider>();

        musicVolume = new VolumeSampleProvider(musicBuffer.ToSampleProvider())
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
            microphoneCapture = new WasapiCapture(microphoneDevice, true, 20);
            var microphoneFormat = microphoneCapture.WaveFormat;
            microphoneBuffer = CreateBuffer(
                WaveFormat.CreateIeeeFloatWaveFormat(
                    microphoneFormat.SampleRate,
                    microphoneFormat.Channels
                )
            );
            ISampleProvider microphoneProvider = microphoneBuffer.ToSampleProvider();
            if (microphoneFormat.SampleRate != mixFormat.SampleRate)
            {
                microphoneProvider = new WdlResamplingSampleProvider(
                    microphoneProvider,
                    mixFormat.SampleRate
                );
            }
            if (microphoneFormat.Channels != mixFormat.Channels)
            {
                microphoneProvider = new ChannelMapperSampleProvider(
                    microphoneProvider,
                    mixFormat.Channels
                );
            }
            microphoneVolume = new VolumeSampleProvider(microphoneProvider)
            {
                Volume = (float)microphoneGain
            };
            sources.Add(microphoneVolume);
            microphoneCapture.DataAvailable += OnMicrophoneData;
            microphoneCapture.StartRecording();
            Log($"麦克风已接入：{microphoneDevice.FriendlyName}（{microphoneFormat.SampleRate}Hz/{microphoneFormat.Channels}ch）");
        }

        mixer = new MixingSampleProvider(sources) { ReadFully = true };
        masterBuffer = CreateBuffer(mixFormat);
        masterOutput = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 50);
        masterOutput.Init(masterBuffer);
        masterOutput.Play();

        pumpCancellation = new CancellationTokenSource();
        pumpThread = new Thread(Pump) { IsBackground = true, Name = "sound-connect-mix" };
        pumpThread.Start();

        IsRunning = true;
        Log($"通路已建立：混音 → {outputDevice.FriendlyName}（已按 -30dB 补偿虚拟线增益）");
        RaiseChanged();
    }

    public Task StopAsync()
    {
        var cancellation = pumpCancellation;
        pumpCancellation = null;
        cancellation?.Cancel();
        pumpThread?.Join(500);
        pumpThread = null;

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
        masterBuffer = null;
        mixer = null;
        musicVolume = null;
        microphoneVolume = null;
        musicBuffer = null;
        microphoneBuffer = null;

        musicLevel = 0;
        microphoneLevel = 0;
        outputLevel = 0;
        outputPeak = 0;
        clipping = false;
        warnedClipping = false;
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
        musicBuffer?.AddSamples(chunk, 0, chunk.Length);
    }

    private void OnMicrophoneData(object? sender, WaveInEventArgs eventArgs)
    {
        microphoneLevel =
            Level(eventArgs.Buffer, microphoneBuffer?.WaveFormat, eventArgs.BytesRecorded)
            * microphoneGain;
        microphoneBuffer?.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
    }

    private void Pump()
    {
        var cancel = pumpCancellation;
        var block = mixer;
        var target = masterBuffer;
        if (cancel is null || block is null || target is null) return;

        var format = block.WaveFormat;
        var frames = format.SampleRate * BlockMilliseconds / 1000;
        var samples = new float[frames * format.Channels];
        var bytes = new byte[samples.Length * 4];
        var peakSinceReport = 0.0;

        while (!cancel.IsCancellationRequested)
        {
            if (target.BufferedDuration.TotalMilliseconds > 150)
            {
                Thread.Sleep(5);
                continue;
            }

            var read = block.Read(samples, 0, samples.Length);
            if (read <= 0)
            {
                Thread.Sleep(5);
                continue;
            }

            // 这里量到的就是"队友实际听到的电平"：虚拟线自带的 +30dB 正好抵消下面这次衰减
            outputLevel = Level(samples, read);
            for (var index = 0; index < read; index++)
            {
                var magnitude = Math.Abs(samples[index]);
                if (magnitude > peakSinceReport) peakSinceReport = magnitude;
                samples[index] *= (float)CableCompensation;
            }

            Buffer.BlockCopy(samples, 0, bytes, 0, read * 4);
            target.AddSamples(bytes, 0, read * 4);

            if ((DateTime.UtcNow - lastMeterAt).TotalMilliseconds >= MeterIntervalMs)
            {
                lastMeterAt = DateTime.UtcNow;
                outputPeak = peakSinceReport;
                clipping = peakSinceReport >= 0.99;
                if (clipping && !warnedClipping)
                {
                    warnedClipping = true;
                    Log("输出削顶了：把音乐或麦克风音量往下调一点");
                }
                peakSinceReport = 0;
                RaiseChanged();
            }
        }
    }

    private BufferedWaveProvider CreateBuffer(WaveFormat format)
    {
        return new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            ReadFully = true,
            DiscardOnBufferOverflow = true
        };
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
                    var sourceChannel = sourceChannels == 1 ? 0 : Math.Min(channel, sourceChannels - 1);
                    destination[offset + frame * targetChannels + channel] =
                        buffer[frame * sourceChannels + sourceChannel];
                }
            }
            return readFrames * targetChannels;
        }
    }
}
