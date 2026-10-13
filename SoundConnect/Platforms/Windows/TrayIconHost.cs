using SoundConnect.Shared.Services;

namespace SoundConnect.WinUI;

/// <summary>托盘的启动入口：找到 MAUI 主窗口的原生句柄，把图标挂上去。</summary>
internal static class TrayIconHost
{
    private static TrayIcon? instance;

    public static void EnsureStarted(
        IServiceProvider? services,
        Microsoft.Maui.Controls.Window? mauiWindow
    )
    {
        try
        {
            if (instance is not null) return;
            // MAUI 里取服务容器最稳的是 IPlatformApplication.Current，页面 Handler 上的 MauiContext 这时可能还没填
            services ??= Microsoft.Maui.IPlatformApplication.Current?.Services;
            if (services is null)
            {
                Log("services 为空");
                return;
            }
            if (services.GetService(typeof(IAudioEngine)) is not IAudioEngine engine)
            {
                Log("拿不到 IAudioEngine");
                return;
            }
            if (mauiWindow is null)
            {
                Log("拿不到 MAUI 窗口");
                return;
            }

            // 窗口的原生 Handler 可能要过一会儿才建好，这时先挂上事件等它
            if (mauiWindow.Handler is null)
            {
                Log("窗口 Handler 还没好，等它建好再挂托盘");
                mauiWindow.HandlerChanged += (_, _) => Attach(engine, mauiWindow);
            }
            else
            {
                Attach(engine, mauiWindow);
            }
        }
        catch (Exception error)
        {
            Log($"异常：{error}");
        }
    }

    private static void Attach(IAudioEngine engine, Microsoft.Maui.Controls.Window mauiWindow)
    {
        if (instance is not null) return;
        if (mauiWindow.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
        {
            Log("Handler 建好了，但拿不到原生窗口");
            return;
        }
        instance = TrayIcon.TryCreate(engine, nativeWindow);
        Log(instance is null ? "托盘创建失败" : "托盘已创建");
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "soundconnect-tray.log"),
                $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"
            );
        }
        catch
        {
            // 日志写不了就算了
        }
    }
}
