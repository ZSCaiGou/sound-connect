using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using SoundConnect.Shared.Services;
using WinRT.Interop;

namespace SoundConnect.WinUI;

/// <summary>
/// 系统托盘图标：任务栏右下角的图标 + 右键菜单（显示主窗口 / 开始共享 / 停止共享 / 退出）。
/// 关窗口默认是收进托盘，不退出；要真退出走托盘菜单里的"退出"。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private const int MenuShow = 1;
    private const int MenuToggle = 2;
    private const int MenuExit = 3;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint MfGrayed = 0x0001;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int NifMessage = 0x0001;
    private const int NifIcon = 0x0002;
    private const int NifTip = 0x0004;
    private const int NimAdd = 0x0000;
    private const int NimModify = 0x0001;
    private const int NimDelete = 0x0002;
    private const int SwShow = 5;
    private const int SwHide = 0;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;

    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly IntPtr IdiApplication = new(32512);

    private readonly IAudioEngine engine;
    private readonly Microsoft.UI.Xaml.Window window;
    private readonly WndProcHandler handler; // 必须留引用，否则委托会被回收
    private readonly IntPtr mainHandle;
    private readonly IntPtr notifyHandle;
    private readonly uint taskbarCreatedMessage;
    private IntPtr icon;
    private bool exiting;
    private bool disposed;

    private TrayIcon(IAudioEngine engine, Microsoft.UI.Xaml.Window window, IntPtr mainHandle)
    {
        this.engine = engine;
        this.window = window;
        this.mainHandle = mainHandle;

        handler = WindowProc;
        notifyHandle = CreateNotifyWindow(handler);
        taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        icon = LoadAppIcon();

        AddIcon();
        HookCloseToTray();
        engine.Changed += UpdateTip;
    }

    public static TrayIcon? TryCreate(IAudioEngine engine, Microsoft.UI.Xaml.Window? window)
    {
        try
        {
            var handle = WindowNative.GetWindowHandle(window);
            if (handle == IntPtr.Zero)
            {
                Log("窗口句柄是 0");
                return null;
            }
            return new TrayIcon(engine, window!, handle);
        }
        catch (Exception error)
        {
            Log($"创建失败：{error}");
            return null;
        }
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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        engine.Changed -= UpdateTip;
        RemoveIcon();
        if (icon != IntPtr.Zero)
        {
            DestroyIcon(icon);
            icon = IntPtr.Zero;
        }
        if (notifyHandle != IntPtr.Zero)
        {
            DestroyWindow(notifyHandle);
        }
    }

    /// <summary>关窗口时收进托盘；想真退出得走托盘菜单。</summary>
    private void HookCloseToTray()
    {
        var appWindow = window.AppWindow;
        if (appWindow is null) return;
        appWindow.Closing += (_, args) =>
        {
            if (exiting) return;
            args.Cancel = true;
            ShowWindow(mainHandle, SwHide);
        };
    }

    private void AddIcon()
    {
        var data = CreateIconData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = icon;
        data.szTip = BuildTip();
        Shell_NotifyIcon(NimAdd, ref data);
    }

    private void RemoveIcon()
    {
        var data = CreateIconData();
        Shell_NotifyIcon(NimDelete, ref data);
    }

    private void UpdateTip()
    {
        if (disposed) return;
        var data = CreateIconData();
        data.uFlags = NifTip;
        data.szTip = BuildTip();
        Shell_NotifyIcon(NimModify, ref data);
    }

    private string BuildTip()
    {
        return engine.IsRunning ? "SoundConnect · 正在共享" : "SoundConnect · 已停止";
    }

    private void ShowMainWindow()
    {
        ShowWindow(mainHandle, SwShow);
        SetForegroundWindow(mainHandle);
    }

    private async void ToggleSharing()
    {
        try
        {
            if (engine.IsRunning)
            {
                await engine.StopAsync();
            }
            else
            {
                await engine.StartAsync();
            }
        }
        catch
        {
            // 失败时界面和日志里会有说明，托盘不额外弹窗
        }
        UpdateTip();
    }

    private void ExitApplication()
    {
        exiting = true;
        RemoveIcon();
        Microsoft.Maui.Controls.Application.Current?.Quit();
    }

    private void ShowMenu()
    {
        if (!GetCursorPos(out var point)) return;
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MfString, MenuShow, "显示主窗口");
            AppendMenu(menu, MfSeparator, 0, null);
            var toggleText = engine.IsRunning ? "停止共享" : "开始共享";
            AppendMenu(menu, MfString, MenuToggle, toggleText);
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, MenuExit, "退出");

            // TrackPopupMenu 要求先把自己设成前台窗口，否则菜单点了不消失
            SetForegroundWindow(notifyHandle);
            var command = TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCmd,
                point.X,
                point.Y,
                0,
                notifyHandle,
                IntPtr.Zero
            );
            PostMessage(notifyHandle, 0, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case MenuShow:
                    ShowMainWindow();
                    break;
                case MenuToggle:
                    ToggleSharing();
                    break;
                case MenuExit:
                    ExitApplication();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == taskbarCreatedMessage && taskbarCreatedMessage != 0)
        {
            // 资源管理器重启后图标会消失，这里补回来
            AddIcon();
            return IntPtr.Zero;
        }

        if (message == CallbackMessage)
        {
            switch ((int)lParam)
            {
                case WmLButtonUp:
                    ShowMainWindow();
                    return IntPtr.Zero;
                case WmRButtonUp:
                    ShowMenu();
                    return IntPtr.Zero;
            }
        }

        return DefWindowProc(hWnd, message, wParam, lParam);
    }

    private NOTIFYICONDATA CreateIconData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = notifyHandle,
            uID = 1,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var extracted = ExtractIcon(IntPtr.Zero, exe, 0);
                if (extracted != IntPtr.Zero) return extracted;
            }
        }
        catch
        {
            // 取不到就用系统默认图标
        }
        return LoadIcon(IntPtr.Zero, IdiApplication);
    }

    private static IntPtr CreateNotifyWindow(WndProcHandler handler)
    {
        var instance = GetModuleHandle(null);
        var className = "SoundConnectTrayWindow";
        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(handler),
            hInstance = instance,
            lpszClassName = className
        };
        RegisterClassEx(ref windowClass);
        return CreateWindowEx(
            0,
            className,
            "SoundConnect",
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            IntPtr.Zero,
            instance,
            IntPtr.Zero
        );
    }

    private delegate IntPtr WndProcHandler(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param
    );

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr owner,
        IntPtr rect
    );

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr instance, string exeFileName, int iconIndex);
}
