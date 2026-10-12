using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundConnect.ProcessCapture;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var command = args.Length > 0 ? args[0] : "help";
            return command switch
            {
                "list" => ListSessions(),
                "capture" => Capture(args[1..]),
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
        Console.WriteLine("  process-capture list");
        Console.WriteLine("  process-capture capture --process QQMusic --render <名称片段或序号> [--seconds 20] [--gain-db -30]");
        Console.WriteLine("  process-capture capture --pid 47592 --render 0 [--seconds 20] [--gain-db -30]");
        return 1;
    }

    private static int ListSessions()
    {
        using var enumerator = new MMDeviceEnumerator();
        Console.WriteLine("有音频会话的输出设备：");
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var lines = new List<string>();
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var index = 0; index < sessions.Count; index++)
                {
                    var session = sessions[index];
                    var pid = (uint)session.GetProcessID;
                    if (pid == 0) continue;
                    string name;
                    try
                    {
                        name = Process.GetProcessById((int)pid).ProcessName;
                    }
                    catch
                    {
                        name = "(已退出)";
                    }
                    lines.Add($"      PID {pid,-7} {name,-22} {session.State}");
                }
            }
            catch (Exception error)
            {
                lines.Add($"      (枚举失败：{error.Message})");
            }

            if (lines.Count == 0) continue;
            Console.WriteLine($"  {device.FriendlyName}");
            lines.ForEach(Console.WriteLine);
        }
        return 0;
    }

    private static int Capture(string[] args)
    {
        var options = ParseOptions(args);
        var seconds = int.TryParse(options.GetValueOrDefault("seconds"), out var parsed) ? parsed : 20;
        // 虚拟线自己带 +30dB（实测 31.62x），默认先衰减掉，否则送进去的任何东西都会爆表
        var gainDb = double.TryParse(options.GetValueOrDefault("gain-db"), out var parsedGain)
            ? parsedGain
            : -30.0;
        var gainFactor = Math.Pow(10, gainDb / 20);
        var target = ResolveProcess(options);

        using var enumerator = new MMDeviceEnumerator();
        var renderDevice = ResolveRenderDevice(enumerator, options.GetValueOrDefault("render"));
        var mixFormat = renderDevice.AudioClient.MixFormat;
        Console.WriteLine($"目标进程：{target.ProcessName}（PID {target.Id}）");
        Console.WriteLine($"渲染到：{renderDevice.FriendlyName}（{Describe(mixFormat)}）");
        Console.WriteLine($"送出前增益：{gainDb:F1}dB（{gainFactor:F4}x）");

        using var capture = new ProcessLoopbackCapture((uint)target.Id, mixFormat);
        Console.WriteLine($"采集流：{Describe(capture.Format)}");

        var buffer = new BufferedWaveProvider(mixFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(300),
            ReadFully = true,
            DiscardOnBufferOverflow = true
        };

        using var output = new WasapiOut(renderDevice, AudioClientShareMode.Shared, true, 50);
        output.Init(buffer);
        output.Play();

        long packets = 0;
        long bytes = 0;
        double peak = 0;
        capture.DataAvailable += chunk =>
        {
            Interlocked.Increment(ref packets);
            Interlocked.Add(ref bytes, chunk.Length);
            var level = Peak(chunk, mixFormat);
            if (level > peak) peak = level;
            if (Math.Abs(gainFactor - 1) > 0.000001)
            {
                Scale(chunk, mixFormat, gainFactor);
            }
            buffer.AddSamples(chunk, 0, chunk.Length);
        };

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        capture.Start();
        Console.WriteLine($"开始采集并渲染，{seconds} 秒后自动停止（Ctrl+C 提前结束）…");

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!cancellation.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1000);
            var peakDb = peak <= 0.00001 ? "-∞" : $"{20 * Math.Log10(peak):F1}";
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] 采集 {packets} 包 / {bytes / 1024}KB，"
                + $"缓冲 {buffer.BufferedDuration.TotalMilliseconds:F0}ms，峰值 {peakDb}dB"
            );
            peak = 0;
        }

        capture.Stop();
        output.Stop();
        Console.WriteLine($"已停止，共采集 {packets} 个数据包。");
        return packets > 0 ? 0 : 3;
    }

    private static double Peak(byte[] chunk, WaveFormat format)
    {
        var peak = 0.0;
        if (ProcessLoopbackCapture.IsFloat(format))
        {
            for (var offset = 0; offset + 4 <= chunk.Length; offset += 4)
            {
                var value = Math.Abs(BitConverter.ToSingle(chunk, offset));
                if (value > peak) peak = value;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var offset = 0; offset + 2 <= chunk.Length; offset += 2)
            {
                var value = Math.Abs(BitConverter.ToInt16(chunk, offset) / 32768.0);
                if (value > peak) peak = value;
            }
        }
        return peak;
    }

    private static void Scale(byte[] chunk, WaveFormat format, double factor)
    {
        if (!ProcessLoopbackCapture.IsFloat(format)) return;
        for (var offset = 0; offset + 4 <= chunk.Length; offset += 4)
        {
            var scaled = (float)(BitConverter.ToSingle(chunk, offset) * factor);
            BitConverter.TryWriteBytes(chunk.AsSpan(offset, 4), scaled);
        }
    }

    private static Process ResolveProcess(Dictionary<string, string> options)
    {
        var pidText = options.GetValueOrDefault("pid");
        if (uint.TryParse(pidText, out var pid))
        {
            return Process.GetProcessById((int)pid);
        }

        var name = options.GetValueOrDefault("process");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("需要 --pid 或 --process 指定要采集的进程");
        }

        var matches = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
        if (matches.Length == 0)
        {
            throw new ArgumentException($"没有找到进程「{name}」");
        }
        return matches[0];
    }

    private static MMDevice ResolveRenderDevice(MMDeviceEnumerator enumerator, string? selector)
    {
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        if (string.IsNullOrWhiteSpace(selector))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        if (int.TryParse(selector, out var index))
        {
            if (index < 0 || index >= devices.Count)
            {
                throw new ArgumentException($"输出设备序号 {index} 超出范围");
            }
            return devices[index];
        }

        return devices.FirstOrDefault(device =>
                   device.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase) ||
                   device.DeviceFriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"找不到匹配「{selector}」的输出设备");
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[index][2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : string.Empty;
            options[key] = value;
        }
        return options;
    }

    private static string Describe(WaveFormat format)
    {
        var kind = ProcessLoopbackCapture.IsFloat(format) ? "float" : "pcm";
        return $"{format.SampleRate}Hz/{format.Channels}ch/{format.BitsPerSample}bit {kind}";
    }
}
