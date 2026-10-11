# Client transport tests

This dependency-free .NET 8 harness compiles the production `BackendPetClient.cs`
against a fake HTTP handler and tiny Godot/Steam stubs. It does not contact Steam
or a live Worker and does not initialize Godot.

From the repository root:

```powershell
dotnet restore Tests/BackendClientHarness/BackendClientHarness.csproj --configfile Tests/BackendClientHarness/NuGet.Config
dotnet run --no-restore --project Tests/BackendClientHarness/BackendClientHarness.csproj
```

The twelve scenarios cover the Steam ticket identity and authenticated inventory
request contract, offline click queuing, one FIFO grant per click, transport and
rate-limit retries with the original click GUID, replayed grants using a 64-bit
server total, repeated authoritative inventory refreshes, invalid snapshots,
expired-session renewal, Steam account changes, and allowlisted Steam endpoints,
HTTP statuses, failure categories and bounded numeric result diagnostics. The
diagnostic checks reject raw response details, unknown categories and malformed
result values while preserving pending grant retries. They also cover preserving
exchange IDs after uncertain requests, confirmed-only balances and rewards,
serialized grant/exchange snapshots, recovery of a server pending exchange after
a restart, and deterministic preflight rejection.

These are deterministic transport tests. They do not prove that a real Steam
account owns the app, that the deployed Worker has valid secrets, or that a live
Steam Inventory grant succeeds; validate those through the running client.
