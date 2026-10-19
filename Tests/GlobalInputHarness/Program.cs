using System.Reflection;
using System.Runtime.InteropServices;
using Godot;

var checks = 0;
void Check(bool condition, string name)
{
	checks++;
	if (!condition) throw new InvalidOperationException(name);
}

var edges = new GlobalInputEdgeTracker();
var keyboardSources = new uint[] { 0, 0x10, 0x02, 0x12 };
foreach (var flags in keyboardSources)
{
	edges.Reset();
	Check(edges.ObserveKeyboard(0x100, 0x41, flags), "Physical, injected and lower-integrity injected key press edges all count");
	foreach (var repeatedFlags in keyboardSources)
		Check(!edges.ObserveKeyboard(0x100, 0x41, repeatedFlags), "Changing injection flags on a held key cannot award another press");
	Check(!edges.ObserveKeyboard(0x101, 0x41, flags ^ 0x12), "A release never counts regardless of its injection flags");
	Check(edges.ObserveKeyboard(0x100, 0x41, flags ^ 0x02), "A release from another input source rearms the key for its next press");
	for (var repeat = 0; repeat < 5; repeat++)
		Check(!edges.ObserveKeyboard(0x100, 0x41, flags), "Keyboard auto-repeat never counts after a mixed-source press");
}
edges.Reset();
Check(edges.ObserveKeyboard(0x104, 0xA4, 0x30), "Injected system/Alt key press counts");
Check(!edges.ObserveKeyboard(0x104, 0xA4, 0x20), "Physical system/Alt repeat cannot duplicate an injected press");
Check(!edges.ObserveKeyboard(0x105, 0xA4, 0xA2), "Injected system key release does not count");
Check(edges.ObserveKeyboard(0x104, 0xA4, 0x20), "System key is rearmed after an injected release");
Check(edges.ObserveKeyboard(0x100, 0x0D, 0x10), "Injected main Enter press counts");
Check(edges.ObserveKeyboard(0x100, 0x0D, 0x03), "Injected keypad Enter can count while main Enter is held");
Check(!edges.ObserveKeyboard(0x101, 0x0D, 1), "Keypad Enter release does not count");
Check(!edges.ObserveKeyboard(0x100, 0x0D, 0), "Keypad release does not rearm main Enter");
Check(edges.ObserveKeyboard(0x100, 0x0D, 0x11), "Keypad Enter counts another injected press after its own release");
Check(!edges.ObserveKeyboard(0x101, 0x0D, 0x02), "Main Enter release from another source does not count");
Check(edges.ObserveKeyboard(0x100, 0x0D, 0), "Main Enter counts after its injected release");
Check(!edges.ObserveKeyboard(0x100, 0x0D, 0x11), "Releasing main Enter cannot rearm the still-held injected keypad Enter");
Check(!edges.ObserveKeyboard(0x102, 0x43, 0), "Character messages are ignored");
Check(!edges.ObserveKeyboard(0x102, 0x43, 0x12), "Injected character messages are ignored too");
Check(!edges.ObserveKeyboard(0x100, 0, 0), "Zero virtual key is ignored");
Check(!edges.ObserveKeyboard(0x100, 255, 0), "Reserved virtual key is ignored");
Check(!edges.ObserveKeyboard(0x100, uint.MaxValue, 0), "Out-of-range virtual key is ignored");

edges.Reset();
edges.SeedHeldKeyboardKey(0x41);
foreach (var flags in keyboardSources)
{
	Check(!edges.ObserveKeyboard(0x100, 0x41, flags), "A key held at startup suppresses repeats from every input source");
	Check(!edges.ObserveKeyboard(0x100, 0x41, flags | 1), "Startup key state also suppresses injected extended repeats");
}
Check(!edges.ObserveKeyboard(0x101, 0x41, 0x10), "An injected startup-held key release rearms without counting");
Check(edges.ObserveKeyboard(0x100, 0x41, 0x02), "Startup held key counts its next injected press after release");
Check(edges.ObserveKeyboard(0x100, 0x41, 0x11), "Startup release clears the alternative extended key identity too");

var buttons = new (uint Down, uint Up, uint Data, int VirtualKey)[]
{
	(0x201, 0x202, 0, 1),
	(0x204, 0x205, 0, 2),
	(0x207, 0x208, 0, 4),
	(0x20B, 0x20C, 1u << 16, 5),
	(0x20B, 0x20C, 2u << 16, 6)
};
foreach (var button in buttons)
{
	foreach (var flags in new uint[] { 0, 1, 2, 3 })
	{
		edges.Reset();
		Check(edges.ObserveMouse(button.Down, button.Data, flags) == 1, "Every mouse button counts physical and injected press edges");
		foreach (var repeatedFlags in new uint[] { 0, 1, 2, 3 })
			Check(edges.ObserveMouse(button.Down, button.Data, repeatedFlags) == 0, "Changing injection flags cannot duplicate a held mouse button");
		Check(edges.ObserveMouse(button.Up, button.Data, flags ^ 3) == 0, "A mixed-source mouse release never counts");
		Check(edges.ObserveMouse(button.Down, button.Data, flags ^ 2) == 1, "A mixed-source release rearms every mouse button for its next press");
		edges.Reset();
		edges.SeedHeldMouseButton(button.VirtualKey);
		Check(edges.ObserveMouse(button.Down, button.Data, flags) == 0, "A mouse button held at startup suppresses injected and physical repeats");
		Check(edges.ObserveMouse(button.Up, button.Data, flags ^ 3) == 0, "A startup-held button release does not count across input sources");
		Check(edges.ObserveMouse(button.Down, button.Data, flags) == 1, "A startup-held button counts its next press from any source after release");
	}
}
edges.Reset();
Check(edges.ObserveMouse(0x20B, 3u << 16, 1) == 2, "An injected combined X-button press counts both new buttons");
Check(edges.ObserveMouse(0x20C, 1u << 16, 0) == 0, "One X-button can release independently");
Check(edges.ObserveMouse(0x20B, 2u << 16, 2) == 0, "Other injected X-button remains held");
Check(edges.ObserveMouse(0x20B, 1u << 16, 0) == 1, "Released X-button can count again from a physical press");
foreach (var flags in new uint[] { 0, 1, 2, 3 })
{
	Check(edges.ObserveMouse(0x200, 0, flags) == 0, "Physical and injected mouse movement is ignored");
	Check(edges.ObserveMouse(0x20A, 120u << 16, flags) == 0, "Physical and injected mouse wheel is ignored");
	Check(edges.ObserveMouse(0x20E, 120u << 16, flags) == 0, "Physical and injected horizontal wheel is ignored");
}
Check(edges.ObserveMouse(0x20B, 0, 0) == 0, "Unknown X-button data is ignored");

Check(Marshal.SizeOf<GlobalInputMonitor.NativeMethods.KeyboardInput>() == (IntPtr.Size == 8 ? 24 : 20), "Keyboard hook uses pointer-sized extra info");
Check(Marshal.SizeOf<GlobalInputMonitor.NativeMethods.MouseInput>() == (IntPtr.Size == 8 ? 32 : 24), "Mouse hook uses pointer-sized extra info");
Check(Marshal.SizeOf<GlobalInputMonitor.NativeMethods.Message>() == (IntPtr.Size == 8 ? 48 : 32), "Message struct matches Windows pointer alignment");
Check(Marshal.OffsetOf<GlobalInputMonitor.NativeMethods.KeyboardInput>("ExtraInfo").ToInt32() == 16, "Keyboard ULONG_PTR offset is correct");
Check(Marshal.OffsetOf<GlobalInputMonitor.NativeMethods.MouseInput>("ExtraInfo").ToInt32() == (IntPtr.Size == 8 ? 24 : 20), "Mouse ULONG_PTR offset is correct");

var savedFlag = Environment.GetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT");
try
{
	// Every monitor initialization here takes a disabled path; no native hook is installed.
	Environment.SetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT", "1");
	DisplayServer.Name = "Windows";
	var disabled = new GlobalInputMonitor();
	disabled._Ready();
	Check(!disabled.IsAvailable && !disabled.Processing && disabled.Status.Contains("disabled"), "Explicit disable flag prevents monitor startup");
	disabled._ExitTree();
	Environment.SetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT", null);
	DisplayServer.Name = "headless";
	var headless = new GlobalInputMonitor();
	headless._Ready();
	Check(!headless.IsAvailable && !headless.Processing, "Headless runs never install native hooks");
	headless._ExitTree();
	DisplayServer.Name = "Windows";
	Engine.EditorHint = true;
	var editor = new GlobalInputMonitor();
	editor._Ready();
	Check(!editor.IsAvailable && !editor.Processing, "Editor runs never install native hooks");
	editor._ExitTree();
	Engine.EditorHint = false;

	var monitor = new GlobalInputMonitor();
	var countField = typeof(GlobalInputMonitor).GetField("_observedActivity", BindingFlags.Instance | BindingFlags.NonPublic)!;
	var delivered = 0;
	var callbackThread = 0;
	monitor.ActivityObserved += count => { delivered += count; callbackThread = Environment.CurrentManagedThreadId; };
	Task.Run(() => countField.SetValue(monitor, 7L)).GetAwaiter().GetResult();
	Check(delivered == 0, "Native-side aggregate is not delivered before the Godot frame");
	monitor._Process(0.016);
	Check(delivered == 7 && callbackThread == Environment.CurrentManagedThreadId, "Aggregate is delivered on the _Process caller thread");
	monitor._Process(0.016);
	Check(delivered == 7, "A count batch is delivered exactly once");
	countField.SetValue(monitor, 2L);
	monitor._ExitTree();
	monitor._Process(0.016);
	Check(delivered == 7, "Shutdown discards undelivered counts and removes subscribers");

	// Simulate a worker taking longer to stop without installing a native hook.
	// Keep the disable flag set, so even a regressed startup guard stays isolated.
	Environment.SetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT", "1");
	var stoppingMonitor = new GlobalInputMonitor();
	var monitorThreadField = typeof(GlobalInputMonitor).GetField("_monitorThread", BindingFlags.Instance | BindingFlags.NonPublic)!;
	var stopField = typeof(GlobalInputMonitor).GetField("_stopRequested", BindingFlags.Instance | BindingFlags.NonPublic)!;
	var availableField = typeof(GlobalInputMonitor).GetField("_available", BindingFlags.Instance | BindingFlags.NonPublic)!;
	using var releaseWorker = new ManualResetEventSlim();
	var stoppingWorker = new Thread(() => releaseWorker.Wait(TimeSpan.FromSeconds(5))) { IsBackground = true };
	stoppingWorker.Start();
	try
	{
		monitorThreadField.SetValue(stoppingMonitor, stoppingWorker);
		stopField.SetValue(stoppingMonitor, 1);
		availableField.SetValue(stoppingMonitor, 1);
		Check(!stoppingMonitor.IsAvailable, "Shutdown request makes monitor unavailable before worker exits");
		stoppingMonitor._Ready();
		Check(ReferenceEquals(monitorThreadField.GetValue(stoppingMonitor), stoppingWorker), "A stopping worker is not replaced");
		Check((int)stopField.GetValue(stoppingMonitor)! == 1, "Repeated startup preserves a pending shutdown request");
		Check(stoppingMonitor.Status.Contains("stopping"), "Repeated startup preserves stopping state");
	}
	finally
	{
		releaseWorker.Set();
		stoppingMonitor._ExitTree();
	}
}
finally
{
	Environment.SetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT", savedFlag);
	Engine.EditorHint = false;
	DisplayServer.Name = "headless";
}

if (args.Contains("--native-lifecycle", StringComparer.Ordinal))
{
	if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native lifecycle checks require Windows.");
	var lifecycleFlag = Environment.GetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT");
	var monitor = new GlobalInputMonitor();
	var threadField = typeof(GlobalInputMonitor).GetField("_monitorThread", BindingFlags.Instance | BindingFlags.NonPublic)!;
	Thread? MonitorThread() => (Thread?)threadField.GetValue(monitor);
	void VerifyStarted(string name)
	{
		var started = SpinWait.SpinUntil(() => monitor.IsAvailable || MonitorThread()?.IsAlive != true, TimeSpan.FromSeconds(5));
		Check(started && monitor.IsAvailable, $"{name}: hooks install successfully; {monitor.Status}");
		Check(MonitorThread()?.IsAlive == true, $"{name}: dedicated message-loop thread is running");
	}
	void VerifyStopped(string name)
	{
		Check(!monitor.IsAvailable, $"{name}: hooks are unavailable after shutdown");
		Check(MonitorThread()?.IsAlive != true, $"{name}: message-loop thread has stopped");
	}
	try
	{
		// Opt-in installation only. No event subscribers, frame dispatch, key logs,
		// Steam client, backend client, or network calls exist in this harness.
		Environment.SetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT", null);
		DisplayServer.Name = "Windows";
		Engine.EditorHint = false;
		monitor._Ready();
		VerifyStarted("Normal startup");
		monitor._ExitTree();
		VerifyStopped("Normal shutdown");
		monitor._Ready();
		monitor._ExitTree();
		VerifyStopped("Immediate startup/shutdown race");
		monitor._Ready();
		VerifyStarted("Restart after immediate shutdown");
		monitor._ExitTree();
		VerifyStopped("Restart shutdown");
	}
	finally
	{
		monitor._ExitTree();
		Environment.SetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT", lifecycleFlag);
		DisplayServer.Name = "headless";
	}
	Console.WriteLine($"Global input harness passed {checks} checks, including native Windows hook startup, shutdown, immediate-stop race and restart. No event counts or keys were logged; no Steam or backend calls were made. Physical input delivery was not tested.");
}
else
	Console.WriteLine($"Global input harness passed {checks} checks; no native hooks or Steam calls were made.");
