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
- `Pet_Da_Dog_CSharp/Scripts/GlobalInputMonitor.cs` observes Windows keyboard/mouse press edges without consuming them and delivers only activity counts to the main thread.
- `Pet_Da_Dog_CSharp/Scripts/NativeWindowBridge.cs` applies Windows overlay styles and owns the native tray menu.
- `Pet_Da_Dog_CSharp/Scripts/SteamIntegration.cs` wraps Steamworks.NET initialization and Web API ticket requests.
- `Pet_Da_Dog_CSharp/Scripts/BackendPetClient.cs` talks to the backend and keeps pending pet grants in memory.
- `Pet_Da_Dog_CSharp/Scripts/SteamCosmeticCatalog.cs` maps the 136 Steam cosmetic itemdefs (73 dogs and 63 accessories) to local art; `StatusWindow.Inventory.cs` binds Shop and dog selection controls. Preserve existing IDs when adding cosmetics; accessory IDs currently end at 2062 and dog IDs at 3072. The latest eight dogs (3065–3072) are prepared locally; Steam schema upload and Worker redeployment remain pending.
- The Dogs tab places the saved outfit preview grid before the owned dog selection grid. `UI/DogChoiceCard.tscn` and `UI/DogPresetCard.tscn` author their reusable cards. Items hides unowned Steam accessory cards and counts only usable accessories; starter cosmetics and Text Box remain available.
- Accessory placements have independent instance IDs. The first copy keeps its catalog ID; extra copies append `::` and a GUID. Confirmed Steam quantities cap equipped copies and preset availability; quantities are never saved locally. Starter image accessories are repeatable, while Text Box is a single editable message. Catalog clicks or drags place an unused copy; equipped copies are selected directly on the dog, with a small outline delete button. The Items inspector's Add another button remains available.
- Menu size changes scale window contents only. Saved window size and position remain independent of UI scale; options sliders ignore the mouse wheel so scrolling cannot change their values.

Main backend entrypoint:

- `PetDaDog.Backend/Program.cs` validates Steam tickets, signs backend sessions, grants pets through Steam Inventory, and reads the Steam Inventory-derived pets total.

## Core Invariants

- The dog is a transparent, borderless, always-on-top Windows desktop overlay.
- Transparent overlay space should pass clicks through to windows behind it.
- Only visible dog pixels should accept left-clicks.
- Each physical mouse-button or keyboard press anywhere on the desktop enqueues exactly one pending pet grant. Holding a key, releases, motion, wheel scrolling, and injected input do not count.
- Local dog input adds feedback or opens menus; it must never duplicate a background input grant. Background earning continues during click-through, invisible-dog, stationary, and editing modes.
- Pets are Steam-authoritative. The client must never store, edit, or fake an authoritative pets total.
- The player-facing Pets total must come from backend/Steam responses; pending grant counts remain internal.
- Failed pet grants retry from an in-memory queue for the current run only.
- The Steam publisher key is backend-only and must never be added to the Godot client build.
- Steam Inventory, not Steam Stats, is the authority for the pets currency.

## How It Works

The earning path is:

`Windows mouse/key press -> activity count -> client GUID queue -> Steam Web API auth ticket -> backend session token -> Steam Inventory AddItem -> Steam Inventory GetInventory -> confirmed pets total`

`DesktopPet` updates Godot's mouse passthrough polygon every frame so transparent desktop space remains usable. `_Input` checks dog texture alpha only for feedback/menu interaction. `GlobalInputMonitor` independently observes press edges, and `DesktopPet.RecordInputActivity` enqueues one GUID per observed press.

Movement preferences are saved locally: Stationary pauses walking/patrol and movement animation, BounceEnabled controls hop/squash/rotation, and patrol traversal can Loop or go BackAndForth. Existing routes default to Loop. Stationary preserves the current patrol target and direction; ordinary post-edit falling still lands the dog.
`FlipHorizontally` mirrors the dog and attached outfit relative to its current facing. It is a saved appearance preference, defaults to false, and leaves walking direction, patrol progress, and bounce timing unchanged. Text anchors mirror with the outfit while message text stays readable.

`SteamIntegration` initializes Steam with `PDD_STEAM_APP_ID` or local `steam_appid.txt`, requests a Web API ticket for identity `petdadog-backend`, runs Steam callbacks each frame, and shuts Steam down on exit.
Failed startup initialization retries every five seconds, so launching Steam after the game can resume the queued earning flow without restarting the pet. Technical details also expose input-monitor availability without recording keys or mouse coordinates.

`BackendPetClient` authenticates with `/v1/auth/steam`, sends queued grants to `/v1/pets/grant`, and reads inventory from `/v1/inventory`. It is intentionally non-authoritative.

The backend uses Steam `ISteamUserAuth/AuthenticateUserTicket` for identity, HMAC-signed short-lived backend session tokens for API auth, `IInventoryService/AddItem` for grants, and `IInventoryService/GetInventory` to sum the configured pets itemdef quantity.

The Worker also checks app ownership, serves `/v1/inventory`, and handles `/v1/boxes/buy` and `/v1/boxes/open` through Steam `ExchangeItem`. Pets itemdef is 100; Dog Box 1001 costs 1 Pet and opens through generator 1101; Accessories Box 1000 costs 2 Pets and opens through generator 1100. ExchangeItem has no documented request ID: persist an intent before calling Steam and never blindly repeat an uncertain exchange. Pending exchanges recover from the Worker after a client restart. Confirmed inventory gates Steam cosmetics; only local outfit/dog selection preferences are saved. Existing starter cosmetics remain available.

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

Client HTTP tests: `dotnet run --project Tests/BackendClientHarness/BackendClientHarness.csproj`. Godot ownership/Shop smoke: `Pet_Da_Dog_CSharp/Tests/Run-SteamInventorySmoke.ps1` with Godot 4.6.3.

## Development Notes For Future Agents

- Prefer small, behavior-preserving changes; the project is intentionally simple.
- Do not replace Steam Inventory authority with client-side saves or Steam Stats for pets.
- Do not make the whole overlay window mouse-transparent with `WS_EX_TRANSPARENT`; the dog must still receive clicks.
- Keep Windows tray behavior in `NativeWindowBridge` isolated from the Godot scene logic.
- Use Godot 4.6.3 C# tooling for build/smoke validation unless the project is intentionally upgraded.
- Show only backend-confirmed Pets totals in user-visible pet status. Keep the in-memory grant queue and retry counts internal.
