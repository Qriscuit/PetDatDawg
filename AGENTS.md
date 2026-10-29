# Pet Da Dog Project Context

## Project Shape

This repo contains a tiny Godot C# desktop pet client, an ASP.NET Core pet-grant backend, and a Cloudflare Worker for the complete inventory and box workflow.

- `Pet_Da_Dog_CSharp/` is the Godot 4.6.3 C# desktop overlay client.
- `PetDaDog.Backend/` is the deployable ASP.NET Core minimal API backend for Steam auth and Steam Inventory pet grants.
- `PetDaDog.Worker/` is the Cloudflare Worker for Steam auth, inventory, Pets grants, and buying/opening boxes. Its SQLite Durable Object records operations and serializes each player's requests; Steam remains the inventory authority.
- `Builds/`, `.godot/`, `bin/`, `obj/`, and exported runtime files are generated artifacts unless a task explicitly targets packaging.

Main client entrypoints:

- `Pet_Da_Dog_CSharp/project.godot` configures the transparent always-on-top window and autoloads `NativeWindowBridge`.
- `Pet_Da_Dog_CSharp/Main.tscn` contains the simple dog scene: `DesktopPet -> FootAnchor -> VisualRoot -> PetSprite`.
- `Pet_Da_Dog_CSharp/Scripts/DesktopPet.cs` drives dog layout, walking, optional bouncing, dog feedback, Steam callbacks, and backend grant retries.
- `Pet_Da_Dog_CSharp/Scripts/GlobalInputMonitor.cs` observes Windows keyboard/mouse press edges, including remote and injected presses, without consuming them and delivers only activity counts to the main thread.
- `Pet_Da_Dog_CSharp/Scripts/NativeWindowBridge.cs` applies Windows overlay styles and owns the native tray menu.
- `Pet_Da_Dog_CSharp/Scripts/SteamIntegration.cs` wraps Steamworks.NET initialization and Web API ticket requests.
- `Pet_Da_Dog_CSharp/Scripts/BackendPetClient.cs` talks to the backend and keeps pending pet grants in memory.
- `Pet_Da_Dog_CSharp/Scripts/SteamCosmeticCatalog.cs` maps the 140 Steam cosmetic itemdefs (77 dogs and 63 accessories) to local art. `StatusWindow.Inventory.cs` binds dog selection/legacy boxes; `StatusWindow.Shop.cs` implements the two-currency Shop. Preserve all cosmetic IDs; accessory IDs end at 2062 and dog IDs at 3076. `SteamInventory/economy.json` is the versioned economy source; `scripts/economy-generate.mjs` generates 154 definitions and Worker configuration and validates the C# catalog. Publication is tracked separately from local generation.
- The Dogs tab places the saved outfit preview grid before the owned dog selection grid. `UI/DogChoiceCard.tscn` and `UI/DogPresetCard.tscn` author their reusable cards. Items hides unowned Steam accessory cards and counts only usable accessories; starter cosmetics and Text Box remain available.
- Accessory placements have independent instance IDs. The first copy keeps its catalog ID; extra copies append `::` and a GUID. Confirmed Steam quantities cap equipped copies and preset availability; quantities are never saved locally. Starter image accessories are repeatable, while Text Box is a single editable message. Catalog clicks or drags place an unused copy; equipped copies are selected directly on the dog, with a small outline delete button. The Items inspector's Add another button remains available.
- Menu size changes scale window contents only. Font size is a separate saved `FontScale` preference (75–150%, default 100%) applied by `MenuFontScale` to menu and preset text. Saved window size and position remain independent of both settings; options sliders ignore the mouse wheel so scrolling cannot change their values.
- Settings can show a click-through Pets counter at the dog's feet or a screen corner. `PetCounterDisplay` reads only the backend-confirmed total and shows a dash until sync; counter settings never save a balance. Corner placement expands the transparent overlay while keeping ordinary dog movement at the usable screen ground.
- `OpenMenuOnRightClick` defaults to true. A configurable global menu shortcut defaults to Ctrl+Alt+D and remains available when right-click opening is off, the dog is invisible, or clicks pass through. `NativeWindowBridge` registers it on the tray thread with no repeat, reports conflicts, and pauses it during shortcut recording; Alt+backtick retains its click-through toggle behavior.

Main backend entrypoint:

- `PetDaDog.Backend/Program.cs` validates Steam tickets, signs backend sessions, grants pets through Steam Inventory, and reads the Steam Inventory-derived pets total.

## Core Invariants

- The dog is a transparent, borderless, always-on-top Windows desktop overlay.
- Transparent overlay space should pass clicks through to windows behind it.
- Only visible dog pixels should accept left-clicks.
- Each mouse-button or keyboard press observed on the Windows desktop enqueues exactly one pending pet grant, whether physical, remote, or injected. Holding a key, releases, motion, and wheel scrolling do not count.
- Local dog input adds feedback or opens menus; it must never duplicate a background input grant. Background earning continues during click-through, invisible-dog, stationary, and editing modes.
- Pets are Steam-authoritative. The client must never store, edit, or fake an authoritative pets total.
- The player-facing Pets total must come from backend/Steam responses; pending grant counts remain internal.
- Golden Bones are also Steam-authoritative. Never credit currency from a checkout-start callback, animation, local preference, or inventory-difference guess. Only confirmed inventory establishes either balance.
- Shop foreground operations are separate from ordinary Pets synchronization. Background grants must not flash/disable purchase buttons. Accepted purchases queue behind the current request; repeated clicks cannot create another operation.
- Developer economy seeding/reset requires a Debug client, explicit developer environment, and an isolated fake-Steam Worker binding. Release clients and production Steam transports must never expose those operations.
- Failed pet grants retry from an in-memory queue for the current run only.
- The Steam publisher key is backend-only and must never be added to the Godot client build.
- Steam Inventory, not Steam Stats, is the authority for the pets currency.

## How It Works

The earning path is:

`Windows mouse/key press -> activity count -> client GUID queue -> Steam Web API auth ticket -> backend session token -> Steam Inventory AddItem -> Steam Inventory GetInventory -> confirmed pets total`

`DesktopPet` keeps Godot's Windows drawing region fixed to the full overlay viewport until its geometry changes. Cursor alpha/interaction hit tests still run every frame so transparent desktop space remains usable without moving the native clip with each animation frame. `NativeWindowBridge` avoids `SWP_FRAMECHANGED` when only pointer passthrough changes, preventing unnecessary renderer resize notifications. `_Input` checks dog texture alpha only for feedback/menu interaction. `GlobalInputMonitor` independently observes physical, remote, and injected Windows press edges, and `DesktopPet.RecordInputActivity` enqueues one GUID per observed press. Direct Godot events never enqueue grants or duplicate the monitor's count.

Movement preferences are saved locally: Stationary pauses walking/patrol and movement animation, BounceEnabled controls hop/squash/rotation, and patrol traversal can Loop or go BackAndForth. Existing routes default to Loop. Stationary preserves the current patrol target and direction; ordinary post-edit falling still lands the dog.
`FlipHorizontally` mirrors the dog and attached outfit relative to its current facing. It is a saved appearance preference, defaults to false, and leaves walking direction, patrol progress, and bounce timing unchanged. Text anchors mirror with the outfit while message text stays readable.

`SteamIntegration` initializes Steam with `PDD_STEAM_APP_ID` or local `steam_appid.txt`, requests a Web API ticket for identity `petdadog-backend`, runs Steam callbacks each frame, and shuts Steam down on exit.
Failed startup initialization retries every five seconds, so launching Steam after the game can resume the queued earning flow without restarting the pet. Technical details also expose input-monitor availability without recording keys or mouse coordinates.

`BackendPetClient` authenticates with `/v1/auth/steam`, settles nonempty current-run input batches through `/v1/pets/grant-batch` every 30 seconds, and reads inventory from `/v1/inventory`. Opening/refreshing Shop and buying immediately settle a fixed queued prefix before fresh affordability checks. Frozen batch IDs/ranges survive retries and same-account reauthentication; no client balance is invented. The legacy single-grant route remains for older clients. See `docs/GRANT_BATCHING.md` for protocol, limits and rollout requirements. It is intentionally non-authoritative.

The backend uses Steam `ISteamUserAuth/AuthenticateUserTicket` for identity, HMAC-signed short-lived backend session tokens for API auth, `IInventoryService/AddItem` for grants, and `IInventoryService/GetInventory` to sum the configured pets itemdef quantity.

The Worker checks app ownership and serves confirmed inventory and `/v2/shop` catalog/state/purchase/recycle/receipt APIs. Pets itemdef 100 buys the 500-Pets Dig Box, with a 20-minute account cooldown enforced by a server-issued single-use permit 102. Golden Bones 101 buy premium dog/accessory boxes (50/25) and specific dogs/accessories (200/100). Recycling one spare cosmetic returns 5 Bones. Current test prices/pools are in the economy manifest. The 20-item starter pool is excluded from premium boxes. Local starter cosmetics cannot be recycled.

Steam `ExchangeItem` has no documented request ID: persist frozen materials before calling Steam and never repeat an uncertain exchange. Permit issuance and rejection cleanup have stable idempotent request IDs. Save successful receipts and cooldown together. Unacknowledged receipts survive restart. Legacy boxes 1000/1001 still open through generators 1100/1101, but the new schema removes their cheap purchase recipes. Production capability flags control coordinated migration; cash/Market stay off until their separate Steam setup is verified. See `SteamInventory/README.md` and `PetDaDog.Worker/README.md`.

The Shop hides during `DesktopPet.BoxReveal` presentation. The actual dog/outfit pivots to dig, a closed/open SVG box rises, and `BoxRewardCard` displays only confirmed results. Presentation cannot spend, grant, reroll, or reset a cooldown. Restore transient movement state without changing saved preferences or resizing the native overlay every frame.

## Commands

Build the Godot client and solution from the client directory:

```powershell
dotnet build
```

Build the backend explicitly:

```powershell
dotnet build S:\CodexProjects\PetDaDog\PetDaDog.Backend\PetDaDog.Backend.csproj
```

Smoke test the Godot scene with the known-good local Godot 4.6.3 runtime:

```powershell
& 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe' --path 'S:\CodexProjects\PetDaDog\Pet_Da_Dog_CSharp' --scene 'res://Main.tscn' --quit-after 5 --no-header
```

Do not use the local Godot 4.6.2 console runtime for smoke tests; it was observed to hang/crash on this project even when tray and Steam were disabled.

## Runtime Config

Client config:

- `PDD_BACKEND_URL`, default `https://petdadawg.americlasagna.workers.dev`; set it to `http://127.0.0.1:5155` explicitly for a local backend
- Godot project setting `pdd/backend_url` supplies the packaged backend URL; the environment variable overrides it.
- `PDD_STEAM_APP_ID`, or local `Pet_Da_Dog_CSharp/steam_appid.txt` for development
- The client defaults to the real app, `4817200`; set the development file to this AppID for real Steam inventory tests.
- `PDD_DISABLE_STEAM=1` for local smoke/debug runs without Steam
- `PDD_DISABLE_TRAY=1` for local smoke/debug runs without the tray icon
- `PDD_DISABLE_GLOBAL_INPUT=1` for deterministic smoke/debug runs without observing desktop input. All test runners and export launch checks set it, including live read-only checks.

Backend config:

- `PDD_STEAM_APP_ID`
- `PDD_STEAM_PUBLISHER_KEY`
- `PDD_STEAM_PETS_ITEMDEF_ID`
- `PDD_BACKEND_SESSION_SECRET` with at least 32 UTF-8 bytes

Real pet grants require the app's actual Steamworks Inventory itemdef and publisher key. Steam AppID `480` is only useful for local client initialization tests.

Worker configuration and deployment are covered in `docs/CLOUDFLARE_SETUP.md`. Node 22.13 or newer is required. From `PetDaDog.Worker`, use `npm test`, `npm run test:runtime`, and `npm run check` for fake-Steam verification and a deployment dry run. Real deployment is separate from these checks and requires Cloudflare login and backend-only secrets.

Live connection diagnosis, daily Durable Object quotas, and the October 28, 2026 storage outage are recorded in `docs/CLOUDFLARE_OPERATIONS.md`. A healthy `/health` response does not verify authenticated inventory. Authenticated inventory and v1 Shop recovery were verified at approximately October 29 05:47 UTC with 44 zero-grant assertions. The live quota-reporting hotfix preserves economy `golden-bones-v1`; the locally staged v2 economy still requires its coordinated publication.

The low-cost backend capacity model is in `docs/BACKEND_CAPACITY_PLAN.md`. On October 29 the owner chose 30-second batching with immediate Shop synchronization while keeping Cloudflare; implementation and rollout are documented in `docs/GRANT_BATCHING.md`. The v1-preserving Worker batch release deployed at 06:00:47.739 UTC as version `04fa0561-39fc-458d-b1d8-157f4f461365`; production replay passed 54 assertions with zero additional Pets, followed by 44 zero-grant v1 Shop/inventory assertions. The updated client code builds, but no updated Steam client distribution has been published; existing builds need an update to gain the 30-second batching benefit. Minimized recurring cost supersedes the previous $0-only preference, but no particular paid plan has been selected. Two-write storage, Steam denomination bundles and alternative hosting remain proposals.

Controlled real-Steam batch testing uses `Pet_Da_Dog_CSharp/Tests/Run-LiveSteamBatchSmoke.ps1`; see `Tests/LIVE_STEAM_BATCH_SMOKE.md` inside the client. Preserve its ignored frozen plan after any uncertain response and resume the same IDs. The preflight confirmed repeated AddItem snapshots of one stacked item ID: validate each receipt row without summing it, and obtain balances through fresh GetInventory. The two- and 300-Pet transport preflight passed 65 assertions (148 → 450 Pets), with duplicate replays adding zero. Production same-ID replay and altered-payload/overlap rejection passed 54 assertions at 450 Pets; the final v1 Shop check retained 5 Golden Bones and disabled cash/Market/developer capabilities. The new batch client honors Retry-After with bounded exponential backoff and positive jitter; old single-grant builds did not.

Client HTTP tests: `dotnet run --project Tests/BackendClientHarness/BackendClientHarness.csproj`. Godot ownership/Shop smoke: `Pet_Da_Dog_CSharp/Tests/Run-SteamInventorySmoke.ps1` with Godot 4.6.3.
Native menu shortcut smoke: `Pet_Da_Dog_CSharp/Tests/Run-MenuHotkeySmoke.ps1`. It checks registration, conflicts, rebinding, capture suspension, cleanup, and restart using its own hidden window; Steam and global input earning stay disabled, and it injects no keyboard input.
Overlay rendering smoke: `Pet_Da_Dog_CSharp/Tests/Run-OverlayRenderingSmoke.ps1`. It exercises 1920-, 3440-, and 5120-pixel overlay widths without changing monitor settings, checking stable native regions and transparent margins during movement and menu use with Steam and global input earning disabled.

## Development Notes For Future Agents

- Prefer small, behavior-preserving changes; the project is intentionally simple.
- Do not replace Steam Inventory authority with client-side saves or Steam Stats for pets.
- Do not make the whole overlay window mouse-transparent with `WS_EX_TRANSPARENT`; the dog must still receive clicks.
- Keep Windows tray behavior in `NativeWindowBridge` isolated from the Godot scene logic.
- Use Godot 4.6.3 C# tooling for build/smoke validation unless the project is intentionally upgraded.
- Show only backend-confirmed Pets totals in user-visible pet status. Keep the in-memory grant queue and retry counts internal.
