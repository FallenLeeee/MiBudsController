using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace MiBudsController.Services;

/// <summary>
/// 系统通知区域托盘图标（纯 Win32 实现，不依赖额外 NuGet 包）。
/// 左键单击切换迷你面板，右键弹出 Win32 上下文菜单。
/// </summary>
public sealed class TrayService : IDisposable
{
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmCommand = 0x0111;
    private const uint WmDestroy = 0x0002;
    private const uint NimAdd = 0x0000;
    private const uint NimModify = 0x0001;
    private const uint NimDelete = 0x0002;
    private const uint NifMessage = 0x0001;
    private const uint NifIcon = 0x0002;
    private const uint NifTip = 0x0004;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmBottomAlign = 0x0020;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const int CmdOpenMain = 2001;
    private const int CmdOpenMini = 2002;
    private const int CmdExit = 2003;
    private static readonly IntPtr HwndMessage = new(-3);
    private const uint WmAppTray = 0x8001;

    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;
    private bool _disposed;
    private long _lastToggleTick;
    private NativeMethods.WndProc? _wndProcKeepAlive;
    private NOTIFYICONDATA _data;

    /// <summary>创建托盘服务，注入用于回传界面事件的 UI 调度队列。</summary>
    public TrayService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>用户请求打开主界面。</summary>
    public event Action? OpenMainRequested;

    /// <summary>用户请求打开迷你控制面板。</summary>
    public event Action? OpenMiniRequested;

    /// <summary>用户请求退出应用。</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// 初始化托盘图标：注册消息窗口、加载图标文件（失败则用系统默认图标），
    /// 最后调用 Shell_NotifyIcon 添加图标；重复初始化直接返回成功。
    /// </summary>
    public bool Initialize(string iconPath, string tooltip = "小米耳机控制器")
    {
        lock (_gate)
        {
            if (_added)
            {
                return true;
            }

            _wndProcKeepAlive = WndProc;
            var wc = new NativeMethods.WNDCLASS
            {
                lpszClassName = "MiBudsControllerTrayMsgWindow",
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            };
            NativeMethods.RegisterClass(ref wc);
            _hwnd = NativeMethods.CreateWindowEx(
                0, "MiBudsControllerTrayMsgWindow", string.Empty, 0,
                0, 0, 0, 0, HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                return false;
            }

            if (File.Exists(iconPath))
            {
                _icon = NativeMethods.LoadImage(
                    IntPtr.Zero, iconPath, NativeMethods.ImageIcon, 0, 0,
                    NativeMethods.LrLoadFromFile | NativeMethods.LrDefaultSize | NativeMethods.LrShared);
            }

            if (_icon == IntPtr.Zero)
            {
                _icon = NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)32512);
            }

            _data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NifMessage | NifIcon | NifTip,
                uCallbackMessage = WmAppTray,
                hIcon = _icon,
                szTip = tooltip,
            };
            _added = NativeMethods.Shell_NotifyIcon(NimAdd, ref _data);
            return _added;
        }
    }

    /// <summary>更新托盘图标的悬浮提示文字。</summary>
    public void UpdateTooltip(string text)
    {
        lock (_gate)
        {
            if (!_added)
            {
                return;
            }

            _data.szTip = text;
            _data.uFlags = NifMessage | NifIcon | NifTip;
            NativeMethods.Shell_NotifyIcon(NimModify, ref _data);
        }
    }

    /// <summary>
    /// 托盘消息窗口回调：处理左键/右键点击和菜单 WM_COMMAND 消息，
    /// 其余消息交给 DefWindowProc。
    /// </summary>
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmAppTray)
        {
            uint mouse = (uint)lParam.ToInt64() & 0xFFFF;
            // 仅处理左键抬起做开关切换；忽略双击消息，避免一次连点触发多次。
            if (mouse == WmLButtonUp)
            {
                RaiseToggle(OpenMiniRequested);
            }
            else if (mouse == WmRButtonUp)
            {
                ShowContextMenu();
            }

            return IntPtr.Zero;
        }

        if (msg == WmCommand)
        {
            int id = wParam.ToInt32() & 0xFFFF;
            switch (id)
            {
                case CmdOpenMain:
                    Raise(OpenMainRequested);
                    break;
                case CmdOpenMini:
                    Raise(OpenMiniRequested);
                    break;
                case CmdExit:
                    Raise(ExitRequested);
                    break;
            }

            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>在鼠标位置弹出“打开主界面 / 迷你控制面板 / 退出”菜单。</summary>
    private void ShowContextMenu()
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT pt);
        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.AppendMenu(menu, MfString, (IntPtr)CmdOpenMain, "打开主界面");
        NativeMethods.AppendMenu(menu, MfString, (IntPtr)CmdOpenMini, "迷你控制面板（开/关）");
        NativeMethods.AppendMenu(menu, MfSeparator, IntPtr.Zero, string.Empty);
        NativeMethods.AppendMenu(menu, MfString, (IntPtr)CmdExit, "退出");
        NativeMethods.SetForegroundWindow(_hwnd);
        NativeMethods.TrackPopupMenu(
            menu, TpmRightButton | TpmBottomAlign, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);
    }

    /// <summary>把托盘事件封送到初始化时捕获的 UI 调度队列，窗口隐藏时也能执行。</summary>
    private void Raise(Action? handler)
    {
        if (handler is null)
        {
            return;
        }

        // 始终封送到托盘初始化时捕获的 UI 调度队列，主窗口隐藏时同样有效。
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => handler());
        }
        else
        {
            handler();
        }
    }

    /// <summary>托盘切换去抖：短时间内的重复点击只触发一次，防止双击事件叠加重入。</summary>
    private void RaiseToggle(Action? handler)
    {
        long now = Environment.TickCount64;
        if (now - _lastToggleTick < 350)
        {
            return;
        }

        _lastToggleTick = now;
        Raise(handler);
    }

    /// <summary>释放托盘资源：删除图标、销毁消息窗口和图标句柄。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_added)
            {
                NativeMethods.Shell_NotifyIcon(NimDelete, ref _data);
                _added = false;
            }

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            if (_icon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    private static class NativeMethods
    {
        public const uint ImageIcon = 1;
        public const uint LrLoadFromFile = 0x0010;
        public const uint LrDefaultSize = 0x0040;
        public const uint LrShared = 0x8000;

        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASS
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
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadImage(
            IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        public static extern uint TrackPopupMenu(
            IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
