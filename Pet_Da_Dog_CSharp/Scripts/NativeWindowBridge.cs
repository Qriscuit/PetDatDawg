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
	private const long WsExTopmost = 0x00000008L;
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
	private const int HotkeyOpenMenu = 2;
	private const int HotkeyOpenMenuReplacement = 3;
	private const uint WmRefreshMenuHotkey = 0x8002;
	private const uint ModNoRepeat = 0x4000;
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
	private bool _pointerPassthrough;
	private bool _accessoryEditing;
	private PetSettings? _settings;
	private sealed record MenuHotkeyRequest(int VirtualKey, int Modifiers);
	private MenuHotkeyRequest _requestedMenuHotkey = new(MenuShortcut.DefaultVirtualKey, MenuShortcut.DefaultModifiers);
	private MenuHotkeyRequest? _lastWorkingMenuHotkey;
	private int _activeMenuHotkeyId;
	private int _menuHotkeyCaptureActive, _menuHotkeyRequested, _menuHotkeyRegistered, _trayStopRequested;
	private string _menuHotkeyStatus = "Menu shortcut is starting.";
	public string MenuHotkeyStatus => Volatile.Read(ref _menuHotkeyStatus);
	public bool MenuHotkeyRegistered => Volatile.Read(ref _menuHotkeyRegistered) != 0;

	public override void _Ready()
	{
		_settings = GetNodeOrNull<PetSettings>("/root/PetSettings");
		if (_settings != null) _settings.SettingsChanged += RefreshMenuHotkey;
		RefreshMenuHotkey();
		SetProcess(true);
		CallDeferred(MethodName.ApplyDesktopPetWindowStylesAndRaise);
		CallDeferred(MethodName.InstallTrayMenu);
	}

	public override void _Process(double delta)
	{
		var openMenu = _statusRequested;
		_statusRequested = false;
		if (Interlocked.Exchange(ref _menuHotkeyRequested, 0) != 0 && Volatile.Read(ref _menuHotkeyCaptureActive) == 0) openMenu = true;
		if (openMenu)
		{
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
		if (_settings != null) _settings.SettingsChanged -= RefreshMenuHotkey;
		RemoveTrayMenu();
	}

	public void SetMenuHotkeyCaptureActive(bool active)
	{
		Volatile.Write(ref _menuHotkeyCaptureActive, active ? 1 : 0);
		Interlocked.Exchange(ref _menuHotkeyRequested, 0);
		RefreshMenuHotkey();
	}

	private void RefreshMenuHotkey()
	{
		Volatile.Write(ref _requestedMenuHotkey, new MenuHotkeyRequest(
			_settings?.MenuHotkeyVirtualKey ?? MenuShortcut.DefaultVirtualKey,
			_settings?.MenuHotkeyModifiers ?? MenuShortcut.DefaultModifiers));
		var window = Interlocked.CompareExchange(ref _trayWindow, IntPtr.Zero, IntPtr.Zero);
		if (window != IntPtr.Zero && !PostMessage(window, WmRefreshMenuHotkey, IntPtr.Zero, IntPtr.Zero))
			Volatile.Write(ref _menuHotkeyStatus, "The menu shortcut could not be updated. Use the tray icon or restart Pet Da Dog.");
	}

	public void ApplyDesktopPetWindowStyles()
	{
		ApplyDesktopPetWindowStyles(forceZOrder: false);
	}

	public void ApplyDesktopPetWindowStylesAndRaise()
	{
		ApplyDesktopPetWindowStyles(forceZOrder: true);
	}

	public void SetPointerPassthrough(bool value)
	{
		if (_pointerPassthrough == value)
		{
			return;
		}
		_pointerPassthrough = value;
		ApplyDesktopPetWindowStyles();
	}

	public void SetAccessoryEditing(bool value)
	{
		if (_accessoryEditing == value) return;
		_accessoryEditing = value;
		ApplyDesktopPetWindowStyles(forceZOrder: true);
	}

	public static bool IsLeftMouseButtonDown() => OS.GetName() == "Windows"
		? (GetAsyncKeyState(0x01) & 0x8000) != 0 : Input.IsMouseButtonPressed(MouseButton.Left);

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
		var dogPassThrough = _pointerPassthrough || (!_accessoryEditing &&
			((settings?.DogClickThrough ?? PetSettings.DefaultDogClickThrough)
			|| (settings?.DogTransparency ?? PetSettings.DefaultDogTransparency) <= InvisibleDogAlphaThreshold));
		ApplyWindowStyles(
			new IntPtr(hwndValue),
			_accessoryEditing || (settings?.AlwaysOnTop ?? PetSettings.DefaultAlwaysOnTop),
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
			if (_trayThread == null)
				Volatile.Write(ref _menuHotkeyStatus, System.Environment.GetEnvironmentVariable("PDD_DISABLE_TRAY") == "1"
					? "Global menu shortcut is disabled for this run." : "Global menu shortcuts are available in the Windows desktop game.");
			return;
		}

		Volatile.Write(ref _trayStopRequested, 0);
		_trayWindowProc = TrayWindowProc;
		_trayThread = new Thread(TrayThreadMain)
		{
			IsBackground = true,
			Name = "Pet Da Dog Tray",
		};
		try { _trayThread.Start(); }
		catch (Exception)
		{
			_trayThread = null;
			_trayWindowProc = null;
			Volatile.Write(ref _menuHotkeyStatus, "The menu shortcut could not start. Right-click the dog to open the menu.");
		}
	}

	private void RemoveTrayMenu()
	{
		Volatile.Write(ref _trayStopRequested, 1);
		var trayWindow = _trayWindow;
		if (trayWindow != IntPtr.Zero)
		{
			PostMessage(trayWindow, WmClose, IntPtr.Zero, IntPtr.Zero);
		}
		else if (_trayThreadId != 0)
		{
			PostThreadMessage(_trayThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
		}

		if (_trayThread != null && _trayThread.IsAlive && _trayThread.ManagedThreadId != System.Environment.CurrentManagedThreadId)
		{
			if (!_trayThread.Join(1000))
			{
				// A still-running native window must retain its managed callback delegate.
				Volatile.Write(ref _menuHotkeyStatus, "Menu shortcut is stopping.");
				return;
			}
		}

		_trayThread = null;
		_trayWindowProc = null;
		_trayWindow = IntPtr.Zero;
		_trayIcon = IntPtr.Zero;
		_trayThreadId = 0;
		_trayInstalled = false;
		Volatile.Write(ref _menuHotkeyRegistered, 0);
	}

	private void TrayThreadMain()
	{
		var instance = GetModuleHandle(null);
		var trayWindow = IntPtr.Zero;
		var classRegistered = false;
		try
		{
			// Publish the thread ID only after creating its message queue.
			PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
			Volatile.Write(ref _trayThreadId, GetCurrentThreadId());
			if (Volatile.Read(ref _trayStopRequested) != 0) return;
			var windowClass = new WindowClassEx
			{
				CbSize = (uint)Marshal.SizeOf<WindowClassEx>(),
				LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_trayWindowProc!),
				HInstance = instance,
				LpszClassName = _trayWindowClassName,
			};
			classRegistered = RegisterClassEx(ref windowClass) != 0;
			if (!classRegistered) throw new InvalidOperationException();
			trayWindow = CreateWindowEx(0, _trayWindowClassName, "Pet Da Dog Tray", 0,
				0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
			if (trayWindow == IntPtr.Zero) throw new InvalidOperationException();
			Interlocked.Exchange(ref _trayWindow, trayWindow);
			if (Volatile.Read(ref _trayStopRequested) != 0) return;
			RegisterHotKey(trayWindow, HotkeyToggleClickThrough, ModAlt, VkOem3);
			ApplyMenuHotkey(trayWindow);
			InstallTrayIcon(trayWindow);
			while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
			{
				TranslateMessage(ref message);
				DispatchMessage(ref message);
			}
		}
		catch (Exception)
		{
			Volatile.Write(ref _menuHotkeyStatus, "The global menu shortcut is unavailable. Right-click the dog or use the tray icon.");
		}
		finally
		{
			// Registration and cleanup belong to the native window's owning thread.
			var remainingWindow = Interlocked.Exchange(ref _trayWindow, IntPtr.Zero);
			if (remainingWindow != IntPtr.Zero)
			{
				UnregisterHotKey(remainingWindow, HotkeyToggleClickThrough);
				UnregisterMenuHotkeys(remainingWindow);
				RemoveTrayIcon(remainingWindow);
				DestroyWindow(remainingWindow);
			}
			if (classRegistered) UnregisterClass(_trayWindowClassName, instance);
			Interlocked.Exchange(ref _trayWindow, IntPtr.Zero);
			Volatile.Write(ref _trayThreadId, 0);
			Volatile.Write(ref _menuHotkeyRegistered, 0);
		}
	}

	private void UnregisterMenuHotkeys(IntPtr window)
	{
		UnregisterHotKey(window, HotkeyOpenMenu);
		UnregisterHotKey(window, HotkeyOpenMenuReplacement);
		_activeMenuHotkeyId = 0;
		Volatile.Write(ref _menuHotkeyRegistered, 0);
	}

	private void ApplyMenuHotkey(IntPtr window)
	{
		var requested = Volatile.Read(ref _requestedMenuHotkey);
		if (Volatile.Read(ref _menuHotkeyCaptureActive) != 0)
		{
			UnregisterMenuHotkeys(window);
			Volatile.Write(ref _menuHotkeyStatus, "Press your new shortcut. The menu shortcut is paused while recording.");
			return;
		}
		if (!MenuShortcut.IsValid(requested.VirtualKey, requested.Modifiers))
		{
			Volatile.Write(ref _menuHotkeyRegistered, 0);
			Volatile.Write(ref _menuHotkeyStatus, "Choose a shortcut with Ctrl, Alt or Shift and another key.");
			return;
		}
		if (_activeMenuHotkeyId != 0 && requested == _lastWorkingMenuHotkey)
		{
			Volatile.Write(ref _menuHotkeyRegistered, 1);
			Volatile.Write(ref _menuHotkeyStatus, MenuShortcut.Format(requested.VirtualKey, requested.Modifiers) + " opens the menu.");
			return;
		}
		// Stage on a second ID so a conflicting replacement cannot discard a working shortcut.
		var replacement = _activeMenuHotkeyId == HotkeyOpenMenu ? HotkeyOpenMenuReplacement : HotkeyOpenMenu;
		if (RegisterHotKey(window, replacement, (uint)requested.Modifiers | ModNoRepeat, (uint)requested.VirtualKey))
		{
			if (_activeMenuHotkeyId != 0) UnregisterHotKey(window, _activeMenuHotkeyId);
			_activeMenuHotkeyId = replacement;
			_lastWorkingMenuHotkey = requested;
			Volatile.Write(ref _menuHotkeyRegistered, 1);
			Volatile.Write(ref _menuHotkeyStatus, MenuShortcut.Format(requested.VirtualKey, requested.Modifiers) + " opens the menu.");
			return;
		}
		var error = Marshal.GetLastWin32Error();
		if (_activeMenuHotkeyId == 0 && _lastWorkingMenuHotkey is { } previous
			&& previous != requested && RegisterHotKey(window, HotkeyOpenMenu,
				(uint)previous.Modifiers | ModNoRepeat, (uint)previous.VirtualKey)) _activeMenuHotkeyId = HotkeyOpenMenu;
		Volatile.Write(ref _menuHotkeyRegistered, 0);
		var message = error == 1409 ? " is already in use." : " could not be enabled.";
		message = MenuShortcut.Format(requested.VirtualKey, requested.Modifiers) + message;
		if (_activeMenuHotkeyId != 0 && _lastWorkingMenuHotkey is { } working)
			message += " " + MenuShortcut.Format(working.VirtualKey, working.Modifiers) + " still opens the menu.";
		Volatile.Write(ref _menuHotkeyStatus, message + " Choose another shortcut, right-click the dog, or use the tray icon.");
	}

	private IntPtr TrayWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
	{
		if (message == WmRefreshMenuHotkey)
		{
			ApplyMenuHotkey(hwnd);
			return IntPtr.Zero;
		}
		if (message == WmHotkey && wParam.ToInt32() == _activeMenuHotkeyId && _activeMenuHotkeyId != 0)
		{
			if (Volatile.Read(ref _menuHotkeyCaptureActive) == 0) Interlocked.Exchange(ref _menuHotkeyRequested, 1);
			return IntPtr.Zero;
		}
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
			UnregisterMenuHotkeys(hwnd);
			RemoveTrayIcon(hwnd);
			DestroyWindow(hwnd);
			return IntPtr.Zero;
		}

		if (message == WmDestroy)
		{
			Interlocked.CompareExchange(ref _trayWindow, IntPtr.Zero, hwnd);
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
			AppendMenu(menu, MfString, new UIntPtr(MenuStatus), "Open Pet Da Dog");
			AppendMenu(menu, MfString, new UIntPtr(MenuExit), "Quit Pet Da Dog");

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
		var previousStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
		var style = previousStyle;
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

		var styleChanged = style != previousStyle;
		var topmostChanged = ((previousStyle & WsExTopmost) != 0) != alwaysOnTop;
		// Keep periodic repair checks, but leave an already-correct transparent surface
		// alone. FRAMECHANGED on every refresh forces unnecessary native repainting.
		if (!styleChanged && !topmostChanged && !forceZOrder) return;
		if (styleChanged) SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
		ApplyWindowZOrder(hwnd, alwaysOnTop, forceZOrder || topmostChanged, styleChanged);
	}

	private static void ApplyWindowZOrder(IntPtr hwnd, bool alwaysOnTop, bool forceZOrder, bool styleChanged)
	{
		var flags = SwpNoMove | SwpNoSize | SwpNoActivate;
		if (styleChanged) flags |= SwpFrameChanged;

		if (!forceZOrder)
		{
			SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, flags | SwpNoZOrder);
			return;
		}

		if (alwaysOnTop)
		{
			SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, flags);
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
		public uint Private;
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
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnregisterClass(string className, IntPtr instance);

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

	[DllImport("user32.dll", EntryPoint = "PeekMessageW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool PeekMessage(out Message message, IntPtr hwnd, uint messageFilterMin, uint messageFilterMax, uint remove);

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

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int virtualKey);

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
