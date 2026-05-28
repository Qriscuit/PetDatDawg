using System;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;

public partial class NativeWindowBridge : Node
{
	private static readonly IntPtr HwndTopmost = new(-1);
	private static readonly IntPtr HwndNoTopmost = new(-2);
	private static int MainWindowId => (int)DisplayServer.MainWindowId;

	[Signal]
	public delegate void StatusRequestedEventHandler();

	[Signal]
	public delegate void DogClickThroughToggleRequestedEventHandler();

	private const int GwlExStyle = -20;
	private const long WsExToolWindow = 0x00000080L;
	private const long WsExAppWindow = 0x00040000L;
	private const long WsExLayered = 0x00080000L;
	private const long WsExNoActivate = 0x08000000L;
	private const long WsExTransparent = 0x00000020L;
	private const float InvisibleDogAlphaThreshold = 0.01f;

	private const uint SwpNoSize = 0x0001;
	private const uint SwpNoMove = 0x0002;
	private const uint SwpNoZOrder = 0x0004;
	private const uint SwpNoActivate = 0x0010;
	private const uint SwpFrameChanged = 0x0020;

	private const uint TrayIconId = 1;
	private const uint TrayCallbackMessage = 0x8001;
	private const uint NimAdd = 0x00000000;
	private const uint NimDelete = 0x00000002;
	private const uint NimSetVersion = 0x00000004;
	private const uint NifMessage = 0x00000001;
	private const uint NifIcon = 0x00000002;
	private const uint NifTip = 0x00000004;
	private const uint NotifyIconVersion4 = 4;

	private const int IdiApplication = 32512;
	private const uint WmNull = 0x0000;
	private const uint WmClose = 0x0010;
	private const uint WmDestroy = 0x0002;
	private const uint WmQuit = 0x0012;
	private const uint WmHotkey = 0x0312;
	private const uint WmLButtonUp = 0x0202;
	private const uint WmRButtonUp = 0x0205;
	private const uint WmContextMenu = 0x007B;
	private const uint MfString = 0x00000000;
	private const uint TpmRightButton = 0x0002;
	private const uint TpmReturnCommand = 0x0100;
	private const int HotkeyToggleClickThrough = 1;
	private const uint ModAlt = 0x0001;
	private const uint VkOem3 = 0xC0;

	private const uint MenuStatus = 1001;
	private const uint MenuExit = 1002;

	private readonly string _trayWindowClassName = $"PetDaDogTrayWindow_{System.Environment.ProcessId}";

	private Thread? _trayThread;
	private WindowProc? _trayWindowProc;
	private IntPtr _trayWindow;
	private IntPtr _trayIcon;
	private uint _trayThreadId;
	private bool _trayInstalled;
	private volatile bool _statusRequested;
	private volatile bool _exitRequested;
	private volatile bool _dogClickThroughToggleRequested;
	private double _refreshTimer;

	public override void _Ready()
	{
		SetProcess(true);
		CallDeferred(MethodName.ApplyDesktopPetWindowStylesAndRaise);
		CallDeferred(MethodName.InstallTrayMenu);
	}

	public override void _Process(double delta)
	{
		if (_statusRequested)
		{
			_statusRequested = false;
			EmitSignal(SignalName.StatusRequested);
		}

		if (_dogClickThroughToggleRequested)
		{
			_dogClickThroughToggleRequested = false;
			EmitSignal(SignalName.DogClickThroughToggleRequested);
		}

		if (_exitRequested)
		{
			_exitRequested = false;
			GetTree().Quit();
			return;
		}

		_refreshTimer += delta;
		if (_refreshTimer < 1.0)
		{
			return;
		}

		_refreshTimer = 0.0;
		ApplyDesktopPetWindowStyles();
	}

	public override void _ExitTree()
	{
		RemoveTrayMenu();
	}

	public void ApplyDesktopPetWindowStyles()
	{
		ApplyDesktopPetWindowStyles(forceZOrder: false);
	}

	public void ApplyDesktopPetWindowStylesAndRaise()
	{
		ApplyDesktopPetWindowStyles(forceZOrder: true);
	}

	private void ApplyDesktopPetWindowStyles(bool forceZOrder)
	{
		if (OS.GetName() != "Windows" || Engine.IsEmbeddedInEditor())
		{
			return;
		}

		var hwndValue = DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, MainWindowId);
		if (hwndValue == 0)
		{
			return;
		}

		var settings = GetNodeOrNull<PetSettings>("/root/PetSettings");
		var dogPassThrough = (settings?.DogClickThrough ?? PetSettings.DefaultDogClickThrough)
			|| (settings?.DogTransparency ?? PetSettings.DefaultDogTransparency) <= InvisibleDogAlphaThreshold;
		ApplyWindowStyles(
			new IntPtr(hwndValue),
			settings?.AlwaysOnTop ?? PetSettings.DefaultAlwaysOnTop,
			dogPassThrough,
			forceZOrder
		);
	}

	public void InstallTrayMenu()
	{
		if (OS.GetName() != "Windows"
			|| Engine.IsEmbeddedInEditor()
			|| _trayThread != null
			|| System.Environment.GetEnvironmentVariable("PDD_DISABLE_TRAY") == "1")
		{
			return;
		}

		_trayWindowProc = TrayWindowProc;
		_trayThread = new Thread(TrayThreadMain)
		{
			IsBackground = true,
			Name = "Pet Da Dog Tray",
		};
		_trayThread.Start();
	}

	private void RemoveTrayMenu()
	{
		var trayWindow = _trayWindow;
		if (trayWindow != IntPtr.Zero)
		{
			UnregisterHotKey(trayWindow, HotkeyToggleClickThrough);
			RemoveTrayIcon(trayWindow);
			PostMessage(trayWindow, WmClose, IntPtr.Zero, IntPtr.Zero);
		}

		if (_trayThreadId != 0)
		{
			PostThreadMessage(_trayThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
		}

		if (_trayThread != null && _trayThread.IsAlive && _trayThread.ManagedThreadId != System.Environment.CurrentManagedThreadId)
		{
			_trayThread.Join(1000);
		}

		_trayThread = null;
		_trayWindowProc = null;
		_trayWindow = IntPtr.Zero;
		_trayIcon = IntPtr.Zero;
		_trayThreadId = 0;
		_trayInstalled = false;
	}

	private void TrayThreadMain()
	{
		_trayThreadId = GetCurrentThreadId();
		var instance = GetModuleHandle(null);
		var windowClass = new WindowClassEx
		{
			CbSize = (uint)Marshal.SizeOf<WindowClassEx>(),
			LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_trayWindowProc!),
			HInstance = instance,
			LpszClassName = _trayWindowClassName,
		};

		if (RegisterClassEx(ref windowClass) == 0)
		{
			return;
		}

		var trayWindow = CreateWindowEx(
			0,
			_trayWindowClassName,
			"Pet Da Dog Tray",
			0,
			0,
			0,
			0,
			0,
			IntPtr.Zero,
			IntPtr.Zero,
			instance,
			IntPtr.Zero
		);

		if (trayWindow == IntPtr.Zero)
		{
			return;
		}

		_trayWindow = trayWindow;
		RegisterHotKey(trayWindow, HotkeyToggleClickThrough, ModAlt, VkOem3);
		InstallTrayIcon(trayWindow);

		while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
		{
			TranslateMessage(ref message);
			DispatchMessage(ref message);
		}

		_trayWindow = IntPtr.Zero;
	}

	private IntPtr TrayWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
	{
		if (message == WmHotkey && wParam.ToInt32() == HotkeyToggleClickThrough)
		{
			_dogClickThroughToggleRequested = true;
			return IntPtr.Zero;
		}

		if (message == TrayCallbackMessage)
		{
			var mouseMessage = unchecked((uint)lParam.ToInt64() & 0xFFFF);
			if (mouseMessage is WmLButtonUp or WmRButtonUp or WmContextMenu)
			{
				ShowTrayMenu(hwnd);
				return IntPtr.Zero;
			}
		}

		if (message == WmClose)
		{
			UnregisterHotKey(hwnd, HotkeyToggleClickThrough);
			RemoveTrayIcon(hwnd);
			DestroyWindow(hwnd);
			return IntPtr.Zero;
		}

		if (message == WmDestroy)
		{
			PostQuitMessage(0);
			return IntPtr.Zero;
		}

		return DefWindowProc(hwnd, message, wParam, lParam);
	}

	private void InstallTrayIcon(IntPtr hwnd)
	{
		if (_trayInstalled)
		{
			return;
		}

		_trayIcon = LoadIcon(IntPtr.Zero, new IntPtr(IdiApplication));
		var data = CreateNotifyIconData(hwnd);
		if (!Shell_NotifyIcon(NimAdd, ref data))
		{
			return;
		}

		data.UTimeoutOrVersion = NotifyIconVersion4;
		Shell_NotifyIcon(NimSetVersion, ref data);
		_trayInstalled = true;
	}

	private void RemoveTrayIcon(IntPtr hwnd)
	{
		if (!_trayInstalled)
		{
			return;
		}

		var data = CreateNotifyIconData(hwnd);
		Shell_NotifyIcon(NimDelete, ref data);
		_trayInstalled = false;
	}

	private NotifyIconData CreateNotifyIconData(IntPtr hwnd)
	{
		return new NotifyIconData
		{
			CbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
			HWnd = hwnd,
			UID = TrayIconId,
			UFlags = NifMessage | NifIcon | NifTip,
			UCallbackMessage = TrayCallbackMessage,
			HIcon = _trayIcon,
			SzTip = "Pet Da Dog",
			SzInfo = string.Empty,
			SzInfoTitle = string.Empty,
			GuidItem = Guid.Empty,
			HBalloonIcon = IntPtr.Zero,
		};
	}

	private void ShowTrayMenu(IntPtr hwnd)
	{
		var menu = CreatePopupMenu();
		if (menu == IntPtr.Zero)
		{
			return;
		}

		try
		{
			AppendMenu(menu, MfString, new UIntPtr(MenuStatus), "Status");
			AppendMenu(menu, MfString, new UIntPtr(MenuExit), "Exit Game");

			GetCursorPos(out var point);
			SetForegroundWindow(hwnd);
			var command = TrackPopupMenuEx(
				menu,
				TpmRightButton | TpmReturnCommand,
				point.X,
				point.Y,
				hwnd,
				IntPtr.Zero
			);
			PostMessage(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

			if (command == MenuStatus)
			{
				_statusRequested = true;
			}
			else if (command == MenuExit)
			{
				_exitRequested = true;
			}
		}
		finally
		{
			DestroyMenu(menu);
		}
	}

	private static void ApplyWindowStyles(IntPtr hwnd, bool alwaysOnTop, bool dogPassThrough, bool forceZOrder)
	{
		var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
		style |= WsExToolWindow | WsExNoActivate | WsExLayered;
		style &= ~WsExAppWindow;
		if (dogPassThrough)
		{
			style |= WsExTransparent;
		}
		else
		{
			style &= ~WsExTransparent;
		}

		SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
		ApplyWindowZOrder(hwnd, alwaysOnTop, forceZOrder);
	}

	private static void ApplyWindowZOrder(IntPtr hwnd, bool alwaysOnTop, bool forceZOrder)
	{
		var flags = SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged;

		if (alwaysOnTop)
		{
			SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, flags);
			return;
		}

		if (!forceZOrder)
		{
			SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, flags | SwpNoZOrder);
			return;
		}

		SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, flags);

		var foreground = GetForegroundWindow();
		if (foreground != IntPtr.Zero && foreground != hwnd)
		{
			SetWindowPos(hwnd, foreground, 0, 0, 0, 0, flags);
		}
	}

	private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
	{
		return IntPtr.Size == 8
			? GetWindowLongPtr64(hwnd, index)
			: new IntPtr(GetWindowLong32(hwnd, index));
	}

	private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
	{
		return IntPtr.Size == 8
			? SetWindowLongPtr64(hwnd, index, value)
			: new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
	}

	private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WindowClassEx
	{
		public uint CbSize;
		public uint Style;
		public IntPtr LpfnWndProc;
		public int CbClsExtra;
		public int CbWndExtra;
		public IntPtr HInstance;
		public IntPtr HIcon;
		public IntPtr HCursor;
		public IntPtr HBackground;
		public string? LpszMenuName;
		public string LpszClassName;
		public IntPtr HIconSm;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NotifyIconData
	{
		public uint CbSize;
		public IntPtr HWnd;
		public uint UID;
		public uint UFlags;
		public uint UCallbackMessage;
		public IntPtr HIcon;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string SzTip;
		public uint DwState;
		public uint DwStateMask;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string SzInfo;
		public uint UTimeoutOrVersion;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string SzInfoTitle;
		public uint DwInfoFlags;
		public Guid GuidItem;
		public IntPtr HBalloonIcon;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Message
	{
		public IntPtr Hwnd;
		public uint MessageId;
		public IntPtr WParam;
		public IntPtr LParam;
		public uint Time;
		public Point Point;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Point
	{
		public int X;
		public int Y;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string? moduleName);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateWindowEx(
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
		IntPtr param
	);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyWindow(IntPtr hwnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetMessage(out Message message, IntPtr hwnd, uint messageFilterMin, uint messageFilterMax);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool TranslateMessage(ref Message message);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr DispatchMessage(ref Message message);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern void PostQuitMessage(int exitCode);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr CreatePopupMenu();

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr newItemId, string newItem);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyMenu(IntPtr menu);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetCursorPos(out Point point);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetForegroundWindow(IntPtr hwnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
	private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
	private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

	[DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
	private static extern int GetWindowLong32(IntPtr hwnd, int index);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
	private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(
		IntPtr hwnd,
		IntPtr insertAfter,
		int x,
		int y,
		int cx,
		int cy,
		uint flags
	);
}
