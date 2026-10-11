# Live Steam and Cloudflare Worker validation

This opt-in test uses the production Steam integration and backend client. It
authenticates AppID **4817200**, reads the configured HTTPS Cloudflare Worker,
checks three fresh inventory responses, and independently reads the same
account's inventory through Steamworks `GetAllItems` and its result callback.
It sums Pet itemdef **100** and compares that result with the Worker total.
Optional `-UseDogInput` instantiates the real dog scene and reuses that scene's
Steam connection and backend client.

Steam must be running and signed in to an account that owns the app. Close other
Pet Da Dog clients and avoid purchases, grants, or inventory mutations while the
test runs, because it verifies exact totals.

From the repository root, the default check performs **zero grants**:

```powershell
& '.\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamSmoke.ps1' -AllowLiveSteam
```

To explicitly test three real Pet grants:

```powershell
& '.\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamSmoke.ps1' -AllowLiveSteam -GrantCount 3 -UseDogInput
```

`-GrantCount` permits 0 through 3 and defaults to 0. Each requested grant creates
one GUID through the production in-memory queue, waits for confirmation, verifies
an increase of exactly one, and verifies that a fresh Worker inventory read
retains it. Final native Steam inventory must match the Worker total. With
`-UseDogInput`, each grant calls the real `DesktopPet._Input` with a pressed
left-click positioned over an opaque texture pixel; the test also verifies that
positions outside the sprite and mouse release cannot grant Pets. The separate
offline inventory smoke checks an actual alpha-zero pixel in the dog texture.
Actual Windows pointer delivery and click passthrough still require a separate
desktop interaction check.

If native Steam inventory access is unavailable, an explicit Worker-only run can
validate the production click → Worker → Steam path independently of that SDK
availability problem:

```powershell
& '.\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamSmoke.ps1' -AllowLiveSteam -WorkerOnly -GrantCount 3 -UseDogInput
```

`-WorkerOnly` still requires real Steam authentication and uses the confirmed
totals and item stacks returned by the trusted Worker after its Steam Inventory
requests. It checks three initial inventory reads, each exact grant increment
and its fresh inventory response, and three final reads with matching Pet stack
sums. It does not invent or locally save a balance. The initial native SDK read
is diagnostic and limited to ten seconds in this mode; required native
comparisons are skipped. Its separate success marker is
`LIVE_WORKER_SMOKE_PASS` with `nativeVerified=false`. This proves the Worker path
and dog input handler, **not independent native inventory verification**.
The default run continues to require all native comparisons and emits only
`LIVE_STEAM_SMOKE_PASS` on success.

The runner defaults to the known-good Godot 4.6.3 Mono console runtime, builds the
client, and stores standard output/error in `.godot/live-steam-smoke-*`. Use
`-SkipBuild` after building, `-BackendUrl` to override the configured Worker,
`-TimeoutSeconds` (default 150), or `-LogDirectory` as needed. A successful run
prints `LIVE_STEAM_SMOKE_PASS` with the initial/final totals and grant count.
It never logs tickets or session credentials. The runner isolates test process
settings from the user's appearance preferences and restores its environment
when finished. Use the runner for input mode: directly launching the test scene
does not provide that isolation and can change local dog preferences.

The scene itself requires `PDD_LIVE_STEAM_SMOKE=1` and `PDD_STEAM_APP_ID=4817200`.
`PDD_LIVE_SMOKE_GRANTS` defaults to 0, and the runner sets it from `-GrantCount`.
Native Steam inventory can be cached when requested frequently, so a mismatching
SDK snapshot is retried every ten seconds until convergence or the timeout.
Handles and callbacks are disposed after success or failure.
If the initial native read fails, the test prints its safe result code and still
attempts Worker authentication so the two services can be diagnosed separately.
Subsequent native comparisons remain required for the default test to pass.

If a grant test times out, a pending grant may already have reached Steam. Read
inventory with the default zero-grant check before requesting another grant run.
This test never decreases or resets a Steam inventory total.

To recover a timed-out request, use its exact `clientEventId` from
`LIVE_PET_REQUESTED` and the total that its original click was expected to reach:

```powershell
& '.\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamSmoke.ps1' -AllowLiveSteam -WorkerOnly -GrantCount 1 -RetryEventId '<original-request-guid>' -RetryExpectedPets 1
```

This mode requires an explicit positive expected total, `-WorkerOnly` and
`-GrantCount 1`; it cannot be combined with `-UseDogInput`. It enqueues the supplied
GUID through the production queue without generating a new GUID or claiming a
new dog click. The expected value is only a test assertion: confirmed totals
still come exclusively from Worker/Steam responses. Initial inventory must be
either the original pre-grant total or the original expected total, allowing a
Steam grant that succeeded before a lost response to replay safely. The final
confirmed total must equal the original expected value and survive three fresh
inventory reads with matching Pet stack sums. Success emits
`LIVE_WORKER_RETRY_PASS` with `input=retry-existing` and `nativeVerified=false`.
Use the recorded ID; inventing another ID would request another real grant.

Native inventory query behavior is documented in the
[Steamworks ISteamInventory reference](https://partner.steamgames.com/doc/api/ISteamInventory).
