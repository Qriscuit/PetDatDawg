using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;

/// <summary>Own-window hotkey lifecycle checks, without keyboard injection or economy calls.</summary>
public partial class NativeMenuHotkeySmokeTest : Node
{
	private int _checks;
	private NativeWindowBridge _bridge = null!;
	private int _opened;
	public override void _Ready() => CallDeferred(nameof(Run));

	private async void Run()
	{
		var code = 0;
		var probe = IntPtr.Zero;
		var settings = GetNode<PetSettings>("/root/PetSettings");
		var originalKey = settings.MenuHotkeyVirtualKey;
		var originalModifiers = settings.MenuHotkeyModifiers;
		var trayFlag = System.Environment.GetEnvironmentVariable("PDD_DISABLE_TRAY");
		try
		{
			Require(OperatingSystem.IsWindows(), "Native shortcut checks require Windows.");
			Require(System.Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") == "1"
				&& System.Environment.GetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT") == "1",
				"Native shortcut checks require Steam and physical input earning disabled.");
			_bridge = GetNode<NativeWindowBridge>("/root/NativeWindowBridge");
			_bridge.StatusRequested += OnMenuOpened;
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			StopBridge();
			probe = CreateWindowEx(0, "STATIC", "Pet that Dog shortcut test", 0, 0, 0, 0, 0,
				IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			Require(probe != IntPtr.Zero, "An isolated conflict window is available.");
			const int modifiers = MenuShortcut.Control | MenuShortcut.Alt | MenuShortcut.Shift;
			var first = 0; var second = 0;
			foreach (var key in new[] { 0x77, 0x78, 0x79, 0x7A, 0x7C, 0x7D, 0x7E, 0x7F })
			{
				if (first == 0 && RegisterHotKey(probe, 100, modifiers | 0x4000, (uint)key)) first = key;
				else if (first != 0 && RegisterHotKey(probe, 101, modifiers | 0x4000, (uint)key)) { second = key; break; }
			}
			Require(first != 0 && second != 0, "Two unused Ctrl+Alt+Shift function shortcuts can be reserved honestly.");
			UnregisterHotKey(probe, 100);
			settings.SetMenuHotkey(first, modifiers);
			_bridge.InstallTrayMenu();
			await WaitUntil(() => _bridge.MenuHotkeyRegistered, "Initial shortcut registration");
			Require(_bridge.MenuHotkeyStatus.Contains(MenuShortcut.Format(first, modifiers)), "The registered shortcut is visible in status.");
			await RequestMenu();

			_bridge.SetMenuHotkeyCaptureActive(true);
			await WaitUntil(() => !_bridge.MenuHotkeyRegistered, "Shortcut capture suspension");
			var before = _opened;
			PostMessage(Window(), 0x0312, new IntPtr(2), IntPtr.Zero);
			await Frames(4);
			Require(_opened == before, "A queued shortcut cannot reopen the menu during capture.");
			_bridge.SetMenuHotkeyCaptureActive(false);
			await WaitUntil(() => _bridge.MenuHotkeyRegistered, "Capture cancellation restores the shortcut");
			await RequestMenu();

			settings.SetMenuHotkey(second, modifiers);
			await WaitUntil(() => _bridge.MenuHotkeyStatus.Contains("already in use"), "A forced shortcut conflict is reported");
			Require(!_bridge.MenuHotkeyRegistered && _bridge.MenuHotkeyStatus.Contains("still opens"),
				"A conflicting replacement reports failure and preserves the earlier binding.");
			await RequestMenu();
			UnregisterHotKey(probe, 101);
			_bridge.SetMenuHotkeyCaptureActive(true);
			await WaitUntil(() => !_bridge.MenuHotkeyRegistered && ActiveId() == 0, "Recapturing suspends the fallback shortcut");
			_bridge.SetMenuHotkeyCaptureActive(false);
			await WaitUntil(() => _bridge.MenuHotkeyRegistered
				&& _bridge.MenuHotkeyStatus.Contains(MenuShortcut.Format(second, modifiers)), "The replacement registers after its conflict is removed");
			Require(RegisterHotKey(probe, 100, modifiers | 0x4000, (uint)first), "Changing shortcuts releases the old combination.");
			UnregisterHotKey(probe, 100);
			await RequestMenu();

			StopBridge();
			Require(!_bridge.MenuHotkeyRegistered && RegisterHotKey(probe, 101, modifiers | 0x4000, (uint)second),
				"Stopping the bridge releases its native shortcut.");
			UnregisterHotKey(probe, 101);
			_bridge.InstallTrayMenu();
			await WaitUntil(() => _bridge.MenuHotkeyRegistered, "The native shortcut window can restart after cleanup");
			await RequestMenu();
			StopBridge();
			System.Environment.SetEnvironmentVariable("PDD_DISABLE_TRAY", "1");
			_bridge.InstallTrayMenu();
			Require(!_bridge.MenuHotkeyRegistered && _bridge.MenuHotkeyStatus.Contains("disabled"), "The test disable flag prevents every native shortcut registration.");
			GD.Print($"NATIVE_MENU_HOTKEY_SMOKE_PASS: {_checks} checks; own-window messages only; no injected keys, Steam calls or grants.");
		}
		catch (Exception exception)
		{
			code = 1;
			GD.PushError("NATIVE_MENU_HOTKEY_SMOKE_FAIL: " + exception);
		}
		finally
		{
			if (_bridge != null) { _bridge.StatusRequested -= OnMenuOpened; StopBridge(); }
			if (probe != IntPtr.Zero) { UnregisterHotKey(probe, 100); UnregisterHotKey(probe, 101); DestroyWindow(probe); }
			settings.SetMenuHotkey(originalKey, originalModifiers);
			System.Environment.SetEnvironmentVariable("PDD_DISABLE_TRAY", trayFlag);
			GetTree().Quit(code);
		}
	}
	private void OnMenuOpened() => _opened++;
	private void StopBridge() => typeof(NativeWindowBridge).GetMethod("RemoveTrayMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, null);
	private IntPtr Window() => (IntPtr)typeof(NativeWindowBridge).GetField("_trayWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_bridge)!;
	private int ActiveId() => (int)typeof(NativeWindowBridge).GetField("_activeMenuHotkeyId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_bridge)!;
	private async Task RequestMenu()
	{
		var before = _opened;
		Require(PostMessage(Window(), 0x0312, new IntPtr(ActiveId()), IntPtr.Zero), "The test can post only to its own shortcut window.");
		await WaitUntil(() => _opened == before + 1, "The native shortcut requests the menu on Godot's main thread");
		await Frames(2);
		Require(_opened == before + 1, "One native shortcut message emits exactly one menu request.");
	}
	private async Task WaitUntil(Func<bool> condition, string operation)
	{
		var timeout = Time.GetTicksMsec() + 5000;
		while (!condition() && Time.GetTicksMsec() < timeout) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Require(condition(), operation + ": " + _bridge.MenuHotkeyStatus);
	}
	private async Task Frames(int count) { for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
	private void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; }

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
	[DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
	[DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
	[DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
	[DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
