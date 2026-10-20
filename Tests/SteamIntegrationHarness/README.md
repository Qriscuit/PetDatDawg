# Steam startup recovery and commerce checks

From the repository root:

```powershell
dotnet run --project Tests/SteamIntegrationHarness/SteamIntegrationHarness.csproj
```

This isolated harness compiles the production Steam integration with a fake SDK.
It verifies that launching before Steam starts recovers automatically, retries
wait five seconds after failed initialization or exceptions, successful sessions
are not reinitialized, and explicitly disabling Steam prevents initialization.
Commerce checks use fake Steam callbacks to verify gated pack checkout, real
Steam price lookup, hundredths-based currency formatting, duplicate-click
protection, checkout cancellation/return refresh, delayed inventory updates,
and Steam Market/browser routing. Starting checkout never grants currency or
claims payment completion. Neither native checkout nor an external browser is
opened by these tests.
It does not load Godot or the native Steam SDK, observe desktop input, contact a
backend, or grant items. It sets `PDD_DISABLE_GLOBAL_INPUT=1` and restores its
process environment when finished.
