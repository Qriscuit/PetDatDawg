// Runs the real workerd engine and SQLite Durable Object with a fake Steam
// transport. Deliberately separate from the dependency-free unit tests.
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { Miniflare, convertV4MiniflareOptions, Response } from "miniflare";

const steamId = "76561198000000000";
const headers = { "Content-Type": "application/json" };
let grantCalls = 0;
let exchangeCalls = 0;
let pets = 3;
let box = false;
let dog = false;
let rejectInventory = false;
const row = (itemid, itemdefid, quantity, state = "") => ({ itemid, itemdefid, quantity, state, appid: 4817200 });
const asResponse = value => new Response(JSON.stringify(value), { headers });
const inventoryResponse = () => asResponse({ response: { item_json: JSON.stringify([
  row("9007199254740993", 100, pets),
  ...(box ? [row("18446744073709551001", 1001, 1)] : []),
  ...(dog ? [row("18446744073709551002", 3000, 1)] : []),
]) } });

const mf = new Miniflare(convertV4MiniflareOptions({
  name: "petdadog-api",
  modules: true,
  scriptPath: fileURLToPath(new URL("../src/index.js", import.meta.url)),
  compatibilityDate: "2026-10-01",
  cf: false,
  bindings: {
    PDD_STEAM_APP_ID: "4817200", PDD_STEAM_PETS_ITEMDEF_ID: "100", PDD_GRANTS_PER_MINUTE: "300",
    PDD_STEAM_PUBLISHER_KEY: "runtime-test-key", PDD_BACKEND_SESSION_SECRET: "runtime-test-secret-of-more-than-32-bytes",
  },
  durableObjects: { PLAYER_INVENTORY: { className: "PlayerInventory", useSQLite: true } },
  outboundService: async request => {
    const url = new URL(request.url);
    assert.equal(url.host, "partner.steam-api.com");
    assert.equal(request.headers.get("x-webapi-key"), "runtime-test-key");
    if (url.pathname.includes("AuthenticateUserTicket")) return asResponse({ response: { params: { result: "OK", steamid: steamId } } });
    if (url.pathname.includes("CheckAppOwnership")) return asResponse({ appownership: { ownsapp: true } });
    if (url.pathname.includes("GetInventory")) {
      const response = inventoryResponse();
      if (rejectInventory) {
        response.headers.set("x-eresult", "2");
        response.headers.set("x-error_message", "Inventory Service is not enabled for this app.");
      }
      return response;
    }
    const params = new URLSearchParams(await request.text());
    assert.equal(params.get("steamid"), steamId);
    if (url.pathname.includes("AddItem")) {
      grantCalls++;
      pets++;
      return asResponse({ response: { success: true, item_json: JSON.stringify([row("9007199254740993", 100, pets)]) } });
    }
    if (url.pathname.includes("ExchangeItem")) {
      exchangeCalls++;
      if (params.get("outputitemdefid") === "1001") {
        assert.equal(params.get("materialsitemid[0]"), "9007199254740993");
        pets--;
        box = true;
        return asResponse({ response: { item_json: JSON.stringify([row("9007199254740993", 100, pets), row("18446744073709551001", 1001, 1)]) } });
      }
      assert.equal(params.get("outputitemdefid"), "1101");
      assert.equal(params.get("materialsitemid[0]"), "18446744073709551001");
      box = false;
      dog = true;
      return asResponse({ response: { item_json: JSON.stringify([row("18446744073709551001", 1001, 0, "removed"), row("18446744073709551002", 3000, 1)]) } });
    }
    throw new Error(`Unexpected Steam endpoint: ${url.pathname}`);
  },
}));

try {
  await mf.ready;
  const auth = await mf.dispatchFetch("https://example.test/v1/auth/steam", { method: "POST", headers,
    body: JSON.stringify({ ticketHex: "aabbcc", identity: "petdadog-backend" }) });
  assert.equal(auth.status, 200, await auth.clone().text());
  const token = (await auth.json()).sessionToken;
  const send = async (path, body) => {
    const response = await mf.dispatchFetch(`https://example.test${path}`, {
      method: body ? "POST" : "GET", headers: { ...headers, Authorization: `Bearer ${token}` },
      ...(body ? { body: JSON.stringify(body) } : {}),
    });
    const result = await response.json();
    assert.equal(response.status, 200, JSON.stringify(result));
    return result;
  };
  assert.equal((await send("/v1/inventory")).pets, 3);
  const grant = { clientEventId: "01234567-89ab-cdef-0123-456789abcdef" };
  const parallel = await Promise.all([send("/v1/pets/grant", grant), send("/v1/pets/grant", grant)]);
  assert.ok(parallel.every(result => result.pets === 4));
  assert.equal(grantCalls, 1);
  const buy = { clientEventId: "11234567-89ab-cdef-0123-456789abcdef", boxType: "dog" };
  const bought = await send("/v1/boxes/buy", buy);
  assert.equal(bought.status, "complete");
  assert.equal(bought.pets, 3);
  assert.equal(bought.receivedItems[0].itemId, "18446744073709551001");
  const open = { clientEventId: "21234567-89ab-cdef-0123-456789abcdef", boxItemId: bought.receivedItems[0].itemId };
  const opened = await send("/v1/boxes/open", open);
  assert.equal(opened.status, "complete");
  assert.equal(opened.receivedItems[0].itemDefId, 3000);
  await send("/v1/boxes/open", open);
  assert.equal(exchangeCalls, 2);
  rejectInventory = true;
  const failedRead = await mf.dispatchFetch("https://example.test/v1/inventory", {
    headers: { Authorization: `Bearer ${token}` },
  });
  assert.equal(failedRead.status, 503);
  const failure = await failedRead.json();
  assert.equal(Object.hasOwn(failure, "pets"), false);
  assert.equal(failure.steamFailureCategory, "inventory_disabled");
  assert.equal(failure.steamResult, 2);
  console.log("Actual workerd + SQLite DO smoke passed: auth, inventory, concurrent grant dedup, buy, open, exchange dedup, HTTP200 Steam-header failure rejection.");
} finally { await mf.dispose(); }
