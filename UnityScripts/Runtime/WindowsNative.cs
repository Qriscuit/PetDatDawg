using System;
using System.Runtime.InteropServices;

namespace PetDaDog.Unity
{
    /// <summary>
    /// Windows-only P/Invoke surface shared by the overlay, tray, and status
    /// window. Calling code always guards it with RuntimePlatform.WindowsPlayer.
    /// </summary>
    internal static class WindowsNative
    {
        public const int GwlStyle = -16;
        public const int GwlExStyle = -20;
        public const int GwlpWndProc = -4;

        public const uint WsPopup = 0x80000000;
        public const uint WsOverlappedWindow = 0x00CF0000;
        public const uint WsChild = 0x40000000;
        public const uint WsVisible = 0x10000000;
        public const uint WsTabStop = 0x00010000;
        public const uint WsVScroll = 0x00200000;

        public const uint WsExToolWindow = 0x00000080;
        public const uint WsExAppWindow = 0x00040000;
        public const uint WsExLayered = 0x00080000;
        public const uint WsExNoActivate = 0x08000000;
        public const uint WsExTransparent = 0x00000020;

        public const uint LwaColorKey = 0x00000001;
        public const uint SwpNoSize = 0x0001;
        public const uint SwpNoMove = 0x0002;
        public const uint SwpNoZOrder = 0x0004;
        public const uint SwpNoActivate = 0x0010;
        public const uint SwpFrameChanged = 0x0020;
        public const int HtTransparent = -1;
        public const int HtClient = 1;
        public static readonly IntPtr HwndTopmost = new IntPtr(-1);
        public static readonly IntPtr HwndNoTopmost = new IntPtr(-2);

        public const uint WmNull = 0x0000;
        public const uint WmDestroy = 0x0002;
        public const uint WmClose = 0x0010;
        public const uint WmQuit = 0x0012;
        public const uint WmNchitTest = 0x0084;
        public const uint WmCommand = 0x0111;
        public const uint WmHScroll = 0x0114;
        public const uint WmHotkey = 0x0312;
        public const uint WmLButtonDown = 0x0201;
        public const uint WmRButtonDown = 0x0204;
        public const uint WmLButtonUp = 0x0202;
        public const uint WmRButtonUp = 0x0205;
        public const uint WmContextMenu = 0x007B;
        public const uint WmApp = 0x8000;

        public const uint SwHide = 0;
        public const uint SwShow = 5;
        public const uint SwRestore = 9;

        public const uint BmGetCheck = 0x00F0;
        public const uint BstChecked = 0x0001;
        public const uint BnClicked = 0;
        public const uint BsPushButton = 0x00000000;
        public const uint BsAutoCheckBox = 0x00000003;

        public const uint TbmGetPos = 0x0400;
        public const uint TbmGetRangeMin = 0x0401;
        public const uint TbmGetRangeMax = 0x0402;
        public const uint TbmSetPos = 0x0405;
        public const uint TbmSetRange = 0x0406;
        public const uint TbsAutoTicks = 0x0001;

        public const uint IccBarClasses = 0x00000004;
        public const uint IccWin95Classes = 0x000000FF;

        public const uint NimAdd = 0x00000000;
        public const uint NimDelete = 0x00000002;
        public const uint NimSetVersion = 0x00000004;
        public const uint NifMessage = 0x00000001;
        public const uint NifIcon = 0x00000002;
        public const uint NifTip = 0x00000004;
        public const uint NotifyIconVersion4 = 4;
        public const int IdiApplication = 32512;
        public const uint MfString = 0x00000000;
        public const uint TpmRightButton = 0x0002;
        public const uint TpmReturnCommand = 0x0100;
        public const uint ModAlt = 0x0001;
        public const uint VkOem3 = 0xC0;

        public const uint SpiGetWorkArea = 0x0030;
        public const string TrackbarClass = "msctls_trackbar32";

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WndClassEx
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int left;
            public int top;
            public int right;
            public int bottom;

            public int Width => right - left;
            public int Height => bottom - top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Message
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public Point point;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NotifyIconData
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
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct InitCommonControlsEx
        {
            public uint dwSize;
            public uint dwICC;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WndClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint exStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetWindowText(IntPtr hwnd, string text);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetMessage(out Message message, IntPtr hwnd, uint minFilter, uint maxFilter);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr DispatchMessage(ref Message message);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CallWindowProc(IntPtr previousWindowProc, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr itemId, string text);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetDlgCtrlID(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SystemParametersInfo(uint action, uint parameter, out Rect rect, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("comctl32.dll", SetLastError = true)]
        public static extern bool InitCommonControlsEx(ref InitCommonControlsEx controls);

        public static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hwnd, index)
                : new IntPtr(GetWindowLong32(hwnd, index));
        }

        public static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hwnd, index, value)
                : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
        }

        public static uint Rgb(byte red, byte green, byte blue)
        {
            return (uint)(red | (green << 8) | (blue << 16));
        }

        public static int LowWord(IntPtr value) => unchecked((short)((long)value & 0xffff));
        public static int HighWord(IntPtr value) => unchecked((short)(((long)value >> 16) & 0xffff));
        public static int SignedLowWord(IntPtr value) => unchecked((short)((long)value & 0xffff));
        public static int SignedHighWord(IntPtr value) => unchecked((short)(((long)value >> 16) & 0xffff));
    }
}
