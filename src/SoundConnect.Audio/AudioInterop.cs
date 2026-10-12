using System.Runtime.InteropServices;

namespace SoundConnect.Audio;

/// <summary>
/// WASAPI 进程回环需要的 COM 互操作。Windows 10 2004（build 19041）起提供：
/// ActivateAudioInterfaceAsync 配 AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK，
/// 就能只抓指定进程（含其子进程树）的声音，其他程序一概不碰。
/// </summary>
public static class AudioInterop
{
    /// <summary>进程回环专用的虚拟设备路径，不是系统里真实存在的端点。</summary>
    private const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";

    private const int ActivationTypeProcessLoopback = 1;
    private const int ProcessLoopbackModeIncludeTargetProcessTree = 0;
    private const ushort VtBlob = 65;

    public const int ShareModeShared = 0;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint BufferFlagsSilent = 0x00000002;

    public static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(
            out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface
        );
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        void Initialize(
            int shareMode,
            uint streamFlags,
            long bufferDuration,
            long periodicity,
            IntPtr format,
            IntPtr audioSessionGuid
        );

        void GetBufferSize(out uint bufferSize);

        void GetStreamLatency(out long latency);

        void GetCurrentPadding(out uint padding);

        void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

        void GetMixFormat(out IntPtr format);

        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        void Start();

        void Stop();

        void Reset();

        void SetEventHandle(IntPtr eventHandle);

        void GetService(
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.IUnknown)] out object service
        );
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        void GetBuffer(
            out IntPtr data,
            out uint framesToRead,
            out uint flags,
            out ulong devicePosition,
            out ulong qpcPosition
        );

        void ReleaseBuffer(uint framesRead);

        void GetNextPacketSize(out uint packetSize);
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Completed = new(false);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            Completed.Set();
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid interfaceId,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        [MarshalAs(UnmanagedType.Interface)] out IActivateAudioInterfaceAsyncOperation operation
    );

    public static IAudioClient ActivateProcessLoopback(uint processId)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = processId,
                ProcessLoopbackMode = ProcessLoopbackModeIncludeTargetProcessTree
            }
        };

        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPointer = Marshal.AllocHGlobal(paramsSize);
        Marshal.StructureToPtr(activationParams, paramsPointer, false);

        var blobPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        var blob = new PropVariantBlob
        {
            Vt = VtBlob,
            BlobSize = (uint)paramsSize,
            BlobData = paramsPointer
        };
        Marshal.StructureToPtr(blob, blobPointer, false);

        try
        {
            var interfaceId = IidAudioClient;
            var handler = new ActivationHandler();
            var result = ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref interfaceId,
                blobPointer,
                handler,
                out var operation
            );
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }
            if (!handler.Completed.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("等待进程回环激活返回超时");
            }

            operation.GetActivateResult(out var activateResult, out var activated);
            Marshal.ThrowExceptionForHR(activateResult);
            return (IAudioClient)activated;
        }
        finally
        {
            Marshal.FreeHGlobal(blobPointer);
            Marshal.FreeHGlobal(paramsPointer);
        }
    }
}
