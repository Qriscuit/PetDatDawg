import assert from "node:assert/strict";
import { test } from "node:test";
import worker, { PlayerInventory, createSession, serviceFailureDiagnostic } from "../src/index.js";

const steamId = "76561198000000000";
const env = { PDD_STEAM_APP_ID: "4817200", PDD_STEAM_PETS_ITEMDEF_ID: "100",
  PDD_STEAM_PUBLISHER_KEY: "test-only-publisher-key", PDD_BACKEND_SESSION_SECRET: "test-only-secret-at-least-32-characters" };
const genericResponse = { error: "The service is temporarily unavailable. Please retry.", code: "service_unavailable" };

test("caught inventory storage errors emit bounded diagnostics while preserving the generic response", async () => {
  const token = await createSession(steamId, env.PDD_BACKEND_SESSION_SECRET);
  const secretText = `${env.PDD_STEAM_PUBLISHER_KEY} ${token} ${steamId} ticket=private-ticket`;
  const error = new TypeError(`Cannot read properties of undefined ${secretText}`);
  error.stack = `TypeError: ${secretText}\n    at PlayerInventory.handle (index.js:1680:23)\n    at https://private.example/${secretText}:17:42`;
  const object = new PlayerInventory({ storage: { get: async () => { throw error; } } }, env);
  const logs = [];
  const originalWarn = console.warn;
  console.warn = record => logs.push(record);
  try {
    const response = await object.fetch(new Request("https://example.test/v1/inventory", { headers: { Authorization: `Bearer ${token}` } }));
    assert.equal(response.status, 503);
    assert.deepEqual(await response.json(), genericResponse);
    assert.equal(response.headers.get("Retry-After"), null);
    assert.deepEqual(logs, [{ event: "service_request_failed", stage: "player_inventory",
      errorName: "TypeError", serviceFailureCategory: "invalid_value_access", sourceLine: 1680, sourceColumn: 23 }]);
    for (const sensitive of [token, steamId, env.PDD_STEAM_PUBLISHER_KEY, "private-ticket", "private.example"]) {
      assert.equal(JSON.stringify(logs).includes(sensitive), false);
    }
  } finally { console.warn = originalWarn; }
});

test("a Durable Object binding rejection is logged at the worker boundary without exposing its message", async () => {
  const token = await createSession(steamId, env.PDD_BACKEND_SESSION_SECRET);
  const logs = [];
  const originalWarn = console.warn;
  console.warn = record => logs.push(record);
  try {
    const error = new Error("Daily free tier row read quota exceeded: private-details");
    error.stack = "Error: private-details\n    at Object.fetch (worker.js:681:20)";
    const bindings = { ...env, PLAYER_INVENTORY: { idFromName: () => "fixture-object",
      get: () => ({ fetch: async () => { throw error; } }) } };
    const response = await worker.fetch(new Request("https://example.test/v1/inventory", { headers: { Authorization: `Bearer ${token}` } }), bindings);
    assert.equal(response.status, 503);
    assert.deepEqual(await response.json(), { error: "Pet syncing is paused because the server reached its daily storage quota. Please retry after 00:00 UTC.", code: "storage_quota_exceeded" });
    const retryAfter = Number(response.headers.get("Retry-After"));
    assert.ok(Number.isInteger(retryAfter) && retryAfter >= 1 && retryAfter <= 86400);
    assert.deepEqual(logs, [{ event: "service_request_failed", stage: "worker", errorName: "Error",
      serviceFailureCategory: "daily_quota", sourceLine: 681, sourceColumn: 20 }]);
    assert.equal(JSON.stringify(logs).includes("private-details"), false);
    logs.length = 0;
    const expired = await worker.fetch(new Request("https://example.test/v1/inventory"), bindings);
    assert.equal(expired.status, 401);
    assert.deepEqual(logs, []); // Expected API errors do not become unexpected service errors.
  } finally { console.warn = originalWarn; }
});

test("unexpected-error diagnostics classify platform failures using only fixed labels", () => {
  for (const [error, expected] of [
    [new Error("SQLITE_FULL: database or disk is full"), "storage_full"],
    [new Error("SQLite row writes exceeded the free plan limit"), "daily_quota"],
    [new Error("Exceeded allowed rows written in Durable Objects free tier"), "daily_quota"],
    [new Error("Exceeded allowed rows read in Durable Objects free tier"), "daily_quota"],
    [new Error("Storage quota exceeded"), "quota_exceeded"],
    [new Error("Durable Object is overloaded"), "overloaded"],
    [new Error("Durable Object reset because code was updated"), "object_reset"],
    [new TypeError("this.storage.get is not a function"), "invalid_function_call"],
    [new TypeError("Cannot convert null to object"), "invalid_value_access"],
    [new TypeError("Invalid receiver"), "type_error"],
    [new ReferenceError("privateIdentifier is not defined"), "undefined_reference"],
    [new Error("unrecognized private failure"), "unexpected_error"],
  ]) {
    error.stack = "private stack data";
    const record = serviceFailureDiagnostic(error, "private-stage");
    assert.deepEqual(record, { event: "service_request_failed", stage: "unknown", errorName: error.name, serviceFailureCategory: expected });
  }
  assert.deepEqual(serviceFailureDiagnostic({ name: "private-error", message: "private-message", stack: "private-stack" }, steamId), {
    event: "service_request_failed", stage: "unknown", errorName: "unknown", serviceFailureCategory: "unexpected_error",
  });
  const hostile = new Proxy({}, { get() { throw new Error("private getter"); } });
  assert.deepEqual(serviceFailureDiagnostic(hostile, "worker"), {
    event: "service_request_failed", stage: "worker", errorName: "unknown", serviceFailureCategory: "unexpected_error",
  });
  const malformed = { name: "Error", stack: "Error\n    at handler (index.js:000:4)\n    at https://private.example/handler.js:124:42" };
  assert.equal(Object.hasOwn(serviceFailureDiagnostic(malformed, "worker"), "sourceLine"), false);
});

test("daily storage quota retry timing follows UTC midnight and other quotas retain generic responses", async () => {
  const originalNow = Date.now;
  const originalWarn = console.warn;
  console.warn = () => {};
  try {
    for (const [now, retrySeconds] of [
      [Date.parse("2026-10-28T12:00:00.000Z"), 43200],
      [Date.parse("2026-10-28T23:59:59.999Z"), 1],
      [Date.parse("2026-10-29T00:00:00.000Z"), 86400],
    ]) {
      Date.now = () => now;
      const token = await createSession(steamId, env.PDD_BACKEND_SESSION_SECRET, now);
      for (const [message, expectedCode] of [
        ["Exceeded allowed rows written in Durable Objects free tier: private-details", "storage_quota_exceeded"],
        ["Storage quota exceeded: private-details", "service_unavailable"],
        ["SQLITE_FULL: private-details", "service_unavailable"],
      ]) {
        const object = new PlayerInventory({ storage: { get: async () => { throw new Error(message); } } }, env);
        const response = await object.fetch(new Request("https://example.test/v1/inventory", { headers: { Authorization: `Bearer ${token}` } }));
        assert.equal(response.status, 503);
        const body = await response.json();
        assert.equal(body.code, expectedCode);
        assert.equal(JSON.stringify(body).includes("private-details"), false);
        assert.equal(response.headers.get("Retry-After"), expectedCode === "storage_quota_exceeded" ? String(retrySeconds) : null);
      }
    }
  } finally { Date.now = originalNow; console.warn = originalWarn; }
});
