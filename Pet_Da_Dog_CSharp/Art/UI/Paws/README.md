# Woodland Workshop UI art

Selected UI direction: earthy greens, parchment, warm wood and stitching.

The three raster textures were created with built-in image generation. Full final prompts are in `prompts.json`. Dog and accessory source artwork was not changed.

| Asset | Runtime use | Import size limit |
| --- | --- | --- |
| `woodland-window.png` | Moss cloth, wood border and corner foliage behind the status UI | 384 px |
| `woodland-panel.png` | Quiet parchment inside a curved wood frame for the dog preview and settings panels | 256 px |
| `woodland-stitched.png` | Nine-slice stitched surfaces for buttons, catalog cards, parchment sections and the sage catalog tray | 128 px |

`Scripts/WoodlandTheme.cs` owns palette, fonts, stretch margins, button states, focus rings, slider/check-box styling and popup presentation. Source PNGs retain their generated resolution and transparency; Godot import limits provide appropriate UI texture density without rewriting the artwork.

`Icons/` contains small code-native SVG control icons matching the pine, cream and brass palette. These are UI controls, never equippable items. UI art remains outside `Sprites/Accessories/` so the wardrobe does not discover it as accessory content.

Reference mockups remain excluded from import under `Art/UI/Concepts/`. Runtime labels, counters, settings and color-wheel values remain real Godot controls; no mockup screenshot is used as the game's interface.
