# Golden Bones Shop

The Shop has persistent confirmed Pets/Golden Bones balances and five sections:
Boxes, Dogs, Accessories, Golden Bones, and Recycle. It retains the woodland
theme and independent menu/font scaling. Navigation and content scroll in small
windows while balances remain visible.

| Action | Initial amount |
| --- | --- |
| Dig Box: one starter dog or accessory | 500 Pets, once per 20 minutes |
| Premium Dog Box | 50 Golden Bones |
| Premium Accessory Box | 25 Golden Bones |
| Choose a specific dog | 200 Golden Bones |
| Choose a specific accessory | 100 Golden Bones |
| Recycle one spare cosmetic | Receive 5 Golden Bones |

The basic pool contains 10 dogs and 10 accessories at 5% each. The premium
boxes exclude them, with 63 equally likely dogs or 53 accessories. Contents
and odds are available before purchase. Specific-item purchases support all
136 cosmetics and repeated copies. Search and ownership filters narrow the
grids. Recycling protects `max(1, equipped copies)` and always confirms the
single-copy conversion; saved presets do not reserve copies.

A box purchase starts digging immediately. The desktop dog travels to ground,
pivots and dips below it with its outfit, and tosses a closed box outward.
The box lands clear of the outfit, squashes on impact, then stretches and opens
before the result card appears. The sequence lasts about three seconds and
keeps the overlay geometry fixed. The result card uses the confirmed Steam receipt. Keep
returns to the Shop; Equip rechecks ownership. Skip affects presentation only.
A slow or uncertain operation returns control and remains pending on the
server. Reconnecting recovers the original receipt instead of buying again.
`BoxRevealVisual.tscn` includes a `DirtFrames` attachment point for future animation frames.

Reward windows fit the dog's monitor work area. Their content can scroll while
the action footer stays visible. Golden Bones receipts use a compact icon and
a Done button, including on small screens at 150% font size.

Steam currency checkout is implemented through Inventory price lookup and
StartPurchase. The test pack sizes are 100, 550, and 1200 Bones, with provisional
USD prices of $0.99, $4.99, and $9.99. Public prices are absent from the safe
item definitions. Steam confirms payment and inventory; a started checkout is
never treated as a grant. Market navigation is separately gated. Market sales
pay Steam Wallet funds; recycling pays Golden Bones.

## Local developer economy

Use Node 22.13+ and Godot 4.6.3 Mono. From the repository root, run
`scripts/Run-TestEconomy.ps1` to start hidden fake Workers and the Debug game
with an isolated profile. Closing the game stops its Workers. Add
`-TransportSmoke` to run the actual client/Worker protocol checks without a UI.

For manual setup, start `npm run dev:economy` in `PetDaDog.Worker`, then launch
a Debug client with:

```powershell
$env:PDD_BACKEND_URL = 'http://127.0.0.1:8787'
$env:PDD_DEV_ECONOMY = '1'
$env:PDD_DISABLE_STEAM = '1'
$env:PDD_DISABLE_GLOBAL_INPUT = '1'
$env:PDD_DISABLE_TRAY = '1'
```

Use an isolated APPDATA profile as the smoke scripts do. The developer path
authenticates only against an HTTP loopback backend with explicit fake-Steam
capabilities. Debug controls preview digging, seed fake currencies, and reset
the test cooldown. The fake Worker transport cannot contact production Steam.
Release builds exclude developer methods and controls.

## Verification and release

Run Worker unit/runtime tests and `npm run check`; run the backend/Steam client
harnesses and the Shop, BoxReveal, SteamInventory, UiAuthoring, Accessory, and
OverlayRendering smoke scripts. Build the Windows export with
`scripts/Export-Windows.ps1` and its isolated launch checks.

The schema generator and icon staging do not publish to Steam. Follow the
coordinated release in [SteamInventory/README.md](../SteamInventory/README.md):
verify tests, host icons, freeze old purchases, publish/read back 150 definitions,
then enable the matching Worker offers. Public cash sales and Market require
their separate Steam eligibility/pricing checks and stay disabled by default.

### Validation record — 2026-10-19

The digging and reward-window update passed 508 deterministic Godot assertions,
covering ejection/landing order, deeper digging, dog/outfit clearance at screen
edges and both dog scales, flipped appearance, receipt timing, and repeated
Bones/cosmetic windows at 640×360 and 1280×720 work areas. The 289-assertion
ultrawide rendering suite passed. The updated Windows Release package passed
its isolated export launch check. These presentation checks disable Steam,
global input earning, and the tray and use isolated profiles.
The private checkout/depot package was also refreshed with these fixes under
`.godot/steam-checkout-sandbox/steam-depot-upload/20261019-225121-5452d5d4`.
Its 190 files passed archive hash verification and both offline export launches;
the real Steam checkout remains pending the private branch upload/install/test.

- Worker: 55 unit tests and both SQLite/workerd runtime suites passed.
- Client transport: 25 scenarios; Steam commerce: 23 checks plus 17 reconnect
  checks; actual C# client through both local Workers: 21 checks passed.
- Godot 4.6.3: Shop 37, UI authoring 281 runtime/41 editor, accessories 1769,
  Steam inventory 466, overlay rendering 289, and box reveal 99 assertions passed.
- Windows Release export and isolated launch smoke passed. Its assembly excludes
  the developer seed, cooldown-reset, and fake-authentication entry points.
- The live read-only Steam/Worker smoke passed 74 assertions with zero grants
  or purchases; native Steam and Worker inventory remained identical through
  repeated refreshes. It also verified the deployed Shop prices, 139 offers,
  136 direct cosmetics, all three pools, Golden Bones inventory sum, and gates.
- Compatible Worker `af4ded17-440b-47d2-b68f-dcc75fcae210` was deployed. Health
  returned 200; all 136 public icon PNGs matched the staged source bytes.
- After publication, the read-only publisher API check at **20:12:17 UTC**
  verified all **150 definitions**, with zero missing IDs or property
  differences. This includes dogs 3065–3072, all reward weights, 136 icon URLs,
  the Dig permit recipe, and retirement of the cheap legacy purchase recipes.
  Cash price fields and Market flags remained absent/disabled. The report is
  preserved as
  `PetDaDog.Worker/.wrangler/economy-readback/safe-publisher-report-20261019T201217Z.json`.
- A temporary priced candidate was subsequently published for the unreleased
  app's developer checkout test. The read-only publisher check at **20:28:04 UTC**
  verified all **150 definitions** with zero differences. Only Golden Bones
  `101` and packs `1200`, `1201`, and `1202` have price fields: USD 0.01 unit
  pricing metadata and exact bundle prices of USD 0.99, 4.99, and 9.99.
  The currency uses `store_hidden=true`; pack bundle quantities are 100, 550,
  and 1200. All Market flags remain disabled. The report is preserved as
  `PetDaDog.Worker/.wrangler/economy-readback/priced-publisher-report.json`.
- The sandbox was launched from Steam. At **20:35:56 UTC**, Steam price lookup
  loaded **USD 0.99 for pack 1200**. The SDK reported overlay availability as
  **False**, so no checkout was started. Manual cancellation, successful
  completion, and restart recovery remain unverified; local callback tests do
  not establish those live outcomes.
- The player subsequently confirmed that **Shift+Tab opens a visible, usable
  Steam overlay** despite the SDK reporting **False** in that run.
- A fresh read-only publisher check at **21:03:06 UTC** again matched all
  **150 test-priced definitions**, with zero differences and Market disabled.
  Its timestamped report is
  `PetDaDog.Worker/.wrangler/economy-readback/priced-publisher-report-20261019T210306Z.json`.
- After relaunching from the same Steam shortcut at **21:05:57 UTC**, the SDK
  reported overlay availability as **True**, with the correct app/account and
  a confirmed Golden Bones balance of **0**. The sandbox-only identity probe
  showed `SteamGameId=4817200` both before and after configuration;
  `SteamOverlayGameId` retained the shortcut identity. Preserving the inherited
  value did not change these values, so the successful relaunch **cannot be
  attributed to that code change**. The cause of the earlier discrepancy is
  still unproven; normal client source and Worker configuration are unchanged.
- After the overlay availability check passed, Steam accepted the test
  `StartPurchase` request, but the player saw only the ordinary Steam overlay;
  **the checkout UI did not appear**. An accepted start does not establish
  payment, cancellation, or successful checkout. The isolated shortcut test
  was stopped and its checkout unlock removed; its persisted checkout session
  was retained for investigation.
- A fresh isolated, input-disabled Steam/Worker check completed at **21:12:00
  UTC** with **74 assertions, zero grants and zero purchases**. The Worker
  confirmed **Golden Bones 0**; Pets remained **291**, matching native Steam
  through repeated refreshes. Logs are under
  `Pet_Da_Dog_CSharp/.godot/live-steam-smoke-20261019-171147/`. This establishes
  no credited Golden Bones at that observation time; it does not establish a
  cancellation or completed payment. Cancellation, completed purchase, and
  restart recovery remain unverified. A complete private Steam depot ZIP was
  prepared at **21:12:51 UTC**: 190 files, 83,575,442 bytes, with source/archive
  hashes verified and the renamed executable passing its isolated offline
  launch. It is under `.godot/steam-checkout-sandbox/steam-depot-upload/20261019-211234-d02e5a55`.
  Steamworks launch configuration, upload, and installation remain pending.

After the 20:12 UTC safe-schema readback, Worker
`2630587c-88e4-4f9b-a369-7488a877cf8e` enabled the matching Shop. Legacy purchases
are retired; owned legacy boxes remain openable.
The normal Worker retains `PDD_CASH_PURCHASES_ENABLED=0` and
`PDD_MARKET_ENABLED=0`. Its disabled checkout button does not remove prices from
Steam: **the temporary priced candidate is currently published**. App Market
eligibility also remains pending.

The separate [Steam checkout sandbox export](../Tests/SteamCheckoutSandbox/README.md)
has passed an offline launch check and the live price lookup above. It uses
the actual dog scene with global earning disabled, stays outside the normal
`Builds` package, and requires a
verified priced schema plus a short-lived account-bound marker before its
checkout button can work. After the manual test, **republish the unpriced
`SteamInventory/itemdefs.json` and obtain a fresh successful publisher
readback showing zero priced definitions**. The earlier safe-schema report
does not prove that the currently published test prices have been removed.
