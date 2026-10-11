# Pet Da Dog Steam Inventory

The upload schema is `SteamInventory/itemdefs.json` for Steam AppID **4817200**.
It contains 93 definitions: Pets currency, 43 accessories, 45 dog variants, two
inventory boxes, and two random generators. Each PNG in `Pet_Da_Dog_CSharp/Art/Items` or
`Pet_Da_Dog_CSharp/Art/Dogs` maps to exactly one cosmetic definition. Matching
`Art/UI` images are previews of the same items.

## Definition IDs

| Definition | ID |
| --- | --- |
| Pets currency | 100 |
| Accessories Box | 1000 |
| Dog Box | 1001 |
| Accessories Box Contents generator | 1100 |
| Dog Box Contents generator | 1101 |
| Accessories | 2000–2042 |
| Dog variants | 3000–3044 |

IDs are permanent inventory identities once uploaded or granted. Keep existing
asset-to-ID mappings when adding items. Use new unused IDs for additions.
The schema's `pdd_kind`, `pdd_asset`, and `pdd_ui_asset` fields are custom
metadata for the client catalog mapping; local resource paths are
not Steam image URLs.

## Earning Pets and buying boxes

**Pets (100)** is a regular inventory item with `auto_stack: true`. Its stack
quantity is the player's spendable currency balance. Trading and marketplace
sales are disabled for this currency.

Set `PDD_STEAM_PETS_ITEMDEF_ID=100` on the backend/Worker. Each accepted dog click
grants one unit of item 100 through the trusted backend's `AddItem` call. Keep
the same Steam `requestid` for retries of the same click. Ten accepted clicks
earn ten Pets, even if the client sends those click IDs in a batch. Grant only
the event IDs the backend has accepted, and display the balance read from Steam.
The definition itself does not detect clicks or initiate rewards.

Fixed click rewards use direct item grants. The two random generators remain
responsible for selecting cosmetic rewards when boxes are opened.

The initial box purchase recipes are:

| Box | Pets cost | Recipe on the box definition |
| --- | --- | --- |
| Dog Box (1001) | 1 | `100x1` |
| Accessories Box (1000) | 2 | `100x2` |

Call Steam's exchange operation with the appropriate Pets stack instance ID,
the required material quantity, the box as the output definition, and output
quantity 1. Steam verifies the player has the currency, consumes the specified
amount, and grants one unopened box in the same transaction. If the balance is
insufficient, the exchange fails. The existing box-opening recipes on generators
1100 and 1101 then consume that box and award a cosmetic.

For backend HTTP purchases, treat an uncertain `ExchangeItem` result as pending
and investigate its saved operation record against Steam before resolving it.
Do not automatically submit the exchange again: inventory changes alone cannot
prove which request succeeded. Repeating the purchase against the same Pets
stack can spend again; this API has no documented `requestid`.

Steam's `price` and `price_category` fields configure real-money item-store
pricing; use `exchange` recipes for purchases with earned Pets currency.

## Equal odds

| Box | Outcomes | Weight per outcome | Exact chance | Approximate chance |
| --- | --- | --- | --- | --- |
| Accessories Box | 43 accessories | 1 | 1/43 | 2.325581% |
| Dog Box | 45 dog variants | 1 | 1/45 | 2.222222% |

Each generator lists every eligible item once in its `bundle` field with
weight `x1`. Steam chooses one outcome when the generator is expanded.
This is equal odds per individual asset across 22 breeds. Labrador has three
variants; the other breeds have two variants each. Consequently, the combined
chance of any Labrador variant is 3/45, while any other breed has a combined
chance of 2/45.

Duplicates are possible and stack as quantities of the same cosmetic item.
Cosmetics and boxes have trading and marketplace sales disabled. Each box is an
individual inventory instance (`auto_stack: false`), so an opening operation
can identify one specific box instance.

## Opening a box

An unopened box has `type: "item"`. Its `container_contents_generator`
points to the corresponding generator, which lets Steam describe the possible
contents. That field alone does not open or consume a box.

The generators declare these exchange recipes:

- Generator **1100**: `exchange: "1000x1"`.
- Generator **1101**: `exchange: "1001x1"`.

To open an owned box, call Steam's `ISteamInventory::ExchangeItems` with one
instance of that box, material quantity **1**, the corresponding generator as
the output definition, and output quantity **1**. Steam checks ownership and the
recipe, consumes that box, and creates one random cosmetic in one transaction.
Use the returned inventory result to identify the award.

A backend can use `IInventoryService/ExchangeItem` for the same recipe. That
Web API does not document a `requestid` parameter: do not assume it has the same
retry behavior as `AddItem`. Resolve an uncertain result against Steam and keep
the same specific box instance associated with that opening attempt.

Grant **1000** or **1001** through the trusted backend to give a player an unopened
box. Granting **1100** or **1101** directly through `AddItem` immediately generates
an award; it does not enforce the exchange recipe or consume an existing box.
The production opening path must use the exchange operation.

The Cloudflare Worker and Godot client now implement earning Pets, buying and
opening boxes, and checking Steam ownership before equipping cosmetics. Follow
[the Cloudflare setup guide](CLOUDFLARE_SETUP.md) to deploy the Worker, add its
secrets, set the client's URL, and verify the flow against real Steam inventory.

## Dog and accessory presets

In **Items**, optionally enter a preset name and click **Save Preset**. Open
**Presets** from Items or Dogs to see a preview of each saved look. Clicking a
preview switches the desktop dog and replaces its complete outfit, including
positions, sizes, rotations, tint, text, text background, and layer order.
Applying a preset can be undone as one outfit change. Deleting a preset removes
the saved look without changing the current dog.

Presets survive restarts in `user://dog-presets.cfg`. They contain local cosmetic
preferences only; they do not save Pets balances or Steam ownership. A saved
preset whose Steam dog or accessories are no longer owned remains visible but
cannot be applied until ownership is confirmed again. The Dogs selector itself
continues to show only owned dogs and the starter dog.

Run `Pet_Da_Dog_CSharp/Tests/Run-DogPresetSmoke.ps1` with Godot 4.6.3 to validate
save/load, complete outfit application, undo, ownership gates, and preset UI
previews using isolated preferences and fake ownership.

## Upload and images

1. Open the Inventory Service configuration for app **4817200**:
   https://partner.steamgames.com/apps/inventoryservice/4817200/
2. Upload `SteamInventory/itemdefs.json` using the item-definition upload control.
   If the previous 92-definition version was already uploaded, upload this
   updated file to add Pets (100) and the box purchase recipes; all previous
   definition IDs are preserved.
3. Check Steamworks' validation results and enable Inventory Service.
4. Test grants and opening with a Steamworks partner-group account while item
   visibility is Private.

The file contains no publisher key or other secret. Use the actual Steamworks
validation result to confirm import; local JSON validation cannot confirm that
Steam accepted it.

Steam inventory icons need publicly accessible HTTPS image URLs. The schema
currently omits `icon_url` and `icon_url_large` until an image host exists.
For each cosmetic, host its `pdd_ui_asset` image (or suitable exported icon)
and put the public URL in those fields. Add dedicated box images for IDs 1000
and 1001. Local `res://` paths in the custom metadata are for the game only.

## Official references

- [Steam Inventory schema, generators, and exchange recipes](https://partner.steamgames.com/doc/features/inventory/schema)
- [Inventory Service setup](https://partner.steamgames.com/doc/features/inventory)
- [Client ExchangeItems API](https://partner.steamgames.com/doc/api/ISteamInventory#ExchangeItems)
- [Backend ExchangeItem API](https://partner.steamgames.com/doc/webapi/IInventoryService#ExchangeItem)
