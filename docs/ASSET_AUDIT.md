# Drive asset audit — October 28, 2026

Reference: [shared source folder](https://drive.google.com/drive/folders/1FuRSUheNvpy9rXZlQkTX8Rh3XpYCGfUV) and [PTD Content Spreadsheet](https://docs.google.com/spreadsheets/d/1aR_roVu087EJY2NQ9XuJy2QUzMDrrS6F-BcRcUJvi1Q/edit).

The audit covers every uploaded PNG and SVG in the folder and its nested folders. Historical `PTDBuild.zip` is compiled output and is excluded from source synchronization. Spreadsheet entries marked planned or considered without uploaded artwork remain roadmap entries.

| Source | Files | Project representation |
| --- | ---: | --- |
| Dogs, including Imported | 78 | 77 dog variants; the extra Maltese source shares identical visible pixels with Maltese 1 |
| Items, including Imported | 64 | 63 Steam accessories plus the petting-hand feedback image |
| Vector, including Imported | 35 | Existing woodland and Shop artwork, including renamed present and currency SVGs |
| StoreAssets | 7 | Original marketing PNGs in `SteamStore/Art` |
| Total | 184 | See the per-file source manifest |

The [source manifest](asset-source-manifest.json) records each Drive ID, original folder/name, canonical project path and SHA-256 hashes. Byte equality verifies all sources except `Maltese.png`, which uses different PNG encoding from `Maltese1.png`; their decoded RGBA pixels match after ignoring invisible RGB values. It shares the existing Maltese 1 reward. Drive's `BaseballCatHat.png` maps to the byte-identical `Art/Items/BaseballHat.png`, preserving item 2045.

## Additions and cleanup

Four missing dog images were added: Irish Setter 1/2 and Wirehaired Pointing Griffon 1/2, using new IDs 3073–3076. All seven Steam store images were added outside the client package. The 35 vector files were already present, sometimes under game-specific names; they share their existing canonical files. The accessory-present bottom filename was corrected so its open artwork resolves.

Removed 139 byte-identical PNG copies and their import sidecars: 136 redundant game/UI pairs and three legacy accessory copies. Retained canonical hashes were checked before deletion, references and resource UIDs were updated, and every existing cosmetic ID was preserved. This removes 15,555,206 bytes of redundant source files and import metadata. The six local starter accessory identities remain available; the three PNG starters share artwork with their distinct Steam definitions.

Validation also repaired the pre-existing starter catalog regression, the stale accessory shelf wiring, Cyber Visor classification and two theme texture UIDs. Existing seven-category and accessory-size behavior is preserved. Smoke expectations now match the current Corgi 3 starter and present artwork, with exact texture-path checks.

## Economy and build

The local `golden-bones-v2` candidate has 154 Steam definitions: 77 dogs, 63 accessories and 14 economy definitions. The Dig Box starter pool remains unchanged; the premium dog pool becomes 67 outcomes. Steam publication, public icon hosting and matching Worker deployment are separate rollout steps. These files do not enable the four new rewards on the live service.

The Windows release is exported with Godot 4.6.3 Mono into `Builds/`. Build and test results are recorded in `Builds/Build-info.txt`; isolated smoke profiles disable Steam, tray interaction and global input earning.

Completed validation: economy generation/readback-contract checks; 55 Worker unit tests, both fake-Steam runtime suites and deployment dry-run; 25 client HTTP scenarios; Godot accessory (1,786), inventory (560), box reveal (520) and Shop (37) assertions. The isolated Release build, fresh resource imports, complete Windows export and exported-entrypoint launch smoke all passed. The final source-image scan found zero byte-identical duplicate groups across all 203 retained images, including documentation previews and store artwork.
