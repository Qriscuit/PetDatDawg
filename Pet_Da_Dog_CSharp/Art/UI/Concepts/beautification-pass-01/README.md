# Pet Da Dog UI concepts

Four visual directions generated with the built-in image generation tool. Open `comparison.html` to compare and enlarge them.

| Option | Direction | Character |
| --- | --- | --- |
| 1 | Warm Paw Café | Oat, peach, sage and cocoa; soft fabric/paper surfaces and warm rounded controls. |
| 2 | Sticker Club | Lavender, butter yellow, coral and mint; playful paper layers, pill tabs and sticker edges. |
| 3 | Moonlit Meadow | Plum/navy, lavender, teal and gold; a cozy dark theme with curved glowing borders. |
| 4 | Woodland Workshop | Moss, parchment, pine and terracotta; painted leaves, curved wood and stitched details. |

The common Accessories layout makes the directions comparable. These PNGs are **concept mockups**, not production atlases. Generated lettering, decorative sayings, and representations of the dog/accessories are for visual exploration. During integration, keep all existing dog and accessory source textures and use real Godot labels, counters, and controls.

Woodland Workshop (option 4) was selected and integrated using separate UI-only textures and real Godot controls. Production artwork, notes and prompts are in `../../Woodland/`. This concept folder is excluded from Godot import with `.gdignore`, so exploratory images do not become shipping game resources.

## After choosing a direction

1. Create separate UI-only artwork: background, panel frame, button states, card states, header accents and small icons. Avoid baking text or existing dog/accessory artwork into assets.
2. Build a shared Godot Theme so Status, Accessories, Discoveries, Settings and the Godot color picker follow the same palette, typography and control states.
3. Preserve the 720×720 starting window, 560×620 minimum, UI scaling, scrollable accessory cards, clear pet/pending counters, a quiet dog stage, color wheel and placement controls.
4. Verify small-window layout, keyboard focus, selected/hovered/disabled states, saved outfits, tinting and dog-only pet clicks. Rebuild the complete Windows package after runtime integration.

The complete final prompt set is recorded in `prompts.json`; generation used the built-in tool, with the current native UI screenshot as its edit reference.
