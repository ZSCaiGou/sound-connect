namespace SoundConnect.Shared.Services;

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    string DriverName,
    bool IsVirtual,
    string Kind
);

public sealed record AudioProcessInfo(int ProcessId, string Name, string State);

public sealed record AudioStartOptions(
    int MusicProcessId,
    string MicrophoneDeviceId,
    string OutputDeviceId
);

public sealed class AudioEngineSnapshot
{
    public bool IsRunning { get; init; }
    public double MusicLevel { get; init; }
    public double MicrophoneLevel { get; init; }
    public double OutputLevel { get; init; }
    public double OutputPeak { get; init; }
    public bool IsClipping { get; init; }
    public long MusicUnderruns { get; init; }
    public long MicrophoneUnderruns { get; init; }
    public double AutoGainDb { get; init; }
    /// <summary>从虚拟线录音端量到的"队友实际收到的信号"。</summary>
    public double SentLevel { get; init; }
    public double SentPeak { get; init; }
    public long SentClipSamples { get; init; }
    public double MusicGain { get; init; }
    public double MicrophoneGain { get; init; }
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 音频通路。界面只认这个接口，实现放在 Windows 宿主里（要用 WASAPI）。
/// </summary>
public interface IAudioEngine
{
    event Action? Changed;

    bool IsRunning { get; }

    /// <summary>麦克风自动增益：把说话电平自动拉到合适响度，省得每次靠手调。</summary>
    bool AutoGainEnabled { get; set; }

    AudioEngineSnapshot Snapshot { get; }

    IReadOnlyList<AudioDeviceInfo> RenderDevices { get; }

    IReadOnlyList<AudioDeviceInfo> CaptureDevices { get; }

    Task RefreshDevicesAsync();

    Task<IReadOnlyList<AudioProcessInfo>> ListAudioProcessesAsync();

    Task StartAsync(AudioStartOptions options);

    Task StopAsync();

    void SetGain(string channel, double volume);

    double GetGain(string channel);
}
