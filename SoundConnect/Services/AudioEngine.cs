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
    /// <summary>
    /// 不同虚拟线自带的增益不一样，实测：Steam 那条 +30dB（31.62x），VB-CABLE 是单位增益。
    /// 这条线涨多少，我们送进去之前就压多少，否则不是削顶就是小声。
    /// 换新线用 tools/audio-probe 的 probe 命令量一下，再回来加一行。
    /// </summary>
    private static double CompensationFor(string driverName)
    {
        if (driverName.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase))
        {
            return 0.0316; // 实测 +30dB
        }
        return 1.0; // VB-CABLE 等实测单位增益
    }
    private const int AlignmentBlockMs = 5;
    private const int MeterIntervalMs = 100;
    private const int LogLimit = 200;
    private const int OutputLatencyMs = 20;
    private const int MicrophoneLatencyMs = 10;
    /// <summary>源缓冲涨到这个深度就说明两边时钟漂了，直接跳到最新数据，避免延迟越积越大。</summary>
    private const int MaxSourceBufferedMs = 150;
    /// <summary>噪声门开门阈值（约 -51dB）：说话电平要高于它才放行。</summary>
    private const double MicrophoneGateOpenRms = 0.0028;
    /// <summary>噪声门关门阈值（约 -61dB）：留 10dB 回差，免得电平卡在阈值附近时门反复开关发出怪声。</summary>
    private const double MicrophoneGateCloseRms = 0.0009;
    /// <summary>门打开后的保持时间，避免说话换气的间隙被切掉。</summary>
    private const int MicrophoneGateHoldMs = 250;
    /// <summary>自动增益的目标：说话时送到虚拟线里的 RMS（约 -26dB）。</summary>
    private const double MicrophoneTargetRms = 0.05;
    private const double AutoGainMin = 1.0;
    /// <summary>上限 +26dB。再往上就不是放大语音，而是在放大底噪了——那听感就是"电流麦"。</summary>
    private const double AutoGainMax = 20.0;
    /// <summary>自动增益每次（100ms）最多变化的比例，约 0.6dB，避免电平一跳一跳。</summary>
    private const double AutoGainStep = 1.07;

    private readonly object sync = new();
    private readonly List<string> log = new();
    private readonly MMDeviceEnumerator enumerator = new();

    private ProcessLoopbackCapture? musicCapture;
    private WasapiCapture? microphoneCapture;
    private WasapiCapture? partnerCapture;
    private BufferedWaveProvider? musicBuffer;
    private BufferedWaveProvider? microphoneBuffer;
    private MixingSampleProvider? mixer;
    private VolumeSampleProvider? musicVolume;
    private VolumeSampleProvider? microphoneVolume;
    private BlockAlignedSampleProvider? musicAligned;
    private BlockAlignedSampleProvider? microphoneAligned;
    private WasapiOut? masterOutput;
    private MasterWaveProvider? masterProvider;
    private WasapiOut? monitorOutput;
    private EffectMixerSampleProvider? effectsBus;
    private EffectMixerSampleProvider? monitorBus;
    private VolumeSampleProvider? effectsVolume;
    private readonly EffectLibrary effectLibrary = new();
    private IReadOnlyList<EffectInfo> effects = [];

    private double musicGain = 1.0;
    private double cableCompensation = 1.0;
    private double microphoneGain = 1.0;
    private double effectsGain = 0.9;
    private double autoGain = 1.0;
    private double autoGainTarget = 1.0;
    private double speechSumSquares;
    private long speechSamples;
    private byte[] gateSilence = [];
    private DateTime gateOpenUntil = DateTime.MinValue;
    private double musicLevel;
    private double microphoneLevel;
    private double outputLevel;
    private double outputPeak;
    private bool clipping;
    private bool warnedClipping;
    private DateTime lastMeterAt = DateTime.MinValue;
    private AudioStartOptions? lastOptions;
    private DateTime startedAt;
    private long musicUnderruns;
    private long microphoneUnderruns;
    private long lastMusicStarved;
    private long lastMicrophoneStarved;
    private DateTime lastUnderrunAt = DateTime.MinValue;
    private double sentLevel;
    private double sentPeak;
    private double sentPeakSinceReport;
    private long sentClipSamples;

    public event Action? Changed;

    public bool IsRunning { get; private set; }

    public bool AutoGainEnabled { get; set; } = true;

    /// <summary>手动增益乘上自动增益，就是最终送给队友的麦克风倍数。</summary>
    private double EffectiveMicrophoneGain => microphoneGain * (AutoGainEnabled ? autoGain : 1);

    public IReadOnlyList<EffectInfo> Effects => effects;

    public string EffectFolder => effectLibrary.Folder;

    public int ActiveEffects => effectsBus?.ActiveVoices ?? 0;

    public bool IsMonitoring => monitorOutput is not null;

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
        AutoGainDb = AutoGainEnabled ? 20 * Math.Log10(Math.Max(autoGain, 0.0001)) : 0,
        SentLevel = sentLevel,
        SentPeak = sentPeak,
        SentClipSamples = sentClipSamples,
        Effects = effects,
        EffectFolder = effectLibrary.Folder,
        ActiveEffects = ActiveEffects,
        IsMonitoring = IsMonitoring,
        ElapsedSeconds = IsRunning ? (DateTime.UtcNow - startedAt).TotalSeconds : 0,
        MusicGain = musicGain,
        MicrophoneGain = microphoneGain,
        EffectsGain = effectsGain,
        Log = log.ToArray()
    };

    /// <summary>
    /// 枚举设备要读属性存储，枚举音频会话是 COM 调用，都不是瞬间的事——
    /// 放到后台线程做，界面才不会一开始就卡住。注意枚举器要在各自线程里新建，
    /// WASAPI 的 COM 对象跨公寓用会直接报 E_NOINTERFACE。
    /// </summary>
    public async Task RefreshDevicesAsync()
    {
        var started = Environment.TickCount64;
        var (render, capture) = await Task.Run(() => ReadAllDevices(Log));
        RenderDevices = render;
        CaptureDevices = capture;
        Log($"设备枚举用了 {Environment.TickCount64 - started}ms（{render.Count} 输出 / {capture.Count} 输入）");
        RaiseChanged();
    }

    private static (List<AudioDeviceInfo> Render, List<AudioDeviceInfo> Capture) ReadAllDevices(
        Action<string> log
    )
    {
        var total = Environment.TickCount64;
        using var enumerator = new MMDeviceEnumerator();
        var started = Environment.TickCount64;
        var render = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var capture = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        log($"计时：枚举端点 {Environment.TickCount64 - started}ms（{render.Count} + {capture.Count}）");
        var readStarted = Environment.TickCount64;
        var renderInfo = ToDeviceInfoList(render, "输出");
        var captureInfo = ToDeviceInfoList(capture, "输入");
        log($"计时：读设备名 {Environment.TickCount64 - readStarted}ms，合计 {Environment.TickCount64 - total}ms");
        return (
            renderInfo,
            captureInfo
        );
    }

    /// <summary>
    /// 读设备名是 COM 调用，实测每个要 100-300ms（往音频服务跑一趟），串行读十几个设备就是好几秒。
    /// 这里并行读，整体降到最慢那一个的量级。驱动名不在这里读——它只在开始共享时对选中的设备读一次。
    /// </summary>
    private static List<AudioDeviceInfo> ToDeviceInfoList(List<MMDevice> devices, string kind)
    {
        var result = new AudioDeviceInfo[devices.Count];
        Parallel.For(
            0,
            devices.Count,
            index =>
            {
                var device = devices[index];
                var name = device.FriendlyName;
                result[index] = new AudioDeviceInfo(
                    device.ID,
                    name,
                    string.Empty,
                    LooksVirtual(name),
                    kind
                );
            }
        );
        return result.ToList();
    }

    public async Task<IReadOnlyList<AudioProcessInfo>> ListAudioProcessesAsync()
    {
        var started = Environment.TickCount64;
        var processes = await Task.Run(ListAudioProcesses);
        Log($"音频会话枚举用了 {Environment.TickCount64 - started}ms（{processes.Count} 个程序）");
        return processes;
    }

    private static IReadOnlyList<AudioProcessInfo> ListAudioProcesses()
    {
        using var enumerator = new MMDeviceEnumerator();
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
                Console.WriteLine($"枚举 {device.FriendlyName} 的会话失败：{error.Message}");
            }
        }

        return found
            .Values.OrderByDescending(item => item.State == "正在发声")
            .ThenBy(item => item.Name)
            .ToList();
    }

    public async Task StartAsync(AudioStartOptions options)
    {
        await StopAsync();
        lastOptions = options;

        var outputDevice =
            FindDevice(DataFlow.Render, options.OutputDeviceId)
            ?? throw new InvalidOperationException("找不到送队友的输出设备");
        var deviceFormat = outputDevice.AudioClient.MixFormat;
        cableCompensation = CompensationFor(outputDevice.DeviceFriendlyName);
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

        effectsBus = new EffectMixerSampleProvider();
        effectsVolume = new VolumeSampleProvider(effectsBus) { Volume = (float)effectsGain };
        sources.Add(effectsVolume);

        mixer = new MixingSampleProvider(sources) { ReadFully = true };
        masterProvider = new MasterWaveProvider(this, mixer);
        masterOutput = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, OutputLatencyMs);
        masterOutput.Init(masterProvider);
        masterOutput.Play();

        StartPartnerMonitor(outputDevice);
        StartMonitorOutput(options.MonitorDeviceId);

        IsRunning = true;
        startedAt = DateTime.UtcNow;
        Log(
            $"通路已建立：混音 → {outputDevice.FriendlyName}"
                + $"（线路增益补偿 {20 * Math.Log10(cableCompensation):F1}dB，"
                + $"送出规格 {deviceFormat.SampleRate}Hz/{deviceFormat.Channels}ch）"
        );
        RaiseChanged();
    }

    public async Task StartAsync()
    {
        await StartAsync(lastOptions ?? await BuildDefaultOptionsAsync());
    }

    /// <summary>没配过时的兜底：优先 VB-CABLE，麦克风取第一个真实设备，音乐取正在发声的那个程序。</summary>
    private async Task<AudioStartOptions> BuildDefaultOptionsAsync()
    {
        if (RenderDevices.Count == 0 || CaptureDevices.Count == 0)
        {
            await RefreshDevicesAsync();
        }

        var output =
            RenderDevices.FirstOrDefault(device =>
                device.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
            )
            ?? RenderDevices.FirstOrDefault(device =>
                device.Name.Contains("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase)
            )
            ?? RenderDevices.FirstOrDefault(device => device.IsVirtual);
        var microphone = CaptureDevices.FirstOrDefault(device => !device.IsVirtual);
        var processes = await ListAudioProcessesAsync();
        var music = processes.FirstOrDefault(item => item.State == "正在发声");

        if (output is null)
        {
            throw new InvalidOperationException("没找到可用的虚拟线，先在界面里选一条");
        }

        return new AudioStartOptions(
            music?.ProcessId ?? 0,
            microphone?.Id ?? string.Empty,
            output.Id,
            string.Empty
        );
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
        if (partnerCapture is not null)
        {
            partnerCapture.DataAvailable -= OnPartnerData;
            partnerCapture.StopRecording();
            partnerCapture.Dispose();
            partnerCapture = null;
        }

        masterOutput?.Stop();
        masterOutput?.Dispose();
        masterOutput = null;
        masterProvider = null;
        monitorOutput?.Stop();
        monitorOutput?.Dispose();
        monitorOutput = null;
        monitorBus = null;
        effectsBus = null;
        effectsVolume = null;
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
        sentLevel = 0;
        sentPeak = 0;
        sentPeakSinceReport = 0;
        sentClipSamples = 0;
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
        var clamped = Math.Clamp(volume, 0, 100); // 上限 100x，也就是 +40dB
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
        else if (channel == "effect")
        {
            effectsGain = clamped;
            if (effectsVolume is not null) effectsVolume.Volume = (float)clamped;
        }
    }

    public double GetGain(string channel)
    {
        return channel switch
        {
            "music" => musicGain,
            "effect" => effectsGain,
            _ => microphoneGain
        };
    }

    private void OnMusicData(byte[] chunk)
    {
        musicLevel = Level(chunk, musicBuffer?.WaveFormat) * musicGain;
        Push(musicBuffer, chunk, chunk.Length, "音乐");
    }

    /// <summary>
    /// 捕获虚拟线的录音端，量"队友实际收到的信号"。
    /// 质量好不好不用再等朋友反馈：这里直接能看到电平、峰值和削顶采样数。
    /// </summary>
    /// <summary>
    /// 本地监听：只把音效送到你的耳机。音乐你本来就在直接听，麦克风不能监听（会听到自己）。
    /// </summary>
    private void StartMonitorOutput(string monitorDeviceId)
    {
        if (string.IsNullOrWhiteSpace(monitorDeviceId)) return;
        var monitorDevice = FindDevice(DataFlow.Render, monitorDeviceId);
        if (monitorDevice is null)
        {
            Log("找不到本地监听设备，音效只有队友能听到");
            return;
        }

        monitorBus = new EffectMixerSampleProvider();
        monitorOutput = new WasapiOut(monitorDevice, AudioClientShareMode.Shared, true, 30);
        monitorOutput.Init(monitorBus.ToWaveProvider());
        monitorOutput.Play();
        Log($"本地监听已开启：音效 → {monitorDevice.FriendlyName}");
    }

    public Task ScanEffectsAsync(string? folder)
    {
        effects = effectLibrary.Scan(folder);
        Log($"音效库已加载：{effects.Count} 个（{effectLibrary.Folder}）");
        RaiseChanged();
        return Task.CompletedTask;
    }

    public void TriggerEffect(string name)
    {
        var effect = effectLibrary.Resolve(name);
        if (effect is null)
        {
            Log($"音效「{name}」打不开，跳过");
            return;
        }
        effectsBus?.Trigger(effect, 1.0);
        monitorBus?.Trigger(effect, 1.0);
    }

    public void StopEffects()
    {
        effectsBus?.StopAll();
        monitorBus?.StopAll();
    }

    private void StartPartnerMonitor(MMDevice outputDevice)
    {
        try
        {
            var partner = FindCapturePartner(outputDevice);
            if (partner is null)
            {
                Log("没找到这条线的录音端，跳过线路监测");
                return;
            }
            partnerCapture = new WasapiCapture(partner, true, 20);
            partnerCapture.DataAvailable += OnPartnerData;
            partnerCapture.StartRecording();
            Log($"线路监测已接入：{partner.FriendlyName}");
        }
        catch (Exception error)
        {
            Log($"线路监测启动失败（不影响通路）：{error.Message}");
        }
    }

    private MMDevice? FindCapturePartner(MMDevice outputDevice)
    {
        foreach (
            var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
        )
        {
            if (
                string.Equals(
                    device.DeviceFriendlyName,
                    outputDevice.DeviceFriendlyName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return device;
            }
        }
        return null;
    }

    private void OnPartnerData(object? sender, WaveInEventArgs eventArgs)
    {
        double sum = 0;
        var samples = 0;
        for (var offset = 0; offset + 4 <= eventArgs.BytesRecorded; offset += 4)
        {
            var value = BitConverter.ToSingle(eventArgs.Buffer, offset);
            sum += value * value;
            samples++;
            var magnitude = Math.Abs(value);
            if (magnitude > sentPeakSinceReport) sentPeakSinceReport = magnitude;
            if (magnitude >= 0.99) sentClipSamples++;
        }
        if (samples > 0)
        {
            sentLevel = Math.Sqrt(sum / samples);
        }
    }

    private void OnMicrophoneData(object? sender, WaveInEventArgs eventArgs)
    {
        var buffer = microphoneBuffer;
        if (buffer is null) return;

        var level = Level(eventArgs.Buffer, buffer.WaveFormat, eventArgs.BytesRecorded);
        microphoneLevel = level * EffectiveMicrophoneGain;

        if (level >= MicrophoneGateOpenRms)
        {
            // 只统计说话那部分，静音段不参与，否则自动增益会把"会说"当成"很轻"
            var samples = eventArgs.BytesRecorded / 4;
            speechSumSquares += level * level * samples;
            speechSamples += samples;
        }

        var gateOpen = DateTime.UtcNow < gateOpenUntil;
        if (level >= (gateOpen ? MicrophoneGateCloseRms : MicrophoneGateOpenRms))
        {
            // 回差 + 保持时间：门一旦开了，要掉到更低的值并持续一段时间才关
            gateOpenUntil = DateTime.UtcNow.AddMilliseconds(MicrophoneGateHoldMs);
            gateOpen = true;
        }
        if (!gateOpen)
        {
            // 底噪，门关着：送等长静音，别把噪声放大以后塞给队友
            if (gateSilence.Length < eventArgs.BytesRecorded)
            {
                gateSilence = new byte[eventArgs.BytesRecorded];
            }
            Push(buffer, gateSilence, eventArgs.BytesRecorded, "麦克风");
            return;
        }

        Push(buffer, eventArgs.Buffer, eventArgs.BytesRecorded, "麦克风");
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
            samples[index] *= (float)cableCompensation;
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
            UpdateAutoGain();
            SmoothAutoGain();
            RaiseChanged();
        }
    }

    /// <summary>每秒按说话电平调一次自动增益，每次最多动 6dB，避免抽气感。</summary>
    private void UpdateAutoGain()
    {
        if (speechSamples <= 0) return;

        var speechRms = Math.Sqrt(speechSumSquares / speechSamples);
        speechSumSquares = 0;
        speechSamples = 0;
        if (!AutoGainEnabled || !IsRunning) return;

        autoGainTarget = Math.Clamp(
            MicrophoneTargetRms / Math.Max(speechRms, 1e-5),
            AutoGainMin,
            AutoGainMax
        );
    }

    /// <summary>把当前增益朝目标平滑推进：增益跳变会让队友听到"电流"一样的噪声。</summary>
    private void SmoothAutoGain()
    {
        if (!AutoGainEnabled) return;
        autoGain = Math.Clamp(autoGainTarget, autoGain / AutoGainStep, autoGain * AutoGainStep);
        if (microphoneVolume is not null)
        {
            microphoneVolume.Volume = (float)EffectiveMicrophoneGain;
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

        sentPeak = sentPeakSinceReport;
        sentPeakSinceReport = 0;

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

    private static List<AudioDeviceInfo> ReadDevices(MMDeviceEnumerator enumerator, DataFlow flow, string kind)
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
