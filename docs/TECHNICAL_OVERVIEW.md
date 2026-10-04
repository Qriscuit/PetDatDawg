# Pet Da Dog Technical Overview

Compact reference for the current code flow, project structure, Steam pipeline, and safety boundaries.

## 1. Code Flow Chart

```text
APP START
  |
  v
Godot reads project.godot
  |
  +--> Autoload: PetSettings
  |       loads user://pet_settings.cfg
  |       stores local UI/dog preferences only
  |
  +--> Autoload: AccessoryWardrobe
  |       discovers Sprites/Accessories textures
  |       loads local placements from user://accessories.cfg
  |
  +--> Autoload: AccessoryEditingSession
  |       shares Items mode, selected item, and desktop editing transactions
  |
  +--> Autoload: PatrolRoute
  |       loads normalized screen stops and enablement from user://patrol_route.cfg
  |
  +--> Autoload: NativeWindowBridge
  |       applies Win32 overlay styles
  |       installs tray menu and Alt+` hotkey
  |
  v
Godot loads Main.tscn
  |
  v
DesktopPet._Ready()
  |
  +--> gets FootAnchor/VisualRoot/PetSprite
  +--> renders equipped accessories as PetSprite children
  +--> connects NativeWindowBridge signals
  +--> configures transparent borderless overlay window
  +--> initializes SteamIntegration
  +--> starts BackendPetClient authentication if Steam is ready
  |
  v
DesktopPet._Process(delta), every frame
  |
  +--> Items / route editing active: polls editing gestures and keeps dog upright
  +--> leaving Items: resumes enabled patrol, otherwise falls to the walking area
  +--> normal play: follows the ordered patrol loop or walks left/right with hop animation
  +--> applies dog scale and transparency settings
  +--> animates existing floating hearts regardless of editing mode
  +--> recomputes padded integer render bounds around dog, accessories, and hearts
  |       includes editing handles, selection outline, Move dog pill, and placement ghost
  |       changes the native region only when those bounds change
  +--> uses cursor alpha hit testing for desktop passthrough
  +--> runs Steam callbacks
  +--> retries pending backend pet grants
  +--> refreshes browser status if StatusWindow exists
```

```text
DOG CLICK TO STEAM-AUTHORITATIVE PETS

User left-clicks dog
  |
  v
DesktopPet._Input()
  |
  +--> accessory / patrol editing mode: route gesture to editing controls, then return
  |       no pet grants or pet feedback from editing
  +--> reject if click-through is enabled
  +--> reject if transparency <= 0.01
  +--> reject if click is outside current dog bounds
  +--> reject if clicked texture pixel is transparent
  |
  v
BackendPetClient.EnqueuePetGrant(Guid)
  |
  v
SteamIntegration.GetAuthTicketForWebApi("petdadog-backend")
  |
  v
POST /v1/auth/steam
  |
  +--> backend calls Steam AuthenticateUserTicket
  +--> backend returns signed short-lived session token
  |
  v
POST /v1/pets/grant
  |
  +--> backend derives Steam requestid from SteamID + clientEventId
  +--> backend calls Steam Inventory AddItem
  +--> duplicate requestid replays without double-granting
  |
  v
GET /v1/pets
  |
  +--> backend calls Steam Inventory GetInventory
  +--> backend sums configured Pets itemdef quantity
  |
  v
Client receives confirmed pets total
  |
  v
StatusWindow displays backend-confirmed pets
```

```text
TRAY, BROWSER, DESKTOP EDITING, AND SETTINGS

Windows tray icon click
  |
  v
NativeWindowBridge popup menu
  |
  +--> Open Pet Da Dog
  |       emits StatusRequested
  |       DesktopPet opens/focuses the portrait StatusWindow browser
  |       visible, nonminimized Items activates desktop editing
  |
  +--> Quit Pet Da Dog
          calls GetTree().Quit()
          DesktopPet disposes Steam/backend clients
          NativeWindowBridge removes tray icon

StatusWindow setting changed
  |
  v
PetSettings setter
  |
  +--> clamps value
  +--> saves user://pet_settings.cfg
  +--> emits SettingsChanged
  |
  v
DesktopPet applies visual/input/window effects
  |
  +--> Always On Top: Godot flag + Win32 z-order
  +--> Doggo Click Through: full native passthrough, no pet grants
  +--> Transparency 0.0: invisible, passthrough, no pet grants
  +--> Dog Scale: recalculates overlay height and dog bounds
  +--> UI Scale: resizes the browser UI

Browser tab changed or browser closed/hidden
  |
  +--> Items visible and not minimized: edit the real desktop dog
  |       full usable-screen transparent overlay and temporary visibility/input/topmost overrides
  +--> other tabs / hidden / closed / minimized: cancel accessory editing gesture
  |       restore preferences, resume enabled patrol or fall to ordinary walking
  +--> existing hearts, cursor polling, Steam callbacks, and backend retries continue
```

```text
ACCESSORY PLACEMENT

StatusWindow Items page
  |
  +--> browse collapsible Wings / Collars / Glasses / Decorations / Text shelves
  |       vertical category scrolling and one horizontal card row per shelf
  +--> select bundled artwork from Sprites/Accessories or built-in Text Box
  +--> click the actual desktop dog to place it
  +--> drag equipped artwork directly on the dog to adjust its position
  +--> use accessory resize/rotation handles or inspector sliders
  +--> drag the dog body / Move dog pill and use its corner resize handle
  +--> edit available colors, text, and Show textbox background in the inspector
  +--> right-click accessory/dog for Move up/down one layer, or use inspector Up/Down
  +--> Undo reverses the last outfit action (Ctrl + Z)
  |
  v
AccessoryWardrobe
  |
  +--> one placement per item ID
  +--> normalized anchor inside dog bounds, or near-dog bounds for Text Box
  +--> bottom-to-top layer stack includes the dog and every equipped item
  +--> emits Changed for immediate inspector/overlay updates
  +--> saves local cosmetics to user://accessories.cfg
  |
  v
DesktopPet synchronizes accessory sprites and text nodes under PetSprite
  |
  +--> inherits walking, hopping, flipping, scale, and transparency
  +--> sets accessory ZIndex relative to the dog; negative values draw behind it
  +--> cancels text mirroring when the dog turns
  +--> includes accessory overhang in overlay and walk bounds
  +--> accessory-only pixels pass clicks through to the desktop
```

```text
PATROL ROUTE

Settings -> Set patrol route / Edit route
  +--> hide browser and open native route toolbar
  +--> click numbered desktop stops; drag to move, right-click to remove
  +--> draft needs two distinct stops, with at most 16 stops total
  +--> Done commits and enables; Cancel preserves the saved route
  |
  v
PatrolRoute saves normalized points + enabled in user://patrol_route.cfg
  |
  v
DesktopPet follows feet positions in order: 1 -> 2 -> 3 -> 1
  +--> clamp stops for outfit margins; skip zero-length segments
  +--> Use patrol route off preserves stops and returns to ordinary walking
  +--> Clear route removes stops and returns to ordinary walking
```

## 2. Legend

| Name | What it is | Main responsibility |
| --- | --- | --- |
| Godot | Game engine/runtime | Opens the window, loads scenes, calls `_Ready`, `_Process`, `_Input`, and `_ExitTree`. |
| `project.godot` | Client project config | Registers autoloads, the shared authored Theme, and default window settings. |
| `Main.tscn` | Main client scene | Contains the dog hierarchy and authored desktop-control instances; exports the browser and text-accessory templates. |
| `DesktopPet.cs` | Main client controller | Moves/hops the dog, manages overlay size, dog-pixel clicks, status window, Steam ticking, and backend retries. |
| `PetSettings.cs` | Local settings autoload | Loads/saves user preferences only. It must never save pets or currency totals. |
| `AccessoryWardrobe.cs` | Local cosmetic autoload | Discovers accessory textures and manages equipped IDs, normalized positions, the shared dog/accessory layer stack, and local persistence. |
| `AccessoryEditor.cs` | Items browser and inspector | Binds `UI/AccessoryEditor.tscn`, populates reusable catalog templates, and shares selection and gesture state with the desktop editing session. |
| `AccessoryCategoryRow.cs` | Collapsible category shelf | Binds its authored heading and horizontal card row; includes the shared category classifier. |
| `AccessoryCard.cs` / `AccessoryColorControls.cs` | Reusable Items controls | Bind authored card and color-popup templates, with editor sample data and live wardrobe state. |
| `AccessoryEditingSession.cs` | Shared local editing autoload | Tracks Items activity, selection, pending placement, and outfit transactions; cancels gestures when editing ends. |
| `AccessoryLayerMenu.cs` | Native Godot layer popup | Moves the clicked accessory or dog one step up/down in the shared stack; disables moves at its boundaries. |
| `DesktopAccessoryControls.cs` | Direct desktop editing controls | Draws selection/handles/ghost and handles moving/resizing the dog plus moving/resizing/rotating accessories. |
| `PatrolRoute.cs` | Local route autoload | Validates normalized saved stops, enablement, and a separate editing draft. |
| `DesktopPatrolEditor.cs` | Desktop route editor | Draws numbered stops and loop arrows; adds, drags, and removes draft stops. |
| `PatrolRouteToolbar.cs` | Native route controls | Provides Done, Cancel, Undo last, and Clear without sharing the dog's canvas. |
| `PetTextAccessory.cs` | Dynamic accessory artwork | Draws editable, wrapping, colored text with an optional rounded background in the desktop overlay. |
| `DesktopAppearance.cs` | Inspector-editable drawing resource | Exposes desktop selection, handles, patrol paths, Move dog, placement preview, and text presentation through `UI/Theme/DesktopAppearance.tres`. |
| `NativeWindowBridge.cs` | Windows bridge | Uses Win32 APIs for tray menu, no-activate overlay behavior, passthrough styles, topmost state, and Alt+` hotkey. |
| `StatusWindow.cs` | Portrait browser window | Binds the authored browser scene, navigation, live status, and local preferences; previews tabs in the editor and controls Items activity during play. |
| `WoodlandTheme.cs` | Shared UI resource helper | Loads `DefaultTheme.tres` and external style resources; supplies palette/icon lookups and color-picker integration. |
| `SteamIntegration.cs` | Steamworks.NET wrapper | Initializes Steam, requests Web API auth tickets, runs callbacks, and shuts Steam down. |
| `BackendPetClient.cs` | Client HTTP queue | Authenticates with the backend, stores pending grant GUIDs in memory, retries failures, and keeps the last confirmed pets total. |
| `Program.cs` | Backend entrypoint | Defines HTTP routes, validates config, verifies Steam tickets, signs sessions, grants pets, and reads inventory totals. |
| Steam Web API ticket | Client proof of identity | Sent to backend so the backend can verify the local Steam user. |
| Backend session token | Temporary API auth | HMAC-signed token issued after Steam ticket validation. |
| Steam Inventory | Currency authority | Stores the real Pets item quantity. The client only displays totals returned through the backend. |
| Steam publisher key | Backend secret | Required for Inventory Web API calls. It must never be included in the Godot client. |
| `clientEventId` | Grant idempotency key | A GUID created per valid click so retries do not double-grant. |

## 3. Project Map

```text
S:/CodexProjects/PetDaDog
  |
  +-- AGENTS.md
  |     short project rules for future Codex sessions
  |
  +-- docs/
  |     TECHNICAL_OVERVIEW.md, GODOT_UI_GUIDE.md, and UI_EDITOR_GUIDE.md
  |
  +-- Pet_Da_Dog_CSharp/
  |     Godot 4.6.3 C# desktop overlay client
  |
  |     +-- project.godot
  |     +-- Main.tscn
  |     +-- StatusWindow.tscn
  |     +-- Sprites/Doggo.png
  |     +-- Sprites/Accessories/
  |     |     bundled equipable accessory artwork
  |     +-- Art/UI/Woodland/
  |     |     generated UI textures, SVG controls, and final generation prompts
  |     +-- UI/
  |     |     AccessoryEditor.tscn, AccessoryCategoryRow.tscn, AccessoryCard.tscn
  |     |     AccessoryColorControls.tscn, AccessoryLayerMenu.tscn
  |     |     DesktopAccessoryControls.tscn, DesktopPatrolEditor.tscn
  |     |     PatrolRouteToolbar.tscn, PetTextAccessory.tscn
  |     |     Theme/DefaultTheme.tres and shared style/font/appearance resources
  |     +-- Scripts/
  |           AccessoryCard.cs
  |           AccessoryCategoryRow.cs
  |           AccessoryColorControls.cs
  |           AccessoryEditor.cs
  |           AccessoryEditingSession.cs
  |           AccessoryGeometry.cs
  |           AccessoryLayerMenu.cs
  |           AccessoryWardrobe.cs
  |           DesktopAccessoryControls.cs
  |           DesktopAppearance.cs
  |           DesktopPatrolEditor.cs
  |           DesktopPet.cs
  |           NativeWindowBridge.cs
  |           PatrolRoute.cs
  |           PatrolRouteToolbar.cs
  |           PetTextAccessory.cs
  |           PetSettings.cs
  |           StatusWindow.cs
  |           WoodlandTheme.cs
  |           SteamIntegration.cs
  |           SteamAppId.cs
  |           BackendPetClient.cs
  |     +-- Tests/
  |           AccessorySmokeTest.cs and AccessorySmokeTest.tscn
  |           Run-AccessorySmoke.ps1
  |
  +-- PetDaDog.Backend/
        ASP.NET Core minimal API backend

        +-- Program.cs
```

## 4. Runtime Flow By File

`project.godot` starts the Godot app with transparent window support, sets `UI/Theme/DefaultTheme.tres` as the GUI custom theme, and loads autoload singletons: `/root/PetSettings`, `/root/AccessoryWardrobe`, `/root/AccessoryEditingSession`, `/root/PatrolRoute`, and `/root/NativeWindowBridge`.

`Main.tscn` creates the visible pet scene. `DesktopPet` is the root script. The dog sprite sits under `FootAnchor/VisualRoot/PetSprite` so movement, hopping, scale, and opacity can be applied cleanly without changing unrelated scene nodes. Root children `AccessoryControls` and `PatrolEditor` instance `UI/DesktopAccessoryControls.tscn` and `UI/DesktopPatrolEditor.tscn`; their sample drawings are disabled in Main. The root's exported `StatusScene` and `TextAccessoryScene` select `StatusWindow.tscn` and `UI/PetTextAccessory.tscn` for runtime instantiation.

`DesktopPet.cs` is the central coordinator. On startup it reads settings, configures the desktop overlay, connects tray/hotkey, accessory-editing, and patrol signals, initializes Steam, and starts backend auth. When Items is visible in a nonminimized browser, it expands the transparent overlay to the usable screen area and holds the real dog upright for direct manipulation. Leaving Items, hiding/closing the browser, or minimizing it cancels the active accessory gesture and restores normal preferences. An enabled patrol resumes smoothly from the edited position; otherwise the dog falls under gravity before ordinary walking resumes. Dogs, Shop, and Settings allow walking while the browser remains open, except during explicit patrol editing. Every frame still animates existing hearts, recomputes rendering bounds, polls cursor hit tests, runs Steam callbacks, retries queued grants, and refreshes browser status.

`NativeWindowBridge.cs` is Windows-specific. It keeps the overlay out of the taskbar, prevents focus stealing, applies `HWND_TOPMOST` or `HWND_NOTOPMOST`, toggles `WS_EX_TRANSPARENT` when input should pass through, owns the tray icon, and turns tray/hotkey events into Godot signals. Accessory and patrol editing temporarily override click-through and topmost preferences; Items preserves passthrough outside its editable pixels/controls, while drawing a patrol accepts route gestures across the usable desktop. Leaving editing restores the saved preferences. Its periodic refresh reads the actual extended styles and topmost state to detect changes requiring repair. Unchanged routine state skips native style, frame-change, and z-order writes; `SWP_FRAMECHANGED` is used only when style bits change.

The editing overlay is one pixel shorter than the usable screen height. An exact screen/usable rectangle makes Godot's borderless Windows window maximized or fullscreen, which blocks subsequent size and position requests ([Godot's Windows mode handling](https://github.com/godotengine/godot/blob/4.6.3-stable/platform/windows/display_server_windows.cpp#L5435)). `DesktopPet.SetOverlayGeometry` restores Windowed mode before changing the root `Window.Size` and `Position`, keeping its viewport and native geometry synchronized. Returning to the walking strip verifies actual window geometry before accepting a cached layout. Landing uses the usable-screen ground rather than the shortened editing viewport. Regression checks wait through native frames after landing and verify the dog's actual screen position while it walks.

`PetSettings.cs` persists local preferences in `user://pet_settings.cfg`: always-on-top, click-through, visibility, dog size, menu size, and welcome-hint dismissal. Settings are saved immediately and broadcast through `SettingsChanged`. The desktop dog's resize handle changes the same Dog size preference, bounded to 50–200%. Items temporarily renders the dog fully visible and keeps it editable/on top without overwriting saved visibility, click-through, or topmost preferences. `StatusWindow` only reapplies window sizing when menu size changes; `user://menu_layout.cfg` retains the browser dimensions and position across reopening and restarts.

`PatrolRoute.cs` maintains saved `Points` and separate `DraftPoints`, with at most `MaxPoints = 16`. Each finite normalized coordinate is clamped to 0–1, and consecutive approximate duplicates are rejected or coalesced. At least two distinct stops are required by `CanFinish` and `SetEnabled(true)`. `BeginEdit` clones saved stops; `AddPoint`, `MovePoint`, `RemovePoint`, `UndoLastPoint`, and `ClearDraft` affect only the draft. `FinishEdit` commits a valid draft, enables it, saves, and ends editing; `CancelEdit` discards it without changing the saved route or enablement. `ClearRoute` clears and disables the saved route, also ending an active draft. `Changed` reports mutations, and `EditingChanged` reports editing transitions. These local preferences have no Steam or pet-grant authority.

The route file is `user://patrol_route.cfg`, with `[route] points` as a packed Vector2 array and `enabled` as a bool. Loading also accepts Variant arrays, skips malformed/nonfinite entries, clamps coordinates, removes consecutive duplicates, limits the list to 16, and leaves invalid stationary routes disabled. `LastSaveSucceeded` records disk-save success. A valid `FinishEdit` still returns true and ends editing if saving fails; the current run uses the committed route while Settings reports the persistence failure.

`DesktopPet` maps route stops to the primary usable screen and clamps feet positions for outfit margins. Enabled patrol keeps the transparent overlay across that area, retaining ordinary alpha-based passthrough and petting outside editing. `StepPatrol` travels through the stops in order and closes the loop from the last to the first; it can cross multiple segments in a frame and bounds traversal when clamping produces zero-length segments. Disabling or clearing patrol restores the ordinary gravity/ground walking behavior while preserving normal hearts and syncing.

`AccessoryWardrobe.cs` recursively loads supported images from `Sprites/Accessories/` and crops transparent borders. It keeps one placement per item ID, clamps finite normalized positions, bounds per-accessory size to 0.5–2.0 and rotation to −180°–180°, and broadcasts `Changed`. Position, tint, size, and rotation preferences save in `user://accessories.cfg`; no currency or grants are stored there. Legacy files default to size 1 and rotation 0. A bounded 30-entry in-memory history groups drag/color gestures and restores complete cosmetic snapshots for Undo. Equipped sprites inherit dog animation; `AccessoryGeometry` supplies consistent rotated bounds for desktop controls and clipping.

`AccessoryWardrobe.LayerOrder` is a bottom-to-top list containing `DogLayerId` (`@dog`) and each equipped ID exactly once. `GetLayerIndex`, `CanMoveLayer`, and `MoveLayer(id, direction)` expose adjacent swaps: +1 moves forward/up and −1 backward/down, including across the dog. Invalid IDs, directions, and boundary moves are rejected. New equips append at the top; moving an existing placement retains its layer. Removal deletes its entry, and Clear leaves the dog alone. Save writes a packed string array to `[layers] order` in `user://accessories.cfg`. Loading accepts packed strings or Variant arrays, removes duplicate/unknown entries, places a missing dog below the outfit, and appends missing equipped IDs in equip order. Legacy files therefore place all accessories above the dog. Complete Undo and edit-cancel snapshots include layer order.

`DesktopPet` keeps the dog sprite at `ZIndex = 0` and assigns each accessory or text node `GetLayerIndex(id) - GetLayerIndex(DogLayerId)`. These nodes remain children of the dog and inherit its transforms; negative relative indices render behind the dog and positive indices in front. Layers do not alter text reflection correction, opacity, geometry, or currency handling.

The built-in `@text-box` accessory stores its message and background visibility in the same cosmetic file. `SetText` sanitizes line breaks and limits the message to 64 grapheme clusters. It has bounded placement anchors around the dog, live message editing and text color, and the same transform and Undo controls. Show textbox background binds to `GetTextBackgroundVisible` / `SetTextBackgroundVisible`; the preference remains when unequipped and is part of Undo snapshots. Older files default to a visible background. `PetTextAccessory` wraps text and reduces font size to fit; disabling the background skips only its rounded fill and border, preserving text color and alpha. The desktop node follows the dog's animation while canceling horizontal text reflection when the dog turns.

Pet feedback uses the original heart sprite cropped at runtime to its opaque bounds. It pops quickly to about 46 pixels, then rises 90 pixels and drifts outward 46 pixels while shrinking and fading over 1.8 seconds. Existing hearts continue during Items editing; edit gestures do not spawn new feedback or grants. Overlay height and walk limits reserve this trajectory; the Windows render region includes each active heart. At most eight hearts exist at once, and heart-only clicks never enqueue grants.

`RecolorableIds` explicitly enables the three neutral JPG assets. Their black backgrounds are removed into runtime RGBA textures with softened edges, preserving gray and white artwork for RGB tinting. `SetTint` rejects nonfinite colors and fixed-color assets, clamps RGB, and keeps alpha opaque. Color preferences can be set before placement and survive unequipping. The editor uses a native HSV wheel with immediate desktop/thumbnail updates, then saves on popup close, selection/page changes, or Reset white. Desktop sprites inherit the dog's opacity, with full visibility temporarily applied during Items editing.

`StatusWindow.tscn` authors the portrait browser, initially 640 × 780 pixels with a 560 × 600 minimum. Its four tabs are Dogs, Items, Shop, and Settings, with Items selected initially. `StatusWindow.cs` binds named controls and updates live state without rebuilding the layout. The root's exported Initial Tab previews each page in the editor; runtime sizing uses the authored Size, Min Size, and Content Scale Factor as its baseline, then applies the player's Menu size and saved layout. Dogs presents only the current original dog. Shop is a noninteractive coming-soon page without a purchase flow. Preferences precede the connection summary and expandable technical details in Settings. Confirmed pets and waiting clicks remain separate; Quit is in Settings and the tray. First-use hints explain browser access and closing without quitting. Changes flow through `PetSettings` or `AccessoryWardrobe`.

`UI/AccessoryEditor.tscn` supplies Items with a compact side inspector and a vertically scrolling category stack. Its script binds the vertical Size and Rotation sliders, contextual text/color and Show textbox background controls, layer Up/Down buttons, Remove, Undo, and Clear all. Layer controls show whether the selected equipped item is behind or in front of the dog and disable boundary moves. Selecting its catalog card makes these controls available even if desktop artwork is fully covered. Authored `AccessoryCategoryRow.tscn` instances provide collapsible Wings, Collars, Glasses, Decorations, and Text shelves, each with one horizontal card row. Empty Collars starts collapsed. Its classifier checks built-in text, then wing, collar, and glasses/goggle names; remaining artwork goes in Decorations. The editor's exported Card Scene, Category Row Scene, and Color Controls Scene select the reusable templates. Editor sample cards are replaced with live wardrobe cards at runtime; their layout and theme remain authored. Equipped cards select for editing rather than starting placement. `AccessoryColorControls.tscn` authors the eight swatches, Reset, Done, and Advanced actions around Godot's color picker; Advanced reveals numeric modes and hex entry.

`AccessoryEditingSession.cs` shares active mode, selection, placement state, and gesture transactions between the browser and desktop. It flushes inspector-owned edits before desktop transactions start and cancels active manipulation when editing deactivates. The authored `DesktopAccessoryControls.tscn` instance draws the Move dog pill, selection outline, resize/rotation handles, and placement ghost using its exported Appearance resource. Dog body/pill dragging repositions the actual dog; its corner handle persists Dog size through `PetSettings`. Accessory dragging and resize/rotation handles update `AccessoryWardrobe` within its transform limits. The current runtime edits the real desktop dog and does not instantiate a preview window.

`DesktopAccessoryControls._layerMenu` instances its exported `LayerMenuScene`, normally `UI/AccessoryLayerMenu.tscn`, when models are bound. Right-clicking the visible accessory or dog while Items is active opens it. The popup's actions, styling, Size, and Min Size are authored. Native Shrink Width/Height flags are false so opening preserves its dimensions; runtime changes its target heading and boundary disabled states. Action IDs 1 and 2 map to up/down. It completes current gestures and flushes inspector edits before changing layers, saves successful moves, and closes when editing ends or its target is no longer valid. It owns a separate `World2D` (`World2D = new World2D()`) so the desktop dog's canvas does not bleed into its native window. It uses the shared Woodland theme and an opaque background.

`UI/DesktopPatrolEditor.tscn` draws numbered markers and directional loop lines from its Appearance resource. Its script handles click-to-add, drag-to-move, and right-click-to-remove gestures. During route editing, `DesktopPet._Input` routes input here and returns before dog petting or browser actions. Its exported Toolbar Scene selects `UI/PatrolRouteToolbar.tscn`, a separate opaque native window with its own `World2D`. `PatrolRouteToolbar.cs` binds its authored Undo last, Clear draft, Cancel/Escape, and Done/Enter buttons; Backspace undoes the last stop. The authored Size supplies its runtime baseline, and exported dynamic-text/placement properties control route summaries and screen positioning. Opening remains deferred through native frames. The Settings Patrol route card offers Set patrol route or Edit route, Use patrol route, and Clear route. Edits started from this card hide the browser and return to Settings after finishing or canceling.

`UI/Theme/DefaultTheme.tres` authors shared fonts, colors, button variations and states, sliders, checkboxes, and popup styling. `WoodlandTheme.cs` loads this resource and the external Window, Panel, PlainPanel, Catalog, and Focus style resources, with palette/icon lookups and color-picker integration. Browser Menu size changes apply authored Content Scale Factor and sizing without replacing its theme. Stretchable nine-slice styleboxes reuse the three Woodland PNG surfaces; the theme references 14 SVG icon resources, including spare legacy icons. Labels remain actual controls. Windows title bars and the Win32 tray menu retain operating-system styling. Category shelves scroll horizontally when cards exceed their available width. UI art is excluded from the wardrobe catalog; dog and accessory source artwork is unchanged.

`DesktopAppearance.cs` is a Tool/GlobalClass resource backed by `UI/Theme/DesktopAppearance.tres`. Its exported fields configure code-drawn selection outlines, handles and optional icons, Move dog styling/text, patrol markers/lines/arrows, text bubble padding/fonts/outlines, and placement opacity. `DesktopAccessoryControls.tscn` and `DesktopPatrolEditor.tscn` expose Show Editor Preview with sample bounds or route points; `PetTextAccessory.tscn` previews sample text and background. Tool previews draw from these authored properties without activating live models, saving preferences, or showing runtime windows. Native toolbar/menu scripts return before model bindings and native show/focus operations in editor mode. StatusWindow exports dynamic captions and live pet/connection/patrol text formats. Items templates export inspector instructions/status formats, card state captions/tooltips, and category header formats; the editor also exports its Color Wheel Shape. Category identities still match the classifier, while Display Title independently changes the visible shelf heading. See [UI_EDITOR_GUIDE.md](UI_EDITOR_GUIDE.md) for Inspector editing and bound-node rules.

`SteamIntegration.cs` wraps Steamworks.NET. It uses `PDD_STEAM_APP_ID` or `steam_appid.txt`, calls `SteamAPI.Init`, requests a Web API ticket for `petdadog-backend`, runs callbacks every frame, and calls `SteamAPI.Shutdown` on exit.

`BackendPetClient.cs` is intentionally non-authoritative. It queues click grants in memory, authenticates with the backend using a Steam ticket, posts grants, retries failures during the current run, and stores only the latest confirmed total returned by the backend.

`PetDaDog.Backend/Program.cs` is the trusted server. It validates required environment variables at startup, exposes `/health`, `/v1/auth/steam`, `/v1/pets`, and `/v1/pets/grant`, talks to Steam partner APIs with the publisher key, and signs short-lived backend session tokens.

## 5. API And Steam Pipeline

Client API calls:

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/v1/auth/steam` | Exchange Steam Web API ticket for backend session token. |
| `GET` | `/v1/pets` | Read backend-confirmed pets total from Steam Inventory. |
| `POST` | `/v1/pets/grant` | Grant one pet for one valid dog click. |

Backend Steam calls:

| Steam API | Why it is used |
| --- | --- |
| `ISteamUserAuth/AuthenticateUserTicket` | Proves the session belongs to a real Steam user. |
| `IInventoryService/AddItem` | Grants the configured Pets itemdef. |
| `IInventoryService/GetInventory` | Reads the real inventory quantity used as the pets total. |

Important rule: the client never decides the final pets total. It only asks for a grant and displays the confirmed number returned through the backend.

## 6. Overlay And Click Rules

The overlay is a borderless transparent Godot window. Ordinary walking uses a strip aligned to the bottom of the usable screen; enabled patrol uses the primary usable screen area. Visible, nonminimized Items expands the overlay for direct editing. Leaving Items or hiding/closing/minimizing the browser cancels the accessory gesture and restores preferences, then resumes patrol or lets the dog fall to the ordinary walking area. Normal transparent space should pass clicks to whatever app is behind the dog.

The mouse region includes the dog, accessory bounds, and active hearts because Windows also uses this region to clip the overlay's rendering. Editing adds handles, selection outlines, the Move dog pill, and the placement ghost to those drawing bounds. Bounds are recomputed every frame, padded for filtered edges, and rounded outward to integer pixels; a cached integer rectangle prevents native region updates when bounds are unchanged. Global cursor polling continues on cache hits. During Items editing it captures only editing interactions using dog/image-accessory alpha tests, textbox bounds, visible controls, and the bounded area for an armed placement; other desktop space passes through. `_Input` routes editing gestures and returns before the grant path. During normal play it independently checks original dog texture alpha before granting pets; accessory/text/heart-only pixels pass through.

Desktop editing hits traverse `LayerOrder` from top to bottom. Image hits use their texture alpha, text uses its bounds, and the dog's original visible pixels stop traversal before covered lower accessories. This gives dragging and right-click layer menus the visible target. A fully covered accessory remains selectable through its catalog card and inspector layer buttons. Normal dog-alpha tests and one-grant-per-valid-click behavior are unchanged by the layer stack.

Explicit patrol editing reserves the full usable overlay region for route markers and gestures. It pauses movement, temporarily forces dog visibility/input/topmost, and suppresses grants and dog-menu actions. The native toolbar remains a separate canvas. Done or Cancel restores saved preferences; playback again captures only valid dog pixels for petting.

Outside Items editing, the dog is non-interactive when either setting is true:

| Condition | Result |
| --- | --- |
| `Doggo Click Through = true` | Clicks pass through the dog and no grants are queued. |
| `Doggo Transparency <= 0.01` | Dog is invisible, clicks pass through, and no grants are queued. |

`Always On Top` is separate from click handling. It controls window stacking only. Turning it off makes the normal overlay non-topmost; turning it on restores topmost behavior. Items temporarily forces full visibility, editing input, and topmost stacking without changing the saved preferences. Those preferences take effect again when editing ends. None of these editing overrides authorizes pet grants.

## 7. Security Boundary

| Data | Stored where | Authority |
| --- | --- | --- |
| Local dog preferences | `user://pet_settings.cfg` | Client-local only. |
| Accessory positions, colors, sizes, rotations, text, background visibility, and dog/accessory layer order | `user://accessories.cfg` | Client-local cosmetics only; Undo stays in memory. |
| Browser dimensions and position | `user://menu_layout.cfg` | Local window preferences only. |
| Normalized patrol stops and enablement | `user://patrol_route.cfg` | Client-local walking preferences only. |
| Patrol editing draft | Memory only | Cancel preserves the saved route; no currency authority. |
| Items editing activity, dog position, and temporary visibility/input/topmost overrides | Memory only | Session-local editing state; saved preferences are preserved. |
| Pending click grants | Memory only | Temporary client queue. |
| Confirmed pets total | Backend response only | Steam Inventory. |
| Steam publisher key | Backend environment only | Server secret. |
| Backend session token | Client memory | Backend-signed temporary auth. |

Do not add local pet saves, editable pet totals, client-side publisher keys, or Steam Stats currency. Pets are a Steam Inventory item quantity.

## 8. Build And Smoke Test

Build the Godot client from `Pet_Da_Dog_CSharp/`:

```powershell
dotnet build .\PetDaDogCSharp.csproj
```

Run the accessory smoke checks from the same directory:

```powershell
.\Tests\Run-AccessorySmoke.ps1
```

The runner uses Godot 4.6.3, imports artwork, builds only the client, and disables Steam and tray integration. Test settings are isolated under `.godot/accessory-smoke-home`. It covers category classification/collapse and horizontal shelves, browser lifecycle/layout, direct desktop placement and dog/accessory handles, gesture cancellation and preference restoration, placement/text/background/transform/color persistence, Undo, movement/clipping bounds, heart animation, and rejection of grants during editing. Current native captures are saved under `.godot/`; former floating-preview screenshots may remain as historical UI references. Success prints `ACCESSORY_SMOKE_PASS:`. Pass `-SkipBuild` when resources and the client build are already current.

Build the backend:

```powershell
dotnet build S:\CodexProjects\PetDaDog\PetDaDog.Backend\PetDaDog.Backend.csproj
```

Smoke test with the known-good Godot 4.6.3 console runtime:

```powershell
& 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe' --path 'S:\CodexProjects\PetDaDog\Pet_Da_Dog_CSharp' --scene 'res://Main.tscn' --quit-after 5 --no-header
```

Avoid the local Godot 4.6.2 console runtime for this project because it was observed to hang or crash during smoke tests.

