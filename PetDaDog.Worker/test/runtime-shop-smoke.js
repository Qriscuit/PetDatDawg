// Real workerd + SQLite exercise of v2 routes. Steam is wholly fake.
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { Miniflare, convertV4MiniflareOptions, Response } from "miniflare";
import { FakeSteamBackend } from "../src/fake-steam.js";

const fake = new FakeSteamBackend(); const steamId = "76561198000000000";
const account = fake.account(steamId); fake.add(account, 100, 2000); fake.add(account, 101, 1000);
const guid = n => `${String(n).padStart(8, "0")}-89ab-cdef-0123-456789abcdef`;
const transport = async request => {
  const response = await fake.fetch(request.url, { method: request.method, headers: Object.fromEntries(request.headers), ...(request.method !== "GET" ? { body: await request.text() } : {}) });
  return new Response(await response.text(), { status: response.status, headers: Object.fromEntries(response.headers) });
};
const mf = new Miniflare(convertV4MiniflareOptions({
  name: "petdadog-api", modules: ["index.js", "shop.js", "economy.generated.js"].map(name => ({ type: "ESModule", path: fileURLToPath(new URL(`../src/${name}`, import.meta.url)) })),
  compatibilityDate: "2026-10-10", cf: false,
  bindings: { PDD_STEAM_APP_ID: "4817200", PDD_STEAM_PETS_ITEMDEF_ID: "100", PDD_GRANTS_PER_MINUTE: "300", PDD_SHOP_ENABLED: "1", PDD_LEGACY_PURCHASES_ENABLED: "0", PDD_STEAM_PUBLISHER_KEY: "test-only-publisher-key", PDD_BACKEND_SESSION_SECRET: "runtime-test-secret-of-more-than-32-bytes", PDD_ECONOMY_ENVIRONMENT: "isolated-test" },
  durableObjects: { PLAYER_INVENTORY: { className: "PlayerInventory", useSQLite: true } },
  serviceBindings: { PDD_FAKE_STEAM: transport },
  outboundService: () => { throw new Error("Isolated economy must never access the network"); },
}));
try {
  await mf.ready;
  let token;
  const send = async (path, body, expected = 200) => {
    const response = await mf.dispatchFetch(`https://example.test${path}`, { method: body ? "POST" : "GET",
      headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) }, ...(body ? { body: JSON.stringify(body) } : {}) });
    const result = await response.json(); assert.equal(response.status, expected, JSON.stringify(result)); return result;
  };
  const authentication = await send("/v1/auth/steam", { ticketHex: "aabb", identity: "petdadog-backend" });
  assert.equal(authentication.maxGrantBatchSize, 300);
  token = authentication.sessionToken;
  const buy = (id, offerId) => ({ clientEventId: guid(id), offerId, economyVersion: "golden-bones-v2" });
  const replies = await Promise.all([send("/v2/shop/purchase", buy(1, "dig")), send("/v2/shop/purchase", buy(1, "dig"))]);
  assert.ok(replies.every(r => r.pets === 1500 && r.status === "complete"));
  await send("/v2/shop/purchase", buy(2, "dig"), 409);
  assert.equal((await send("/v2/shop/purchase", buy(3, "dog:3076"))).goldenBones, 800);
  assert.equal((await send("/v2/shop/purchase", buy(4, "dog:3076"))).goldenBones, 600);
  const recycled = await send("/v2/shop/recycle", { clientEventId: guid(5), itemDefId: 3076, quantity: 1, equippedQuantity: 0, economyVersion: "golden-bones-v2" });
  assert.equal(recycled.goldenBones, 605); assert.equal(recycled.receivedItems[0].quantity, 5);
  const status = await send(`/v2/shop/operations/${guid(1)}`);
  assert.equal(status.receipts.length, 4); assert.equal(status.status, "complete");
  assert.equal((await send("/v2/shop/ack", { clientEventId: guid(1) })).receipts.length, 3);
  await send("/v1/boxes/buy", { clientEventId: guid(6), boxType: "dog" }, 409);
  const seeded = await send("/v2/shop/dev/seed", { clientEventId: guid(7), pets: 100, goldenBones: 100 });
  assert.equal(seeded.goldenBones, 705); assert.equal(seeded.capabilities.developerControls, true);
  assert.equal((await send("/v2/shop/dev/reset-cooldown", { clientEventId: guid(8) })).nextDigAt, null);
  const beforeBatch = account.items.find(row => row.itemdefid === 100).quantity;
  const addCallsBefore = [...account.requests.keys()].filter(key => key.startsWith("add:")).length;
  const batch = { clientEventId: guid(9), runId: guid(12), firstSequence: 1, count: 300 };
  const batches = await Promise.all([send("/v1/pets/grant-batch", batch), send("/v1/pets/grant-batch", batch)]);
  assert.ok(batches.every(result => result.granted === 300 && result.pets === beforeBatch + 300));
  assert.equal([...account.requests.keys()].filter(key => key.startsWith("add:")).length, addCallsBefore + 1);
  assert.equal((await send("/v2/shop")).pendingOperation, undefined);
  assert.equal((await send("/v1/pets/grant-batch", { ...batch, count: 299 }, 409)).code, "operation_conflict");
  assert.equal((await send("/v1/pets/grant-batch", { ...batch, clientEventId: guid(11), firstSequence: 300, count: 1 }, 409)).code, "sequence_conflict");
  const afterBatchPurchase = await send("/v2/shop/purchase", buy(10, "dig"));
  assert.equal(afterBatchPurchase.status, "complete");
  assert.equal(afterBatchPurchase.pets, beforeBatch + 300 - 500);
  console.log("Actual workerd + SQLite v2 Shop smoke passed: permit, cooldown, concurrent grant-batch dedup/range guards followed by purchase, direct copies, recycling, receipts/ack, legacy retirement, isolated seed/reset and zero real network.");
} finally { await mf.dispose(); }
