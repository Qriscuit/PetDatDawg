// Verify the exact release bundle in real workerd + SQLite. All Steam calls
// terminate in a local fake; any other outbound network request is rejected.
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { Miniflare, convertV4MiniflareOptions, Response } from "miniflare";
import { inspectBundle, sha256 } from "./build-preserved-economy-candidate.js";

const directory = path.resolve(process.argv[2] ?? ".wrangler/batching-release-20261029/candidate");
const manifest = JSON.parse(await readFile(path.join(directory, "manifest.json"), "utf8"));
const liveText = await readFile(manifest.livePath, "utf8");
const candidateText = await readFile(manifest.candidatePath, "utf8");
assert.equal(sha256(liveText), manifest.liveSha256);
assert.equal(sha256(candidateText), manifest.candidateSha256);
const live = inspectBundle(liveText), candidate = inspectBundle(candidateText);
assert.equal(candidate.prefix, live.prefix);
assert.equal(candidate.shop, live.shop);
assert.equal(sha256(candidate.prefix), manifest.preservedPrefixSha256);
assert.equal(candidate.economy.version, "golden-bones-v1");
const { FakeSteamBackend } = await import(pathToFileURL(path.join(directory, "fixture/fake-steam.js")));
const { createSession } = await import(pathToFileURL(manifest.candidatePath));
const secret = "runtime-preserved-economy-test-secret-never-live";
const steamId = "76561198000000000", secondSteamId = "76561198000000001";
const guid = n => `${String(n).padStart(8, "0")}-89ab-cdef-0123-456789abcdef`;
const bindings = {
  PDD_STEAM_APP_ID: "4817200", PDD_STEAM_PETS_ITEMDEF_ID: "100", PDD_GRANTS_PER_MINUTE: "300",
  PDD_SHOP_ENABLED: "1", PDD_LEGACY_PURCHASES_ENABLED: "0", PDD_CASH_PURCHASES_ENABLED: "0", PDD_MARKET_ENABLED: "0",
  PDD_STEAM_PUBLISHER_KEY: "test-only-publisher-key", PDD_BACKEND_SESSION_SECRET: secret, PDD_ECONOMY_ENVIRONMENT: "isolated-test",
};
const instances = [];
async function instance(name, scriptPath) {
  const fake = new FakeSteamBackend();
  for (const id of [steamId, secondSteamId]) { fake.add(fake.account(id), 100, 10000); fake.add(fake.account(id), 101, 10000); }
  const mf = new Miniflare(convertV4MiniflareOptions({
    name, modules: [{ type: "ESModule", path: scriptPath }], compatibilityDate: "2026-10-10", cf: false, bindings,
    durableObjects: { PLAYER_INVENTORY: { className: "PlayerInventory", useSQLite: true } },
    serviceBindings: { PDD_FAKE_STEAM: async request => {
      const response = await fake.fetch(request.url, { method: request.method, headers: Object.fromEntries(request.headers),
        ...(request.method !== "GET" ? { body: await request.text() } : {}) });
      return new Response(await response.text(), { status: response.status, headers: Object.fromEntries(response.headers) });
    } },
    outboundService: () => { throw new Error("Release verification must never contact the network"); },
  }));
  instances.push(mf); await mf.ready;
  let token;
  const send = async (route, body, expected = 200, overrideToken) => {
    const response = await mf.dispatchFetch(`https://example.test${route}`, {
      method: body ? "POST" : "GET", headers: { "Content-Type": "application/json", ...(overrideToken || token ? { Authorization: `Bearer ${overrideToken ?? token}` } : {}) },
      ...(body ? { body: JSON.stringify(body) } : {}),
    });
    const result = await response.json(); assert.equal(response.status, expected, JSON.stringify(result));
    return result;
  };
  const auth = await send("/v1/auth/steam", { ticketHex: "aabb", identity: "petdadog-backend" }); token = auth.sessionToken;
  return { fake, send, auth };
}

try {
  const baseline = await instance("preserved-live", manifest.livePath);
  const release = await instance("preserved-candidate", manifest.candidatePath);
  const before = await baseline.send("/v2/shop"), current = await release.send("/v2/shop");
  assert.equal(current.economyVersion, before.economyVersion);
  assert.deepEqual(current.catalog, before.catalog, "Every live price, pool, cosmetic, pack and icon URL must remain unchanged");
  assert.deepEqual(current.capabilities, before.capabilities);
  assert.equal(current.catalog.offers.filter(offer => offer.kind === "dog" || offer.kind === "accessory").length, live.economy.cosmetics.length);
  assert.equal(release.auth.maxGrantBatchSize, 300);
  const batch = { clientEventId: guid(1), runId: guid(2), firstSequence: 1, count: 300 };
  const results = await Promise.all([release.send("/v1/pets/grant-batch", batch), release.send("/v1/pets/grant-batch", batch)]);
  assert.ok(results.every(result => result.pets === 10300 && result.granted === 300));
  assert.equal([...release.fake.account(steamId).requests.keys()].filter(key => key.startsWith("add:")).length, 1);
  assert.equal((await release.send("/v1/pets/grant-batch", { ...batch, count: 299 }, 409)).code, "operation_conflict");
  assert.equal((await release.send("/v1/pets/grant-batch", { ...batch, clientEventId: guid(3), firstSequence: 300, count: 1 }, 409)).code, "sequence_conflict");
  assert.equal((await release.send("/v2/shop")).pendingOperation, undefined);
  const buy = (id, offerId) => ({ clientEventId: guid(id), offerId, economyVersion: current.economyVersion });
  const dig = current.catalog.offers.find(offer => offer.id === "dig");
  const digResult = await release.send("/v2/shop/purchase", buy(4, "dig"));
  assert.equal(digResult.status, "complete"); assert.equal(digResult.pets, 10300 - dig.price);
  assert.ok(digResult.receivedItems.every(item => dig.rewardItemDefIds.includes(item.itemDefId)));
  assert.equal((await release.send("/v2/shop/purchase", buy(5, "dig"), 409)).code, "dig_cooldown");
  const dog = current.catalog.offers.find(offer => offer.kind === "dog");
  await release.send("/v2/shop/purchase", buy(6, dog.id));
  const bought = await release.send("/v2/shop/purchase", buy(7, dog.id));
  assert.equal(bought.goldenBones, 10000 - 2 * dog.price);
  const recycled = await release.send("/v2/shop/recycle", { clientEventId: guid(8), itemDefId: dog.outputItemDefId, quantity: 1, equippedQuantity: 0, economyVersion: current.economyVersion });
  assert.equal(recycled.goldenBones, 10000 - 2 * dog.price + current.catalog.recycleValue);
  assert.equal((await release.send(`/v2/shop/operations/${guid(4)}`)).status, "complete");
  const receiptCount = recycled.receipts.length;
  assert.equal((await release.send("/v2/shop/ack", { clientEventId: guid(4) })).receipts.length, receiptCount - 1);
  const replay = await release.send("/v1/pets/grant-batch", batch);
  assert.equal(replay.pets, 10300 - dig.price); assert.equal(replay.granted, 300); assert.equal(replay.replayed, true);
  await release.send("/v1/boxes/buy", { clientEventId: guid(9), boxType: "dog" }, 409);
  const otherToken = await createSession(secondSteamId, secret);
  await release.send("/v1/pets/grant", { clientEventId: guid(10) }, 200, otherToken);
  const mixed = await release.send("/v1/pets/grant-batch", { clientEventId: guid(11), runId: guid(12), firstSequence: 1, count: 299 }, 200, otherToken);
  assert.equal(mixed.pets, 10300);
  assert.equal((await release.send("/v1/pets/grant", { clientEventId: guid(13) }, 429, otherToken)).code, "rate_limited");
  console.log(JSON.stringify({ status: "passed", economyVersion: current.economyVersion, cosmeticCount: live.economy.cosmetics.length,
    catalogEqualToLive: true, preservedPrefixSha256: manifest.preservedPrefixSha256, candidateSha256: manifest.candidateSha256,
    checks: ["exact live economy and Shop bytes", "full catalog equality", "concurrent 300-input batch deduplication", "changed payload and overlap rejection", "batch followed by v1 Dig purchase", "cooldown", "v1 direct dog copies and recycling", "receipts and acknowledgment", "fresh balance on completed replay", "shared legacy/batch quantity cap"], network: "local fake Steam only" }, null, 2));
} finally { await Promise.all(instances.map(instance => instance.dispose())); }
