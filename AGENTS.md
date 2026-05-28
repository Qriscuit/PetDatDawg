# Pet Da Dog Project Context

## Project Shape

This repo contains a tiny Godot C# desktop pet client plus a separate ASP.NET Core backend.

- `Pet_Da_Dog_CSharp/` is the Godot 4.6.3 C# desktop overlay client.
- `PetDaDog.Backend/` is the deployable ASP.NET Core minimal API backend for Steam auth and Steam Inventory pet grants.
- `Builds/`, `.godot/`, `bin/`, `obj/`, and exported runtime files are generated artifacts unless a task explicitly targets packaging.

Main client entrypoints:

- `Pet_Da_Dog_CSharp/project.godot` configures the transparent always-on-top window and autoloads `NativeWindowBridge`.
- `Pet_Da_Dog_CSharp/Main.tscn` contains the simple dog scene: `DesktopPet -> FootAnchor -> VisualRoot -> PetSprite`.
- `Pet_Da_Dog_CSharp/Scripts/DesktopPet.cs` drives dog layout, walking, hopping, dog-only click handling, Steam callbacks, and backend grant retries.
- `Pet_Da_Dog_CSharp/Scripts/NativeWindowBridge.cs` applies Windows overlay styles and owns the native tray menu.
- `Pet_Da_Dog_CSharp/Scripts/SteamIntegration.cs` wraps Steamworks.NET initialization and Web API ticket requests.
- `Pet_Da_Dog_CSharp/Scripts/BackendPetClient.cs` talks to the backend and keeps pending pet grants in memory.

Main backend entrypoint:

- `PetDaDog.Backend/Program.cs` validates Steam tickets, signs backend sessions, grants pets through Steam Inventory, and reads the Steam Inventory-derived pets total.

## Core Invariants

- The dog is a transparent, borderless, always-on-top Windows desktop overlay.
- Transparent overlay space should pass clicks through to windows behind it.
- Only visible dog pixels should accept left-clicks.
- Each valid dog click enqueues exactly one pending pet grant.
- Pets are Steam-authoritative. The client must never store, edit, or fake an authoritative pets total.
- Confirmed pet totals must come from backend/Steam responses.
- Failed pet grants retry from an in-memory queue for the current run only.
- The Steam publisher key is backend-only and must never be added to the Godot client build.
- Steam Inventory, not Steam Stats, is the authority for the pets currency.

## How It Works

The click path is:

`visible dog click -> client GUID queue -> Steam Web API auth ticket -> backend session token -> Steam Inventory AddItem -> Steam Inventory GetInventory -> confirmed pets total`

`DesktopPet` updates Godot's mouse passthrough polygon every frame so the window only catches input over the moving dog. `_Input` then checks the clicked texture pixel alpha before enqueueing a grant.

`SteamIntegration` initializes Steam with `PDD_STEAM_APP_ID` or local `steam_appid.txt`, requests a Web API ticket for identity `petdadog-backend`, runs Steam callbacks each frame, and shuts Steam down on exit.

`BackendPetClient` authenticates with `/v1/auth/steam`, sends queued grants to `/v1/pets/grant`, and reads totals from `/v1/pets`. It is intentionally non-authoritative.

The backend uses Steam `ISteamUserAuth/AuthenticateUserTicket` for identity, HMAC-signed short-lived backend session tokens for API auth, `IInventoryService/AddItem` for grants, and `IInventoryService/GetInventory` to sum the configured pets itemdef quantity.

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

- `PDD_BACKEND_URL`, default `http://127.0.0.1:5155`
- `PDD_STEAM_APP_ID`, or local `Pet_Da_Dog_CSharp/steam_appid.txt` for development
- `PDD_DISABLE_STEAM=1` for local smoke/debug runs without Steam
- `PDD_DISABLE_TRAY=1` for local smoke/debug runs without the tray icon

Backend config:

- `PDD_STEAM_APP_ID`
- `PDD_STEAM_PUBLISHER_KEY`
- `PDD_STEAM_PETS_ITEMDEF_ID`
- `PDD_BACKEND_SESSION_SECRET` with at least 32 UTF-8 bytes

Real pet grants require the app's actual Steamworks Inventory itemdef and publisher key. Steam AppID `480` is only useful for local client initialization tests.

## Development Notes For Future Agents

- Prefer small, behavior-preserving changes; the project is intentionally simple.
- Do not replace Steam Inventory authority with client-side saves or Steam Stats for pets.
- Do not make the whole overlay window mouse-transparent with `WS_EX_TRANSPARENT`; the dog must still receive clicks.
- Keep Windows tray behavior in `NativeWindowBridge` isolated from the Godot scene logic.
- Use Godot 4.6.3 C# tooling for build/smoke validation unless the project is intentionally upgraded.
- If adding user-visible pet status, show backend-confirmed totals and pending in-memory grants separately.
