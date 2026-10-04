# Editing The UI In Godot

The game UI is authored in Godot scenes and resources. Change its layout, text, textures, fonts, colors, and control styles in the editor; C# scripts bind the named controls, handle actions, and refresh live state. Windows title bars and the native tray menu retain operating-system styling.

Open `Pet_Da_Dog_CSharp/project.godot` with Godot 4.6.3 .NET. Open `StatusWindow.tscn` from the FileSystem dock, then select the 2D workspace. On the root Window, change **Initial Tab** to preview Dogs, Items, Shop, or Settings. This editor preview does not activate desktop editing, connect to Steam, or save preferences.

## Which File To Open

| File under `Pet_Da_Dog_CSharp/` | What to edit |
| --- | --- |
| `StatusWindow.tscn` | Browser header/tabs, welcome hint, Dogs, Shop, Settings, connection status, and window size. |
| `Main.tscn` | Desktop UI instances and the exported browser/text-accessory scene references. |
| `UI/AccessoryEditor.tscn` | Items inspector, category area, instructions, outfit actions, text/background and transform controls. |
| `UI/AccessoryCategoryRow.tscn` | One reusable collapsible heading and horizontal card shelf. |
| `UI/AccessoryCard.tscn` | Accessory thumbnail, name/status labels, card dimensions, padding, and button styling. |
| `UI/AccessoryColorControls.tscn` | Color selection controls, quick swatches, popup actions, and basic/advanced presentation. |
| `UI/PatrolRouteToolbar.tscn` | Native route toolbar heading/instructions, Undo last/Clear/Cancel/Done buttons, status, and size. |
| `UI/AccessoryLayerMenu.tscn` | Right-click layer menu labels, item layout, popup size, and styling. |
| `UI/DesktopAccessoryControls.tscn` | Desktop selection/handle preview, Appearance resource, and Layer Menu Scene reference. |
| `UI/DesktopPatrolEditor.tscn` | Numbered route/path preview, Appearance resource, and Toolbar Scene reference. |
| `UI/PetTextAccessory.tscn` | Text accessory preview, sample text/color/background, and Appearance resource. |
| `UI/Theme/DefaultTheme.tres` | Shared fonts, control colors, icons, button variations/states, sliders, checkboxes, and popup styles. |
| `UI/Theme/DesktopAppearance.tres` | Desktop selection/handles, Move dog pill, patrol markers/path/arrows, placement preview, and text accessory drawing. |

The browser tree starts at `Background/Margin/Layout`. Its pages are under `Pages/DogsPage`, `Pages/ItemsPage`, `Pages/ShopPage`, and `Pages/SettingsPage`. ItemsPage instances the reusable AccessoryEditor scene. To change every Items page, open that source scene. For a local instance override, enable **Editable Children** on the instance and edit its children.

AccessoryEditor has authored category rows and sample cards so the Items page is visible before running the game. The root's **Card Scene**, **Category Row Scene**, and **Color Controls Scene** exports choose its reusable templates. A card's Display Name, Preview Texture, Preview Status, and Preview Tint provide editor sample data. A row's Category, Preview Count, and Expanded properties preview its heading and shelf. Runtime fills the same templates from the real wardrobe, replacing sample cards rather than hardcoding their layout.

Keep each shelf's **Category** identity as Wings, Collars, Glasses, Decorations, or Text, with one instance per identity. Use **Display Title** to rename its visible heading without changing catalog matching. The **Header Copy** fields customize the header format, expansion glyphs, and tooltips. Supported tokens are `{glyph}`, `{title}`, `{title_lower}`, `{category}`, and `{count}`. An empty Display Title uses the category name.

The root's **Color Wheel Shape** chooses Godot's picker shape. The three JPG sample cards have an authored preview material that removes their black background; runtime replaces them with the wardrobe's prepared transparent textures.

Card content nodes are `CardTexture`, `CardName`, and `CardStatus`; row content is `CategoryHeader`, `CategoryScroll`, `CategoryCards`, and `EmptyCategoryLabel`. The Items inspector exposes named size/rotation sliders, the text input, TextBackgroundToggle, and AccessoryColorPicker. In AccessoryColorControls, QuickColors contains individually editable swatch StyleBoxFlat resources, alongside AdvancedColor, PopupResetColor, and DoneColor actions.

## Layout And Text

Select a Control in the scene tree and use the Inspector to change Text, Tooltip Text, Custom Minimum Size, size flags, alignment, and theme overrides. The browser root's Size and Min Size supply its authored default dimensions; the player's saved layout and Menu size setting still apply during a normal run. The patrol toolbar similarly uses its authored scene Size, then clamps it to the usable desktop.

Containers own their children's positions. Use `VBoxContainer`/`HBoxContainer` separation, `MarginContainer` margins, and Expand/Fill flags to adjust spacing. Keep the Items category stack vertically scrollable and each shelf horizontally scrollable so adding accessories does not force a larger browser.

Static button and instruction text stays as authored. Live counts, connection messages, selected item names, and route summaries change at runtime. In the patrol toolbar root, **Dynamic Text** exports control its empty/single-stop hints, invalid-route hint, and loop summary format. The loop format supports `{count}` and `{loop}`. Its **Desktop Placement** exports control screen margins and vertical placement.

AccessoryEditor's **Templates** group selects reusable scenes, while **State Captions**, **Instructions**, **Save Status**, and **Dynamic Formats** customize live inspector wording. Selected and placement instruction formats and Selection Tooltip Format support `{name}`; the catalog heading uses `{count}`; size/rotation values use `{value}`; the Undo tooltip uses `{label}`. On the AccessoryCard template, **State Captions** controls available, equipped, and placement labels, with `{name}`, `{id}`, and `{category}` supported in Choose Tooltip Format. Use the card's **Editor Sample** group to preview content; runtime item names and textures come from the wardrobe.

On StatusWindow, **Dynamic Captions** provides Show Details Text, Hide Details Text, Set Route Text, and Edit Route Text. **Pet Status Text**, **Connection Summary Text**, and **Patrol Summary Text** expose the remaining live status wording. Count formats support `{count}`; service and Steam formats support `{status}`; Patrol Route Format supports `{count}` and `{loop}`; Patrol Summary Format combines `{route}` and `{state}`. Edit these properties to customize text that changes with state. Keep the placeholders needed to display the live values.

In the layer menu, edit the popup's **Items** in the Inspector. Item 0 is the target heading/separator; item 1 is the up action with ID 1, and item 2 is the down action with ID 2. Keep these action IDs. The root's **Target Caption** exports supply Dog/Accessory fallback captions and a format containing `{name}`. Runtime updates this heading for the clicked target and disables boundary actions; it preserves the authored action labels. Edit **Size** and **Min Size** for the popup's dimensions. Its native **Shrink Width** and **Shrink Height** flags are authored false so Godot honors this sizing when it opens; turning them on makes the menu shrink to its content minimum. **Fit To Contents** permits content growth while keeping the authored size as a floor. Native validation confirms a customized 450 × 190 popup retains its dimensions, label, and font.

## Theme And Art

Open `UI/Theme/DefaultTheme.tres` in Godot's Theme editor. It is also assigned as the project's GUI custom theme. Shared variations include SecondaryButton, ActionButton, TabButton, CardButton, and WoodlandHeading. Edit the appropriate normal/hover/pressed/disabled/focus style rather than changing only one state. Browser Menu size scales its authored Content Scale Factor and dimensions without replacing the assigned theme.

The reusable surface resources are:

| Resource under `UI/Theme/` | Shared surface |
| --- | --- |
| `Window.tres` | Outer browser frame/background. |
| `Panel.tres` | Framed content panels and native popup/toolbar surfaces. |
| `PlainPanel.tres` | Quiet plain panels and controls. |
| `Catalog.tres` | Category/catalog trays. |
| `Focus.tres` | Keyboard focus outline. |
| `TextBubble.tres` | Optional rounded text accessory background/border. |
| `BodyFont.tres`, `HeadingFont.tres` | Body and heading font resources. |

Select a StyleBoxTexture resource to replace its texture, tint, content margins, or texture margins. Texture margins preserve the corners when its nine-slice surface stretches. Existing art is under `Art/UI/Woodland/`; control icons are in its `Icons/` folder. Drag an imported texture into the resource's Texture field to replace it.

The theme reuses three PNG surfaces and references 14 SVG icon resources, including spare legacy icons. Icons remain independent of labels, so replacing artwork does not require painting button text into it.

A node's Theme Overrides take precedence over the shared Theme. Edit those overrides for a local change. Duplicate or make a resource unique before customizing only one control; editing a shared external resource affects every reference to it. Save the changed scene and resources, then run the main project to verify all control states.

## Desktop Appearance

Open `UI/Theme/DesktopAppearance.tres` to change the procedural desktop UI in the Inspector. Its groups cover selection colors/widths/padding; handle radius, border, hover colors, glyphs and optional resize/rotation textures; the Move dog style/font/text; patrol marker radius/fonts/colors; route line/arrow dimensions; text bubble sizing/padding/fonts/outlines; and placement opacity.

These controls are drawn in code from the authored resource because their positions follow the moving dog and selected item. Changing the resource adjusts their presentation without rewriting drawing code. `TextBubble.tres` provides the rounded background; disabling Show textbox background during play still preserves the text and its outline.

Open `UI/DesktopAccessoryControls.tscn` to see its **Show Editor Preview** sample, adjusting **Editor Preview Bounds** to inspect handles and the Move dog pill. Open `UI/DesktopPatrolEditor.tscn` and edit **Editor Preview Points** to see markers, paths, and arrows. In `UI/PetTextAccessory.tscn`, change **Text**, **Text Color**, and **Background Visible** to preview the text presentation. Select each root's **Appearance** resource to inspect or replace its presentation fields. These Tool previews redraw as the resource changes.

Main instances AccessoryControls and PatrolEditor with sample previews disabled. Its root's **Status Scene** and **Text Accessory Scene** exports choose the runtime browser and text template; desktop roots similarly expose **Layer Menu Scene** and **Toolbar Scene**. Edit the source templates for shared changes, or make the Appearance resource unique for an instance-specific presentation.

The dog, heart, and accessory artwork remains under `Sprites/`. Item cards reuse those textures, and the Text Box card uses a separate UI icon. Theme artwork stays outside `Sprites/Accessories/` so it does not appear as equippable content.

## Bound Nodes And Runtime Windows

Preserve unique node names marked with `%` in script lookups. You may move these nodes between containers, change their presentation, and add decorative siblings while retaining their names and expected control types. Renaming or deleting a bound button/label requires updating its binding.

The native toolbar and layer popup templates are visible for editor inspection. Runtime controllers instantiate them, hide them, and set Force Native before adding them to the tree; they show only for an active interaction. Do not enable Force Native on a visible template: Godot rejects that property change during scene instantiation. Their Tool scripts guard editor mode before model bindings, native show/focus operations, autoload access, and saves. Leave native setup and deferred opening in their scripts. Each runtime window owns a separate 2D world so the desktop dog does not render behind its controls.

Run the main project to check interaction and native window behavior. Standalone UI scene inspection is intended for the editor; the desktop controller supplies the live models and opening lifecycle. Saved outfits, routes, layout, and preferences remain local user data, while confirmed pets still come from Steam/backend responses.
