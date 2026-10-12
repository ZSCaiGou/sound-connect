using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using SoundConnect.Shared.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SoundConnect.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        /// <summary>
        /// 带 --selftest 启动时不建界面，直接把音频通路跑一遍并把过程写进 selftest.log。
        /// 用来在没有界面的情况下验证"采集 → 混音 → 送虚拟线"这条链路。
        /// </summary>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (Environment.GetCommandLineArgs().Any(item => item == "--selftest"))
            {
                _ = RunSelfTestAsync();
                return;
            }
            base.OnLaunched(args);
        }

        private static async Task RunSelfTestAsync()
        {
            var report = new List<string>();
            IAudioEngine? engine = null;
            try
            {
                var mauiApp = MauiProgram.CreateMauiApp();
                engine = mauiApp.Services.GetRequiredService<IAudioEngine>();
                await engine.RefreshDevicesAsync();

                var output =
                    engine.RenderDevices.FirstOrDefault(device =>
                        device.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
                    )
                    ?? engine.RenderDevices.FirstOrDefault(device =>
                        device.Name.Contains("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase)
                    )
                    ?? engine.RenderDevices.FirstOrDefault(device => device.IsVirtual);
                var microphone = engine.CaptureDevices.FirstOrDefault(device => !device.IsVirtual);
                var processes = await engine.ListAudioProcessesAsync();
                string[] players = ["QQMusic", "cloudmusic", "msedge", "chrome", "firefox", "Spotify"];
                var music = processes
                    .Where(item => item.State == "正在发声")
                    .OrderByDescending(item =>
                        players.Any(name => item.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                    )
                    .FirstOrDefault();

                report.Add($"输出设备：{output?.Name ?? "(无)"}");
                report.Add($"麦克风：{microphone?.Name ?? "(无)"}");
                report.Add($"音乐来源：{music?.Name ?? "(没有正在发声的进程)"} / PID {music?.ProcessId ?? 0}");

                if (output is null || music is null)
                {
                    report.Add("缺少必要设备或没有正在发声的进程，自检中止");
                }
                else
                {
                    await engine.StartAsync(
                        new AudioStartOptions(music.ProcessId, microphone?.Id ?? string.Empty, output.Id)
                    );
                    for (var second = 1; second <= 8; second++)
                    {
                        await Task.Delay(1000);
                        var snapshot = engine.Snapshot;
                        report.Add(
                            $"t={second}s 音乐 {snapshot.MusicLevel:F4} / 麦克风 {snapshot.MicrophoneLevel:F4}"
                                + $" / 总输出 {snapshot.OutputLevel:F4} / 峰值 {snapshot.OutputPeak:F3}"
                                + $" / 欠载 音乐{snapshot.MusicUnderruns} 麦克风{snapshot.MicrophoneUnderruns}"
                        );
                    }
                    await engine.StopAsync();
                }
            }
            catch (Exception error)
            {
                report.Add($"自检异常：{error}");
            }

            report.Add("--- 引擎日志 ---");
            report.AddRange(engine?.Snapshot.Log ?? []);
            var logPath = Path.Combine(AppContext.BaseDirectory, "selftest.log");
            await File.WriteAllLinesAsync(logPath, report);
            Console.WriteLine(string.Join(Environment.NewLine, report));
            Environment.Exit(0);
        }
    }

}
