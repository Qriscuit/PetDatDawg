# Steam startup recovery checks

From the repository root:

```powershell
dotnet run --project Tests/SteamIntegrationHarness/SteamIntegrationHarness.csproj
```

This isolated harness compiles the production Steam integration with a fake SDK.
It verifies that launching before Steam starts recovers automatically, retries
wait five seconds after failed initialization or exceptions, successful sessions
are not reinitialized, and explicitly disabling Steam prevents initialization.
It does not load Godot or the native Steam SDK, observe desktop input, contact a
backend, or grant items. It sets `PDD_DISABLE_GLOBAL_INPUT=1` and restores its
process environment when finished.
