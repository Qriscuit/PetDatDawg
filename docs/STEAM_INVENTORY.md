# Pet Da Dog Steam Inventory

The upload schema is `SteamInventory/itemdefs.json` for Steam AppID **4817200**.
The current Golden Bones implementation prepares **154 definitions** from
`SteamInventory/economy.json`: both currencies, all 140 cosmetics, legacy boxes,
new generators, a server-issued permit, recycling, and gated currency packs.
Use [the current schema/release guide](../SteamInventory/README.md) and
[Shop behavior](GOLDEN_BONES_SHOP.md). Publication is a separate operation;
the historical release record below is not evidence that the new schema is live.
Shop previews and Steam icons use the same canonical `Art/Dogs` and `Art/Items` images as the desktop pet; byte-identical UI copies have been removed.

The October 18, 2026 [source asset sync](https://drive.google.com/drive/folders/1FuRSUheNvpy9rXZlQkTX8Rh3XpYCGfUV)
adds 20 accessories and 20 dog variants while preserving every existing ID.
`Maltese.png` has the same visible artwork as `Maltese1.png` and shares that
single reward. `Art/UI/Feedback/PetHand.png` supplies brief petting feedback
outside the wearable pool. Steam accepted the expanded schema on October 18,
2026 (`Modified 44/133 item definitions. Flushed Econ caches: yes.`). Worker
version `da77dafc-73eb-47ed-a9d2-e02716dd499c` deployed on October 19 with the
expanded reward ranges. Its health check passed. Desktop Steam readback confirmed
all 133 IDs and 40 additions. A secure read-only publisher `GetItemDefs` request
on October 19 returned HTTP 200 and `x-eresult: 1`, with zero differences from the
uploaded schema, including both exact generator pools and exchange recipes.
The publisher response omits `auto_stack=false`, its documented default; the
native client also omits the generator bundles and container-content references,
so the publisher readback supplies the pool verification. No live box purchase
or opening was used for validation.

The October 19 Golden Bones release subsequently published 150 definitions
and 136 cosmetics, including dogs 3065–3072; its verified readback is recorded
in [the release guide](../SteamInventory/README.md). The October 28 Drive sync
adds dogs 3073–3076 locally, bringing the candidate to 154 definitions, 77 dogs
and 63 accessories under `golden-bones-v2`. The premium dog pool grows to 67
outcomes. Steam publication, public icons, and matching Worker deployment for
this new candidate remain separate steps; local generation does not enable
these four rewards on the live service.

## Definition IDs

| Definition | ID |
| --- | --- |
| Pets currency | 100 |
| Accessories Box | 1000 |
| Dog Box | 1001 |
| Accessories Box Contents generator | 1100 |
| Dog Box Contents generator | 1101 |
| Accessories | 2000–2062 |
| Dog variants | 3000–3076 |

IDs are permanent inventory identities once uploaded or granted. Keep existing
asset-to-ID mappings when adding items. Use new unused IDs for additions.
The schema's `pdd_kind`, `pdd_asset`, and `pdd_ui_asset` fields are custom
metadata for the client catalog mapping; local resource paths are
not Steam image URLs.

The latest additions preserve the source filenames, including their spelling:

| Dog | Definition ID | Source filename |
| --- | --- | --- |
| Dalmatian 1 | 3065 | `Dalmatian1.png` |
| Dalmatian 2 | 3066 | `Dalmatian2.png` |
| Dachshund 1 | 3067 | `Daschund1.png` |
| Dachshund 2 | 3068 | `Daschund2.png` |
| Portuguese Water Dog 1 | 3069 | `PortugeseWaterDog1.png` |
| Portuguese Water Dog 2 | 3070 | `PortugeseWaterDog2.png` |
| Shiba Inu 1 | 3071 | `ShibaInu1.png` |
| Shiba Inu 2 | 3072 | `ShibaInu2.png` |
| Irish Setter 1 | 3073 | `IrishSetter1.png` |
| Irish Setter 2 | 3074 | `IrishSetter2.png` |
| Wirehaired Pointing Griffon 1 | 3075 | `WirehairPointingGriffon1.png` |
| Wirehaired Pointing Griffon 2 | 3076 | `WirehairPointingGriffon2.png` |

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

These odds describe the prepared local schema. The live Dog Box retains its
65-outcome pool until the updated schema is uploaded.

| Box | Outcomes | Weight per outcome | Exact chance | Approximate chance |
| --- | --- | --- | --- | --- |
| Accessories Box | 63 accessories | 1 | 1/63 | 1.587302% |
| Dog Box | 73 dog variants | 1 | 1/73 | 1.369863% |

Each generator lists every eligible item once in its `bundle` field with
weight `x1`. Steam chooses one outcome when the generator is expanded.
This is equal odds per individual asset. A breed's combined chance is its number
of eligible variants divided by 73: Corgi has four variants (4/73), Labrador
has three (3/73), and the other breed groups have two variants each (2/73).

Duplicates are possible and stack as quantities of the same cosmetic item.
The client keeps the confirmed quantities and allows one independently positioned
accessory placement per owned copy. Click a catalog card and then the dog, or
drag a card onto the dog, to place an unused copy. Each copy keeps its own
transform and layer; select it on the dog to edit it or use its outline delete
button to remove it from the outfit. **Add another** also remains in the inspector.
Starter image accessories can be repeated freely. The editable Text Box remains
a single message. Saved outfits retain placement preferences across ownership
changes, but only copies covered by fresh Steam ownership are displayed, and
presets require enough owned copies. Local saves never supply item quantities.
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
   This version adds dogs 3065–3072 to the deployed 133-definition catalog,
   producing 141 definitions; all existing IDs, names and resource paths are
   preserved.
   Upload the expanded schema before deploying the Worker's expanded reward
   ranges. A local catalog update does not change the live Steam reward pools.
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
