# Shop API and isolated economy development

Steam is the inventory authority. The SQLite Durable Object stores operation
intents, receipts, acknowledgment state and account cooldowns, never currency
balances. Per-account requests are serialized across Steam I/O.

## v2 contract

All routes use the existing `/v1/auth/steam` bearer session.

| Method and path | Body / behavior |
| --- | --- |
| `GET /v2/shop` | Confirmed balances, items, catalog, capabilities, clock, cooldown and unacknowledged receipts |
| `POST /v2/shop/purchase` | `{clientEventId, offerId, economyVersion}` |
| `POST /v2/shop/recycle` | `{clientEventId, itemDefId, quantity:1, equippedQuantity, economyVersion}` |
| `GET /v2/shop/operations/{guid}` | Same envelope with `status: complete/pending/rejected/not_found` and any receivedItems |
| `POST /v2/shop/ack` | `{clientEventId}` acknowledges a completed reward presentation, idempotently |

Purchase offers are `dig`, `dog`, `accessory`, `dog:{itemDefId}`, and
`accessory:{itemDefId}`. Version is currently `golden-bones-v1`.

The state envelope contains `pets`, `goldenBones`, `items`, `economyVersion`,
`serverTime`, nullable `nextDigAt`, `catalog`, `capabilities`, optional
`pendingOperation`, and `receipts`. Times are UTC ISO 8601 strings. The catalog
includes `offers`, `packs`, `recycleValue`, and `maxRecycleQuantity:1`. Receipt
fields are `clientEventId`, `kind`, optional `offerId`, `receivedItems`,
`completedAt`, and `isDuplicate`. Legacy opening receipts have `kind:open`.
Never render a missing/failed inventory response as a zero balance.

Fresh operations validate the manifest version, confirmed inventory, and
cooldown. Accepted operations freeze price, version, materials, explicit
reward IDs, quantities and cooldown. Stable IDs persist before permit AddItem
and cleanup ConsumeItem calls. A definitive reward receipt and the Dig
cooldown timestamp persist in the same storage transaction.

An uncertain ExchangeItem outcome remains locked: neither a request replay nor
an inventory delta can reroll, charge again, or prove completion. Recovery can
repeat idempotent permit/cleanup phases, or finish an already persisted reward
receipt. `GET /v2/shop` performs those safe recovery phases automatically.
True exchange uncertainty needs investigation against Steam before a deliberate
support resolution; there is no automatic force-unlock endpoint.

`202 pending` responses do not authorize a second transaction. Operation lookup
also returns `200 not_found` when no request was accepted, allowing the client
to resend its original GUID. Rejected operations return `200 rejected`; a Dig
permit must be cleaned up before releasing the mutation lock.

Ordinary inventory/Pets grants also return confirmed `goldenBones` so the
client can refresh both balances without manufacturing a local balance.

## Deployment capabilities

| Variable | Default / effect |
| --- | --- |
| `PDD_SHOP_ENABLED` | `1` in the verified release config; fallback is `0` until schema readback |
| `PDD_LEGACY_PURCHASES_ENABLED` | `0` in the release config; compatibility fallback is `1` when omitted, and Shop enablement also retires legacy purchases |
| `PDD_CASH_PURCHASES_ENABLED` | `0`; client checkout visibility gate, not a Steam sale restriction |
| `PDD_MARKET_ENABLED` | `0`; UI gate pending app eligibility and verified icons |

The checked-in Steam schema separately removes prices and disables Market.
See [schema generation and coordinated release](../SteamInventory/README.md).

## Local test economy

Run with Node 22.13+ from this directory:

```powershell
npm run dev:economy
```

This uses `wrangler.test.jsonc` plus `wrangler.fake-steam.jsonc` on
`http://127.0.0.1:8787`. The fake service has no network client and no real
publisher key. Its inventory resets when the fake service restarts; use a new
local persistence directory after resetting fake inventory to avoid retaining
receipts from a previous test inventory. Never deploy these local test configs.

Point an explicitly marked developer build at that URL. Isolated test auth
accepts a dummy Steam ticket (for example hex `aabb`) and uses one test account.
Do not use real account tickets in this transport. Developer buttons require
both the client developer setting and the server's developer capability.

Test-only authenticated routes:

- `POST /v2/shop/dev/seed`: `{clientEventId,pets,goldenBones}` adds bounded test
  amounts (0–100000 each); stable GUID deduplicates the seed.
- `POST /v2/shop/dev/reset-cooldown`: `{clientEventId}` clears test cooldown.

The production Worker cannot enable these routes with a Boolean flag: they
require `PDD_ECONOMY_ENVIRONMENT=isolated-test`, the exact dummy publisher-key
value and a fake-Steam **service binding**. Every Steam call is then routed
through that binding. A missing test binding fails configuration closed.
Actual credentials and real transport never execute developer currency grants.

## Verification

```powershell
npm run economy:check
npm test
npm run test:runtime
npm run check
```

Unit tests include cooldown boundaries/restarts, permit replays and cleanup,
unknown exchanges, concurrent Wallet stack changes, last/equipped-copy checks,
receipt recovery/ack, repeated purchases, legacy opening, production developer
route exclusion, and per-account HTTP serialization. Runtime smokes exercise
the real workerd engine and SQLite bindings with fake Steam, without real
grants or spending.
