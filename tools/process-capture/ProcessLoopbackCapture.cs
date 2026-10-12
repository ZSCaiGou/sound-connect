using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SoundConnect.ProcessCapture;

/// <summary>
/// 用 WASAPI 进程回环抓某个进程的声音。
/// 采集流直接用渲染端的格式初始化，这样两头格式一致，整条路不用重采样。
/// </summary>
internal sealed class ProcessLoopbackCapture : IDisposable
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly AudioInterop.IAudioClient client;
    private readonly AudioInterop.IAudioCaptureClient captureClient;
    private readonly AutoResetEvent bufferEvent = new(false);
    private readonly Thread pump;
    private readonly int blockAlign;
    private volatile bool running;

    public WaveFormat Format { get; }

    public event Action<byte[]>? DataAvailable;

    public ProcessLoopbackCapture(uint processId, WaveFormat format)
    {
        Format = format;
        blockAlign = format.BlockAlign > 0
            ? format.BlockAlign
            : Math.Max(1, format.Channels * format.BitsPerSample / 8);

        var waveFormat = ToWaveFormatEx(format);
        var formatPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioInterop.WaveFormatEx>());
        Marshal.StructureToPtr(waveFormat, formatPointer, false);
        try
        {
            client = AudioInterop.ActivateProcessLoopback(processId);
            client.Initialize(
                AudioInterop.ShareModeShared,
                AudioInterop.StreamFlagsLoopback | AudioInterop.StreamFlagsEventCallback,
                0,
                0,
                formatPointer,
                IntPtr.Zero
            );
        }
        finally
        {
            Marshal.FreeHGlobal(formatPointer);
        }

        client.SetEventHandle(bufferEvent.SafeWaitHandle.DangerousGetHandle());
        var captureClientId = AudioInterop.IidAudioCaptureClient;
        client.GetService(ref captureClientId, out var service);
        captureClient = (AudioInterop.IAudioCaptureClient)service;

        pump = new Thread(Pump) { IsBackground = true, Name = "process-loopback-pump" };
    }

    public static bool IsFloat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        return format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat;
    }

    public void Start()
    {
        client.Start();
        running = true;
        pump.Start();
    }

    public void Stop()
    {
        running = false;
        try
        {
            client.Stop();
        }
        catch (COMException)
        {
            // 已经停了就算了
        }
        bufferEvent.Set();
    }

    public void Dispose()
    {
        Stop();
        if (pump.IsAlive)
        {
            pump.Join(500);
        }
        bufferEvent.Dispose();
    }

    private void Pump()
    {
        while (running)
        {
            if (!bufferEvent.WaitOne(500))
            {
                continue;
            }

            while (running)
            {
                captureClient.GetNextPacketSize(out var pendingFrames);
                if (pendingFrames == 0)
                {
                    break;
                }

                captureClient.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                var length = (int)frames * blockAlign;
                var chunk = new byte[length];
                var silent = (flags & AudioInterop.BufferFlagsSilent) != 0 || data == IntPtr.Zero;
                if (!silent)
                {
                    Marshal.Copy(data, chunk, 0, length);
                }
                captureClient.ReleaseBuffer(frames);
                if (length > 0)
                {
                    DataAvailable?.Invoke(chunk);
                }
            }
        }
    }

    private static AudioInterop.WaveFormatEx ToWaveFormatEx(WaveFormat format)
    {
        var blockAlign = (ushort)(format.Channels * format.BitsPerSample / 8);
        return new AudioInterop.WaveFormatEx
        {
            FormatTag = IsFloat(format) ? (ushort)3 : (ushort)1,
            Channels = (ushort)format.Channels,
            SamplesPerSec = (uint)format.SampleRate,
            AvgBytesPerSec = (uint)(format.SampleRate * blockAlign),
            BlockAlign = blockAlign,
            BitsPerSample = (ushort)format.BitsPerSample,
            ExtraSize = 0
        };
    }
}
