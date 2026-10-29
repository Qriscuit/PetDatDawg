# Low-cost backend capacity and purchase synchronization

Updated October 29, 2026, America/New_York. These are proposed architectures
and workload models, not deployed behavior or measured capacity guarantees.

The owner has now selected 30-second batching with immediate Shop synchronization
on Cloudflare. The local implementation is described in
[GRANT_BATCHING.md](GRANT_BATCHING.md). It uses four application writes per batch
and keeps Steam I/O inside the Durable Object; the two-write/outside-DO estimates
below are future optimizations and must not be quoted as its measured costs.

## Current requirements and recommendation

On October 29 the owner rejected five-minute background confirmation as too
slow and accepted a recurring hosting cost if it is minimized. This supersedes
the earlier strict-$0 requirement. No particular subscription or migration was
selected. Exact input earning, Steam authority, current-run pending queues,
injected/remote input support and server earning limits remain requirements.

The recommended starting point is the existing Cloudflare architecture with
**30-second nonempty earning batches and immediate foreground synchronization
when Shop opens or the player attempts a purchase**. Sixty seconds is a cheaper
background alternative to evaluate. The 30-second path is implemented locally;
production rollout and real-Steam batch-size validation are separate.
Workers Paid removes the exhausted Free daily allowance and starts at $5/month,
but is not a $5 spending cap. Optimize request/storage behavior before assuming
the base allowance will cover production traffic.

Oracle was a candidate specifically for the previous $0-only constraint. A VM
is an always-on server computer that runs the API and database. It replaces
metered per-request/per-row billing with a fixed machine allowance or rental,
while adding patching, backups, capacity and recovery work. It is not needed
for batching or correct affordability checks. At sustained larger traffic, a
fixed-price VM may be cheaper and should be benchmarked before migration.

## Responsive purchases without per-input backend work

The server reads fresh Steam inventory and selects actual
materials before purchasing. The implemented batching integration settles
eligible queued earnings before that affordability check. Faster inventory
polling alone cannot reveal inputs that have not yet been granted by Steam.

Example: Steam currently holds 460 Pets, and the client has 70 pending inputs.
For a 500-Pets Dig Box, the purchase flow is:

1. Capture a fixed prefix of this run's pending inputs. New presses go into
   the next batch. If background work owns the batch, await that same operation.
2. Validate account identity, quantity, sequence, immutable retry contents and
   server-time earning allowance. Persist its identity before Steam; use the
   same AddItem request ID on uncertain retries. Foreground synchronization
   must not bypass the earning limit.
3. After Steam confirms the grant, read inventory: if all 70 were eligible,
   the account now has 530 Pets. This is a Steam-confirmed result, not a client
   addition to the displayed balance.
4. Check the server price, actual materials, Dig cooldown and unresolved
   operation lock. Save a separate purchase identity and frozen materials;
   Steam exchanges 500 Pets for the reward. Only a confirmed result is shown.

If grant confirmation fails, leave the batch safely retryable. If the purchase
exchange is uncertain, retain its lock and never repeat ExchangeItem. Grant
and purchase IDs remain separate; replaying a purchase cannot silently create
a new grant batch or a second spend.

The previous UI disabled Buy using the last ConfirmedPets value. The updated
check-and-buy path stays reachable when the displayed balance is insufficient;
the visible number still shows only confirmed inventory. Only foreground Shop
synchronization and transactions show a busy state. Background batches do not
flash or disable Shop controls.

Use a dedicated serialized foreground orchestration path. Marking a purchase
pending and then waiting for the ordinary grant Tick would prevent the grant
from running, because that loop correctly prioritizes pending exchanges.

These checks bound earning and prevent duplicate transactions. They cannot
prove that an owner-controlled client really observed the reported presses.
Per-input database writes do not solve that trust boundary.

## Cost reductions and paid Cloudflare model

Batch inputs, combine earning/sequence/retry metadata in one compact state
record, avoid empty polling, coalesce inventory reads and honor Retry-After.
Limit repetitive success logs while retaining error diagnostics and economic
receipts. Keep key protection, earning limits, idempotency and purchase locks.

A further optimization uses the Durable Object only to prepare and finalize
the durable operation, while the ordinary Worker performs Steam HTTP calls.
Ordinary Workers do not charge for network-wait duration; Durable Objects do.
Finalization must be internal and authenticated, and exchanges must retain
their durable uncertainty state throughout the external request.

Illustrative monthly earning-path costs for that optimized design:

| Average active players | 30-second batching | 60-second batching |
| ---: | ---: | ---: |
| 100 | about $8 | about $6 |
| 1,000 | about $185 | about $68 |

Assumptions: 30 days, every active player has a nonempty batch each interval,
one Worker request, two written rows and two DO calls per batch; 5 ms Worker
CPU and 20 ms total DO duration per batch. These are unmeasured timing and
row-accounting assumptions. Excludes Shop, authentication, retries, logs and
storage beyond 5 GB. At 100 ms DO duration, the 1,000-player scenarios rise
by roughly $12.50/month. Actual billed rows and latency need measurement.

The model includes the $5 base, 10 million Worker requests and 30 million CPU
milliseconds, 1 million DO requests and 400,000 GB-seconds, and 50 million
written rows. At 1,000 average active players and 30 seconds, 172.8 million
written rows/month alone imply about $123 storage overage. The same 100-player
workload with two seconds of Steam waiting inside each DO would be about
$31/month at 30 seconds or $18 at 60 seconds, before the excluded work.
[Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/),
[DO pricing](https://developers.cloudflare.com/durable-objects/platform/pricing/),
[logging controls](https://developers.cloudflare.com/workers/observability/logs/workers-logs/).

Average active players are not total registrations or peak concurrency:
1,000 people using the pet for 2.4 hours/day average 100 active players. Short
peaks still need throughput testing. Steam publisher throughput is an
independent constraint regardless of hosting.

First implement/test common batching and foreground synchronization with fake
Steam, measure actual latency and row usage, then choose the cheapest host
for that workload. A paid VM becomes worth comparing when sustained usage
makes metered hosting exceed the fixed rental plus backup and maintenance
costs. No billing, production code or Steam schema changes were made here.

## Historical October 28 zero-cost scenario

The sections below retain the earlier five-minute/$0-only analysis for
comparison. They are superseded by the current requirements above.

### Previous requirements

The owner selected **strictly $0 monthly hosting** and **up to five minutes
between background earning and confirmed balance updates**. Physical, remote,
accessibility and injected inputs remain eligible. Server earning limits stay.
The existing no-local-balance and current-run-only pending queue rules remain.
Five-minute confirmation is a healthy-service target; outages can exceed it.

Steam remains the currency and inventory authority. Publisher credentials,
account authentication, replay protection, Dig cooldowns and uncertain-purchase
records must remain on trusted servers. No paid subscription, account upgrade,
new deployment or Steam schema publication is authorized by this assessment.

### Earlier zero-cost recommendation

For the most capacity at zero hosting cost, the strongest researched candidate
is **one Oracle Always Free VM running one API service with persistent SQLite**.
Reuse the existing Worker business logic behind an HTTP/storage adapter.
Batch earning every five minutes, use per-account queues, and keep database
transactions short; never hold a global queue or SQL transaction across Steam
HTTP calls. Serve the API over HTTPS without a Free Worker on every request,
which would retain that Worker's daily request ceiling. Domain/TLS setup must
use an existing or otherwise free hostname within the $0 requirement.

Oracle's current documentation specifies 1,500 OCPU-hours and 9,000 GB-hours
monthly: **2 OCPUs and 12 GB RAM continuously**, not the older 4-core/24-GB
figure. It also lists 200 GB combined boot/block storage, five volume backups,
20 GB object storage and 10 TB monthly outbound data. Free capacity may be
unavailable, and idle compute may be reclaimed. These are material availability
tradeoffs, not a guaranteed production service level.
[Oracle Always Free resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm)

Oracle signup requires identity/card verification and can place temporary
authorization holds. Confirm eligibility and capacity without enabling paid
overages. Free-tier resources, backup storage and data transfer all need limits.
[Oracle FAQ](https://www.oracle.com/cloud/free/faq/)

The least disruptive fallback is **optimized Cloudflare Free for a limited
beta**. It preserves the existing deployment and storage. It cannot support
1,000 continuously active players under the five-minute/two-write design.
Do not implement both production hosts at once; validate the common batching
protocol first, then select one authority for each player's mutations.

The existing ASP.NET backend is not a replacement for the Worker. It lacks
the complete inventory/Shop API, ownership check, durable operation journal,
account earning budget and player serialization. Changing only the backend
URL would remove business protections and break client features.

### Earlier five-minute settlement design

1. Each observed input still contributes one pending unit in memory. Submit a
   frozen nonempty batch roughly every 300 seconds, with jitter that never
   extends the normal target beyond five minutes. Empty intervals make no
   request. Graceful-exit and foreground flushes add traffic; abrupt exit can
   lose more unsent inputs than today. No local authoritative balance is added.
2. Use a compact per-account record for the earning budget, run generation,
   sequence high-water mark and frozen in-flight grant. Commit before Steam;
   commit completion after a definitive result. Reuse the exact Steam request
   ID on uncertain retries. Keep two writes per batch as the first target.
   A new run must not automatically submit or retry another run's abandoned
   inputs: replaying an unsuccessful AddItem can execute it. Retain uncertain
   operation evidence and define safe retirement/reconciliation before moving
   to a new run generation, without blocking the account indefinitely.
3. Preserve a 300 Pets/minute server-time refill rate. Five-minute batches need
   room for up to 1,500 Pets, so an explicit 1,500-unit bucket increases burst
   capacity without increasing sustained refill. Start new accounts empty or
   with a deliberately bounded initial allowance, not a free 1,500. Restart,
   reauthentication and session takeover must not reset the durable budget.
   Also bind earning to a server-issued run lease/start time: the usual first
   full batch waits five minutes, and any earlier flush is bounded by server
   elapsed time. Reauthentication retains the lease. This prevents a new run
   immediately claiming an account budget refilled during offline time; it
   does not prove that inputs happened. Backlogged inputs can drain later.
4. Do not retain a 100-input flush threshold: at 300 inputs/minute it would
   still send every 20 seconds. A possible compact Steam representation is
   publisher-granted denomination bundles for 2, 4, 8, ... 1,024 Pets plus
   ordinary itemdef 100 for one Pet. An exact count up to 1,500 then needs at
   most 11 distinct itemdefs. All final rewards remain existing Pets itemdef
   100; no cosmetic IDs change. These bundles must have no client claim,
   crafting, promotional or purchase route.
5. Steam documents fixed-quantity bundles and their expansion, and AddItem
   documents itemdef lists. This combination is a design inference requiring
   bounded verification of stack quantities, receipts and retries, followed
   by separate coordinated schema publication. Do not invent an undocumented
   AddItem quantity parameter or assume an unlimited itemdef list.
6. Fetch confirmed inventory once per settlement initially. Further savings
   can acknowledge a definitive grant and reuse a timely confirmed inventory
   read, but the player-visible balance must still refresh within the agreed
   interval under normal operation. Never display last balance plus a local
   pending count. Shop authorization uses fresh Steam state.
7. Keep authentication, body-size and request-abuse limits before expensive
   Steam work. Account earning limits, sequence validation and frozen retries
   share the normal durable writes. Coarse aggregate diagnostics replace
   per-input history. Respect Retry-After and use bounded backoff with jitter.

Sources: [Steam schema](https://partner.steamgames.com/doc/features/inventory/schema),
[AddItem](https://partner.steamgames.com/doc/webapi/IInventoryService#AddItem).

### Five-minute capacity arithmetic

Use **average input-producing concurrent players over a full day**, not peak
connections or registered accounts. Equivalently, add up active player-hours.
An idle client must make no earning request. With one batch every five minutes,
one AddItem and one GetInventory per batch, and two state writes:

| Average active players | Batch requests/second | Batches/day | State writes/day | Steam calls/day |
| ---: | ---: | ---: | ---: | ---: |
| 100 | 0.33 | 28,800 | 57,600 | 57,600 |
| 1,000 | 3.33 | 288,000 | 576,000 | 576,000 |
| 10,000 | 33.33 | 2,880,000 | 5,760,000 | 5,760,000 |

These are arithmetic workload estimates, not load-test results. They exclude
auth, session renewals, Shop, startup/exit flushes, retries, abuse and cleanup.
If a size limit forces earlier flushes, use the shorter interval. Under steady
input, effective interval = min(timer, batch capacity / input rate).

Cloudflare Free allows 100,000 Worker requests/day, 100,000 Durable Object
requests/day, 100,000 SQLite rows written/day and 13,000 GB-seconds/day of
Durable Object duration. Two writes per five-minute batch yields a row-only
ceiling of about **173 average active players**, with no headroom. Reserving
20% for other work lowers that planning bound to about **139**. Billed rows
must be measured; application put counts are not a complete billing audit.
[Worker limits](https://developers.cloudflare.com/workers/platform/pricing/),
[Durable Object limits](https://developers.cloudflare.com/durable-objects/platform/pricing/).

Duration can bind sooner: the current player Durable Object waits for Steam
and is billed for that wall time at 0.128 GB per object. With a five-second
billable duration per batch, 100 average active players consume 18,432 GB-s/day,
already above Free. A two-second assumption consumes 7,372.8 GB-s/day. These
latencies are scenarios, not measurements. A redesign can keep a Durable Object
only for short prepare/finalize calls and perform Steam I/O in the ordinary
Worker, trading two DO requests per batch for much less duration. Finalization
must be internal and authenticated, with persistent locks protecting exchanges.

For the VM, 10,000 average active players would mean about 33 settlement
requests/second and 67 Steam calls/second before other work. That is a useful
test target, not evidence that a free VM or Steam will sustain it. Measure CPU,
disk commit latency, network traffic, memory, queue depth and p95/p99 response
time with fake Steam and injected delays. Do not load-test real Steam blindly.

Steam's public Web API terms mention 100,000 calls/day; the publisher overview
does not establish a numeric successful Inventory allowance. Do not assume
either that public limit is the proven publisher ceiling or that publisher
access is unlimited. Confirm the app's expected volume with Valve before
promising large concurrency. Different hosting cannot remove this dependency.
[Steam API terms](https://steamcommunity.com/dev/apiterms),
[publisher overview](https://partner.steamgames.com/doc/webapi_overview).

### Zero-cost alternatives and tradeoffs

- **Cloudflare D1:** has the same Free daily row-write allowance, so moving
  the same per-input writes to D1 alone does not solve the capacity problem.
  [D1 pricing](https://developers.cloudflare.com/d1/platform/pricing/)
- **Google Compute Free:** includes an eligible e2-micro VM, 30 GB standard
  persistent disk and 1 GB monthly outbound data. It is a less attractive
  high-traffic fit for this service and requires careful billing configuration.
  [Google Free Tier](https://docs.cloud.google.com/free/docs/free-cloud-features)
- **Render Free:** local files are ephemeral and its free Postgres expires
  after 30 days. It is unsuitable as the persistent operation journal without
  adding another storage service. [Render limits](https://render.com/docs/free)
- **Existing home PC:** may avoid a new hosting invoice, but electricity,
  connectivity, uptime and maintenance remain costs. Do not assume existing
  always-on hardware or a free suitable public HTTPS endpoint.
- **Steam-native playtime rewards:** could eliminate most custom earning
  requests. It would change one-Pet-per-input into time-based rewards and needs
  an explicit product decision. It is not the default recommendation under
  the current invariants. [Steam TriggerItemDrop](https://partner.steamgames.com/doc/api/ISteamInventory#TriggerItemDrop)

Safe simplifications are one region, one API process, SQLite, delayed background
settlement, cached static catalogs, sampled logs and no empty polling. Keep
durable commits, publisher-key protection, account budgets, idempotency, Shop
locks and backups. They are small compared with per-input network/storage work.

### Validation and migration safeguards

First implement and test the transport-independent batch state machine with
fake Steam: duplicates, altered payloads, rate limits, concurrent sessions,
every crash boundary, delayed grants and uncertain exchanges. Validate bundle
behavior separately before schema publication. Measure the proposed workload
at 100, 1,000 and 10,000 virtual active players using delayed fake responses.

For a VM, use a persistent disk and durable SQLite commits. Take consistent
backups using SQLite's backup mechanism; do not copy a live WAL database file
alone. A restored backup can omit an exchange already executed by Steam, a
cooldown already started, or consumed earning budget and grant sequence state.
Freeze all mutations after restore, reject old mutation sessions, and reconcile
earning and Shop state or establish a conservative fenced recovery before
reopening writes. Revoking sessions alone does not recover missing history.
Steam authority does not make operation-journal loss harmless.

Migration must preserve existing budgets, cooldowns, unacknowledged receipts
and uncertain operation records. Fence writes on the old account authority
before enabling the new one; never let two backends independently grant/spend
for the same SteamID. Retain access to the original records for reconciliation.
The staged golden-bones-v2 economy remains a separate publication.

Proceed with one chosen host only after eligibility, throughput and recovery
checks succeed. With a strict zero-cost requirement, capacity and availability
have finite limits; fail with a clear retry response rather than discard
authoritative state or silently weaken purchase safety.
