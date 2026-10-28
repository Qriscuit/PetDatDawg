# Golden Bones economy source and Steam release

`economy.json` is the versioned source for item IDs, cosmetics, prices, reward
pools, cooldown, recycling value, and provisional currency packs. The catalog
contains **140 cosmetics: 77 dogs and 63 accessories**. All existing IDs remain
unchanged; the October 28 Drive sync adds Irish Setters and Wirehaired Pointing
Griffons as dogs **3073–3076**. The checked-in `itemdefs.json` now has
**154 definitions** under candidate economy version `golden-bones-v2`.
The premium dog pool contains 67 equally likely outcomes; the 20-item starter
pool, accessory pool, and prices are unchanged. This version and its matching
icon namespace are prepared locally and are **not yet published or deployed**.
Generating files does not publish anything to Steam; the October 19 v1 live
verification below remains a historical record.

From the repository root, using Node 22.13 or newer:

```powershell
node scripts/economy-generate.mjs
node scripts/economy-generate.mjs --check
```

Generation writes `itemdefs.json` and the Worker's `economy.generated.js`, and
checks every cosmetic against the C# catalog's ID, name, kind and artwork.
The manifest also holds the stable icon base URL. `npm run check` and
`npm run deploy` stage the original preview PNGs through
`scripts/economy-assets.mjs`; verify their public responses after deployment
before publishing Steam definitions. Local staging alone does not host icons.
Change prices or pools in the manifest, increment `version` when live offer
semantics change, regenerate, and deploy matching definitions before enabling
the matching Worker. Previously accepted Worker transactions retain their
frozen offer version and materials.

| Definition | ID | Purpose |
| --- | --- | --- |
| Pets | 100 | Earned currency |
| Golden Bones | 101 | Premium currency |
| Dig Permit | 102 | Server-only grant; one permit is consumed per Dig Box |
| Legacy accessory/dog boxes | 1000 / 1001 | Owned boxes remain openable; purchase recipes removed |
| Legacy accessory/dog generators | 1100 / 1101 | Existing box opening recipes |
| Dig / premium dog / premium accessory generators | 1110 / 1111 / 1112 | One-step purchase and reward |
| Recycling bundle | 1113 | One cosmetic yields five Golden Bones |
| Golden Bones packs | 1200 / 1201 / 1202 | 100 / 550 / 1200 Bones |

The Dig recipe requires `100x500,102x1`; a client cannot bypass the cooldown
with Pets alone. Permit 102 has no price, exchange, promotion, trade or Market
source. Do not add any. Recycling is one cosmetic per atomic exchange. Its
recipes intentionally permit the player to recycle their own item through
Steam; last-copy/outfit safeguards belong to the normal app/API flow.

## Cash and Market are disabled in the checked-in schema

The default generated schema has **no price or price_category fields**. Packs
are hidden and all definitions are nonmarketable. A Worker capability flag
alone is not a safe way to prevent public Steam purchases.

Before a checkout test, verify both that this app is unreleased and that the
signed-in Steam account belongs to its Steamworks development/publishing team.
Those are Steam's documented prerequisites for StartPurchase sandbox routing;
an AppID development file alone does not establish eligibility. After verifying
both, prepare a separate priced candidate:

```powershell
node scripts/economy-generate.mjs --sandbox-prices
```

This writes `itemdefs.release-candidate.json`, never overwriting the safe
default. The currency gets the contained-item pricing metadata Steam needs;
packs use exact override prices of USD 0.99 / 4.99 / 9.99. Display localized
prices returned by Steam rather than hardcoding these USD test prices in the
purchase button. Do not publish the priced candidate to public sale before
pricing and Steam setup are approved.

For a temporary test on an unreleased app with an eligible developer account,
keep the normal Worker's cash capability disabled and use an isolated checkout
probe. Preserve the successful unpriced readback separately before switching
the verifier to `--expected SteamInventory/itemdefs.release-candidate.json`.
After the test, **republish the safe `itemdefs.json`**, prepare the verifier
again without `--expected`, and require a fresh successful publisher readback
with zero priced definitions. A successful test or disabled client button does
not restore Steam's server-side price fields; restoring the safe schema does.

For Market preparation, verify app eligibility and publicly accessible item
icons first. The manifest icon base (or candidate-only override below) must
serve `{itemDefId}.png` for every cosmetic:

```powershell
$env:PDD_STEAM_ICON_BASE_URL = 'https://your-verified-icon-host/icons/version'
node scripts/economy-generate.mjs --market
```

`--market` also writes the separate release candidate and sets only cosmetics
marketable. It does not turn on direct trading, currency trading, or cash
sales. Combine `--market --sandbox-prices` only when both gates are ready.
Market proceeds go to Steam Wallet; recycling gives Golden Bones.

## Coordinated release

1. Run manifest validation, Worker unit/runtime suites, and client/export tests.
2. Deploy the compatible Worker with `PDD_SHOP_ENABLED=0` and
   `PDD_LEGACY_PURCHASES_ENABLED=0` for the migration window. Existing boxes
   remain openable; old purchases receive `update_required`.
3. Upload/publish the complete 154-definition safe schema. Read it back from
   Steam without cache and compare IDs, prices, recipes, and generator weights,
   including dogs 3073–3076. Do not infer publication from a
   local JSON or successful upload alone.
4. Enable `PDD_SHOP_ENABLED=1` and ship the matching client. Keep public cash and
   Market flags off until their separate Steam gates are verified.
5. Before publishing a priced test candidate, verify developer sandbox
   eligibility and the Steam configuration that keeps public cash sales
   unavailable. Then test a Steam-launched export and overlay checkout with
   those eligible accounts. Keep general client checkout disabled until that
   test passes. A successful StartPurchase callback only opens checkout;
   confirmed inventory determines credit.

References: [Steam inventory schema](https://partner.steamgames.com/doc/features/inventory/schema),
[Inventory Web API](https://partner.steamgames.com/doc/webapi/IInventoryService),
[Steam checkout](https://partner.steamgames.com/doc/api/ISteamInventory#StartPurchase).

## Verify the actual published definitions

Native Steam SDK readback omits some generator/recipe metadata; it cannot by
itself verify the complete economy. Use a full publisher `GetItemDefs` response
with `cache_max_age_seconds=0`. The comparison checks all 154 IDs, every expected
property, explicit reward weights, icon URLs, and unexpected live prices,
promotions, legacy purchase recipes, or Market flags.

If a secure server already fetched the response, compare the saved response:

```powershell
node scripts/economy-readback.mjs --input fetched-itemdefs.json --report comparison.json
```

An offline comparison is labeled as such; it does not establish when or where
the input was fetched. Exit status is 0 for a match, 1 for differences, and 2
for malformed or rejected input. It accepts the full publisher response,
decoded definition array, or `{items:[...]}` export. It never accepts a key.

For an authenticated Cloudflare operator, prepare an isolated, temporary remote
preview using the production Worker's existing backend-only secret binding:

```powershell
node scripts/economy-readback.mjs --prepare-preview
Set-Location PetDaDog.Worker
npx.cmd wrangler dev --remote --config .wrangler/economy-readback/wrangler.json --port 8791
```

In another terminal:

```powershell
Invoke-WebRequest 'http://127.0.0.1:8791/verify' -OutFile '.wrangler/economy-readback/publisher-report.json'
```

Then stop the preview with Ctrl+C. **Never deploy the diagnostic script.** It
has one fixed read-only publisher request, no player tickets, no Durable Object
binding and no inventory mutation code. It returns a sanitized comparison with
UTC retrieval time and expected-file SHA-256; the publisher key stays inside
the remote preview and is neither returned nor logged. The preview does not
replace the deployed game Worker.

The post-publication readback on **2026-10-19 at 20:12:17 UTC** verified all
**150 live definitions**, zero missing IDs, zero unexpected IDs, and zero
property differences. All 136 cosmetic icon URLs matched, all generator weights
and recipes matched, and the old 1/2-Pets purchase recipes were absent. No live
price fields or Market flags were enabled. The expected schema SHA-256 was
`f0fbdae166b3d8cf55828ef97a9b4b3ec647557a5de6acb9055529b927bb8db6`.
The matching Shop was then enabled in Worker version
`2630587c-88e4-4f9b-a369-7488a877cf8e`. Re-run this check for future schema changes.
