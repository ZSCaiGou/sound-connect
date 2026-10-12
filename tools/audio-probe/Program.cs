using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundConnect.AudioProbe;

/// <summary>
/// 虚拟音频线探针。往一个输出设备放一段 1kHz 正弦，同时从另一个输入设备录音，
/// 用它判断这对设备是不是真正的回环（声音能不能从输出端走到输入端），
/// 并顺带量出这条线的往返延迟。
/// </summary>
internal static class Program
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");
    private const double ToneFrequency = 1000;
    private const int LeadMs = 500;
    private const int TailMs = 500;

    private static int Main(string[] args)
    {
        try
        {
            var command = args.Length > 0 ? args[0] : "list";
            return command switch
            {
                "list" => ListDevices(),
                "probe" => Probe(args[1..]),
                _ => Usage()
            };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"出错：{error.GetType().Name}: {error.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  audio-probe list");
        Console.WriteLine("  audio-probe probe --render <名称片段或序号> --capture <名称片段或序号> [--ms 1500]");
        return 1;
    }

    private static int ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        PrintDevices(enumerator, DataFlow.Render, "输出设备");
        PrintDevices(enumerator, DataFlow.Capture, "输入设备");
        return 0;
    }

    private static void PrintDevices(MMDeviceEnumerator enumerator, DataFlow flow, string title)
    {
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).ToList();
        Console.WriteLine($"[{title}] 共 {devices.Count} 个");
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            string format;
            try
            {
                format = $"{device.AudioClient.MixFormat.SampleRate}Hz/{device.AudioClient.MixFormat.Channels}ch";
            }
            catch
            {
                format = "格式未知";
            }
            Console.WriteLine($"  [{index}] {device.FriendlyName}  |  驱动: {device.DeviceFriendlyName}  |  {format}");
        }
    }

    private static int Probe(string[] args)
    {
        var options = ParseOptions(args);
        var toneMs = int.TryParse(options.GetValueOrDefault("ms"), out var parsed) ? parsed : 1500;

        using var enumerator = new MMDeviceEnumerator();
        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var renderDevice = Resolve(renderDevices, options.GetValueOrDefault("render"), "输出");
        var captureDevice = Resolve(captureDevices, options.GetValueOrDefault("capture"), "输入");

        Console.WriteLine($"播放端：{renderDevice.FriendlyName}（驱动：{renderDevice.DeviceFriendlyName}）");
        Console.WriteLine($"录制端：{captureDevice.FriendlyName}（驱动：{captureDevice.DeviceFriendlyName}）");

        WaveFormat captureFormat;
        using var recorded = new MemoryStream();
        var gate = new object();
        using (var capture = new WasapiCapture(captureDevice, true, 20))
        {
            captureFormat = capture.WaveFormat;
            capture.DataAvailable += (_, eventArgs) =>
            {
                lock (gate)
                {
                    recorded.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
                }
            };
            capture.StartRecording();
            Thread.Sleep(LeadMs);
            Console.WriteLine($"开始播放 {toneMs}ms 的 1kHz 正弦…");
            PlayTone(renderDevice, toneMs);
            Thread.Sleep(TailMs);
            capture.StopRecording();
        }

        var samples = ToMono(recorded.ToArray(), captureFormat);
        var durationMs = samples.Length * 1000.0 / captureFormat.SampleRate;
        Console.WriteLine($"录到 {durationMs:F0}ms，采样率 {captureFormat.SampleRate}Hz/{captureFormat.Channels}ch");

        var window = Math.Max(1, captureFormat.SampleRate / 50);
        var baselineEnd = (int)(captureFormat.SampleRate * (LeadMs - 100) / 1000.0);
        var baseline = 0.0;
        for (var start = 0; start + window <= baselineEnd; start += window)
        {
            baseline = Math.Max(baseline, Goertzel(samples, start, window, captureFormat.SampleRate));
        }

        var threshold = Math.Max(baseline * 5, 0.004);
        var detectAt = -1;
        var peak = 0.0;
        var searchFrom = (int)(captureFormat.SampleRate * (LeadMs - 100) / 1000.0);
        for (var start = searchFrom; start + window <= samples.Length; start += window)
        {
            var amplitude = Goertzel(samples, start, window, captureFormat.SampleRate);
            peak = Math.Max(peak, amplitude);
            if (detectAt < 0 && amplitude > threshold)
            {
                detectAt = start;
            }
        }

        Console.WriteLine($"1kHz 能量：基线 {baseline:F4} / 峰值 {peak:F4} / 判定阈值 {threshold:F4}");
        if (detectAt < 0)
        {
            Console.WriteLine("结论：没有检测到信号，这对设备不是回环（或者这段线当前没在通）。");
            return 3;
        }

        var latency = detectAt * 1000.0 / captureFormat.SampleRate - LeadMs;
        Console.WriteLine($"结论：是回环，声音从输出端走到了输入端，实测往返延迟约 {latency:F0}ms。");
        return 0;
    }

    private static void PlayTone(MMDevice renderDevice, int toneMs)
    {
        var format = renderDevice.AudioClient.MixFormat;
        using var output = new WasapiOut(renderDevice, AudioClientShareMode.Shared, true, 50);
        var generator = new SignalGenerator(format.SampleRate, format.Channels)
        {
            Frequency = ToneFrequency,
            Gain = 0.25,
            Type = SignalGeneratorType.Sin
        };
        output.Init(generator.ToWaveProvider());
        output.Play();
        Thread.Sleep(toneMs);
        output.Stop();
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal)) continue;
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : string.Empty;
            options[key[2..]] = value;
        }
        return options;
    }

    private static MMDevice Resolve(List<MMDevice> devices, string? selector, string what)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new ArgumentException($"缺少 --{(what == "输出" ? "render" : "capture")} 参数");
        }
        if (int.TryParse(selector, out var index))
        {
            if (index < 0 || index >= devices.Count) throw new ArgumentException($"{what}设备序号 {index} 超出范围");
            return devices[index];
        }
        return devices.FirstOrDefault(device =>
                   device.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase) ||
                   device.DeviceFriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"找不到匹配「{selector}」的{what}设备");
    }

    private static double Goertzel(float[] samples, int start, int length, int sampleRate)
    {
        var coefficient = 2 * Math.Cos(2 * Math.PI * ToneFrequency / sampleRate);
        double s1 = 0;
        double s2 = 0;
        for (var index = 0; index < length && start + index < samples.Length; index++)
        {
            var s0 = samples[start + index] + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return length <= 0 ? 0 : Math.Sqrt(Math.Max(0, power)) / (length / 2.0);
    }

    private static float[] ToMono(byte[] data, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var bits = format.BitsPerSample;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (format.Encoding == WaveFormatEncoding.Extensible &&
                       format is WaveFormatExtensible extensible &&
                       extensible.SubFormat == IeeeFloatSubFormat);
        var bytesPerSample = bits / 8;
        var frameSize = bytesPerSample * channels;
        if (frameSize <= 0) return [];

        var frames = data.Length / frameSize;
        var result = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += ReadSample(data, frame * frameSize + channel * bytesPerSample, bits, isFloat);
            }
            result[frame] = (float)(sum / channels);
        }
        return result;
    }

    private static double ReadSample(byte[] data, int offset, int bits, bool isFloat)
    {
        return bits switch
        {
            16 => BitConverter.ToInt16(data, offset) / 32768.0,
            24 => Read24(data, offset) / 8388608.0,
            32 => isFloat
                ? BitConverter.ToSingle(data, offset)
                : BitConverter.ToInt32(data, offset) / 2147483648.0,
            _ => 0
        };
    }

    private static int Read24(byte[] data, int offset)
    {
        var value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }
}
