using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SoundConnect.Audio;

/// <summary>
/// 用 WASAPI 进程回环抓某个进程的声音。
/// 采集流直接用目标格式初始化，这样两头格式一致，整条路不用重采样。
///
/// 激活和采集都放在采集线程上做，构造函数里不做：
/// WASAPI 这些 COM 对象有公寓亲和性，在 STA 线程（比如 WinUI 的界面线程）里创建、
/// 再拿到别的线程用，QueryInterface 会直接报 E_NOINTERFACE。
/// 让创建方和使用方是同一个线程，这个坑就绕开了。
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly uint processId;
    private readonly WaveFormat format;
    private readonly int blockAlign;
    private readonly AutoResetEvent bufferEvent = new(false);
    private readonly ManualResetEventSlim ready = new(false);
    private readonly Thread pump;
    private Exception? startupError;
    private volatile bool running;

    public WaveFormat Format => format;

    public event Action<byte[]>? DataAvailable;

    public ProcessLoopbackCapture(uint processId, WaveFormat format)
    {
        this.processId = processId;
        this.format = format;
        blockAlign = format.BlockAlign > 0
            ? format.BlockAlign
            : Math.Max(1, format.Channels * format.BitsPerSample / 8);
        pump = new Thread(Pump) { IsBackground = true, Name = "process-loopback" };
    }

    public static bool IsFloat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        return format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat;
    }

    /// <summary>启动采集并等它真正就绪；激活失败会在这里抛出来。</summary>
    public void Start()
    {
        running = true;
        pump.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("进程回环采集启动超时");
        }
        if (startupError is not null)
        {
            throw new InvalidOperationException(
                $"进程回环采集启动失败：{startupError.Message}",
                startupError
            );
        }
    }

    public void Stop()
    {
        running = false;
        bufferEvent.Set();
        if (pump.IsAlive)
        {
            pump.Join(1000);
        }
    }

    public void Dispose()
    {
        Stop();
        bufferEvent.Dispose();
        ready.Dispose();
    }

    private void Pump()
    {
        AudioInterop.IAudioClient? client = null;
        AudioInterop.IAudioCaptureClient? captureClient = null;
        try
        {
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
            client.Start();
        }
        catch (Exception error)
        {
            startupError = error;
            ready.Set();
            return;
        }

        ready.Set();

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

        try
        {
            client.Stop();
        }
        catch (COMException)
        {
            // 已经停了就算了
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
