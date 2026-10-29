# Thirty-second earning batches

Implementation prepared October 29, 2026. Local verification and the bounded
real-Steam transport preflight passed. The v1-preserving Worker batch candidate
was deployed at 06:00:47.739 UTC, followed by successful production replay and
v1 Shop/inventory checks. Cloudflare remains
the backend; billing, Steam item definitions and economy publication are unchanged.

## Player behavior

Each observed press still queues one earning unit in memory and gives immediate
dog feedback. A nonempty queue synchronizes in batches on a 30-second background
interval. The visible Pets balance always comes from confirmed Steam inventory.
There is no locally calculated or persisted currency balance.

Opening or returning to the Shop, manually refreshing it, or attempting a
purchase starts foreground synchronization immediately. It captures the inputs
already queued, waits for an in-flight batch, and settles that fixed prefix.
Inputs arriving afterward remain for later synchronization, so continuous input
cannot extend a checkout indefinitely. Buy remains reachable when the displayed
balance is low; the server checks fresh Steam inventory before spending.

The earning allowance still applies to foreground synchronization. A rate limit,
outage or uncertain grant leaves the frozen batch queued and prevents a new
purchase from being sent. A large backlog can therefore delay checkout until
the allowance recovers. “Immediate” describes when synchronization starts, not
a bypass of earning limits or a guarantee about Steam latency.

Only foreground synchronization and transactions make Shop controls busy.
Background batches do not flash or disable purchase buttons. Pending counts
remain internal. Unsent inputs are abandoned when the client closes, as before;
batching can leave up to an additional interval of inputs awaiting confirmation.

## Grant protocol

Authenticated `POST /v1/pets/grant-batch` accepts:

```json
{
  "clientEventId": "7fb84fd1-2521-449a-ae71-1eaa7ac66192",
  "runId": "4859ecf9-08ec-4d30-94e5-458c6cb6fbe1",
  "firstSequence": 1,
  "count": 70
}
```

The batch ID, run ID, first sequence and count are immutable after the first
attempt. Sequences start at one for the current in-memory client run and advance
only after confirmation. The server accepts contiguous new ranges and rejects
overlaps, gaps and altered retries. IDs and run records are scoped to the
authenticated Steam account. They are retry protection, not proof of physical
input; a modified client can fabricate claims within its account allowance.

The maximum batch contains 300 Pets. Authentication advertises a smaller maximum
when the configured per-minute allowance is lower. Both legacy single grants and
batches consume the same account-wide fixed-minute allowance, defaulting to 300
Pets per minute. Admission reserves the full batch quantity once. Retries,
reauthentication, concurrent runs and Worker restarts do not replenish it.

Preparation atomically persists the operation, run high-water mark and shared
rate record before contacting Steam. Completion persists separately. This is
four application key-value writes per successful new batch, independent of the
number of inputs. An inventory read confirms the resulting balance. These are
application-operation counts; actual Cloudflare billed rows must be measured.

The Steam call uses the documented `itemdefid[i]` list containing Pets itemdef
100, plus a stable nonzero 64-bit request ID derived from the account and batch
ID. A lost reply, failed completion write or inventory-read failure reuses that
same request ID. An uncertain batch is never split, enlarged, renamed or sent
through the legacy single-grant endpoint. A new client reports an unavailable
batch endpoint rather than converting an uncertain batch into different grants.

Server `Retry-After` instructions delay both background and foreground grant
attempts. Other transient grant failures use bounded exponential backoff with
jitter. Same-account authentication renewal preserves the frozen batch; changing
Steam accounts clears the previous account's queue and run state.

## Purchase safety

Foreground synchronization completes before creating the purchase intent. Grant
and purchase IDs are separate. The server then reads fresh inventory, freezes
the offer, price and material item IDs, and persists the purchase state before
calling Steam. Repeated clicks cannot enqueue a second purchase while the first
is reserved or awaiting confirmation.

Cancellation before a purchase POST clears that unsent reservation so background
recovery cannot later spend it. Cancellation after a POST may follow a real
Steam mutation, so its operation ID and recovery state remain intact. The same
distinction applies to legacy box purchases and openings.

Steam `AddItem` supports an idempotent request ID; `ExchangeItem` has no documented
equivalent. The existing exchange rules remain: recover the saved receipt or
safe permit/cleanup phase, but never repeat an uncertain exchange. This can leave
a transaction awaiting investigation instead of risking a second charge. Grant
batches do not acquire a permanent active Shop lock, so a prior client's
abandoned grant cannot permanently block that account.

## Rollout and verification

Deploy the compatible Worker before distributing a client that requires the
batch endpoint. The local economy is `golden-bones-v2`, while the last verified
production release preserves `golden-bones-v1`. Do not deploy the staged economy
as a side effect of this optimization. A v1-preserving deployment or the
separately coordinated economy release is required.

Steam documents item-definition arrays and request-ID replay, but does not state
an array-size limit or explicitly guarantee repeated identical definitions.
Before enabling production batching, finish bounded repeated-item grants,
stacking and same-ID replay against Steam, including the intended maximum batch.
Fake-Steam tests do not establish those production limits. No denomination bundle
definitions are added by this change.
[Steam AddItem reference](https://partner.steamgames.com/doc/webapi/IInventoryService#AddItem)

Run the backend client harness, Worker unit tests and actual workerd/SQLite
runtime smokes with fake Steam. The Godot inventory smoke also verifies that
opening Shop settles queued earnings and leaves check-and-buy accessible with
an insufficient displayed balance. Keep global input, tray and real Steam
disabled during client smokes.

The live smoke runner rejects its obsolete `RetryEventId` option before any
connection. Individual input IDs are no longer batch operation IDs; re-enqueueing
one could create a new grant. The scene also rejects the corresponding
environment variables. The preserved v1 runner is only for original v1 requests.

Validation on October 29, 2026:

- Worker unit suite: 73 tests passed, including persistence-boundary failures,
  exact replay, quantity limits, and a 300-definition Steam request.
- Both actual workerd/SQLite runtime smokes passed with fake Steam, including
  concurrent duplicate 300-input batches followed by a purchase.
- The exact v1-preserving deployment candidate also passed its SQLite runtime
  verification. Its SHA-256 is
  `664d37f87605b8da84aff8abf52c86f65e4e41ce93005faa82839083e37cfba8`.
- C# HTTP harness: 41 scenarios passed, including cancellation before and after
  transmission, same-account reauthentication and input arriving during checkout.
- C# to local Wrangler to SQLite to fake Steam: 23 checks passed, including
  settling 70 Pets before a purchase and five on Shop opening.
- Godot 4.6.3 inventory/Shop smoke: 562 assertions passed with real Steam, tray
  and global input disabled. Client build passed without warnings or errors.
- Worker economy checks/deployment dry run and Git whitespace checks passed.
  The obsolete live replay option was verified to fail before any connection.

These local checks performed no live purchases, billing changes or Steam economy
publication. The separate real-Steam preflight below has already reached Steam;
do not describe the entire verification effort as having made no live grants.

### Verified real-Steam transport preflight, October 29

The authorized live test added exactly 302 Pets in two immutable batches (two
and 300), with same-ID replay and fresh inventory checks. SQLite Durable Object
previews were unavailable, so the first phase uses a private remote Steam
transport probe with an exact account/payload allowlist. This verifies Steam
transport behavior, not the complete deployed Durable Object batch protocol.
The latter remains a separate production replay/conflict-verification step.
See [the controlled live runner](../Pet_Da_Dog_CSharp/Tests/LIVE_STEAM_BATCH_SMOKE.md).

The initial two-Pet request reached Steam: fresh inventory increased from 148 to
150. Its receipt exposed a parser assumption. A same-ID replay returned HTTP 200,
`x-eresult: 1`, `replayed: true`, and two receipt rows for one stacked item ID;
both rows described Pets itemdef 100 with the current stack quantity of 150.
The original parser rejected the repeated item ID as `invalid_item_data`, even
though Steam had accepted the grant. Sanitized evidence is in
`Pet_Da_Dog_CSharp/.godot/live-batch-20261029/run-20261029-015602/`.

The candidate now permits repeated stack snapshots only while parsing an
`AddItem` receipt. Every row still passes the normal item, quantity and state
validation. Receipt rows are never summed or treated as fresh grant deltas;
separate `GetInventory` remains the balance authority. Inventory reads and
exchange receipts retain strict duplicate-ID rejection. A replay may describe
a stack that was subsequently spent, so it cannot establish an original delta.

After the parser correction, resuming the same plan passed **65 assertions**:
the two-Pet batch confirmed 148 → 150 through its original request ID; the
300-Pet batch then confirmed 150 → 450. Replaying each original batch left the
balance unchanged. Fresh inventory confirmed the exact 302-Pet total increase.
The initial native inventory baseline was also verified. Logs are in
`Pet_Da_Dog_CSharp/.godot/live-batch-20261029/run-20261029-015806/`.

No Shop reads, purchases, recycling or acknowledgments ran in this preflight.
Altered-payload and overlap checks were deferred to the complete Worker because
the private transport probe only accepts its fixed allowlist. The subsequent
production results are recorded below. Preserve the plan for safe same-ID
verification; never replace the first batch ID
merely because its initial successful Steam response was rejected by the parser.

### Production Worker deployment, October 29

At **06:00:47.739 UTC**, the exact v1-preserving candidate was deployed:

| Release field | Value |
| --- | --- |
| Worker version | `04fa0561-39fc-458d-b1d8-157f4f461365` |
| Deployment ID | `c5296edc-52e0-450f-9062-1f035338bcf3` |
| Bundle SHA-256 | `664d37f87605b8da84aff8abf52c86f65e4e41ce93005faa82839083e37cfba8` |
| Economy | `golden-bones-v1` |

The deployed Shop configuration and legacy reward bounds match the previous
live v1 release. Bindings, SQLite namespace, account settings, workers.dev
subdomain and a known v1 icon hash remained unchanged. No billing API writes or
Steam economy publication occurred. `/health` returned HTTP 200 at 06:01:24 UTC.

At **06:02:02 UTC**, production replay passed **54 assertions**.
Both original batch IDs (two and 300) returned `replayed: true`, added **zero
additional Pets**, and left fresh inventory at **450 Pets**. Altered payloads
and overlapping sequence probes were rejected for both batches. Logs are in
`Pet_Da_Dog_CSharp/.godot/live-batch-20261029/run-20261029-020053/`.

The preserved v1 zero-grant verifier then passed **44 assertions** at
**06:02:19 UTC**. Authentication and repeated fresh inventory reads confirmed
450 Pets with zero pending grants. Shop readback remained `golden-bones-v1`,
139 offers (136 direct cosmetics), pools 20/63/53, and **5 Golden Bones**;
Shop was enabled while cash, Market and developer controls remained disabled.
The initial native inventory diagnostic also returned 450 Pets; this Worker-only
run does not establish all independent native comparisons. It requested zero
grants and made no new purchases, recycling or acknowledgments. Logs are in
`Pet_Da_Dog_CSharp/.godot/windows-export/20261019-224859-d1b261cc/project/.godot/live-steam-smoke-20261029-020209/`.

The temporary private transport preview was stopped, and its local proxy port
8791 was no longer listening after testing.

The new client code builds successfully, but **an updated Steam client build has
not been distributed**. Existing installations continue their legacy per-input
requests until they receive the new build. Deploying the Worker alone does not
give those clients the 30-second batching benefit.

The two-write/outside-Durable-Object cost model in
[BACKEND_CAPACITY_PLAN.md](BACKEND_CAPACITY_PLAN.md) remains a future optimization.
This implementation uses four writes and retains Steam I/O inside the existing
serialized Durable Object. It makes no measured concurrency or monthly-cost
guarantee. Operation and run records are retained to preserve replay safety;
bounded retention requires a separately designed replay-expiry protocol.
