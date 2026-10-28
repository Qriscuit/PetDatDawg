import assert from "node:assert/strict";
import { test } from "node:test";
import { DatabaseSync } from "node:sqlite";
import { readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import worker, { configuration, SteamClient, ShopPlayerService, PlayerInventory, createSession } from "../src/index.js";
import { shopCatalog, economy } from "../src/shop.js";
import { FakeSteamBackend } from "../src/fake-steam.js";

const account = "76561198000000000";
const guid = n => `${String(n).padStart(8, "0")}-89ab-cdef-0123-456789abcdef`;
const env = { PDD_STEAM_APP_ID: "4817200", PDD_STEAM_PETS_ITEMDEF_ID: "100", PDD_GRANTS_PER_MINUTE: "300", PDD_SHOP_ENABLED: "1", PDD_LEGACY_PURCHASES_ENABLED: "0", PDD_STEAM_PUBLISHER_KEY: "test-only-publisher-key", PDD_BACKEND_SESSION_SECRET: "test-only-secret-of-more-than-32-characters" };
class Storage {
  constructor() { this.db = new DatabaseSync(":memory:"); this.db.exec("CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL)"); }
  async get(key) { const row = this.db.prepare("SELECT value FROM kv WHERE key=?").get(key); return row ? JSON.parse(row.value) : undefined; }
  async put(key, value) { this.db.prepare("INSERT INTO kv VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value").run(key, JSON.stringify(value)); }
  async delete(key) { this.db.prepare("DELETE FROM kv WHERE key=?").run(key); }
  async transaction(callback) { this.db.exec("BEGIN"); try { const r = await callback(this); this.db.exec("COMMIT"); return r; } catch (e) { this.db.exec("ROLLBACK"); throw e; } }
}
function fixture({ pets = 2000, bones = 1000 } = {}) {
  const fake = new FakeSteamBackend(); const inventory = fake.account(account);
  if (pets) fake.add(inventory, 100, pets); if (bones) fake.add(inventory, 101, bones);
  const config = configuration(env); const storage = new Storage(); let now = 1800000000000;
  const calls = [];
  const steam = new SteamClient(config, async (...args) => { calls.push(new URL(args[0]).pathname); return fake.fetch(...args); });
  const makeService = () => new ShopPlayerService(storage, steam, config, () => now);
  return { fake, config, storage, steam, calls, service: makeService(), makeService, time: value => { now = value; }, now: () => now,
    purchase: (id, offerId) => ({ clientEventId: guid(id), offerId, economyVersion: economy.version }) };
}
const count = (f, name) => f.calls.filter(path => path.includes(name)).length;

test("single manifest validates 140 cosmetics and safe Steam schema, prices, pools and protected permit", () => {
  execFileSync(process.execPath, ["../scripts/economy-generate.mjs", "--check"], { cwd: new URL("..", import.meta.url), stdio: "pipe" });
  const schema = JSON.parse(readFileSync(new URL("../../SteamInventory/itemdefs.json", import.meta.url), "utf8"));
  assert.equal(schema.items.length, 154);
  assert.equal(shopCatalog.offers.length, 143);
  assert.deepEqual(shopCatalog.offers.slice(0, 3).map(o => [o.price, o.rewardItemDefIds.length]), [[500, 20], [50, 67], [25, 53]]);
  assert.ok(shopCatalog.offers.slice(1, 3).every(o => o.rewardItemDefIds.every(id => !economy.starterPool.includes(id))));
  assert.equal(schema.items.find(i => i.itemdefid === 1110).exchange, "100x500,102x1");
  assert.equal(schema.items.find(i => i.itemdefid === 1113).bundle, "101x5");
  for (const id of [1000, 1001, 102]) assert.equal(schema.items.find(i => i.itemdefid === id).exchange, undefined);
  for (const item of schema.items) { assert.equal(item.price, undefined); assert.equal(item.price_category, undefined); assert.equal(item.marketable, false); }
  const permit = schema.items.find(i => i.itemdefid === 102);
  assert.equal(permit.auto_stack, false); assert.equal(permit.tradable, false); assert.equal(permit.promo, undefined);
});

test("Dig Box atomically consumes 500 Pets and one unique permit, receipt and cooldown survive restart and replay", async () => {
  const f = fixture(); const input = f.purchase(1, "dig");
  const result = await f.service.purchase(account, "purchase", input);
  assert.equal(result.status, "complete"); assert.equal(result.pets, 1500); assert.equal(result.goldenBones, 1000);
  assert.equal(result.receivedItems.length, 1); assert.ok(economy.starterPool.includes(result.receivedItems[0].itemDefId));
  assert.equal(result.nextDigAt, new Date(f.now() + 1200000).toISOString());
  assert.equal(result.items.some(i => i.itemDefId === 102), false);
  assert.equal(result.receipts.length, 1);
  assert.equal((await f.makeService().purchase(account, "purchase", input)).pets, 1500);
  assert.equal(count(f, "AddItem"), 1); assert.equal(count(f, "ExchangeItem"), 1);
  await assert.rejects(f.makeService().purchase(account, "purchase", f.purchase(2, "dig")), { code: "dig_cooldown" });
  f.time(f.now() - 100000); // Client-clock manipulation never changes server authority.
  await assert.rejects(f.makeService().purchase(account, "purchase", f.purchase(2, "dig")), { code: "dig_cooldown" });
  f.time(1800000000000 + 1200000);
  assert.equal((await f.makeService().purchase(account, "purchase", f.purchase(2, "dig"))).status, "complete");
});

test("public Steam recipe refuses a direct Dig Box call without its permit", async () => {
  const f = fixture(); const inv = await f.steam.inventory(account);
  const rows = await f.steam.exchange(account, { outputItemDefId: 1110, materials: [{ ...inv.items.find(i => i.itemDefId === 100), consume: 500 }] });
  assert.equal(rows, null); assert.equal((await f.steam.inventory(account)).pets, 2000);
});

test("premium and direct offers allow repeated copies, consume exact currency and do not use Dig cooldown", async () => {
  const f = fixture();
  for (const [n, offer] of [[1, "dog"], [2, "accessory"], [3, "dog:3000"], [4, "dog:3000"], [5, "accessory:2004"]]) {
    const result = await f.service.purchase(account, "purchase", f.purchase(n, offer));
    assert.equal(result.status, "complete"); assert.equal(result.nextDigAt, null);
  }
  const state = await f.service.shopState(account);
  assert.equal(state.goldenBones, 425); assert.equal(state.pets, 2000);
  assert.equal(state.items.find(i => i.itemDefId === 3000).quantity, 2);
  assert.equal(state.receipts.find(r => r.clientEventId === guid(4)).isDuplicate, true);
});

test("recycling protects last/equipped copies, validates quantity and credits five Bones without trusting concurrent balance deltas", async () => {
  const f = fixture(); f.fake.add(f.fake.account(account), 2004, 3);
  const recycle = { clientEventId: guid(1), itemDefId: 2004, quantity: 1, equippedQuantity: 3, economyVersion: economy.version };
  await assert.rejects(f.service.purchase(account, "recycle", recycle), { code: "no_spare_copy" });
  await assert.rejects(f.service.purchase(account, "recycle", { ...recycle, quantity: 2 }), { code: "invalid_request" });
  await assert.rejects(f.service.purchase(account, "recycle", { ...recycle, itemDefId: 101 }), { code: "invalid_request" });
  const original = f.steam.exchange.bind(f.steam);
  f.steam.exchange = async (...args) => { f.fake.add(f.fake.account(account), 101, 550); return original(...args); };
  const result = await f.service.purchase(account, "recycle", { ...recycle, equippedQuantity: 2 });
  assert.equal(result.status, "complete"); assert.equal(result.goldenBones, 1555);
  assert.equal(result.receivedItems[0].quantity, 5);
  await assert.rejects(f.service.purchase(account, "recycle", { ...recycle, clientEventId: guid(2), equippedQuantity: 2 }), { code: "no_spare_copy" });
  await f.service.purchase(account, "recycle", { ...recycle, clientEventId: guid(3), equippedQuantity: 0 });
  await assert.rejects(f.service.purchase(account, "recycle", { ...recycle, clientEventId: guid(4), equippedQuantity: 0 }), { code: "no_spare_copy" });
});

test("concurrent Wallet credit to spent Bones stack does not make a valid purchase uncertain", async () => {
  const f = fixture(); const original = f.steam.exchange.bind(f.steam);
  f.steam.exchange = async (...args) => { f.fake.add(f.fake.account(account), 101, 1200); return original(...args); };
  const result = await f.service.purchase(account, "purchase", f.purchase(1, "dog:3072"));
  assert.equal(result.status, "complete"); assert.equal(result.goldenBones, 2000);
});

test("ambiguous exchange retains frozen intent and blocks grants/purchases across restarts without rerolling", async () => {
  const f = fixture(); const original = f.steam.exchange.bind(f.steam);
  f.steam.exchange = async (...args) => { await original(...args); throw new Error("Connection lost after commit"); };
  assert.equal((await f.service.purchase(account, "purchase", f.purchase(1, "dig"))).status, "pending");
  assert.equal((await f.makeService().purchase(account, "purchase", f.purchase(1, "dig"))).status, "pending");
  assert.equal((await f.makeService().shopOperation(account, guid(1))).status, "pending");
  await assert.rejects(f.makeService().purchase(account, "purchase", f.purchase(2, "dog")), { code: "exchange_pending" });
  await assert.rejects(f.makeService().grant(account, { clientEventId: guid(3) }), { code: "exchange_pending" });
  assert.equal(count(f, "ExchangeItem"), 1); assert.equal(count(f, "AddItem"), 1);
  assert.equal(await f.storage.get("next-dig-at"), undefined);
});

test("permit AddItem timeout replays only the same idempotency key and never creates a second permit", async () => {
  const f = fixture(); const original = f.steam.addItem.bind(f.steam); const ids = []; let failures = 2;
  f.steam.addItem = async (...args) => { ids.push(args[2]); const result = await original(...args); if (failures-- > 0) throw new Error("timeout"); return result; };
  assert.equal((await f.service.purchase(account, "purchase", f.purchase(1, "dig"))).status, "pending");
  assert.equal((await f.makeService().shopOperation(account, guid(1))).status, "complete");
  assert.equal(new Set(ids).size, 1); assert.equal(count(f, "ExchangeItem"), 1);
  assert.equal(f.fake.account(account).items.filter(i => i.itemdefid === 102).length, 0);
});

test("definitive rejection cleans up its permit with stable ConsumeItem ID before unlocking", async () => {
  const f = fixture(); f.steam.exchange = async () => null;
  const consume = f.steam.consumeItem.bind(f.steam); const ids = []; let fail = true;
  f.steam.consumeItem = async (...args) => { ids.push(args[3]); await consume(...args); if (fail) throw new Error("lost cleanup response"); };
  assert.equal((await f.service.purchase(account, "purchase", f.purchase(1, "dig"))).status, "pending");
  assert.equal((await f.storage.get(`op:${guid(1)}`)).state, "cleanup_pending");
  await assert.rejects(f.service.purchase(account, "purchase", f.purchase(2, "dog")), { code: "exchange_pending" });
  fail = false;
  assert.equal((await f.makeService().shopOperation(account, guid(1))).status, "rejected");
  assert.equal(await f.storage.get("active"), undefined); assert.equal(await f.storage.get("next-dig-at"), undefined);
  assert.equal(new Set(ids).size, 1); assert.equal((await f.steam.inventory(account)).pets, 2000);
});

test("a definitive receipt survives failed inventory refresh and is acknowledged idempotently", async () => {
  const f = fixture(); const originalExchange = f.steam.exchange.bind(f.steam); const inventory = f.steam.inventory.bind(f.steam); let fail = false;
  f.steam.exchange = async (...args) => { const result = await originalExchange(...args); fail = true; return result; };
  f.steam.inventory = async (...args) => { if (fail) throw new Error("inventory unavailable"); return inventory(...args); };
  await assert.rejects(f.service.purchase(account, "purchase", f.purchase(1, "dig")));
  assert.equal((await f.storage.get(`op:${guid(1)}`)).state, "applied");
  assert.equal(await f.storage.get("next-dig-at"), f.now() + 1200000);
  fail = false;
  const recovered = await f.makeService().shopOperation(account, guid(1));
  assert.equal(recovered.status, "complete"); assert.equal(recovered.receipts.length, 1); assert.equal(count(f, "ExchangeItem"), 1);
  assert.equal((await f.service.acknowledge(account, { clientEventId: guid(1) })).receipts.length, 0);
  assert.equal((await f.service.acknowledge(account, { clientEventId: guid(1) })).receipts.length, 0);
  assert.equal((await f.service.shopOperation(account, guid(2))).status, "not_found");
});

test("invalid version, offer, recycled local item, insufficient balance, and conflicting IDs never authorize extra spends", async () => {
  const f = fixture({ pets: 499, bones: 0 });
  await assert.rejects(f.service.purchase(account, "purchase", { ...f.purchase(1, "dig"), economyVersion: "old" }), { code: "update_required" });
  await assert.rejects(f.service.purchase(account, "purchase", f.purchase(1, "dig")), { code: "insufficient_pets" });
  await assert.rejects(f.service.purchase(account, "purchase", f.purchase(1, "dog")), { code: "insufficient_items" });
  await assert.rejects(f.service.purchase(account, "purchase", f.purchase(1, "dog:9999")), { code: "invalid_request" });
  assert.equal(count(f, "AddItem"), 0); assert.equal(count(f, "ExchangeItem"), 0);
  f.fake.add(f.fake.account(account), 101, 50);
  await f.service.purchase(account, "purchase", f.purchase(1, "dog"));
  await assert.rejects(f.service.purchase(account, "purchase", f.purchase(1, "accessory")), { code: "operation_conflict" });
});

test("legacy buys retire but owned boxes open and recover a v2 reward receipt", async () => {
  const f = fixture();
  await assert.rejects(f.service.exchange(account, "buy", { clientEventId: guid(1), boxType: "dog" }), { code: "update_required" });
  const box = f.fake.add(f.fake.account(account), 1001, 1);
  assert.equal((await f.service.exchange(account, "open", { clientEventId: guid(2), boxItemId: box.itemid })).status, "complete");
  const state = await f.makeService().shopState(account);
  assert.equal(state.receipts.length, 1); assert.equal(state.receipts[0].kind, "open");
});

test("production flags cannot activate test grants; isolated fake transport supports idempotent seed/reset", async () => {
  const f = fixture();
  assert.equal(configuration({ ...env, PDD_DEV_ECONOMY: "1" }).testEconomy, false);
  assert.throws(() => configuration({ ...env, PDD_ECONOMY_ENVIRONMENT: "isolated-test" }), { code: "configuration_required" });
  await assert.rejects(f.service.developerAction(account, "seed", { clientEventId: guid(1), pets: 10, goldenBones: 100 }), { code: "not_found" });
  const config = configuration({ ...env, PDD_ECONOMY_ENVIRONMENT: "isolated-test", PDD_FAKE_STEAM: f.fake });
  const service = new ShopPlayerService(f.storage, new SteamClient(config, () => { throw new Error("Real network must never run"); }), config);
  const input = { clientEventId: guid(1), pets: 10, goldenBones: 100 };
  assert.equal((await service.developerAction(account, "seed", input)).goldenBones, 1100);
  assert.equal((await service.developerAction(account, "seed", input)).goldenBones, 1100);
  await f.storage.put("next-dig-at", f.now() + 1200000);
  assert.equal((await service.developerAction(account, "reset-cooldown", { clientEventId: guid(2) })).nextDigAt, null);
});

test("v2 HTTP routes serialize concurrent duplicate purchases and carry confirmed receipt/capability DTOs", async () => {
  const f = fixture(); const testEnv = { ...env, PDD_ECONOMY_ENVIRONMENT: "isolated-test", PDD_FAKE_STEAM: f.fake };
  const object = new PlayerInventory({ storage: f.storage }, testEnv);
  const token = await createSession(account, env.PDD_BACKEND_SESSION_SECRET);
  testEnv.PLAYER_INVENTORY = { idFromName: value => value, get: () => object };
  const send = (path, input) => worker.fetch(new Request(`https://test.local${path}`, { method: input ? "POST" : "GET", headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" }, ...(input ? { body: JSON.stringify(input) } : {}) }), testEnv);
  const replies = await Promise.all([send("/v2/shop/purchase", f.purchase(1, "dig")), send("/v2/shop/purchase", f.purchase(1, "dig"))]);
  for (const reply of replies) { assert.equal(reply.status, 200); const body = await reply.json(); assert.equal(body.pets, 1500); assert.equal(body.status, "complete"); }
  const state = await (await send("/v2/shop")).json();
  assert.equal(state.capabilities.developerControls, true); assert.equal(state.capabilities.cashPurchasesEnabled, false);
  assert.equal(state.receipts.length, 1); assert.equal(state.economyVersion, economy.version);
  assert.equal((await send("/v2/shop/ack", { clientEventId: guid(1) })).status, 200);
});

test("failed durable preparation never calls Steam and malformed receipts never unlock a spend", async () => {
  const f = fixture(); f.storage.transaction = async () => { throw new Error("Storage unavailable"); };
  await assert.rejects(f.service.purchase(account, "purchase", f.purchase(1, "dig")));
  assert.equal(count(f, "AddItem"), 0); assert.equal(count(f, "ExchangeItem"), 0);
  for (const change of [
    rows => rows.filter(row => row.itemDefId !== 101),
    rows => rows.map(row => row.itemDefId !== 101 ? { ...row, itemDefId: 3000 } : row),
    rows => rows.map(row => row.itemDefId !== 101 ? { ...row, quantity: 0 } : row),
    () => [],
  ]) {
    const f = fixture(); const exchange = f.steam.exchange.bind(f.steam);
    f.steam.exchange = async (...args) => change(await exchange(...args));
    const result = await f.service.purchase(account, "purchase", f.purchase(1, "accessory:2004"));
    assert.equal(result.status, "pending"); assert.equal(result.receipts.length, 0);
    assert.equal((await f.makeService().shopOperation(account, guid(1))).status, "pending");
    assert.equal(count(f, "ExchangeItem"), 1);
  }
});

test("production exposes neither fake seeding nor test capabilities despite dev-looking client input", async () => {
  const f = fixture();
  const state = await f.service.shopState(account);
  assert.equal(state.capabilities.developerControls, false);
  assert.equal(state.capabilities.cashPurchasesEnabled, false);
  assert.equal(state.capabilities.marketEnabled, false);
  await assert.rejects(f.service.developerAction(account, "reset-cooldown", { clientEventId: guid(1), developer: true }), { code: "not_found" });
  assert.throws(() => configuration({ ...env, PDD_ECONOMY_ENVIRONMENT: "isolated-test", PDD_FAKE_STEAM: f.fake, PDD_STEAM_PUBLISHER_KEY: "a-real-looking-publisher-key" }), { code: "configuration_required" });
});

test("concurrent Market acquisitions to purchased and recycled cosmetic stacks do not misclassify valid receipts", async () => {
  const f = fixture(); f.fake.add(f.fake.account(account), 2004, 3);
  const exchange = f.steam.exchange.bind(f.steam);
  f.steam.exchange = async (...args) => { f.fake.add(f.fake.account(account), 2004, 1); return exchange(...args); };
  const purchased = await f.service.purchase(account, "purchase", f.purchase(1, "accessory:2004"));
  assert.equal(purchased.status, "complete"); assert.equal(purchased.receivedItems[0].quantity, 1);
  assert.equal(purchased.items.find(i => i.itemDefId === 2004).quantity, 5);
  const recycled = await f.service.purchase(account, "recycle", { clientEventId: guid(2), itemDefId: 2004, quantity: 1, equippedQuantity: 1, economyVersion: economy.version });
  assert.equal(recycled.status, "complete"); assert.equal(recycled.receivedItems[0].quantity, 5);
  assert.equal(recycled.items.find(i => i.itemDefId === 2004).quantity, 5);
});

test("successful-but-empty Steam Exchange response cannot clean up a permit or unlock the account", async () => {
  const f = fixture();
  const exchange = f.steam.exchange.bind(f.steam);
  f.steam.exchange = async (...args) => { await exchange(...args); return []; };
  assert.equal((await f.service.purchase(account, "purchase", f.purchase(1, "dig"))).status, "pending");
  assert.equal((await f.makeService().shopOperation(account, guid(1))).status, "pending");
  assert.equal(count(f, "ExchangeItem"), 1); assert.equal(count(f, "ConsumeItem"), 0);
  assert.equal(await f.storage.get("active"), guid(1));
});

test("documented empty rejection through Steam transport cleans Dig permit without spending or cooldown", async () => {
  const f = fixture();
  const steam = new SteamClient(f.config, async (...args) => {
    f.calls.push(new URL(args[0]).pathname);
    if (String(args[0]).includes("ExchangeItem")) return Response.json({ response: { item_json: "[]" } });
    return f.fake.fetch(...args);
  });
  const service = new ShopPlayerService(f.storage, steam, f.config, f.now);
  const result = await service.purchase(account, "purchase", f.purchase(1, "dig"));
  assert.equal(result.status, "rejected"); assert.equal(result.pets, 2000);
  assert.equal(result.nextDigAt, null); assert.equal(result.receipts.length, 0);
  assert.equal(result.items.some(item => item.itemDefId === 102), false);
  assert.equal(await f.storage.get("active"), undefined);
  assert.equal(count(f, "ExchangeItem"), 1); assert.equal(count(f, "ConsumeItem"), 1);
  assert.equal((await service.purchase(account, "purchase", f.purchase(1, "dig"))).status, "rejected");
  assert.equal(count(f, "ExchangeItem"), 1); assert.equal(count(f, "ConsumeItem"), 1);
});

test("contradictory empty success through Steam transport retains Dig permit and mutation lock", async () => {
  const f = fixture();
  const steam = new SteamClient(f.config, async (...args) => {
    f.calls.push(new URL(args[0]).pathname);
    if (String(args[0]).includes("ExchangeItem")) return Response.json({ response: { success: true, item_json: "[]" } });
    return f.fake.fetch(...args);
  });
  const service = new ShopPlayerService(f.storage, steam, f.config, f.now);
  assert.equal((await service.purchase(account, "purchase", f.purchase(1, "dig"))).status, "pending");
  assert.equal((await service.shopOperation(account, guid(1))).status, "pending");
  assert.equal(await f.storage.get("active"), guid(1));
  assert.equal(count(f, "ConsumeItem"), 0); assert.equal(count(f, "ExchangeItem"), 1);
});
