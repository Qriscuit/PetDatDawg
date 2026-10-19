# Global input observer checks

Run the default isolated suite from the repository root:

```powershell
dotnet run --project Tests/GlobalInputHarness/GlobalInputHarness.csproj
```

The harness links the real monitor/classifier source with minimal Godot stubs.
It checks physical, remote and injected key/button press edges, auto-repeat,
mixed-source duplicate suppression and release rearming, startup held state,
independent Enter keys and X-buttons, Windows struct layouts,
disabled/editor/headless guards, and main-frame count delivery. The default suite
never installs native hooks and never calls Steam or the backend.

To opt in to a native installation/shutdown check on Windows:

```powershell
dotnet run --project Tests/GlobalInputHarness/GlobalInputHarness.csproj -- --native-lifecycle
```

This mode briefly installs the real hooks and verifies normal startup/shutdown,
an immediate-stop race, and restart. It never dispatches or logs activity counts,
keys, or text, and never creates a Steam/backend client. Actual physical, remote
or injected event delivery is not tested by this mode and still needs an interactive Windows check with
Steam disabled.

Keyboard injection flags (`0x10`, `0x02`, or both) and mouse injection flags
(`0x01`, `0x02`, or both) do not exclude a press. These sources share the same
held-state bookkeeping: a duplicate down cannot count twice merely because its
flags changed, and a release from any source rearms the next down. Holding a key
or button, releases, movement and wheel scrolling do not earn additional counts.

Low-level hooks observe the current Windows desktop, not
secure desktop prompts, lock screens, or other sessions. Windows can remove a
hook after a callback timeout without reporting that removal. The callbacks do
only pressed-state classification and atomic counting, and always forward input.
No text, key sequence, or pointer-position history is recorded.

Microsoft reference: [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc),
[LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc).
