import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { resolve, dirname } from "node:path";
import assert from "node:assert/strict";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const read = path => JSON.parse(readFileSync(resolve(root, path), "utf8"));
const manifest = read("SteamInventory/economy.json");
const check = process.argv.includes("--check");
const sandboxPrices = process.argv.includes("--sandbox-prices");
const market = process.argv.includes("--market");
const iconBase = ((sandboxPrices || market ? process.env.PDD_STEAM_ICON_BASE_URL : undefined) || manifest.iconBaseUrl)?.replace(/\/$/, "");
if (market && (!iconBase || !iconBase.startsWith("https://"))) throw new Error("--market requires PDD_STEAM_ICON_BASE_URL with publicly verified HTTPS icons.");
if (check && (sandboxPrices || market)) throw new Error("--check checks the safe default schema only.");
const m = manifest;
const cosmetics = m.cosmetics;
const dogs = cosmetics.filter(c => c.kind === "dog").map(c => c.itemDefId);
const accessories = cosmetics.filter(c => c.kind === "accessory").map(c => c.itemDefId);
assert.equal(new Set(cosmetics.map(c => c.itemDefId)).size, 136);
assert.equal(dogs.length, 73); assert.equal(accessories.length, 63);
assert.equal(m.starterPool.length, 20); assert.equal(new Set(m.starterPool).size, 20);
assert.equal(m.starterPool.filter(id => dogs.includes(id)).length, 10);
assert.equal(m.starterPool.filter(id => accessories.includes(id)).length, 10);
assert.ok(m.starterPool.every(id => cosmetics.some(c => c.itemDefId === id)));
assert.ok([m.prices.dogBox, m.prices.accessoryBox, m.prices.dog, m.prices.accessory].every(price => price > m.recycleValue));
const defaults = { tradable: false, marketable: false };
const contents = ids => ids.map(id => `${id}x1`).join(";");
const generator = (id, name, pool, exchange) => ({ itemdefid: id, type: "generator", name,
  description: `One of ${pool.length} equally likely cosmetics. Duplicates are possible.`, ...defaults,
  bundle: contents(pool), exchange, pdd_kind: "generator" });
const items = [
  { itemdefid: m.ids.pets, type: "item", name: "Pets", description: `Earn Pets through desktop activity. Spend ${m.prices.dig} Pets on a Dig Box every ${m.digCooldownSeconds / 60} minutes.`, display_type: "Currency", ...defaults, auto_stack: true, pdd_kind: "currency" },
  { itemdefid: m.ids.goldenBones, type: "item", name: "Golden Bones", description: "Premium currency for dogs, accessories, and premium boxes.", display_type: "Currency", ...defaults, auto_stack: true, store_hidden: true, pdd_kind: "currency", ...(sandboxPrices ? { price: "1;USD1" } : {}) },
  { itemdefid: m.ids.digPermit, type: "item", name: "Dig Permit", description: "Single-use server-issued Dig Box permit.", ...defaults, auto_stack: false, hidden: true, pdd_kind: "permit" },
  ...[ [1000, "Accessories Box", 1100], [1001, "Dog Box", 1101] ].map(([id, name, generatorId]) => ({ itemdefid: id, type: "item", name, description: "Legacy box. Existing boxes can still be opened; new purchases have moved to the Shop.", display_type: "Loot Box", ...defaults, auto_stack: false, container_contents_generator: generatorId, pdd_kind: "box" })),
  generator(1100, "Accessories Box Contents", accessories, "1000x1"),
  generator(1101, "Dog Box Contents", dogs, "1001x1"),
  generator(m.ids.digGenerator, "Dig Box Contents", m.starterPool, `${m.ids.pets}x${m.prices.dig},${m.ids.digPermit}x1`),
  generator(m.ids.dogGenerator, "Premium Dog Box Contents", dogs.filter(id => !m.starterPool.includes(id)), `${m.ids.goldenBones}x${m.prices.dogBox}`),
  generator(m.ids.accessoryGenerator, "Premium Accessory Box Contents", accessories.filter(id => !m.starterPool.includes(id)), `${m.ids.goldenBones}x${m.prices.accessoryBox}`),
  { itemdefid: m.ids.recycleBundle, type: "bundle", name: "Recycled Golden Bones", description: "Recycle one cosmetic for Golden Bones.", ...defaults, bundle: `${m.ids.goldenBones}x${m.recycleValue}`, exchange: contents(cosmetics.map(c => c.itemDefId)), pdd_kind: "recycle" },
  ...m.packs.map(p => ({ itemdefid: p.itemDefId, type: "bundle", name: `${p.quantity} Golden Bones`, description: `${p.quantity} Golden Bones for the Pet Da Dog Shop.`, ...defaults, bundle: `${m.ids.goldenBones}x${p.quantity}`, pdd_kind: "currency_pack", hidden: !sandboxPrices, ...(sandboxPrices ? { price: `1;USD${p.testPriceUsdCents}`, use_bundle_price: true } : {}) })),
  ...cosmetics.map(c => ({ itemdefid: c.itemDefId, type: "item", name: c.name, description: c.description, display_type: c.kind === "dog" ? "Dog Skin" : "Accessory", ...defaults, marketable: market, auto_stack: true, exchange: `${m.ids.goldenBones}x${m.prices[c.kind]}`, pdd_kind: c.kind, pdd_asset: c.assetPath, pdd_ui_asset: c.uiAssetPath,
    ...(iconBase ? { icon_url: `${iconBase}/${c.itemDefId}.png`, icon_url_large: `${iconBase}/${c.itemDefId}.png` } : {}) })),
];
assert.equal(new Set(items.map(i => i.itemdefid)).size, items.length);
const client = readFileSync(resolve(root, "Pet_Da_Dog_CSharp/Scripts/SteamCosmeticCatalog.cs"), "utf8");
for (const c of cosmetics) {
  const exact = `new(${c.itemDefId}, ${JSON.stringify(c.name)}, ${JSON.stringify(c.kind)}, ${JSON.stringify(c.assetPath)}, ${JSON.stringify(c.uiAssetPath)})`;
  assert.ok(client.includes(exact), `Client catalog mismatch for ${c.itemDefId}`);
}
function output(path, content) {
  if (check) assert.equal(readFileSync(resolve(root, path), "utf8").replaceAll("\r\n", "\n"), content, `Regenerate ${path} with node scripts/economy-generate.mjs`);
  else writeFileSync(resolve(root, path), content);
}
const schemaPath = sandboxPrices || market ? "SteamInventory/itemdefs.release-candidate.json" : "SteamInventory/itemdefs.json";
output(schemaPath, JSON.stringify({ appid: m.appId, items }, null, 2) + "\n");
if (!sandboxPrices && !market) {
  const runtime = { ...m, cosmetics: m.cosmetics.map(({ itemDefId, kind, name }) => ({ itemDefId, kind, name })) };
  output("PetDaDog.Worker/src/economy.generated.js", "// Generated from SteamInventory/economy.json. Run node scripts/economy-generate.mjs.\nexport default " + JSON.stringify(runtime, null, 2) + ";\n");
}
console.log(`${check ? "Verified" : "Generated"} ${items.length} definitions, 136 cosmetics, starter 20 / premium dogs 63 / premium accessories 53. Cash sales ${sandboxPrices ? "PRICED CANDIDATE ONLY" : "disabled"}; Market ${market ? "candidate enabled" : "disabled"}.`);
