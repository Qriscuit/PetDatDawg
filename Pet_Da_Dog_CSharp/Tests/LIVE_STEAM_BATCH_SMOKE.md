# Controlled live Steam batch verification

`Run-LiveSteamBatchSmoke.ps1` is an explicitly authorized real-Steam test. Its
intended new currency is exactly **302 Pets**: a batch of two followed by a batch
of 300 in a separate minute budget. It replays the same IDs to verify
idempotency. Unexpected server behavior can fail this assertion; stop and
investigate rather than creating replacement IDs.

The scene uses `SteamIntegration` and its own HTTP client. It never loads the
desktop pet, global input monitor, `BackendPetClient.Tick`, or Shop. Its API
requests are limited to Steam authentication, inventory reads, and the batch
endpoint. It makes no purchases, recycling requests, receipt acknowledgments,
or Shop GET requests that might resume a previous purchase. An existing pending
exchange stops the test. Close all other pet clients before running it.

## Prepare the immutable payloads

Choose one ignored plan directory and retain it through preflight, deployment,
and any retries. Preparation performs no network or Steam operations:

```powershell
$batchPlan = 'S:\CodexProjects\PetDaDog\Pet_Da_Dog_CSharp\.godot\live-batch-20261029'
& '.\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamBatchSmoke.ps1' -PrepareOnly `
    -BackendUrl 'http://127.0.0.1:8791/' -PlanDirectory $batchPlan
```

`prepared-batches.json` contains the two original batch payloads and fixed
overlap probe IDs. Repeating `-PrepareOnly` preserves an existing prepared file.
Before the first grant, the scene atomically saves `batch-plan.json` with these
payloads, the original inventory baseline, a SHA-256 account fingerprint, and
progress. Tickets, session tokens, publisher keys, and raw account IDs are not
saved in this plan.

## Private transport preflight

When a private Wrangler remote proxy exposes a server-side Steam transport
probe with an exact payload/account allowlist, explicitly allow its loopback
HTTP address. The publisher key remains on the remote Worker:

```powershell
& '.\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamBatchSmoke.ps1' -AllowLiveSteam `
    -BackendUrl 'http://127.0.0.1:8791/' -PlanDirectory $batchPlan `
    -AllowLoopbackProxy -TransportProbe -RequireNativeBaseline
```

`-TransportProbe` verifies actual Steam grants, duplicate replays, and fresh
inventory totals. It skips malformed/overlap probes because a strict transport
allowlist is not the complete production batch service. `-RequireNativeBaseline`
requires the native Steam inventory to agree with the Worker before any grants.
Without it, the native read remains an optional diagnostic.

### Observed Steam receipt behavior and current checkpoint

The October 29, 2026 preflight's initial two-Pet request reached Steam and fresh
inventory changed from 148 to 150. A replay of the same request returned HTTP
200 and `x-eresult: 1`, with `replayed: true` and two rows sharing one stacked
item ID. Both rows contained itemdef 100 and the current stack quantity 150.
The original strict duplicate-ID parser rejected that valid receipt.

The candidate permits repeated item IDs only for `AddItem` receipt snapshots,
while validating every row. It does not sum receipt quantities, choose a local
balance from them, or infer a fresh delta from replayed stack state. Separate
fresh inventory reads establish the confirmed total; ordinary inventory and
exchange receipts still reject duplicate IDs. Sanitized receipt-shape evidence
is saved in `.godot/live-batch-20261029/run-20261029-015602/`.

After the parser correction, the same saved plan passed **65 assertions** in
`.godot/live-batch-20261029/run-20261029-015806/`. The original two-Pet batch
confirmed 148 → 150 through same-ID replay; the 300-Pet batch confirmed 150 →
450. Duplicate calls for both batches added zero Pets, and fresh inventory
confirmed the exact 302-Pet increase. No Shop reads, purchases, recycling or
acknowledgments ran. Native Steam matched the initial baseline.

The v1-preserving Worker candidate was deployed at October 29 06:00:47.739 UTC
as version `04fa0561-39fc-458d-b1d8-157f4f461365`. Production verification then
passed **54 assertions** at 06:02:02 UTC in
`.godot/live-batch-20261029/run-20261029-020053/`:
both original batches replayed with zero additional Pets, their altered-payload
and overlap probes were rejected, and fresh inventory remained 450. A subsequent
44-assertion zero-grant v1 Shop/inventory check passed at 06:02:19 UTC, preserving 5 Golden
Bones and the v1 catalog. The private transport preview was stopped afterward.
Retain the completed plan for future same-ID verification. Do not prepare
replacement IDs or treat the original HTTP 503 as proof
that Steam did not grant its two Pets.

The new client code builds, but no updated Steam client distribution has been
published. Existing installed builds need an update before they use 30-second
batching; deploying the compatible Worker alone does not change their cadence.

The second batch starts in the next UTC minute. An actual HTTP 429 causes a
bounded wait using `Retry-After` and an unchanged payload. Other unsuccessful
or uncertain responses stop the run. Output includes only fixed error labels,
bounded numeric diagnostics, and sanitized receipt shape when supplied.

## Resume after any interruption

Use the **same plan directory and endpoint**, adding `-Resume`:

```powershell
& '.\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamBatchSmoke.ps1' -AllowLiveSteam `
    -BackendUrl 'http://127.0.0.1:8791/' -PlanDirectory $batchPlan `
    -AllowLoopbackProxy -TransportProbe -Resume -SkipBuild
```

A saved attempt may already have reached Steam. The test first reads inventory,
then reuses exactly its original batch ID, run ID, sequence and count. It never
substitutes a new request because a response was lost. Successful phases remain
recorded. Use `-SkipBuild` only after building the current test code.

## Verify production without intending additional grants

After successful preflight and deployment of the complete Worker, retain the
same plan and explicitly select deployment verification:

```powershell
& '.\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamBatchSmoke.ps1' -AllowLiveSteam `
    -BackendUrl 'https://petdadawg.americlasagna.workers.dev/' `
    -PlanDirectory $batchPlan -Resume -VerifyDeployment -SkipBuild
```

This requires the completed preflight's final inventory. Both original payloads
must replay with `replayed=true` and add zero Pets. The production Worker must
also reject altered counts for an existing ID and new IDs overlapping an
accepted sequence range, with unchanged fresh inventory. The small batch runs
first; any unexpected additional currency stops verification before the large
batch. The production endpoint is recorded before the first replay, and a later
resume cannot silently switch that deployment attempt to another URL.

Success markers are `LIVE_BATCH_SMOKE_PASS` and
`LIVE_BATCH_DEPLOYMENT_PASS`. Each run writes stdout/stderr under the plan
directory. Preserve the plan and logs for incident review.
