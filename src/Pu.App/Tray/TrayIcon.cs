using System.Runtime.InteropServices;
using Pu.App.Ui;

namespace Pu.App.Tray;

/// <summary>
/// 托盘图标（方案.md 第四节：无 WinForms，Shell_NotifyIcon P/Invoke）。
/// 必须在专用线程上构造并 Run()：窗口与消息循环同线程，
/// 否则线程池线程被回收时窗口会被 Windows 销毁。
/// 右键 → 菜单：打开状态页 / 停止 pu~。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint WM_APP = 0x8000;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint NIN_SELECT = 0x0400 + 0x0400;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100, TPM_NONOTIFY = 0x0080;
    private const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x0010;
    private const int MenuOpen = 1, MenuExit = 2;
    private const string ClassName = "PuTrayWindow";

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly WndProcDelegate WndProc = WindowProc; // 防 GC
    private static TrayIcon? s_current;

    private readonly IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _added;
    private bool _disposed;

    /// <summary>显示主窗口（托盘线程回调）。</summary>
    public event Action? ShowRequested;

    /// <summary>用户点了「停止 pu~」。</summary>
    public event Action? ExitRequested;

    public TrayIcon()
    {
        s_current = this;
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        RegisterClass(ref wc);
        _hwnd = CreateWindowEx(0, ClassName, "pu~", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建托盘消息窗口失败");
        _hIcon = AppIcons.LoadHIcon(16);
        AddToTray();
    }

    /// <summary>消息循环（阻塞，直到 Dispose / 菜单退出）。须与构造函数同线程。</summary>
    public void Run()
    {
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        // 全部清理都在创建线程上做
        RemoveFromTray();
        if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
        DestroyWindow(_hwnd);
        UnregisterClass(ClassName, GetModuleHandle(null));
        s_current = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 窗口/图标/菜单的清理统一在 Run() 退出时（创建线程上）执行
        PostMessage(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private void AddToTray()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP,
            hIcon = _hIcon,
            szTip = "噗~噗噗~~噗噗噗噗~~~~ 视频摆渡",
        };
        Shell_NotifyIcon(NIM_ADD, ref data);

        var v = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uVersion = NOTIFYICON_VERSION_4,
        };
        Shell_NotifyIcon(NIM_SETVERSION, ref v);
        _added = true;
    }

    private void RemoveFromTray()
    {
        if (!_added) return;
        var data = new NOTIFYICONDATA { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1 };
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => s_current is { } t ? t.OnMessage(hWnd, msg, wParam, lParam) : DefWindowProc(hWnd, msg, wParam, lParam);

    private IntPtr OnMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP)
        {
            var code = (uint)lParam.ToInt64();
            if (code == WM_CONTEXTMENU) return ShowMenu(hWnd);
            if (code == NIN_SELECT) ShowRequested?.Invoke(); // 左键单击
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr ShowMenu(IntPtr hWnd)
    {
        GetCursorPos(out var pt);
        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, MenuOpen, "显示窗口");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, MenuExit, "停止 噗~噗噗~~噗噗噗噗~~~~");
        var cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
            pt.X, pt.Y, 0, hWnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == MenuOpen) ShowRequested?.Invoke();
        else if (cmd == MenuExit)
        {
            ExitRequested?.Invoke();
            PostQuitMessage(0);
        }
        return IntPtr.Zero;
    }

    // ── P/Invoke ──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")]
    private static extern IntPtr TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
