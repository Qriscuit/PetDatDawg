# Steam Pet Persistence Smoke Test

This procedure validates one real Steam Inventory grant for Pet Da Dog AppID `4817200`. It is intentionally limited to partner test accounts and a test-only Inventory item. Do not use a production currency item or a personal player account for this procedure.

## Steamworks readiness

Before starting, verify in Steamworks that all of the following are true:

- Steam Inventory Service is enabled for AppID `4817200`.
- The tester is a Steamworks partner/test account with access to the app and private Inventory items.
- An Economy-permission publisher key is available to the operator. Keep it out of source control and shell history.
- A `Pets_Test` item definition exists. It must be private, non-tradable, and non-marketable. Record its numeric itemdef ID.
- The tester understands that each successful run permanently adds one `Pets_Test` item to that test account.

## Automated backend tests

Run these before the live test. They use an in-memory Steam gateway and do not contact Steam or create Inventory items.

```powershell
dotnet test S:\CodexProjects\PetDaDog\PetDaDog.Backend.Tests\PetDaDog.Backend.Tests.csproj --nologo
```

The suite verifies authentication, one grant, idempotent replay, invalid requests, and Steam gateway failures.

## Local HTTPS configuration

Trust the .NET development certificate once on the test machine, then open a fresh PowerShell window. This writes no secrets into the repository.

```powershell
dotnet dev-certs https --trust
```

Set the placeholders below only in the active shell. Generate a new 32-byte-or-longer session secret. Do not paste a publisher key into a tracked file.

```powershell
$env:PDD_STEAM_APP_ID = '4817200'
$env:PDD_STEAM_PUBLISHER_KEY = '<Economy publisher key>'
$env:PDD_STEAM_PETS_ITEMDEF_ID = '<Pets_Test itemdef ID>'
$env:PDD_BACKEND_SESSION_SECRET = '<at least 32 UTF-8 bytes>'
$env:ASPNETCORE_URLS = 'https://localhost:55608'
$env:PDD_BACKEND_URL = 'https://localhost:55608'
$env:PDD_PERSISTENCE_TEST_REPORT_PATH = "$env:LOCALAPPDATA\PetDaDog\persistence-report.json"
$env:PDD_PERSISTENCE_TEST_RESET = '1'
```

Start the backend in that shell and leave it running:

```powershell
dotnet run --project S:\CodexProjects\PetDaDog\PetDaDog.Backend\PetDaDog.Backend.csproj --no-launch-profile
```

## Live test

1. Run the Godot client from the same environment, signed into the partner test account. The checked-in development AppID file is `4817200`; the `PDD_STEAM_APP_ID` value must match it.
2. Open the status window and wait for Steam initialization, backend authentication, and a confirmed pets baseline `N`.
3. Click one visible dog pixel exactly once. Wait for the client to show `Pets synced: N + 1` and no pending grant.
4. Close the client. In the same shell, remove only the reset flag, then relaunch the client:

```powershell
Remove-Item Env:PDD_PERSISTENCE_TEST_RESET
```

5. Wait for the initial backend refresh. The status window must show `N + 1` before any new click.
6. Inspect the report at `%LOCALAPPDATA%\PetDaDog\persistence-report.json`. A successful run has `"phase": "Passed"`, the initial baseline, expected total, redacted Steam identity, and timestamps. The adjacent `.state` file is only test-harness state and never a source of truth for pets.

## Expected failure behavior

- If Steam, the publisher key, Inventory Service, or the test item definition is unavailable, the client keeps the grant pending and the report does not pass.
- If the AppID environment variable differs from `steam_appid.txt`, Steam initialization stops with a clear configuration error instead of silently authenticating the wrong app.
- A duplicated `clientEventId` is covered by the automated fake-gateway test; the manual client click always creates a new event ID.
