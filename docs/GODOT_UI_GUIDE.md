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
| `Node2D` | 2D world objects with positions, scale, rotation | `DesktopPet`, `FootAnchor`, `VisualRoot` |
| `Sprite2D` | Draws a texture in 2D space | `PetSprite`, floating hearts |
| `Control` | UI widgets and layout | labels, buttons, sliders, panels |
| `Window` | A separate OS/Godot window | `StatusWindow` |

`Node2D` positioning is manual. `Control` positioning is usually managed by containers such as `VBoxContainer`, `HBoxContainer`, `GridContainer`, and `ScrollContainer`.

## 2. Project UI Map

`project.godot` is the Godot project config. It sets the main scene to `Main.tscn`, enables transparent window settings, and autoloads two global scripts:

| Autoload | Path | Purpose |
| --- | --- | --- |
| `PetSettings` | `/root/PetSettings` | Loads and saves local dog/UI settings. |
| `NativeWindowBridge` | `/root/NativeWindowBridge` | Applies Windows overlay styles, tray menu, and hotkeys. |

`Main.tscn` is tiny:

```text
DesktopPet
  +-- FootAnchor
        +-- VisualRoot
              +-- PetSprite
```

`DesktopPet.cs` is the main controller. It loads the dog texture, keeps the overlay window at the bottom of the screen, moves and hops the dog, checks dog-pixel clicks, opens the status window, spawns hearts, and coordinates Steam/backend pet grants.

`StatusWindow.tscn` contains a `Window` node with `StatusWindow.cs` attached. The actual UI is built in C# instead of being laid out visually in the Godot editor.

## 3. How The Status UI Works

`StatusWindow.cs` builds its UI in `BuildUi()`. The pattern is:

```text
Window
  +-- PanelContainer background
        +-- MarginContainer padding
              +-- VBoxContainer root
                    +-- header: tab buttons + pets counter + Exit Game action
                    +-- page container
```

The header is custom instead of using Godot's `TabContainer`. That lets the app keep a persistent pets count on the right while the tab buttons stay on the left, and it gives the status menu a tab-styled `Exit Game` action.

The three pages are:

| Page | Current contents |
| --- | --- |
| `Accessories` | Scrollable grid of square placeholder tiles. |
| `Discoveries` | Scrollable grid of square placeholder tiles. |
| `Settings` | Backend/Steam status plus dog settings controls. |

The tab buttons call `SetActiveTab(...)`. That method marks the selected button as pressed and toggles the matching page's `Visible` property. Only one page is visible at a time.

The pet count is updated through `UpdateStatus(...)`, which is called from `DesktopPet._Process()` while the status window exists. The client shows the backend-confirmed pets total and, only when needed, the number of pending in-memory grants.

## 4. Containers And Controls Used Here

Godot UI layout is mostly container-driven. You add controls as children, then the container decides where they go.

| Control | What it does here |
| --- | --- |
| `PanelContainer` | Draws a background or bordered panel around one child. |
| `MarginContainer` | Adds padding around one child. |
| `VBoxContainer` | Stacks children vertically. |
| `HBoxContainer` | Places children horizontally. |
| `GridContainer` | Places children in rows and columns. |
| `ScrollContainer` | Makes content scroll when it exceeds available space. |
| `Label` | Displays text. |
| `Button` | Clickable tab and placeholder tile. |
| `CheckBox` | Boolean setting control. |
| `HSlider` and `SpinBox` | Numeric setting controls. |

`SizeFlagsHorizontal` and `SizeFlagsVertical` tell containers how a control wants to use space. `ExpandFill` means "take available space"; a `CustomMinimumSize` sets a stable floor so buttons, tiles, and sliders do not collapse.

## 5. Dog Overlay And Click Handling

The desktop pet is a transparent, borderless, always-on-top overlay window. The tricky part is input: transparent space should pass clicks through to the desktop, but the dog should still be clickable.

`DesktopPet.UpdateDogMouseRegion()` updates Godot's mouse passthrough polygon every frame. The polygon follows the visible dog rectangle as the dog walks and hops.

`DesktopPet._Input()` applies the second layer of click safety:

1. Ignore input if click-through is enabled or the dog is invisible.
2. Accept only left and right mouse button presses.
3. Reject clicks outside visible dog pixels by sampling the dog texture alpha.
4. Left-click queues one pet grant and spawns one heart.
5. Right-click opens or focuses the status window.

The pixel-alpha check matters because the dog image is a square texture with transparent pixels around the actual dog. Without this check, clicking invisible pixels inside the texture rectangle would count as petting.

## 6. Heart Feedback

The heart feedback uses `Sprites/PetzHeart.png`. On each valid left-click, `DesktopPet` creates a `Sprite2D` under `FootAnchor`.

That placement is intentional:

| Parent | Effect |
| --- | --- |
| `FootAnchor` | Heart follows the dog around the screen. |
| Not under `VisualRoot` | Heart does not inherit dog squash, stretch, flip, or transparency. |

Each heart starts above the dog's visible height, floats upward, fades out over about `0.8s`, then removes itself. The active heart list is capped at 8 so rapid clicking cannot create unlimited nodes.

## 7. How To Extend The Tabs Safely

To add real accessory or discovery content:

1. Keep the persistent pet counter in the top row.
2. Add real buttons or panels inside the existing scroll pages.
3. Keep grids scrollable so the window size does not need to grow with content.
4. Use backend-confirmed data for anything authoritative.
5. Do not store or fake pet totals in client settings.

For a tile that starts doing something, connect its `Pressed` event and keep the action local unless it needs backend authority.

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
dotnet build
```

Smoke test the main scene with Steam and tray disabled:

```powershell
$env:PDD_DISABLE_STEAM='1'; $env:PDD_DISABLE_TRAY='1'; & 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe' --path 'S:\CodexProjects\PetDaDog\Pet_Da_Dog_CSharp' --scene 'res://Main.tscn' --quit-after 5 --no-header
```

Smoke test the status window directly:

```powershell
$env:PDD_DISABLE_STEAM='1'; $env:PDD_DISABLE_TRAY='1'; & 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe' --path 'S:\CodexProjects\PetDaDog\Pet_Da_Dog_CSharp' --scene 'res://StatusWindow.tscn' --quit-after 2 --no-header
```

Use Godot 4.6.3 for this project. The local 4.6.2 runtime has previously hung or crashed during smoke tests.
