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
