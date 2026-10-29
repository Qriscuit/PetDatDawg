# Cloudflare connection operations

## Service context

- Production API: `https://petdadawg.americlasagna.workers.dev`.
- Worker: `petdadawg`; Steam AppID `4817200`; Pets itemdef `100`.
- `PLAYER_INVENTORY` is the SQLite Durable Object class `PlayerInventory`.
  Its production namespace is `66edfacca3da491b8ab38ddd376557be`.
- Steam owns balances and items. Durable storage owns operation records,
  deduplication, mutation locks, rate limits and Dig cooldowns.
- Deployment and secret setup: [Cloudflare setup](CLOUDFLARE_SETUP.md).
  Shop behavior: [Worker guide](../PetDaDog.Worker/README.md).

`GET /health` validates Worker configuration without contacting Steam or the
Durable Object. HTTP 200 there cannot establish working player inventory,
grants or purchases. Likewise, Wrangler tail's invocation outcome `Ok` means
the handler returned normally; an application HTTP 503 can still be `Ok`.

## Diagnose a connection failure

1. Request `/health`. A DNS/TLS/edge error differs from an API JSON error.
2. Check Steam initialization, ticket authentication and the actual authenticated
   `/v1/inventory` response. Use the existing zero-grant live verifier:

   ```powershell
   .\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamSmoke.ps1 -AllowLiveSteam -WorkerOnly -GrantCount 0 -BackendUrl 'https://petdadawg.americlasagna.workers.dev' -TimeoutSeconds 60
   ```

   The runner disables global input and tray behavior. At GrantCount 0 it
   creates no new test grants, purchases, recycling or receipt acknowledgments.
   It is not strictly read-only for every account state: automatic Shop reads
   and background Tick can resume previously accepted operations. Unknown
   exchanges remain protected. Strictly non-mutating checks need an auth and
   inventory-only verifier without automatic Shop refresh or Tick; the existing
   switches do not provide that mode. `WorkerOnly`
   does not establish independent native Steam inventory verification. Its
   catalog expectations follow the local candidate; a later catalog mismatch
   can mean production and the candidate differ, rather than a connection error.
3. Verify both secret *names*, the Durable Object binding and current release
   with `wrangler secret list` and `wrangler deployments status`. Keep secret
   values, auth responses, bearer tokens, tickets and raw request headers out of
   logs. Existing workspace-local Wrangler auth uses
   `PetDaDog.Worker/.wrangler/config` as `XDG_CONFIG_HOME`.
4. Inspect the API error code. `steam_access_denied` and `steam_unavailable`
   with `steamEndpoint` describe Steam failures. `storage_quota_exceeded`
   identifies a recognized daily Cloudflare storage limit. Unknown failures
   remain `service_unavailable`; do not assume they are quota failures.
5. Inspect Cloudflare Durable Object usage for the entire UTC day. The account
   quota covers all its namespaces. The successful query used during this
   incident was a GraphQL POST to `https://api.cloudflare.com/client/v4/graphql`
   using existing Wrangler authorization internally, without printing it:

   ```graphql
   {
     viewer {
       accounts(filter: { accountTag: "ACCOUNT_ID" }) {
         durableObjectsPeriodicGroups(
           limit: 100
           filter: {
             datetime_geq: "2026-10-28T00:00:00Z"
             datetime_leq: "2026-10-28T23:59:59Z"
           }
         ) {
           dimensions { namespaceId }
           sum {
             rowsRead rowsWritten duration
             exceededCpuErrors exceededMemoryErrors fatalInternalErrors
           }
         }
       }
     }
   }
   ```

   Substitute ISO UTC day bounds and the account ID from `wrangler whoami`.
   Check GraphQL `errors`; an authorization failure is not zero usage.
   For more than 100 namespaces, query all pages/groups before totaling usage.

Unexpected failures now emit `service_request_failed` with allowlisted stage,
error name, category and optional numeric source position. Raw exception text,
stacks, URLs and player identifiers are excluded. Durable Object logs may need
to be inspected separately from the top-level Worker invocation.

## Daily storage quota recovery

As verified October 28, 2026, Workers Free allows **100,000 SQLite rows written
per day** and **5 million rows read per day**. Key/value storage calls also use
SQLite rows; deletes count as writes. These are account limits. Daily limits
reset at **00:00 UTC**, which is **8 PM EDT** on the incident date. Use the
actual date/time zone when communicating a later reset.
[Cloudflare Durable Object pricing](https://developers.cloudflare.com/durable-objects/platform/pricing/)

Wait for the reset, or have the account owner enable Workers Paid and accept
its billing terms. The documented minimum is **$5 USD/month plus metered
overages**; confirm the current price before changing plans.
[Cloudflare Workers pricing](https://developers.cloudflare.com/workers/platform/pricing/)

Deploying code or reducing future writes cannot replenish today's exhausted
allowance. Do not replace the namespace, delete operation records, bypass
mutation locks, invent balances or replay an uncertain exchange to recover.
The legacy grant path persists rate, pending-operation and completion records
for each new input. A single input therefore consumes multiple row writes;
100,000 rows does not mean 100,000 Pets. The batch candidate uses four application
writes per batch; its production rollout is tracked separately. Request volume
from multiple players shares this allowance. Plan capacity for real traffic.

Quota responses return HTTP 503 with `code: storage_quota_exceeded`, a fixed
explanation and `Retry-After` seconds through the next UTC midnight. The new
batch client honors this header for both background and foreground grant
attempts and adds bounded exponential backoff with positive jitter. Older
single-grant builds used a short retry loop that ignored the header. Pending
grants exist only for the current client run. After quota relief, rerun a
zero-grant inventory check before declaring recovery. Preserve and investigate
any uncertain Shop operation.

## October 28, 2026 incident

At approximately **22:20 UTC / 6:20 PM EDT**, `/health` returned HTTP 200 and
Steam authentication succeeded, but authenticated inventory returned generic
HTTP 503. Independent native Steam inventory reads succeeded. Both server
secrets and the original SQLite namespace were present.

Cloudflare analytics reported the following for the production namespace on
October 28 UTC:

| Metric | Observed |
| --- | ---: |
| Rows written | 105,994 |
| Rows read | 73,064 |
| Duration (GB-s) | 1,230.44 |
| CPU-limit, memory-limit and fatal internal errors | 0 |

Production previously ran `2630587c-88e4-4f9b-a369-7488a877cf8e` with
`golden-bones-v1`. A narrowly scoped hotfix at **22:27:23 UTC** deployed version
`87c4ef06-d4da-4a97-af5c-c6a8fdd5dd85`. It adds sanitized unexpected-error
diagnostics and the explicit quota response. It retains the deployed v1 economy,
the existing namespace, secrets, flags and static assets. The locally staged
v2 economy and four new dogs were not published by this repair.

A subsequent zero-grant verifier against that release received the explicit
daily storage quota message, confirming the daily-limit error path. `/health`
and a live v1 cosmetic icon still returned HTTP 200. The code change passed all
59 Worker unit tests, both actual-workerd/SQLite smoke tests and a deployment
dry run. Tests did not grant Pets, spend currency or enable desktop input.

Recovery was still unconfirmed at **2026-10-29 00:06:06 UTC**. The post-reset
follow-up still received the daily storage quota error from authenticated
inventory. Health alone is insufficient to establish recovery.

The account owner chose **Keep Free; wait for the reset**. The one-time
follow-up scheduled for **October 28 at 8:05 PM America/New_York** completed:

- At **2026-10-29 00:05:12 UTC**, `/health` returned HTTP 200 with status `ok`.
- The preserved v1 runner started at approximately **00:05:42 UTC** with
  `-SkipBuild -AllowLiveSteam -WorkerOnly -GrantCount 0`, the production URL,
  and a 60-second timeout. Global input and tray were disabled.
- Steam initialized and was logged in. Authenticated inventory still reported
  `Pet syncing is paused because the server reached its daily storage quota.`
  The runner exited 1 before completing its inventory/Shop assertions; Shop
  recovery was therefore not verified. Completion was recorded at
  **00:06:06 UTC / 8:06:06 PM EDT October 28**.
- The test requested zero grants and created no new test purchases, recycling
  or acknowledgments. No billing, deployment, storage replacement or credential
  changes were made. Production was not republished by this follow-up.
- Local logs are under
  `Pet_Da_Dog_CSharp/.godot/windows-export/20261019-224859-d1b261cc/project/.godot/live-steam-smoke-20261028-200542/`.

The documented midnight reset had passed, but that check did not establish why
quota enforcement persisted. The one-time follow-up finished without adding
daily checks or changing billing.

Authenticated recovery was subsequently verified at approximately
**2026-10-29 05:47 UTC / 1:47 AM EDT**. The preserved v1 runner passed **44
assertions**, requesting zero grants. Worker authentication and repeated fresh
inventory reads agreed on **148 Pets**, with no pending grant. The Shop returned
`golden-bones-v1`, 139 offers, **5 Golden Bones**, Shop enabled, and cash, Market
and developer capabilities disabled. Its initial native inventory diagnostic
also returned 148 Pets; this Worker-only run did not perform all independent
native comparisons, so it is not a full native-verification pass. Logs are under
`Pet_Da_Dog_CSharp/.godot/windows-export/20261019-224859-d1b261cc/project/.godot/live-steam-smoke-20261029-014713/`.

This establishes recovered authenticated inventory and Shop reads on the v1
quota hotfix. It does not establish a cause for the delayed recovery, successful
new purchases, or deployment of the later batch candidate. No plan or billing
change was made by the recovery check.

## Grant batching and abuse controls

The [capacity assessment](BACKEND_CAPACITY_PLAN.md) records the latest
requirements. On October 29 the owner rejected five-minute confirmation and
accepted a minimized recurring hosting cost, then selected 30-second batches
plus immediate Shop synchronization on Cloudflare. See
[GRANT_BATCHING.md](GRANT_BATCHING.md) for the implemented local protocol,
verified real-Steam transport preflight, and production rollout requirements. The owner
authorized bounded real-Steam testing followed by conditional v1-preserving
deployment. The transport preflight passed 65 assertions, confirming exactly
148 → 450 Pets across the two original batches and unchanged duplicate replays.
The v1-preserving batch Worker was deployed at **2026-10-29 06:00:47.739 UTC**:
version `04fa0561-39fc-458d-b1d8-157f4f461365`, deployment
`c5296edc-52e0-450f-9062-1f035338bcf3`, bundle SHA-256
`664d37f87605b8da84aff8abf52c86f65e4e41ce93005faa82839083e37cfba8`.
Bindings, namespace, account settings, subdomain, live v1 economy/legacy bounds
and a known v1 icon hash remained unchanged; billing API writes were zero.
Health returned HTTP 200 at 06:01:24 UTC. The original two-Pet replay and its
300-Pet counterpart then passed **54 production assertions** at
06:02:02 UTC: both original IDs replayed with zero additional Pets, altered
payloads/overlaps were rejected, and fresh inventory remained 450. Logs are in
`Pet_Da_Dog_CSharp/.godot/live-batch-20261029/run-20261029-020053/`.

The preserved v1 zero-grant verifier subsequently passed **44 assertions** at
**06:02:19 UTC**, confirming authentication, repeated inventory
reads at 450 Pets, zero pending grants, and a v1 Shop with 139 offers, 136 direct
cosmetics, pools 20/63/53 and 5 Golden Bones. Shop was enabled; cash, Market and
developer capabilities remained disabled. Native inventory before authentication
also returned 450 Pets, but this Worker-only mode does not perform full native
comparisons. It requested zero grants, purchases, recycling and acknowledgments.
Logs are in
`Pet_Da_Dog_CSharp/.godot/windows-export/20261019-224859-d1b261cc/project/.godot/live-steam-smoke-20261029-020209/`.
The temporary private remote preview was stopped and port 8791 was closed.
No billing change has been made.

The updated client code is built, but no updated Steam client distribution has
been published. Existing clients continue making per-input requests until
updated, so the Worker deployment alone does not realize their batching savings.

The historical design below was recorded October 28, 2026. Its compact storage,
token-bucket and anti-abuse extensions remain proposals. The implemented first
step uses four writes per batch, a shared fixed-minute quantity allowance, and
client retry backoff. The owner
chose to keep physical, remote, accessibility and injected/macro input support,
and enforce earning limits. An injection flag or regular input timing is not
evidence of cheating under this policy.

### Legacy cost and current trust boundary

`PlayerService.grant` in `PetDaDog.Worker/src/index.js` performs three
application-level KV writes per new successful input: rate counter, pending
operation, completed operation. It also calls Steam AddItem and GetInventory.
The configured cap is 300 new Pets per Steam account per fixed minute. It is
shared by clients using that account's Durable Object, but permits a burst
across minute boundaries. It is neither a daily earning cap nor proof of input.

The client supplies a GUID, not independently verifiable input evidence.
Steam authentication proves account identity and ownership; a modified client
can fabricate fresh GUIDs within its allowance. Existing idempotency prevents
the same operation from paying twice. Completed-operation replays still call
Steam inventory, and auth/inventory/replay requests have no separate in-code
request-abuse limiter. These are additional load sources worth addressing.
The incident's aggregate usage does not establish that any player cheated.

### Original optimization targets

Keep one pending earning unit per observed press in the current run's memory.
Flush a nonempty batch at a validated size limit or after roughly 30 seconds.
Use 100 inputs as an initial sizing hypothesis, subject to Steam verification.
Dog feedback remains immediate; the visible balance changes only when Steam
inventory confirms it. Closing the app can abandon unsent inputs as it does
today; batching increases the amount potentially waiting in memory.

| Work for 1,000 inputs in full batches of 100 | Application KV writes | Steam calls |
| --- | ---: | ---: |
| Legacy per-input flow | 3,000 | 2,000 |
| Implemented batch with separate rate and run records | 40 | 20 |
| Batch operation with separate rate record | 30 | 20 |
| Compact state with combined rate and operation data | 20 | 20 |

These are design estimates excluding sessions, retries, Shop work and cleanup.
The compact target reduces application writes by 99.3% and Steam calls by 99%.
Measure actual Cloudflare billed rows separately. A multi-key put or database
transaction does not make multiple stored rows count as one write.
[Cloudflare storage accounting](https://developers.cloudflare.com/durable-objects/platform/pricing/)

The compact protocol should persist a frozen batch, server-time earning budget,
session generation and sequence range together before contacting Steam, then
persist completion afterward. Use a stable, nonzero Steam request ID. A retry
must preserve its quantity and sequence range; never split, enlarge or regroup
a batch whose Steam outcome is uncertain. Retry admission must not consume the
earning budget twice.

A batch ID alone cannot detect inputs repackaged under another batch ID.
Server-tracked contiguous sequence ranges and an authenticated run generation
can reject overlaps and altered retries without storing every input GUID.
They still cannot prove an input happened. Session rollover, concurrent clients
and the rejection of retired generations need explicit protocol tests. One
active earning session per Steam account is a candidate simplification; taking
over a session must preserve the account budget and unresolved operation.

Steam documents an itemdef list and idempotent AddItem request IDs, but no bulk
quantity parameter or explicit maximum list size. The live transport preflight
confirmed that repeated Pets entries can return multiple snapshots of one stack
ID, including identical current quantities on replay. The batch receipt parser
validates each row without summing these snapshots; ordinary inventory and
exchange parsing remain strict. The intended two- and 300-Pet transport cases
and same-ID replays passed; deployment still requires the complete Worker
verification. GetInventory remains the balance authority.
[Steam AddItem](https://partner.steamgames.com/doc/webapi/IInventoryService#AddItem)

### Earning limits and investigation

- Count Pets in the limiter, not batches. A server-clock token bucket retaining
  the current 300 Pets/minute refill rate, with an explicit burst allowance,
  would avoid the fixed-minute boundary burst. Reauthentication, restart and
  concurrent clients must not replenish this shared account budget. A daily
  ceiling would be a separate economy decision; none is selected here.
- Keep delayed inputs queued and drain them under the same allowance. Outage
  catch-up is not proof of cheating, and client timestamps cannot establish
  when an input really happened.
- Add separate inexpensive request limits for authentication, inventory reads
  and completed-operation replays. Coalesce simultaneous inventory reads and
  consider a brief cache of the last confirmed response for passive display.
  Shop authorization must still use appropriately fresh Steam state. Edge
  request limiting must not replace the authoritative account earning budget.
- Honor Retry-After, including the quota reset, and use bounded exponential
  backoff with jitter for transient failures. This is now implemented for the
  batch client; the prior two-second grant retry loop wasted requests during a
  known quota outage.
- Record coarse accepted quantities, rate rejections, invalid sequence or
  changed-payload attempts, session changes and retry counts. Aggregate or
  sample these within existing writes/logs; a new database row per rejection
  would create another exhaustion path. Do not collect key identities, text,
  pointer positions or screenshots.
- Reject invalid sessions, altered retries and overlaps deterministically.
  Sustained cap usage or concurrent sessions are review signals, not automatic
  ban evidence. Account farms can remain within each account's allowance;
  per-account limits do not solve that separately. No client-only secret or
  signature can prove honest input from an owner-controlled executable.

### Lower-write options and rollout constraints

Two compact state writes per batch is the conservative target, not a proven
minimum. A more complex stream could reserve each batch before Steam and fold
its completion into the next reservation, approaching one write per batch.
Its crash recovery, final batch and session handover require more work and
may need extra Steam requests. Equivalent zero-write protection is unproven.

Grant batching must preserve the existing distinction between AddItem and
ExchangeItem uncertainty. Current grants do not set the durable active Shop
lock. Do not indefinitely block an account on an abandoned grant, automatically
retry another run's abandoned inputs, or unlock/repeat an uncertain exchange.
Late AddItem completion during a purchase must remain safe.

Old and new grant endpoints must consume the same account budget during
migration. Verify duplicate delivery, altered quantities, overlapping ranges,
concurrent clients, worker restart at each persistence boundary, quota failure
before/after Steam execution, session handover and unchanged Shop behavior.
Start with isolated fake Steam; bounded real-Steam verification is a separate
step. Do not publish the locally staged v2 economy as part of this optimization.

Additional Workers in the same account share its allowance. Separate frontends
that still use the same player store do not remove that store's write load.
Any legitimate multi-store deployment would need stable player routing and
coordinated rate, retry and purchase state; random per-request distribution
would weaken these protections. Using extra Free accounts specifically to
evade quotas conflicts with Cloudflare's usage restrictions unless Cloudflare
permits it in writing. Optimize batching before considering sharding.
[Cloudflare agreement, section 2.2.1(c)](https://www.cloudflare.com/terms/)
