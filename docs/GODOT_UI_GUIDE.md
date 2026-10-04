# Godot UI Guide For Pet Da Dog

This guide is a practical map for working on the Godot client in this repo. It focuses on the UI pieces you will touch most often: scenes, nodes, windows, controls, tabs, dog clicks, and visual feedback.

## 1. Godot Mental Model

Godot apps are built from scenes. A scene is a saved tree of nodes, usually stored in a `.tscn` file. Each node has a type, a name, properties, and optional child nodes.

Scripts attach behavior to nodes. In this project the scripts are C# classes that inherit from Godot node types. For example, `DesktopPet : Node2D` means the script controls a 2D scene node, while `StatusWindow : Window` means the script controls a separate Godot window.

The most important callbacks are:

| Callback | Meaning |
| --- | --- |
| `_Ready()` | Runs once after the node enters the scene tree. Use it for setup and node lookups. |
| `_Process(delta)` | Runs every frame. Use it for animation, polling, and updates that must stay current. |
| `_Input(event)` | Receives input events. Use it for mouse and keyboard handling. |
| `_ExitTree()` | Runs when the node leaves the tree. Use it for cleanup. |

Godot node categories matter:

| Type | Used for | Examples in this project |
| --- | --- | --- |
| `Node2D` | 2D world objects with positions, scale, rotation | `DesktopPet`, `FootAnchor`, `VisualRoot`, `DesktopAccessoryControls` |
| `Sprite2D` | Draws a texture in 2D space | `PetSprite`, floating hearts |
| `Control` | UI widgets and layout | labels, buttons, sliders, panels |
| `Window` | A separate OS/Godot window | `StatusWindow` browser, main transparent desktop overlay |

`Node2D` positioning is manual. `Control` positioning is usually managed by containers such as `VBoxContainer`, `HBoxContainer`, `GridContainer`, and `ScrollContainer`.

## 2. Project UI Map

`project.godot` is the Godot project config. It sets the main scene to `Main.tscn`, enables transparent window settings, and autoloads five global scripts:

| Autoload | Path | Purpose |
| --- | --- | --- |
| `PetSettings` | `/root/PetSettings` | Loads and saves local dog/UI settings. |
| `AccessoryWardrobe` | `/root/AccessoryWardrobe` | Discovers accessory textures and saves local cosmetic placements. |
| `AccessoryEditingSession` | `/root/AccessoryEditingSession` | Shares Items selection, editing mode, and gesture transactions between the browser and desktop dog. |
| `PatrolRoute` | `/root/PatrolRoute` | Saves normalized screen stops, route enablement, and a separate route-editing draft. |
| `NativeWindowBridge` | `/root/NativeWindowBridge` | Applies Windows overlay styles, tray menu, and hotkeys. |

`Main.tscn` is tiny:

```text
DesktopPet
  +-- FootAnchor
        +-- VisualRoot
              +-- PetSprite
```

`DesktopPet.cs` is the main controller. It loads the dog texture, manages the transparent desktop overlay, walks the dog normally or along its enabled patrol route, renders equipped accessories, checks dog-pixel clicks, opens the browser, spawns hearts, and coordinates Steam/backend pet grants. During Items editing it expands the overlay across the usable screen area and lets `DesktopAccessoryControls.cs` manipulate the actual desktop dog. Equipped accessory sprites are children of `PetSprite`, so they inherit dog movement, flipping, animation, scale, and transparency.

`StatusWindow.tscn` contains a `Window` node with `StatusWindow.cs` attached. It is the portrait browser for Dogs, Items, Shop, and Settings. Its controls are built in C#. Items edits the real desktop dog through the shared editing session; the current runtime does not open a separate preview window.

## 3. How The Browser And Desktop Editing Work

`StatusWindow.cs` builds its UI in `BuildUi()`. The pattern is:

```text
StatusWindow: portrait browser
  +-- PanelContainer background
        +-- MarginContainer padding
              +-- VBoxContainer root
                    +-- header: tab buttons + confirmed pets + waiting clicks
                    +-- dismissible first-use hint
                    +-- page container

Main transparent overlay during Items editing
  +-- actual desktop dog and equipped accessories
  +-- DesktopAccessoryControls: selection, handles, placement ghost, Move dog pill
```

The browser starts at 640 × 780 pixels, with a 560 × 600 minimum at the default menu scale. It is a normal resizable window. The desktop dog itself is the editing surface, so it can be moved around the usable desktop while choosing its outfit.

The header keeps navigation, the game name, confirmed pets, and waiting clicks visible. Quit Pet Da Dog belongs to Settings and the tray menu; closing the browser keeps the desktop dog running. First-time launches open it with a hint explaining right-click and tray access; Got it remembers its dismissal.

Accessory editing is active only while Items is visible in an open, nonminimized browser. The desktop dog rests upright without walking, hopping, squash/stretch, or tilt while being edited. Switching tabs, hiding/closing the browser, or minimizing it cancels any active accessory gesture and restores normal interaction preferences. An enabled saved patrol resumes; otherwise the dog falls under gravity to its usual walking area before resuming movement. Dogs, Shop, and Settings allow walking while the browser remains open, except while drawing a patrol route. Existing hearts, Steam callbacks, pet syncing, and cursor hit testing continue throughout. Editing gestures do not grant pets.

The four pages are:

| Page | Current contents |
| --- | --- |
| `Dogs` | The current original dog; no additional dog options are presented as available. |
| `Items` | The default page: compact side inspector and categorized shelves for direct editing of the desktop dog. |
| `Shop` | An honest coming-soon message; purchasing is not implemented. |
| `Settings` | Plain-language preferences and patrol route controls first, connection summary with expandable technical details, and Quit. |

The tab buttons call `SetActiveTab(...)`. That method marks the selected button as pressed and toggles the matching page's `Visible` property. Only one browser page is visible at a time. Items activates `AccessoryEditingSession`; leaving Items or hiding/minimizing the browser deactivates it and cancels pending placement or manipulation.

The pet count is updated through `UpdateStatus(...)`, which is called from `DesktopPet._Process()` while the status window exists. The client shows the backend-confirmed pets total and, only when needed, the number of pending in-memory grants.

### Using The Items Page

During normal play, open the browser by right-clicking a visible dog pixel or selecting Open Pet Da Dog from the tray. Choose a new accessory in Items, then click its desired location on the actual desktop dog. Clicking an equipped catalog card selects it for editing without moving it. Drag equipped artwork directly on the desktop dog to adjust its position. Each item can be equipped once, and changes appear immediately.

Drag the dog's visible body or its Move dog pill to reposition it while editing. Its corner resize handle changes Dog size from 50–200% and saves through `PetSettings`, just like the Settings slider. Moving the dog is temporary; leaving Items resumes its enabled patrol or returns it to the ordinary walking area through gravity. Selected accessories expose their own resize and rotation handles, limited to 50–200% and −180° to 180°.

The browser's side inspector also shows Size and Rotation as vertical sliders, with text and color controls where applicable. Remove, Undo, and Clear all stay with the outfit controls. Reset fit restores the accessory to 100% and 0°. Accessory size and rotation can be chosen before placement, survive re-equipping and restarts, and use the same geometry for desktop drawing, hit testing, and clipping bounds.

While Items is active, right-click visible accessory artwork or the dog to open its layer menu. Move up one layer brings it forward; Move down one layer sends it backward. The dog and every equipped item share the same stack, so moving an accessory below the dog lets the dog cover it. The inspector's Up and Down buttons work for the selected equipped accessory; choose its catalog card to adjust it even when it is completely covered. The inspector also shows whether it is behind or in front of the dog. Actions at the top or bottom of the stack are disabled. Newly equipped or removed-and-re-equipped items start at the top; moving an existing item keeps its layer.

The catalog is a vertical stack of Wings, Collars, Glasses, Decorations, and Text shelves. Each clickable heading expands or collapses its category. An expanded shelf contains one horizontally scrolling row of fixed-size cards, with a native scrollbar when its content overflows. The stack itself scrolls vertically. Empty Collars starts collapsed and has an empty-state message when expanded. `AccessoryCategoryRow.cs` owns these shelves; `AccessoryCategories.For(...)` classifies built-in text first, then wing, collar, and glasses/goggle names, with other artwork in Decorations.

Text Box is a built-in accessory drawn by `PetTextAccessory.cs`, so it requires no image. Choose its card in the Text shelf, type a message of up to 64 grapheme clusters in the side inspector, and place it near the actual desktop dog. The placement area extends above and beside the dog. Text color uses the same color wheel; size and rotation work as for other accessories. Show textbox background toggles the rounded box and border while preserving the text's color and alpha. This preference saves even when the item is unequipped, participates in Undo, and defaults to visible for older outfits. The message wraps and adjusts its font size to fit. The text follows walking and bouncing after editing and stays readable when the dog turns. Text-only pixels pass desktop clicks through during normal play.

Remove (or Delete) takes off the selected item; Clear all takes off the outfit. Undo (or Ctrl + Z) reverses accessory placement, movement, layer changes (including the dog's layer), color, size/rotation, text/background changes, removal, and clearing. A drag or color-wheel gesture is one undo action; the last 30 outfit actions remain available for the current run. Cancel (or Escape) restores an active manipulation, including its starting layer order, without adding outfit undo history.

Dragon Wing, Fairy Wing, and Safety Glasses also support recoloring. Click Color to open a wheel with eight quick swatches, Reset, and Done. Advanced reveals numeric color modes and hex entry; basic mode hides those controls. Changes appear immediately on the catalog thumbnail and desktop dog. Done or closing the picker saves; Reset white restores the original artwork. Color preferences survive removal, re-equipping, and restarts.

`AccessoryWardrobe.cs` discovers images recursively under `Sprites/Accessories/`, including PNG, WebP, SVG, and JPEG textures. It crops transparent padding for the catalog and equipped sprites. Add new accessory artwork in this directory and import it in Godot to include it in the next run; dog and heart sprites outside this directory are excluded.

The three neutral JPG assets are explicitly listed in `AccessoryWardrobe.RecolorableIds`. Their black backgrounds become transparent at runtime, while the gray and white shading is preserved for tinting. Add future neutral artwork to that list to offer the color picker; existing colored artwork keeps its original colors.

Image placements use normalized coordinates within the dog's visible texture bounds; Text Box anchors allow a bounded area around them. `AccessoryWardrobe` saves positions, colors, sizes, rotations, text, textbox background visibility, and the shared layer order in `user://accessories.cfg`. Its `[layers]` section stores `order` from bottom to top, with `@dog` marking the dog's place among equipped IDs. Older position-only or position/color outfits load at 100% size, zero rotation, a visible text background, and all accessories above the dog in their saved equip order. These are local cosmetic preferences, independent of Steam pets. All direct editing and inspector actions bypass pet grants.

Settings display Dog visibility, Dog size, and Menu size as percentages with one value per slider. Click-through explains that petting and right-click are disabled, with Alt + backtick and tray recovery. Items temporarily makes the dog fully visible, interactive for editing, and always on top so saved visibility, click-through, or stacking preferences cannot prevent customization. Those preferences are restored when editing ends; the override does not overwrite them. `user://menu_layout.cfg` remembers the browser's position and dimensions; unrelated settings and reopening do not reset them. Menu size deliberately changes the browser dimensions.

### Drawing A Patrol Route

In Settings, the Patrol route card offers Set patrol route when no stops exist and Edit route otherwise. It hides the browser while you draw on the desktop and reopens Settings after Done or Cancel. Click locations in the order you want the dog to visit them. Stops are numbered, and their connecting path shows the loop: 1 → 2 → 3 → 1. Two different stops are enough; a route can contain up to 16. Drag an existing stop to move it or right-click a stop to remove it. The floating toolbar provides Undo last, Clear, Cancel, and Done; Backspace undoes the last stop, Escape cancels, and Enter finishes a valid route.

Done commits a valid draft, enables the patrol, and starts following its loop. Cancel discards the draft and keeps the saved route and its previous enablement. Editing an existing route begins with its saved stops. Route-editing gestures do not pet the dog or grant currency. Turn off Use patrol route to return to ordinary walking without losing its stops; turn it on again to resume patrolling. Clear route removes the saved stops and resets the dog to ordinary walking. The toolbar's Clear affects only the draft until Done.

`PatrolRoute.cs` stores screen-relative coordinates from 0–1 in `user://patrol_route.cfg`, so the stops adapt to the primary usable screen size instead of saving absolute pixels. Stops represent the dog's feet and are clamped to allow room for its outfit. The saved enablement also survives restarts. Invalid coordinates and consecutive duplicate stops are rejected or normalized on load, and a route with fewer than two distinct stops cannot be enabled. If saving fails, the route remains usable for the current run and the UI reports that it was not saved.

`DesktopPatrolEditor.cs` draws the numbered stops and handles route gestures; `PatrolRouteToolbar.cs` provides a separate native control window. While drawing, the usable desktop captures route gestures and the dog is held still, fully visible, and on top. Done or Cancel restores saved interaction preferences. During ordinary patrol playback, transparent space passes through and original dog pixels still accept normal pet clicks.

### Woodland Workshop Theme

`WoodlandTheme.cs` owns the shared earthy palette, fonts, button states, focus rings, checkbox and slider styling, and popup presentation. The browser's Dogs, Items, Shop, and Settings pages, desktop editing controls, and the native Godot color wheel use this theme. Use its button variations and panel helpers when extending the UI instead of adding unrelated colors or white styleboxes.

The generated raster art lives in `Art/UI/Woodland/`: moss cloth with a wood border, a wood-framed parchment panel, and a neutral stitched surface. Godot nine-slice styleboxes preserve corners while panels resize; import size limits keep border details appropriate for small controls. The neutral stitched surface is tinted for sage catalog trays and terracotta selections. Production prompts and asset notes are saved beside the artwork. Dog and accessory source textures stay unchanged.

Concept mockups remain under `Art/UI/Concepts/` and are excluded from import with `.gdignore`. UI artwork stays outside `Sprites/Accessories/` so it never becomes equippable content. Labels, counters, and settings remain real controls rather than text baked into images.

## 4. Containers And Controls Used Here

Godot UI layout is mostly container-driven. You add controls as children, then the container decides where they go.

| Control | What it does here |
| --- | --- |
| `PanelContainer` | Draws a background or bordered panel around one child. |
| `MarginContainer` | Adds padding around one child. |
| `VBoxContainer` | Stacks children vertically. |
| `HBoxContainer` | Places children horizontally. |
| `ScrollContainer` | Scrolls the category stack vertically and each card shelf horizontally. |
| `Label` | Displays text. |
| `Button` | Tabs, catalog items, outfit actions, disclosure controls, and quick color swatches. |
| `PopupMenu` | Right-click layer actions for an accessory or the dog during Items editing. |
| `CheckBox` | Boolean setting control. |
| `HSlider` | Percentage settings controls. |
| `VSlider` | Size and Rotation in the compact item inspector. |

`SizeFlagsHorizontal` and `SizeFlagsVertical` tell containers how a control wants to use space. `ExpandFill` means "take available space"; a `CustomMinimumSize` sets a stable floor so buttons, tiles, and sliders do not collapse.

## 5. Dog Overlay And Click Handling

The desktop pet is a transparent, borderless, always-on-top overlay window. The tricky part is input: transparent space should pass clicks through to the desktop, but the dog should still be clickable.

`DesktopPet.UpdateDogMouseRegion()` recomputes drawing bounds every frame, including equipped accessories and active floating hearts. Editing also includes selection outlines, resize/rotation handles, the Move dog pill, and the placement ghost. On Windows the mouse polygon also clips rendering, so these bounds are padded and rounded outward to integer pixels. The native region is updated only when those integer bounds change. Global cursor polling continues even when the region is unchanged. During editing, dog/image-accessory alpha hit tests, textbox bounds, and visible editing controls determine which gestures are captured; an armed placement accepts its bounded dog or near-dog area. Other desktop space passes clicks through. Outside editing, only original visible dog pixels accept pet clicks.

Editing hit tests walk the shared stack from top to bottom. Opaque artwork in front wins the hit; a visible dog pixel blocks an accessory below it, while transparent pixels let lower layers be reached. This same ordering chooses the target for dragging and the right-click menu. Normal petting continues to sample the original dog texture alpha and queue one grant per valid dog click, regardless of cosmetic layers.

`NativeWindowBridge` periodically reads the actual extended window styles and topmost state so it can restore required overlay behavior. An unchanged routine refresh skips native frame-change and z-order writes. This avoids repeatedly disturbing a stable transparent window while retaining repairs when its native state changes.

`DesktopPet._Input()` applies the second layer of click safety:

1. While editing, route gestures to `DesktopAccessoryControls` and return without granting pets.
2. During normal play, ignore input if click-through is enabled or the dog is invisible.
3. Accept only left and right mouse button presses.
4. Reject pet clicks outside visible dog pixels by sampling the original dog texture alpha.
5. Left-click queues one pet grant and spawns one heart.
6. Right-click opens or focuses the browser.

The pixel-alpha check matters because the dog image is a square texture with transparent pixels around the actual dog. Without this check, clicking invisible pixels inside the texture rectangle would count as petting.

## 6. Heart Feedback

The heart feedback uses `Sprites/PetzHeart.png`. On each valid left-click, `DesktopPet` creates a `Sprite2D` under `FootAnchor`.

That placement is intentional:

| Parent | Effect |
| --- | --- |
| `FootAnchor` | Heart follows the dog around the screen. |
| Not under `VisualRoot` | Heart does not inherit dog squash, stretch, flip, or transparency. |

The source heart's transparent padding is cropped at runtime. Each heart starts above the dog, pops to about 46 pixels over `0.12s`, then drifts 90 pixels upward and 46 pixels outward while shrinking and fading over a total `1.8s`. Existing hearts continue animating during Items editing, but editing gestures do not spawn new pet feedback. The overlay reserves the trajectory, and its Windows drawing region includes the live heart bounds. The active heart list is capped at 8 so rapid clicking cannot create unlimited nodes.

## 7. How To Extend The Tabs Safely

To extend dog or accessory content:

1. Keep confirmed pets and pending grants visible in the persistent header.
2. Add accessory artwork under `Sprites/Accessories/`; use `AccessoryWardrobe` for outfit changes and `AccessoryEditingSession` for shared selection and gesture state.
3. Keep category shelves horizontal and the category stack vertically scrollable so the browser does not grow with content.
4. Use backend-confirmed data for anything authoritative.
5. Do not store or fake pet totals in client settings.

Keep local cosmetics separate from authoritative currency. If future accessories require ownership or purchases, that entitlement needs a backend design; the current catalog equips bundled artwork locally.

## 8. How To Extend Settings Safely

Local dog preferences belong in `PetSettings.cs`. The usual flow is:

```text
StatusWindow control changes
  -> PetSettings setter clamps and saves
  -> PetSettings emits SettingsChanged
  -> DesktopPet applies the visual/window behavior
```

Do not make `StatusWindow` mutate dog visuals directly. Keeping settings changes centralized makes the overlay, native window styles, and UI refresh behavior easier to reason about.

## 9. Build And Smoke Test

Build the Godot client from the client directory:

```powershell
dotnet build .\PetDaDogCSharp.csproj
```

Run the accessory checks from the same directory:

```powershell
.\Tests\Run-AccessorySmoke.ps1
```

The runner imports resources and builds the client, disables Steam and tray integration, and isolates user settings and temporary files under `.godot/accessory-smoke-home`. Use `-SkipBuild` after a current import/build. It checks catalog discovery, JPG transparency and shading, placement/color/text/transform validation, save/reload, Undo, overlay bounds, direct desktop editing, and native color-picker interactions. Editing checks cover dog movement and resizing, accessory handles, gesture cancellation, preference overrides/restoration, and rejection of grants during customization. Text checks cover editable persistence, background visibility, nonmirrored letters, bounce, and screen-edge placement; heart checks cover size, lifetime, drift, shrinking, fading, clipping, and the node cap. Success prints `ACCESSORY_SMOKE_PASS:`. Current captures are saved under `.godot/` for review; older floating-preview screenshots may remain as historical UI references.

Woodland and UX checks cover Dogs / Items / Shop / Settings switching, category collapse and horizontal scrolling, percentage preference sliders, theme retention during menu-size changes, native basic/advanced color controls, equipped-card selection, undo gestures and keys, first-use dismissal, connection detail disclosure, confirmed/waiting status separation, and persisted browser geometry. Reviewed capture copies live under `docs/previews/`.

For a manual check, choose Items, move the actual dog by its body and Move dog pill, and resize it with its corner handle. Place an accessory, then drag, resize, and rotate it with the desktop handles and inspector. Test colors, text, and Show textbox background, then removal, clearing, and Undo. Check category collapse and horizontal scrolling. Leave Items, hide/close the browser, and minimize it during a gesture: the gesture should cancel, the dog should fall and resume walking, and saved visibility/click-through/always-on-top preferences should return. Restart to check the outfit, dog size, and browser layout. Check desktop click-through around transparent pixels and editing controls, and confirm editing never changes waiting pet clicks. During normal play, pet the visible dog to check hearts and syncing.

Smoke test the main scene with Steam and tray disabled:

```powershell
$env:PDD_DISABLE_STEAM='1'; $env:PDD_DISABLE_TRAY='1'; & 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe' --path 'S:\CodexProjects\PetDaDog\Pet_Da_Dog_CSharp' --scene 'res://Main.tscn' --quit-after 5 --no-header
```

Open the browser in Items mode through the main scene for a smoke check:

```powershell
$env:PDD_DISABLE_STEAM='1'
$env:PDD_DISABLE_TRAY='1'
& 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe' --path 'S:\CodexProjects\PetDaDog\Pet_Da_Dog_CSharp' --scene 'res://Main.tscn' --quit-after 120 --no-header -- --status
```

Use Godot 4.6.3 for this project. The local 4.6.2 runtime has previously hung or crashed during smoke tests.
