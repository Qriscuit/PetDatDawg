import assert from "node:assert/strict";
import { test } from "node:test";
import { DatabaseSync } from "node:sqlite";
import worker, { ApiError, configuration, createSession, validateSession, steamRequestId,
  parseItems, SteamClient, PlayerService, PlayerInventory, exchangeIntent, exchangeReceipt } from "../src/index.js";

const steamId = "76561198000000000";
const guid = "01234567-89ab-cdef-0123-456789abcdef";
const otherGuid = "11234567-89ab-cdef-0123-456789abcdef";
const thirdGuid = "21234567-89ab-cdef-0123-456789abcdef";
const env = { PDD_STEAM_APP_ID: "4817200", PDD_STEAM_PETS_ITEMDEF_ID: "100", PDD_GRANTS_PER_MINUTE: "300",
  PDD_STEAM_PUBLISHER_KEY: "test-only-publisher-key", PDD_BACKEND_SESSION_SECRET: "test-only-secret-at-least-32-characters" };
const config = configuration(env);
const item = (itemId, itemDefId, quantity) => ({ itemId, itemDefId, quantity });
const inventory = items => ({ items, pets: items.filter(i => i.itemDefId === 100).reduce((n, i) => n + i.quantity, 0) });
const rawItem = (itemid, itemdefid, quantity = 1, state = "") => ({ itemid, itemdefid, quantity, state, appid: 4817200 });
const steamResponse = rows => ({ response: { item_json: JSON.stringify(rows) } });
const failure = () => new ApiError(503, "steam_unavailable", "Unconfirmed.");

// Exercise the actual transactional SQLite semantics beneath a small fake of
// the DO storage KV API. No tests contact Steam or alter anyone's inventory.
class DurableStorage {
  constructor() {
    this.db = new DatabaseSync(":memory:");
    this.db.exec("CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
  }
  async get(key) {
    const row = this.db.prepare("SELECT value FROM kv WHERE key = ?").get(key);
    return row ? JSON.parse(row.value) : undefined;
  }
  async put(key, value) {
    this.db.prepare("INSERT INTO kv VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value").run(key, JSON.stringify(value));
  }
  async delete(key) { this.db.prepare("DELETE FROM kv WHERE key = ?").run(key); }
  async transaction(callback) {
    this.db.exec("BEGIN");
    try { const result = await callback(this); this.db.exec("COMMIT"); return result; }
    catch (error) { this.db.exec("ROLLBACK"); throw error; }
  }
}

class FakeSteam {
  constructor(items = [item("9007199254740993", 100, 10)]) {
    this.items = structuredClone(items);
    this.grants = new Set();
    this.grantCalls = [];
    this.exchanges = 0;
    this.nextId = 18446744073709551000n;
    this.readFailures = 0;
    this.failGrantAfterCommit = false;
    this.failExchangeAfterCommit = false;
    this.rejectExchange = false;
    this.emptyReads = false;
  }
  async inventory() {
    if (this.readFailures-- > 0) throw failure();
    return inventory(structuredClone(this.items));
  }
  async grant(id, requestId) {
    assert.equal(id, steamId);
    this.grantCalls.push(requestId);
    const replayed = this.grants.has(requestId);
    if (!replayed) {
      this.grants.add(requestId);
      let pets = this.items.find(i => i.itemDefId === 100);
      if (!pets) this.items.push(pets = item((this.nextId++).toString(), 100, 0));
      pets.quantity++;
    }
    if (this.failGrantAfterCommit) { this.failGrantAfterCommit = false; throw failure(); }
    return { replayed };
  }
  async exchange(id, intent) {
    assert.equal(id, steamId);
    this.exchanges++;
    if (this.rejectExchange) return [];
    const affected = [];
    for (const material of intent.materials) {
      const owned = this.items.find(i => i.itemId === material.itemId);
      assert.ok(owned && owned.quantity >= material.consume);
      owned.quantity -= material.consume;
      affected.push({ ...owned, removed: owned.quantity === 0 });
    }
    this.items = this.items.filter(i => i.quantity > 0);
    let output = this.items.find(i => i.itemDefId === intent.firstReward && ![1000, 1001].includes(i.itemDefId));
    if (!output) this.items.push(output = item((this.nextId++).toString(), intent.firstReward, 0));
    output.quantity++;
    affected.push({ ...output, removed: false });
    if (this.failExchangeAfterCommit) { this.failExchangeAfterCommit = false; throw failure(); }
    return affected;
  }
}

function fixture(items, options = {}) {
  const storage = new DurableStorage();
  const steam = new FakeSteam(items);
  const service = new PlayerService(storage, steam, { ...config, ...options }, () => 120000);
  return { storage, steam, service };
}

test("session identifies a Steam account, expires in one hour, and rejects tampering", async () => {
  const now = 1800000000000;
  const token = await createSession(steamId, config.secret, now);
  assert.equal(await validateSession(token, config.secret, now + 3599000), steamId);
  assert.equal(await validateSession(token, config.secret, now + 3600000), null);
  assert.equal(await validateSession(token, "different-secret", now), null);
  assert.equal(await validateSession(`${token.slice(0, 10)}x${token.slice(11)}`, config.secret, now), null);
  assert.equal(await validateSession("invalid.token.parts", config.secret, now), null);
});

test("Steam request ID matches the Windows backend's little-endian SHA256 vector", async () => {
  assert.equal(await steamRequestId(steamId, guid.toUpperCase()), "5266704354957166947");
});

test("authentication requires a valid ticket and an active license for the configured app", async () => {
  const calls = [];
  const client = new SteamClient(config, async (url, options) => {
    calls.push({ url, options });
    return Response.json(url.includes("AuthenticateUserTicket")
      ? { response: { params: { result: "OK", steamid: steamId } } }
      : { appownership: { ownsapp: true, usercanceled: false } });
  });
  assert.equal(await client.authenticate("aabbcc", "petdadog-backend"), steamId);
  assert.equal(calls.length, 2);
  for (const { url, options } of calls) {
    assert.equal(new URL(url).searchParams.get("appid"), "4817200");
    assert.equal(options.headers["x-webapi-key"], config.key);
    assert.equal(new URL(url).searchParams.has("key"), false);
  }
  await assert.rejects(client.authenticate("aabb", "other"), { status: 400 });
  const noLicense = new SteamClient(config, async url => Response.json(url.includes("AuthenticateUserTicket")
    ? { response: { params: { result: "OK", steamid: steamId } } } : { appownership: { ownsapp: false } }));
  await assert.rejects(noLicense.authenticate("aabb", "petdadog-backend"), { status: 403 });
  const badTicket = new SteamClient(config, async () => Response.json({ response: { error: { errorcode: 101 } } }));
  await assert.rejects(badTicket.authenticate("aabb", "petdadog-backend"), { status: 401 });
});

test("Steam HTTP failures identify only the static endpoint and status without leaking credentials or tickets", async () => {
  const originalFetch = globalThis.fetch;
  const ticket = "aabbccddeeff";
  try {
    for (const endpoint of ["ISteamUserAuth/AuthenticateUserTicket", "ISteamUser/CheckAppOwnership"]) {
      for (const status of [401, 403, 404, 405, 429, 500, 503]) {
        globalThis.fetch = async url => {
          if (endpoint === "ISteamUser/CheckAppOwnership" && url.includes("AuthenticateUserTicket")) {
            return Response.json({ response: { params: { result: "OK", steamid: steamId } } });
          }
          // Steam may echo sensitive inputs in an error body. Never forward it.
          return new Response(`${config.key} ${ticket} ${url}`, { status });
        };
        const response = await worker.fetch(new Request("https://example.test/v1/auth/steam", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ ticketHex: ticket, identity: "petdadog-backend" }),
        }), env);
        assert.equal(response.status, 503);
        const body = await response.text();
        const result = JSON.parse(body);
        assert.equal(result.code, [401, 403].includes(status) ? "steam_access_denied" : "steam_unavailable");
        assert.equal(result.steamEndpoint, endpoint);
        assert.equal(result.steamHttpStatus, status);
        assert.deepEqual(Object.keys(result).sort(), ["code", "error", "steamEndpoint", "steamHttpStatus"].sort());
        for (const sensitive of [config.key, config.secret, ticket, steamId, "https://", "ticket="]) {
          assert.equal(body.includes(sensitive), false);
        }
      }
    }
  } finally { globalThis.fetch = originalFetch; }
});

test("unconfirmed grant replies expose bounded diagnostics and retain the same request ID for retry", async () => {
  const originalFetch = globalThis.fetch;
  const token = await createSession(steamId, config.secret);
  const echo = `${config.key} ${config.secret} ${token} ${steamId} https://example.test/?ticket=aabbcc`;
  const cases = [
    [{ response: { success: false, error: `Unknown itemdef. ${echo}`, result: "8" } }, "item_definition_error", 8],
    [{ response: { success: false, error: `Invalid itempropsjson. ${echo}` } }, "item_properties_error"],
    [{ response: { success: false, error: `Inventory not enabled. ${echo}` } }, "inventory_disabled"],
    [{ response: { success: false, error: `Permission denied. ${echo}` } }, "permission_denied"],
    [{ response: { success: false, error: `Invalid parameter. ${echo}` } }, "invalid_parameters"],
    [{ response: { success: false, error: echo, eresult: 2 } }, "steam_rejected", 2],
    [{ response: { success: false, error: { errorcode: "65535", description: echo } } }, "steam_rejected", 65535],
    [{ response: { success: false, result: steamId } }, "steam_rejected"],
    [{ response: { success: false, result: 65536 } }, "steam_rejected"],
    [{ response: { success: echo } }, "invalid_success_flag"],
    [{}, "missing_response"],
    [{ response: { success: true, error_detail: echo } }, "missing_item_json"],
    [{ response: { item_json: echo } }, "invalid_item_json"],
    [{ response: { item_json: JSON.stringify({ echo }) } }, "invalid_item_json"],
    [{ response: { item_json: JSON.stringify([{ ...rawItem("1", 100), quantity: echo }]) } }, "invalid_item_data"],
    [steamResponse([]), "missing_pets_receipt"],
    [{ response: { ...steamResponse([rawItem("1", 100)]).response, replayed: echo } }, "invalid_replayed_flag"],
    [echo, "invalid_response_json"],
    [[], "invalid_response_document"],
    [new Error(echo), "connection_failed"],
  ];
  try {
    for (const [reply, category, resultCode] of cases) {
      const storage = new DurableStorage();
      const object = new PlayerInventory({ storage }, env);
      const requestIds = [];
      let recovered = false;
      globalThis.fetch = async (url, options) => {
        if (url.includes("GetInventory")) return Response.json(steamResponse([rawItem("1", 100, 11)]));
        assert.ok(url.includes("AddItem"));
        requestIds.push(options.body.get("requestid"));
        if (recovered) return Response.json(steamResponse([rawItem("1", 100, 11)]));
        if (reply instanceof Error) throw reply;
        return typeof reply === "string" ? new Response(reply) : Response.json(reply);
      };
      const request = () => new Request("https://example.test/v1/pets/grant", {
        method: "POST", headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify({ clientEventId: guid }),
      });
      const response = await object.fetch(request());
      assert.equal(response.status, 503);
      const text = await response.text();
      const body = JSON.parse(text);
      assert.equal(body.code, "steam_unavailable");
      assert.equal(body.steamEndpoint, "IInventoryService/AddItem");
      assert.equal(body.steamFailureCategory, category);
      assert.equal(body.steamResult, resultCode);
      assert.deepEqual(Object.keys(body).sort(), ["code", "error", "steamEndpoint", "steamFailureCategory",
        ...(resultCode !== undefined ? ["steamResult"] : [])].sort());
      for (const sensitive of [config.key, config.secret, token, steamId, "https://", "ticket=", "aabbcc"]) {
        assert.equal(text.includes(sensitive), false);
      }
      assert.equal((await storage.get(`op:${guid}`)).state, "pending");
      recovered = true;
      const retry = await object.fetch(request());
      assert.equal(retry.status, 200);
      assert.equal((await retry.json()).pets, 11);
      assert.deepEqual(requestIds, ["5266704354957166947", "5266704354957166947"]);
    }
  } finally { globalThis.fetch = originalFetch; }
});

test("inventory is fail-closed and preserves IDs beyond JS integer precision", async () => {
  const rows = parseItems({ item_json: JSON.stringify([rawItem("18446744073709551615", "100", "4294967295")]) });
  assert.equal(rows[0].itemId, "18446744073709551615");
  assert.equal(rows[0].quantity, 4294967295);
  for (const response of [undefined, {}, { item_json: "" }, { item_json: "{}" }, { success: false, item_json: "[]" },
    { item_json: JSON.stringify([rawItem(9007199254740992, 100)]) },
    { item_json: JSON.stringify([rawItem("18446744073709551616", 100)]) },
    { item_json: JSON.stringify([{ itemid: "1", itemdefid: "100" }]) },
    { item_json: JSON.stringify([rawItem("1", 100, -1)]) },
    { item_json: JSON.stringify([rawItem("1", 100), rawItem("1", 100)]) },
    { item_json: JSON.stringify([{ ...rawItem("1", 100), appid: 480 }]) }]) {
    assert.throws(() => parseItems(response), { status: 503 });
  }
  const client = new SteamClient(config, async () => Response.json(steamResponse([
    rawItem("1", 100, 3), rawItem("2", 100, 50, "removed"), rawItem("3", 3000, 1),
  ])));
  assert.deepEqual(await client.inventory(steamId), inventory([item("1", 100, 3), item("3", 3000, 1)]));
});

test("HTTP 200 inventory failure headers cannot confirm an empty or valid-looking balance", async () => {
  const originalFetch = globalThis.fetch;
  const originalWarn = console.warn;
  const token = await createSession(steamId, config.secret);
  const sensitive = `${config.key} ${config.secret} ${token} ${steamId} https://example.test/?ticket=aabbcc`;
  const cases = [
    [{ "x-eresult": "2", "x-error_message": `Inventory Service is not enabled for this app. ${sensitive}` }, "inventory_disabled", 2],
    [{ "x-eresult": "2" }, "steam_rejected", 2],
    [{ "x-eresult": "0" }, "steam_rejected", 0],
    [{ "x-eresult": "65535" }, "steam_rejected", 65535],
    [{ "x-eresult": "1", "x-error_message": sensitive }, "steam_rejected", 1],
    [{ "x-error_message": `Invalid parameter. ${sensitive}` }, "invalid_parameters"],
    ...["", "01", "1, 1", "-1", "1.0", "1e0", "65536", "999999", sensitive]
      .map(value => [{ "x-eresult": value }, "invalid_success_flag"]),
  ];
  const logs = [];
  console.warn = record => logs.push(record);
  try {
    for (const rows of [[], [rawItem("1", 100, 99)]]) {
      for (const [headers, category, resultCode] of cases) {
        const object = new PlayerInventory({ storage: new DurableStorage() }, env);
        globalThis.fetch = async () => Response.json(steamResponse(rows), { headers });
        const response = await object.fetch(new Request("https://example.test/v1/inventory", {
          headers: { Authorization: `Bearer ${token}` },
        }));
        assert.equal(response.status, 503);
        const body = await response.json();
        assert.equal(Object.hasOwn(body, "pets"), false);
        assert.equal(Object.hasOwn(body, "items"), false);
        assert.equal(body.steamEndpoint, "IInventoryService/GetInventory");
        assert.equal(body.steamFailureCategory, category);
        assert.equal(body.steamResult, resultCode);
        const log = logs.at(-1);
        assert.deepEqual(log, { event: "steam_request_failed", steamEndpoint: "IInventoryService/GetInventory", steamHttpStatus: 200,
          steamFailureCategory: category, ...(resultCode === undefined ? {} : { steamResult: resultCode }) });
        for (const value of [config.key, config.secret, token, steamId, "https://", "ticket=", "aabbcc"]) {
          assert.equal(JSON.stringify([body, log]).includes(value), false);
        }
      }
    }
    assert.equal(logs.length, cases.length * 2);
    for (const headers of [{}, { "x-eresult": "1" }, { "x-eresult": "1", "x-error_message": "" }]) {
      const client = new SteamClient(config, async () => Response.json(steamResponse([]), { headers }));
      assert.deepEqual(await client.inventory(steamId), { pets: 0, items: [] });
    }
    assert.equal(logs.length, cases.length * 2); // Successful reads do not emit failure logs.
  } finally { globalThis.fetch = originalFetch; console.warn = originalWarn; }
});

test("a header-rejected Pets receipt stays pending and retries only its original Steam request ID", async () => {
  const originalWarn = console.warn;
  console.warn = () => {};
  try {
    for (const rows of [[], [rawItem("1", 100, 11)]]) {
      const storage = new DurableStorage();
      const requestIds = [];
      let recovered = false;
      let inventoryCalls = 0;
      const client = new SteamClient(config, async (url, options) => {
        if (url.includes("GetInventory")) {
          inventoryCalls++;
          return Response.json(steamResponse([rawItem("1", 100, 11)]), { headers: { "x-eresult": "1" } });
        }
        requestIds.push(options.body.get("requestid"));
        return Response.json(steamResponse(recovered ? [rawItem("1", 100, 11)] : rows), {
          headers: { "x-eresult": recovered ? "1" : "2", ...(recovered ? {} : { "x-error_message": "Inventory not enabled" }) },
        });
      });
      const service = new PlayerService(storage, client, config);
      await assert.rejects(service.grant(steamId, { clientEventId: guid }), { status: 503, extra: {
        steamEndpoint: "IInventoryService/AddItem", steamFailureCategory: "inventory_disabled", steamResult: 2,
      } });
      assert.equal((await storage.get(`op:${guid}`)).state, "pending");
      assert.equal(inventoryCalls, 0);
      recovered = true;
      const restarted = new PlayerService(storage, client, config);
      const result = await restarted.grant(steamId, { clientEventId: guid });
      assert.equal(result.granted, 1);
      assert.equal(result.pets, 11);
      assert.deepEqual(requestIds, ["5266704354957166947", "5266704354957166947"]);
    }
  } finally { console.warn = originalWarn; }
});

test("HTTP 200 header-rejected exchanges remain uncertain across restart and cannot be repeated", async () => {
  const originalWarn = console.warn;
  console.warn = () => {};
  try {
    for (const headers of [{ "x-eresult": "2" }, { "x-eresult": "01" }, { "x-error_message": "Inventory not enabled" }]) {
      const storage = new DurableStorage();
      let exchangeCalls = 0;
      let rejectInventory = false;
      const client = new SteamClient(config, async url => {
        if (url.includes("GetInventory")) return Response.json(steamResponse([rawItem("1", 100, 10)]), {
          headers: { "x-eresult": rejectInventory ? "2" : "1" },
        });
        exchangeCalls++;
        rejectInventory = true;
        return Response.json(steamResponse([rawItem("1", 100, 9), rawItem("2", 1001, 1)]), { headers });
      });
      const service = new PlayerService(storage, client, config);
      const input = { clientEventId: guid, boxType: "dog" };
      const pending = await service.exchange(steamId, "buy", input);
      assert.equal(pending.status, "pending");
      assert.equal(Object.hasOwn(pending, "pets"), false);
      assert.equal((await storage.get(`op:${guid}`)).state, "pending");
      assert.equal(await storage.get("active"), guid);
      const restarted = new PlayerService(storage, client, config);
      assert.equal((await restarted.exchange(steamId, "buy", input)).status, "pending");
      await assert.rejects(restarted.grant(steamId, { clientEventId: otherGuid }), { code: "exchange_pending" });
      assert.equal(exchangeCalls, 1);
      assert.equal(await storage.get("active"), guid);
    }
  } finally { console.warn = originalWarn; }
});

test("failed Steam calls emit one bounded structured record and never log untrusted errors", async () => {
  const originalWarn = console.warn;
  const logs = [];
  const sensitive = `${config.key} ${config.secret} ${steamId} ${guid} https://example.test/?ticket=aabbcc`;
  console.warn = record => logs.push(record);
  try {
    const cases = [
      [async () => new Response(sensitive, { status: 403 }), { steamHttpStatus: 403, steamFailureCategory: "permission_denied" }],
      [async () => new Response(sensitive), { steamHttpStatus: 200, steamFailureCategory: "invalid_response_json" }],
      [async () => { throw new Error(sensitive); }, { steamFailureCategory: "connection_failed" }],
      [async () => { throw new ApiError(503, sensitive, sensitive, { steamFailureCategory: sensitive, steamResult: sensitive }); },
        { steamFailureCategory: "steam_rejected" }],
    ];
    for (const [fetcher, expected] of cases) {
      const before = logs.length;
      const client = new SteamClient(config, fetcher);
      await assert.rejects(client.call("GET", "IInventoryService/GetInventory/v1/", { steamid: steamId }));
      assert.equal(logs.length, before + 1);
      assert.deepEqual(logs.at(-1), { event: "steam_request_failed", steamEndpoint: "IInventoryService/GetInventory", ...expected });
    }
    const client = new SteamClient(config, async () => { throw new Error(sensitive); });
    await assert.rejects(client.call("GET", sensitive, {}));
    assert.equal(logs.at(-1).steamEndpoint, "unknown");
    for (const value of [config.key, config.secret, steamId, guid, "https://", "ticket=", "aabbcc"]) {
      assert.equal(JSON.stringify(logs).includes(value), false);
    }
  } finally { console.warn = originalWarn; }
});

test("one click grants exactly one Pet, ignoring caller-supplied amounts, and survives duplicate requests/restart", async () => {
  const { storage, steam, service } = fixture();
  const first = await service.grant(steamId, { clientEventId: guid, quantity: 999, itemDefId: 3000 });
  assert.equal(first.pets, 11);
  assert.equal(first.granted, 1);
  const restarted = new PlayerService(storage, steam, config);
  const duplicate = await restarted.grant(steamId, { clientEventId: guid.toUpperCase() });
  assert.equal(duplicate.pets, 11);
  assert.equal(duplicate.replayed, true);
  assert.equal(steam.grantCalls.length, 1);
});

test("grant timeout retries the identical uint64 ID and cannot issue another Pet", async () => {
  const { steam, service } = fixture();
  steam.failGrantAfterCommit = true;
  await assert.rejects(service.grant(steamId, { clientEventId: guid }), { status: 503 });
  const result = await service.grant(steamId, { clientEventId: guid });
  assert.equal(result.pets, 11);
  assert.equal(result.replayed, true);
  assert.deepEqual(steam.grantCalls, ["5266704354957166947", "5266704354957166947"]);
});

test("inventory reads and replayed grants return current Steam totals after external balance changes", async () => {
  const { storage, steam, service } = fixture();
  assert.equal((await service.readInventory(steamId)).pets, 10);
  assert.equal((await service.grant(steamId, { clientEventId: guid })).pets, 11);

  // Another authorized Steam operation spent currency after this click.
  steam.items.find(i => i.itemDefId === 100).quantity = 4;
  assert.equal((await service.readInventory(steamId)).pets, 4);
  const restarted = new PlayerService(storage, steam, config);
  const replay = await restarted.grant(steamId, { clientEventId: guid });
  assert.equal(replay.pets, 4);
  assert.equal(replay.replayed, true);
  assert.equal(steam.grantCalls.length, 1);

  // No cached total may replace the next authoritative inventory response.
  steam.items.push(item("2", 100, 3));
  assert.equal((await restarted.readInventory(steamId)).pets, 7);
  assert.equal((await restarted.grant(steamId, { clientEventId: otherGuid })).pets, 8);
  assert.equal(steam.grantCalls.length, 2);
});

test("a prior run's abandoned pending click does not prevent new clicks", async () => {
  const { storage, steam, service } = fixture();
  steam.failGrantAfterCommit = true;
  await assert.rejects(service.grant(steamId, { clientEventId: guid }));
  const restarted = new PlayerService(storage, steam, config);
  const result = await restarted.grant(steamId, { clientEventId: otherGuid });
  assert.equal(result.pets, 12);
  assert.equal(steam.grantCalls.length, 2);
  assert.notEqual(steam.grantCalls[0], steam.grantCalls[1]);
});

test("a grant confirmed before inventory-read failure is not sent again", async () => {
  const { steam, service } = fixture();
  steam.readFailures = 1;
  await assert.rejects(service.grant(steamId, { clientEventId: guid }), { status: 503 });
  assert.equal((await service.grant(steamId, { clientEventId: guid })).pets, 11);
  assert.equal(steam.grantCalls.length, 1);
});

test("persisted per-player grant cap applies only to new clicks and transactions roll back denied requests", async () => {
  const { steam, storage, service } = fixture(undefined, { grantsPerMinute: 1 });
  await service.grant(steamId, { clientEventId: guid });
  const restarted = new PlayerService(storage, steam, { ...config, grantsPerMinute: 1 }, () => 120000);
  assert.equal((await restarted.grant(steamId, { clientEventId: guid })).replayed, true);
  await assert.rejects(restarted.grant(steamId, { clientEventId: otherGuid }), { status: 429 });
  assert.equal(await storage.get(`op:${otherGuid}`), undefined);
  assert.deepEqual(await storage.get("grant-rate"), { minute: 2, count: 1 });
});

test("buying and opening each box uses schema-defined costs/generators and produces confirmed inventory", async () => {
  for (const [boxType, cost, boxDef, rewardDef] of [["dog", 1, 1001, 3000], ["accessory", 2, 1000, 2000]]) {
    const { storage, steam, service } = fixture();
    const bought = await service.exchange(steamId, "buy", { clientEventId: guid, boxType, cost: 0 });
    assert.equal(bought.status, "complete");
    assert.equal(bought.pets, 10 - cost);
    assert.equal(bought.receivedItems[0].itemDefId, boxDef);
    const input = { clientEventId: otherGuid, boxItemId: bought.receivedItems[0].itemId };
    const opened = await service.exchange(steamId, "open", input);
    assert.equal(opened.status, "complete");
    assert.equal(opened.receivedItems[0].itemDefId, rewardDef);
    assert.equal(opened.receivedItems[0].quantity, 1);
    assert.equal(opened.items.some(i => i.itemDefId === boxDef), false);
    const restarted = new PlayerService(storage, steam, config);
    assert.deepEqual((await restarted.exchange(steamId, "open", input)).receivedItems, opened.receivedItems);
    assert.equal(steam.exchanges, 2);
  }
});

test("buying can consume Pets across multiple Steam stacks and opening recognizes one added copy to an existing reward stack", async () => {
  const { service } = fixture([item("1", 100, 1), item("2", 100, 1), item("3", 2000, 7)]);
  const bought = await service.exchange(steamId, "buy", { clientEventId: guid, boxType: "accessory" });
  assert.equal(bought.pets, 0);
  const opened = await service.exchange(steamId, "open", { clientEventId: otherGuid, boxItemId: bought.receivedItems[0].itemId });
  assert.deepEqual(opened.receivedItems, [item("3", 2000, 1)]);
  assert.equal(opened.items.find(i => i.itemId === "3").quantity, 8);
});

test("a delayed AddItem credit between purchase snapshot and receipt does not strand a successful purchase", async () => {
  const { service, steam, storage } = fixture();
  let delayedCredit = false;
  const originalGrant = steam.grant.bind(steam);
  steam.grant = async (id, requestId) => {
    if (!steam.grants.has(requestId)) {
      steam.grantCalls.push(requestId);
      steam.grants.add(requestId);
      delayedCredit = true;
      throw failure(); // Steam is still processing; no credit visible yet.
    }
    return originalGrant(id, requestId);
  };
  const originalExchange = steam.exchange.bind(steam);
  steam.exchange = async (id, intent) => {
    assert.equal(intent.materials[0].quantity, 10); // Pre-exchange read saw 10.
    if (delayedCredit) {
      steam.items.find(row => row.itemDefId === 100).quantity++;
      delayedCredit = false;
    }
    return originalExchange(id, intent); // 10 + delayed 1 - box cost 1 = 10.
  };

  await assert.rejects(service.grant(steamId, { clientEventId: guid }), { status: 503 });
  const input = { clientEventId: otherGuid, boxType: "dog" };
  const bought = await service.exchange(steamId, "buy", input);
  assert.equal(bought.status, "complete");
  assert.equal(bought.pets, 10);
  assert.equal(bought.receivedItems[0].itemDefId, 1001);
  assert.equal(await storage.get("active"), undefined);
  assert.equal((await service.exchange(steamId, "buy", input)).status, "complete");
  assert.equal(steam.exchanges, 1);
  assert.equal((await service.grant(steamId, { clientEventId: guid })).pets, 10);
  assert.equal(steam.grantCalls[0], steam.grantCalls[1]);
});

test("invalid, missing and insufficient inventory never reaches ExchangeItem", async () => {
  const { service, steam } = fixture([]);
  await assert.rejects(service.exchange(steamId, "buy", { clientEventId: guid, boxType: "dog" }), { code: "insufficient_pets" });
  await assert.rejects(service.exchange(steamId, "buy", { clientEventId: guid, boxType: "toString" }), { status: 400 });
  await assert.rejects(service.exchange(steamId, "open", { clientEventId: guid, boxItemId: "99" }), { code: "box_not_found" });
  await assert.rejects(service.exchange(steamId, "open", { clientEventId: guid, boxItemId: 9007199254740992 }), { status: 400 });
  assert.equal(steam.exchanges, 0);
});

test("an operation GUID cannot be repurposed for a different action", async () => {
  const { service, steam } = fixture();
  await service.exchange(steamId, "buy", { clientEventId: guid, boxType: "dog" });
  await assert.rejects(service.exchange(steamId, "buy", { clientEventId: guid, boxType: "accessory" }), { code: "operation_conflict" });
  await assert.rejects(service.grant(steamId, { clientEventId: guid }), { code: "operation_conflict" });
  assert.equal(steam.exchanges, 1);
});

test("exchange timeout persists frozen intent, survives restart, blocks new mutations and never infers success from inventory deltas", async () => {
  const { service, steam, storage } = fixture();
  steam.failExchangeAfterCommit = true;
  const input = { clientEventId: guid, boxType: "dog" };
  const uncertain = await service.exchange(steamId, "buy", input);
  assert.equal(uncertain.status, "pending");
  assert.equal(uncertain.pets, 9); // Confirmed current Steam state, not invented rollback.
  const frozen = await storage.get(`op:${guid}`);
  assert.equal(frozen.intent.materials[0].itemId, "9007199254740993");
  assert.equal(frozen.before.pets, 10);
  const restarted = new PlayerService(storage, steam, config);
  assert.equal((await restarted.exchange(steamId, "buy", input)).status, "pending");
  await assert.rejects(restarted.exchange(steamId, "buy", { clientEventId: otherGuid, boxType: "dog" }), { code: "exchange_pending" });
  await assert.rejects(restarted.grant(steamId, { clientEventId: thirdGuid }), { code: "exchange_pending" });
  const current = await restarted.readInventory(steamId);
  assert.deepEqual(current.pendingOperation, { clientEventId: guid, kind: "buy", boxType: "dog" });
  assert.equal(steam.exchanges, 1);
  assert.equal(steam.grantCalls.length, 0);
});

test("no Steam call occurs if the exchange intent cannot be persisted", async () => {
  const { storage, steam, service } = fixture();
  storage.transaction = async () => { throw new Error("Storage unavailable"); };
  await assert.rejects(service.exchange(steamId, "buy", { clientEventId: guid, boxType: "dog" }));
  assert.equal(steam.exchanges, 0);
});

test("a persisted successful receipt recovers after inventory refresh failure without repeating ExchangeItem", async () => {
  const { service, steam, storage } = fixture();
  const exchange = steam.exchange.bind(steam);
  steam.exchange = async (...args) => { const affected = await exchange(...args); steam.readFailures = 2; return affected; };
  const input = { clientEventId: guid, boxType: "dog" };
  const pending = await service.exchange(steamId, "buy", input);
  assert.equal(pending.status, "pending");
  assert.equal(Object.hasOwn(pending, "pets"), false);
  assert.equal((await storage.get(`op:${guid}`)).state, "applied");
  const restarted = new PlayerService(storage, steam, config);
  assert.equal((await restarted.exchange(steamId, "buy", input)).status, "complete");
  assert.equal(steam.exchanges, 1);
  assert.equal(await storage.get("active"), undefined);
});

test("explicit empty exchange receipt rejects permanently instead of silently retrying", async () => {
  const { service, steam, storage } = fixture();
  steam.rejectExchange = true;
  const input = { clientEventId: guid, boxType: "dog" };
  await assert.rejects(service.exchange(steamId, "buy", input), { code: "exchange_rejected" });
  await assert.rejects(service.exchange(steamId, "buy", input), { code: "exchange_rejected" });
  assert.equal(steam.exchanges, 1);
  assert.equal(await storage.get("active"), undefined);
});

test("documented Steam access/method rejections do not leave a permanent exchange lock", async () => {
  for (const status of [401, 403, 404, 405]) {
    const storage = new DurableStorage();
    let exchangeCalls = 0;
    const steam = new SteamClient(config, async url => {
      if (url.includes("GetInventory")) return Response.json(steamResponse([rawItem("1", 100, 10)]));
      exchangeCalls++;
      return new Response("Rejected before execution", { status });
    });
    const service = new PlayerService(storage, steam, config);
    const input = { clientEventId: guid, boxType: "dog" };
    await assert.rejects(service.exchange(steamId, "buy", input), { code: "exchange_rejected" });
    assert.equal(await storage.get("active"), undefined);
    await assert.rejects(service.exchange(steamId, "buy", input), { code: "exchange_rejected" });
    assert.equal(exchangeCalls, 1);
    // A fresh intent is permitted once the developer fixes key configuration.
    await assert.rejects(service.exchange(steamId, "buy", { ...input, clientEventId: otherGuid }), { code: "exchange_rejected" });
    assert.equal(exchangeCalls, 2);
  }
});

test("Steam 5xx and malformed successful exchange replies remain ambiguous and cannot be retried", async () => {
  for (const mode of ["server-error", "malformed", "missing-input-proof"]) {
    const storage = new DurableStorage();
    let exchangeCalls = 0;
    const steam = new SteamClient(config, async url => {
      if (url.includes("GetInventory")) return Response.json(steamResponse([rawItem("1", 100, 10)]));
      exchangeCalls++;
      if (mode === "server-error") return new Response("Upstream error", { status: 503 });
      if (mode === "malformed") return Response.json({ response: { success: true } });
      return Response.json(steamResponse([rawItem("2", 1001, 1)]));
    });
    const service = new PlayerService(storage, steam, config);
    const input = { clientEventId: guid, boxType: "dog" };
    assert.equal((await service.exchange(steamId, "buy", input)).status, "pending");
    assert.equal((await service.exchange(steamId, "buy", input)).status, "pending");
    assert.equal(await storage.get("active"), guid);
    assert.equal(exchangeCalls, 1);
  }
});

test("an incomplete or unexpected Steam receipt cannot claim successful consumption or select a reward", () => {
  const before = inventory([item("1", 100, 10)]);
  const intent = exchangeIntent("buy", { boxType: "dog" }, before);
  assert.throws(() => exchangeReceipt(intent, before, [{ ...item("2", 1001, 1), removed: false }]));
  assert.throws(() => exchangeReceipt(intent, before, [{ ...item("wrong-id", 100, 9), removed: false }, { ...item("2", 1001, 1), removed: false }]));
  assert.throws(() => exchangeReceipt(intent, before, [{ ...item("1", 1000, 9), removed: false }, { ...item("2", 1001, 1), removed: false }]));
  assert.throws(() => exchangeReceipt(intent, before, [{ ...item("1", 100, -1), removed: false }, { ...item("2", 1001, 1), removed: false }]));
  assert.throws(() => exchangeReceipt(intent, before, [{ ...item("1", 100, 9), removed: false }, { ...item("2", 3000, 1), removed: false }]));
  assert.throws(() => exchangeReceipt(intent, before, [{ ...item("1", 100, 9), removed: false }, { ...item("2", 1001, 2), removed: false }]));
  const beforeOpen = inventory([item("2", 1001, 1)]);
  const openIntent = exchangeIntent("open", { boxItemId: "2" }, beforeOpen);
  assert.throws(() => exchangeReceipt(openIntent, beforeOpen, [{ ...item("2", 1001, 1), removed: false }, { ...item("3", 3000, 1), removed: false }]));
});

test("Steam timeouts are bounded and mutation forms use exact IDs and supported fields", async () => {
  const calls = [];
  const client = new SteamClient(config, async (url, options) => {
    calls.push({ url, options });
    return Response.json(steamResponse([rawItem("18446744073709551615", 100)]));
  });
  await client.grant(steamId, "18446744073709551615");
  const grantForm = calls[0].options.body;
  assert.equal(grantForm.get("requestid"), "18446744073709551615");
  assert.equal(grantForm.get("itemdefid[0]"), "100");
  await client.exchange(steamId, { outputItemDefId: 1101, materials: [{ itemId: "18446744073709551615", consume: 1 }] });
  const exchangeForm = calls[1].options.body;
  assert.equal(exchangeForm.has("requestid"), false);
  assert.equal(exchangeForm.get("materialsitemid[0]"), "18446744073709551615");
  assert.equal(exchangeForm.get("materialsquantity[0]"), "1");
  assert.equal(exchangeForm.get("outputitemdefid"), "1101");
  const timeout = new SteamClient(config, (url, { signal }) => new Promise((resolve, reject) => {
    signal.addEventListener("abort", () => reject(new Error("timeout")), { once: true });
  }), 5);
  await assert.rejects(timeout.inventory(steamId), { status: 503, extra: {
    steamEndpoint: "IInventoryService/GetInventory", steamFailureCategory: "request_timeout",
  } });
});

test("HTTP routes require authentication, reject malformed requests, and route by verified SteamID", async () => {
  assert.equal((await worker.fetch(new Request("https://example.test/health"), env)).status, 200);
  assert.equal((await worker.fetch(new Request("https://example.test/v1/inventory"), env)).status, 401);
  assert.equal((await worker.fetch(new Request("https://example.test/health"), {})).status, 503);
  const token = await createSession(steamId, config.secret);
  let routed = null;
  const binding = { idFromName: id => { routed = id; return id; }, get: () => ({ fetch: async () => Response.json({ pets: 7, items: [] }) }) };
  const result = await worker.fetch(new Request("https://example.test/v1/inventory", { headers: { Authorization: `Bearer ${token}` } }),
    { ...env, PLAYER_INVENTORY: binding });
  assert.equal(result.status, 200);
  assert.equal(routed, steamId);
});

test("Durable Object queues concurrent requests across external I/O and owner checks survive restart", async () => {
  const storage = new DurableStorage();
  const object = new PlayerInventory({ storage }, env);
  const sequence = [];
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  object.handle = async request => { sequence.push(`start:${request}`); if (request === 1) await gate; sequence.push(`end:${request}`); return request; };
  const first = object.fetch(1);
  const second = object.fetch(2);
  await Promise.resolve();
  assert.deepEqual(sequence, ["start:1"]);
  release();
  assert.deepEqual(await Promise.all([first, second]), [1, 2]);
  assert.deepEqual(sequence, ["start:1", "end:1", "start:2", "end:2"]);
  await storage.put("owner", "76561198000000001");
  const restarted = new PlayerInventory({ storage }, env);
  const token = await createSession(steamId, config.secret);
  const denied = await restarted.fetch(new Request("https://example.test/v1/inventory", { headers: { Authorization: `Bearer ${token}` } }));
  assert.equal(denied.status, 403);
});
