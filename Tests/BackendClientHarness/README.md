# Client transport tests

This dependency-free .NET 8 harness compiles the production `BackendPetClient.cs`
against a fake HTTP handler and tiny Godot/Steam stubs. It does not contact Steam
or a live Worker and does not initialize Godot.

From the repository root:

```powershell
dotnet restore Tests/BackendClientHarness/BackendClientHarness.csproj --configfile Tests/BackendClientHarness/NuGet.Config
dotnet run --no-restore --project Tests/BackendClientHarness/BackendClientHarness.csproj
```

The twenty-five scenarios cover the shared Worker default URL, project settings and
environment overrides (including an explicit local backend), the Steam ticket
identity and authenticated inventory request contract, offline click queuing,
one FIFO grant per click, transport and
rate-limit retries with the original click GUID, replayed grants using a 64-bit
server total, repeated authoritative inventory refreshes, invalid snapshots,
expired-session renewal, Steam account changes, and allowlisted Steam endpoints,
HTTP statuses, failure categories and bounded numeric result diagnostics. The
diagnostic checks reject raw response details, unknown categories and malformed
result values while preserving pending grant retries. They also cover preserving
exchange IDs after uncertain requests, confirmed-only balances and rewards,
serialized grant/exchange snapshots, recovery of a server pending exchange after
a restart, and deterministic preflight rejection.

Golden Bones checks cover foreground purchase queuing during input grants,
rapid-click protection, operation-status recovery without blindly repeating an
exchange, same-ID recovery after a server-confirmed missing request, restart
receipts, automatic acknowledgment retries, recyclable copy limits, capability
gates, monotonic server-based cooldowns, cross-device pending operations, and
receipt identity validation. The DEBUG test bootstrap is verified to reject
remote servers and loopback servers without the isolated-economy capability.

These are deterministic transport tests. They do not prove that a real Steam
account owns the app, that the deployed Worker has valid secrets, or that a live
Steam Inventory grant succeeds; validate those through the running client.

To check the actual C# client against both local Wrangler Workers and SQLite,
with Steam replaced entirely by the isolated fake service:

```powershell
.\scripts\Run-TestEconomy.ps1 -TransportSmoke
```

This opt-in run uses fresh persistence, starts hidden local Workers, checks the
full purchase/recycle/receipt protocol, and stops its process tree afterward.
It requires Node 22.13 or newer and the Worker's installed npm dependencies.
The script discovers the bundled Node runtime or accepts `-NodePath`.

Run `.\scripts\Run-TestEconomy.ps1` without `-TransportSmoke` to open the Debug
game against the same fake economy. The script uses an isolated Godot profile,
disables real Steam and global input, and exposes Shop test controls. Close the
game to stop both Workers. Pass `-GodotPath` for Godot 4.6.3 Mono when needed;
`-SkipBuild` reuses an existing Debug build. Test profiles/logs remain under the
ignored `.godot/isolated-economy` directory for diagnosis.
