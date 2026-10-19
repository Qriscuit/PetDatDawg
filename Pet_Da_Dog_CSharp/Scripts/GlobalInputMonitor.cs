using System;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;

/// <summary>
/// Observes local and remote input on the current Windows desktop without consuming it.
/// Only aggregate activity counts leave the native hook thread.
/// </summary>
public partial class GlobalInputMonitor : Node
{
	private const uint WmQuit = 0x0012;
	private readonly GlobalInputEdgeTracker _edges = new();
	private Thread? _monitorThread;
	private NativeMethods.HookCallback? _keyboardCallback;
	private NativeMethods.HookCallback? _mouseCallback;
	private uint _monitorThreadId;
	private int _stopRequested;
	private int _available;
	private long _observedActivity;
	private string _status = "Desktop input monitoring has not started.";

	/// <summary>Raised only from Godot's main-thread _Process, once per count batch.</summary>
	public event Action<int>? ActivityObserved;
	public bool IsAvailable => Volatile.Read(ref _available) != 0 && Volatile.Read(ref _stopRequested) == 0;
	public string Status => Volatile.Read(ref _status);

	public override void _Ready()
	{
		ProcessMode = ProcessModeEnum.Always;
		// Never reset a shutdown request or replace rooted delegates while an old
		// observer thread is still alive, including a timed-out shutdown join.
		if (_monitorThread is { IsAlive: true })
		{
			if (Volatile.Read(ref _stopRequested) != 0)
				Volatile.Write(ref _status, "Desktop input monitoring is stopping.");
			return;
		}
		if (System.Environment.GetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT") == "1")
		{
			_status = "Desktop input monitoring is disabled for this run.";
			SetProcess(false);
			return;
		}
		if (Engine.IsEditorHint() || DisplayServer.GetName() == "headless")
		{
			_status = "Desktop input monitoring is disabled in editor and headless runs.";
			SetProcess(false);
			return;
		}
		if (!OperatingSystem.IsWindows())
		{
			_status = "Desktop input monitoring is available on Windows only.";
			SetProcess(false);
			return;
		}

		SetProcess(true);
		Volatile.Write(ref _stopRequested, 0);
		_status = "Starting desktop input monitoring.";
		_monitorThread = new Thread(MonitorInput)
		{
			IsBackground = true,
			Name = "PetDaDog desktop input observer"
		};
		try { _monitorThread.Start(); }
		catch (Exception)
		{
			_monitorThread = null;
			_status = "Desktop input monitoring could not start.";
		}
	}

	public override void _Process(double delta)
	{
		var count = Interlocked.Exchange(ref _observedActivity, 0);
		if (Volatile.Read(ref _stopRequested) != 0) return;
		while (count > 0)
		{
			var batch = (int)Math.Min(count, int.MaxValue);
			ActivityObserved?.Invoke(batch);
			count -= batch;
		}
	}

	public override void _ExitTree()
	{
		Volatile.Write(ref _stopRequested, 1);
		var threadId = Volatile.Read(ref _monitorThreadId);
		if (threadId != 0) NativeMethods.PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
		if (_monitorThread is { IsAlive: true } && !_monitorThread.Join(TimeSpan.FromSeconds(2)))
			Volatile.Write(ref _status, "Desktop input monitoring is stopping.");
		ActivityObserved = null;
		Interlocked.Exchange(ref _observedActivity, 0);
	}

	private void MonitorInput()
	{
		var keyboardHook = IntPtr.Zero;
		var mouseHook = IntPtr.Zero;
		try
		{
			// Create the thread's queue before publishing its ID, so shutdown cannot
			// post WM_QUIT to a thread that has no message queue yet.
			NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
			Volatile.Write(ref _monitorThreadId, NativeMethods.GetCurrentThreadId());
			if (Volatile.Read(ref _stopRequested) != 0) return;
			SeedHeldInput();

			_keyboardCallback = ObserveKeyboard;
			_mouseCallback = ObserveMouse;
			var module = NativeMethods.GetModuleHandle(null);
			keyboardHook = NativeMethods.SetWindowsHookEx(13, _keyboardCallback, module, 0);
			if (keyboardHook == IntPtr.Zero)
			{
				Volatile.Write(ref _status, $"Keyboard input monitoring is unavailable (Windows error {Marshal.GetLastWin32Error()}).");
				return;
			}
			mouseHook = NativeMethods.SetWindowsHookEx(14, _mouseCallback, module, 0);
			if (mouseHook == IntPtr.Zero)
			{
				Volatile.Write(ref _status, $"Mouse input monitoring is unavailable (Windows error {Marshal.GetLastWin32Error()}).");
				return;
			}
			Volatile.Write(ref _status, "Observing local and remote keyboard and mouse-button presses.");
			Volatile.Write(ref _available, 1);
			while (Volatile.Read(ref _stopRequested) == 0)
			{
				var result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);
				if (result == 0) break;
				if (result < 0)
				{
					Volatile.Write(ref _status, $"Desktop input monitoring stopped (Windows error {Marshal.GetLastWin32Error()}).");
					break;
				}
				NativeMethods.TranslateMessage(ref message);
				NativeMethods.DispatchMessage(ref message);
			}
		}
		catch (Exception)
		{
			Volatile.Write(ref _status, "Desktop input monitoring is unavailable.");
		}
		finally
		{
			Volatile.Write(ref _available, 0);
			if (mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(mouseHook);
			if (keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(keyboardHook);
			// Keep callback delegates rooted until their hooks have been removed.
			_mouseCallback = null;
			_keyboardCallback = null;
			_edges.Reset();
			Volatile.Write(ref _monitorThreadId, 0);
			if (Volatile.Read(ref _stopRequested) != 0)
				Volatile.Write(ref _status, "Desktop input monitoring has stopped.");
		}
	}

	private void SeedHeldInput()
	{
		_edges.Reset();
		// A key held before startup must not earn Pets through its auto-repeat.
		// This is only transient pressed-state bookkeeping, never a key history.
		for (uint key = 1; key < 255; key++)
			if ((NativeMethods.GetAsyncKeyState((int)key) & 0x8000) != 0) _edges.SeedHeldKeyboardKey(key);
		foreach (var key in new[] { 1, 2, 4, 5, 6 })
			if ((NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0) _edges.SeedHeldMouseButton(key);
	}

	private IntPtr ObserveKeyboard(int code, UIntPtr message, IntPtr data)
	{
		if (code == 0 && Volatile.Read(ref _stopRequested) == 0)
		{
			try
			{
				var input = Marshal.PtrToStructure<NativeMethods.KeyboardInput>(data);
				if (_edges.ObserveKeyboard((uint)message.ToUInt64(), input.VirtualKey, input.Flags))
					Interlocked.Increment(ref _observedActivity);
			}
			catch (Exception) { StopAfterCallbackFailure(); }
		}
		// Input always continues through Windows and every other installed hook.
		return NativeMethods.CallNextHookEx(IntPtr.Zero, code, message, data);
	}

	private IntPtr ObserveMouse(int code, UIntPtr message, IntPtr data)
	{
		var kind = (uint)message.ToUInt64();
		// Movement and wheel messages are the common case and need no inspection.
		if (code == 0 && Volatile.Read(ref _stopRequested) == 0 &&
			kind is 0x0201 or 0x0202 or 0x0204 or 0x0205 or 0x0207 or 0x0208 or 0x020B or 0x020C)
		{
			try
			{
				var input = Marshal.PtrToStructure<NativeMethods.MouseInput>(data);
				var count = _edges.ObserveMouse(kind, input.MouseData, input.Flags);
				if (count > 0) Interlocked.Add(ref _observedActivity, count);
			}
			catch (Exception) { StopAfterCallbackFailure(); }
		}
		return NativeMethods.CallNextHookEx(IntPtr.Zero, code, message, data);
	}

	private void StopAfterCallbackFailure()
	{
		// No exception may cross the native callback boundary.
		Volatile.Write(ref _stopRequested, 1);
		NativeMethods.PostThreadMessage(Volatile.Read(ref _monitorThreadId), WmQuit, UIntPtr.Zero, IntPtr.Zero);
	}

	internal static class NativeMethods
	{
		[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		internal delegate IntPtr HookCallback(int code, UIntPtr message, IntPtr data);

		[StructLayout(LayoutKind.Sequential)]
		internal struct Point { public int X; public int Y; }

		[StructLayout(LayoutKind.Sequential)]
		internal struct KeyboardInput
		{
			public uint VirtualKey;
			public uint ScanCode;
			public uint Flags;
			public uint Time;
			public UIntPtr ExtraInfo;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct MouseInput
		{
			public Point Position;
			public uint MouseData;
			public uint Flags;
			public uint Time;
			public UIntPtr ExtraInfo;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Message
		{
			public IntPtr Window;
			public uint Id;
			public UIntPtr WParam;
			public IntPtr LParam;
			public uint Time;
			public Point Position;
			public uint Private;
		}

		[DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
		internal static extern IntPtr SetWindowsHookEx(int hook, HookCallback callback, IntPtr module, uint threadId);

		[DllImport("user32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool UnhookWindowsHookEx(IntPtr hook);

		[DllImport("user32.dll")]
		internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, UIntPtr message, IntPtr data);

		[DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
		internal static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);

		[DllImport("user32.dll", EntryPoint = "PeekMessageW")]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);

		[DllImport("user32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool TranslateMessage(ref Message message);

		[DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
		internal static extern IntPtr DispatchMessage(ref Message message);

		[DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		internal static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll")]
		internal static extern short GetAsyncKeyState(int virtualKey);

		[DllImport("kernel32.dll")]
		internal static extern uint GetCurrentThreadId();

		[DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
		internal static extern IntPtr GetModuleHandle(string? moduleName);
	}
}

/// <summary>Pure input classification, accessed only by the native observer thread.</summary>
internal sealed class GlobalInputEdgeTracker
{
	private readonly bool[] _keyboardDown = new bool[512];
	private readonly bool[] _initiallyHeldKeys = new bool[256];
	private int _mouseDown;

	public bool ObserveKeyboard(uint message, uint virtualKey, uint flags)
	{
		// Remote desktop software may mark real user input as injected. Count all
		// sources through the same press-edge state so a held key still counts once.
		if (virtualKey is 0 or >= 255) return false;
		var index = (int)(virtualKey * 2 + (flags & 1));
		if (message is 0x0101 or 0x0105)
		{
			if (_initiallyHeldKeys[virtualKey])
			{
				_keyboardDown[virtualKey * 2] = false;
				_keyboardDown[virtualKey * 2 + 1] = false;
				_initiallyHeldKeys[virtualKey] = false;
			}
			else _keyboardDown[index] = false;
			return false;
		}
		if (message is not (0x0100 or 0x0104) || _keyboardDown[index]) return false;
		_keyboardDown[index] = true;
		return true;
	}

	public int ObserveMouse(uint message, uint mouseData, uint flags)
	{
		// Injection flags do not exclude remote clicks from earning. Button state
		// still rejects duplicate downs, releases, movement and wheel messages.
		var buttons = message switch
		{
			0x0201 or 0x0202 => 1,
			0x0204 or 0x0205 => 2,
			0x0207 or 0x0208 => 4,
			0x020B or 0x020C => (int)((mouseData >> 16) & 3) << 3,
			_ => 0
		};
		if (message is 0x0202 or 0x0205 or 0x0208 or 0x020C)
		{
			_mouseDown &= ~buttons;
			return 0;
		}
		var newButtons = buttons & ~_mouseDown;
		_mouseDown |= buttons;
		var count = 0;
		while (newButtons != 0)
		{
			newButtons &= newButtons - 1;
			count++;
		}
		return count;
	}

	public void SeedHeldKeyboardKey(uint virtualKey)
	{
		if (virtualKey is 0 or >= 255) return;
		_keyboardDown[virtualKey * 2] = true;
		_keyboardDown[virtualKey * 2 + 1] = true;
		_initiallyHeldKeys[virtualKey] = true;
	}

	public void SeedHeldMouseButton(int virtualKey)
	{
		_mouseDown |= virtualKey switch { 1 => 1, 2 => 2, 4 => 4, 5 => 8, 6 => 16, _ => 0 };
	}

	public void Reset()
	{
		Array.Clear(_keyboardDown);
		Array.Clear(_initiallyHeldKeys);
		_mouseDown = 0;
	}
}
