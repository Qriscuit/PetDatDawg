import assert from "node:assert/strict";
import { test } from "node:test";
import { createHash } from "node:crypto";
import { createAccountMigration } from "../src/account-migration.js";

const SOURCE_KEY = "__pdd_account_migration_source_v1";
const TARGET_KEY = "__pdd_account_migration_target_v1";
const PREFIX = "/__pdd_migration/v1/";
const account = "76561198000000000";
const secondAccount = "76561198000000001";
const generation = "account-handoff-unit-test";
const sourceUrl = "https://source.example.test";
const targetUrl = "https://target.example.test";
const sha = value => createHash("sha256").update(value).digest("hex");
const pair = await crypto.subtle.generateKey({ name: "Ed25519" }, true, ["sign", "verify"]);
const publicKey = Buffer.from(await crypto.subtle.exportKey("raw", pair.publicKey)).toString("base64");

class Storage {
  constructor() { this.rows = new Map(); this.failKey = null; }
  async get(key) { return structuredClone(this.rows.get(key)); }
  async put(key, value) {
    if (this.failKey === key) throw new Error("simulated storage write failure");
    this.rows.set(key, structuredClone(value));
  }
  async list({ startAfter, limit = Number.MAX_SAFE_INTEGER } = {}) {
    return new Map([...this.rows].sort(([a], [b]) => Buffer.compare(Buffer.from(a), Buffer.from(b)))
      .filter(([key]) => startAfter === undefined || Buffer.compare(Buffer.from(key), Buffer.from(startAfter)) > 0)
      .slice(0, limit).map(([key, value]) => [key, structuredClone(value)]));
  }
  async transaction(callback) {
    const previous = structuredClone(this.rows);
    try { return await callback(this); } catch (error) { this.rows = previous; throw error; }
  }
}

class BaseInventory {
  constructor(ctx, env) { this.ctx = ctx; this.env = env; this.tail = Promise.resolve(); }
  fetch(request) {
    const result = this.tail.then(() => this.handle(request)); this.tail = result.catch(() => {}); return result;
  }
  async handle(request) {
    this.env.businessCalls++;
    if (request.method === "POST") await this.ctx.storage.put("op:post-import", { state: "complete", afterMigration: true });
    return Response.json({ active: await this.ctx.storage.get("active"), pending: await this.ctx.storage.get("op:unknown-exchange") });
  }
}
const baseWorker = {
  async fetch(request, env) {
    if (new URL(request.url).pathname === "/health") return Response.json({ status: "ok" });
    if (request.headers.get("Authorization") !== "Bearer test-session") return Response.json({ error: "unauthorized" }, { status: 401 });
    return env.PLAYER_INVENTORY.get(env.PLAYER_INVENTORY.idFromName(account)).fetch(request);
  },
};
const migration = createAccountMigration(baseWorker, BaseInventory);

function fixture(role) {
  const stores = new Map(), instances = new Map();
  const env = { PDD_MIGRATION_ROLE: role, PDD_MIGRATION_ID: generation, PDD_MIGRATION_SOURCE_URL: sourceUrl,
    PDD_MIGRATION_TARGET_URL: targetUrl, PDD_MIGRATION_PUBLIC_KEY: publicKey, businessCalls: 0 };
  const namespace = {
    idFromName: name => sha(`${role.startsWith("source") ? "old-namespace" : "new-namespace"}:${name}`),
    idFromString: id => id,
    get(id) {
      if (!stores.has(id)) stores.set(id, new Storage());
      if (!instances.has(id)) instances.set(id, new migration.PlayerInventory({ id: { toString: () => id }, storage: stores.get(id) }, env));
      return instances.get(id);
    },
  };
  env.PLAYER_INVENTORY = namespace;
  return { env, namespace, stores,
    id: name => namespace.idFromName(name),
    storage(id) { namespace.get(id); return stores.get(id); },
    restart(nextRole) { if (nextRole) env.PDD_MIGRATION_ROLE = nextRole; instances.clear(); },
    fetch: request => migration.worker.fetch(request, env, {}),
  };
}

async function signed(path, body, options = {}) {
  const raw = options.raw ?? JSON.stringify(body), timestamp = options.timestamp ?? String(Date.now());
  const canonical = `PDD-MIGRATION-V1\n${options.generation ?? generation}\n${timestamp}\nPOST\n${PREFIX}${path}\n${sha(raw)}`;
  const signature = Buffer.from(await crypto.subtle.sign({ name: "Ed25519" }, pair.privateKey, new TextEncoder().encode(canonical))).toString("base64");
  return new Request(`${options.origin ?? sourceUrl}${PREFIX}${path}`, { method: "POST", headers: {
    "Content-Type": "application/json", "X-PDD-Migration-Time": timestamp,
    "X-PDD-Migration-Signature": options.signature ?? signature,
  }, body: raw });
}
async function send(f, path, body = {}, expected = 200, options) {
  const response = await f.fetch(await signed(path, body, options));
  const result = await response.json(); assert.equal(response.status, expected, JSON.stringify(result)); return result;
}
async function seed(f, count = 0) {
  const id = f.id(account), storage = f.storage(id);
  await storage.put("owner", account);
  await storage.put("active", "unknown-exchange");
  await storage.put("op:unknown-exchange", { id: "unknown-exchange", kind: "purchase", state: "pending",
    before: { items: [{ itemId: "9007199254740993", itemDefId: 100, quantity: 500 }] },
    intent: { materials: [{ itemId: "9007199254740993", consume: 500 }], rewardItemDefIds: [3000] } });
  await storage.put("op:completed", { id: "completed", kind: "purchase", state: "complete", acknowledged: false, receivedItems: [{ itemDefId: 3000 }] });
  await storage.put("unacknowledged", ["completed"]);
  await storage.put("next-dig-at", 1900000000000);
  await storage.put("grant-rate", { minute: 30000000, count: 299 });
  await storage.put("grant-run:existing", 300);
  for (let i = 0; i < count; i++) await storage.put(`op:historical-${String(i).padStart(5, "0")}`, { state: "complete", sequence: i });
  return { id, storage, rows: structuredClone(storage.rows) };
}
async function transfer(source, target, id) {
  const fence = await send(source, "source/fence", { objectId: id });
  let progress = await send(target, "target/begin", { steamId: fence.steamId, sourceObjectId: id });
  let after = progress.lastKey, lastPage;
  for (;;) {
    const exported = await send(source, "source/page", { objectId: id, after });
    lastPage = { steamId: fence.steamId, sourceObjectId: id, index: progress.nextIndex,
      previousHash: progress.chainHash, entries: exported.entries, pageHash: exported.pageHash };
    progress = await send(target, "target/page", lastPage);
    after = exported.cursor;
    if (exported.done) break;
  }
  const finish = { steamId: fence.steamId, sourceObjectId: id, pageCount: progress.nextIndex, entryCount: progress.entryCount, chainHash: progress.chainHash };
  return { progress: await send(target, "target/finish", finish), lastPage, finish };
}

test("admin signatures bind body, path/query, generation and time; requests remain bounded", async () => {
  const f = fixture("source-freeze");
  const request = await signed("status", {});
  assert.equal((await f.fetch(new Request(request, { body: '{"tampered":true}' }))).status, 401);
  await send(f, "status", {}, 401, { generation: "wrong-generation" });
  await send(f, "status", {}, 401, { timestamp: String(Date.now() - 121000) });
  await send(f, "status", {}, 401, { signature: Buffer.alloc(64).toString("base64") });
  const queryRequest = await signed("status?test=1", {});
  assert.equal((await f.fetch(queryRequest)).status, 200);
  const changedQuery = await signed("status?test=1", {});
  assert.equal((await f.fetch(new Request(`${sourceUrl}${PREFIX}status?test=2`, changedQuery))).status, 401);
  await send(f, "status", {}, 413, { raw: JSON.stringify({ data: "x".repeat(1024 * 1024) }) });
  assert.equal(f.env.businessCalls, 0);
});

test("source fencing is immutable across restarts and blocks edge and direct old-worker requests", async () => {
  const f = fixture("source-freeze"), { id } = await seed(f);
  const first = await send(f, "source/fence", { objectId: id });
  f.restart();
  assert.deepEqual(await send(f, "source/fence", { objectId: id }), first);
  const ordinary = () => new Request(`${sourceUrl}/v2/shop`, { headers: { Authorization: "Bearer test-session" } });
  assert.equal((await f.fetch(ordinary())).status, 503);
  assert.equal((await f.namespace.get(id).fetch(ordinary())).status, 503);
  f.env.PDD_MIGRATION_TARGET_URL = "https://other.example.test";
  await send(f, "source/fence", { objectId: id }, 409);
  f.env.PDD_MIGRATION_TARGET_URL = targetUrl; f.env.PDD_MIGRATION_ID = "different-generation";
  await send(f, "source/fence", { objectId: id }, 409, { generation: "different-generation" });
  assert.equal(f.env.businessCalls, 0);
});

test("complete multi-page transfer preserves unknown exchange, receipts, cooldown and every business key", async () => {
  const source = fixture("source-freeze"), target = fixture("target-staging");
  const original = await seed(source, 275);
  const { progress, lastPage, finish } = await transfer(source, target, original.id);
  assert.equal(progress.phase, "complete"); assert.ok(progress.nextIndex >= 3);
  assert.equal(progress.entryCount, original.rows.size);
  const destination = target.storage(target.id(account));
  assert.deepEqual(new Map([...destination.rows].filter(([key]) => key !== TARGET_KEY)), original.rows);
  assert.equal(source.env.businessCalls + target.env.businessCalls, 0, "transfer must not execute inventory or Steam business methods");
  target.restart();
  assert.deepEqual(await send(target, "target/finish", finish), progress);
  await destination.put("op:post-import", { state: "complete", afterMigration: true });
  const replay = await send(target, "target/page", lastPage);
  assert.equal(replay.duplicate, true); assert.equal(replay.phase, "complete");
  assert.deepEqual(await destination.get("op:post-import"), { state: "complete", afterMigration: true });
  const altered = { ...lastPage, entries: [["owner", secondAccount]] }; altered.pageHash = sha(JSON.stringify(altered.entries));
  await send(target, "target/page", altered, 409);
  const ordinary = () => new Request(`${targetUrl}/v2/shop`, { headers: { Authorization: "Bearer test-session" } });
  assert.equal((await target.fetch(ordinary())).status, 503);
  target.restart("target-live");
  const state = await (await target.fetch(ordinary())).json();
  assert.equal(state.active, "unknown-exchange"); assert.equal(state.pending.state, "pending");
  assert.deepEqual(state.pending, original.rows.get("op:unknown-exchange"));
  await send(target, "target/page", lastPage, 409);
});

test("failed page transaction rolls back all entries; restart and duplicate delivery resume without overwrites", async () => {
  const source = fixture("source-freeze"), target = fixture("target-staging");
  const original = await seed(source, 140);
  await send(source, "source/fence", { objectId: original.id });
  const started = await send(target, "target/begin", { steamId: account, sourceObjectId: original.id });
  const exported = await send(source, "source/page", { objectId: original.id, after: null });
  const body = { steamId: account, sourceObjectId: original.id, index: 0, previousHash: started.chainHash, entries: exported.entries, pageHash: exported.pageHash };
  const destination = target.storage(target.id(account)); destination.failKey = exported.entries[3][0];
  await send(target, "target/page", body, 503);
  assert.equal(destination.rows.size, 1); assert.equal((await destination.get(TARGET_KEY)).nextIndex, 0);
  destination.failKey = null; target.restart();
  const after = await send(target, "target/page", body);
  assert.equal(after.nextIndex, 1);
  assert.equal((await send(target, "target/page", body)).duplicate, true);
  const outOfSequence = { ...body, index: 2 }; await send(target, "target/page", outOfSequence, 409);
  target.restart("target-live");
  const blocked = await target.fetch(new Request(`${targetUrl}/v2/shop`, { headers: { Authorization: "Bearer test-session" } }));
  assert.equal(blocked.status, 503); assert.equal(target.env.businessCalls, 0);
});

test("target refuses occupied journals, mismatched owners, bad finish totals and destination changes", async () => {
  const target = fixture("target-staging"), sourceObjectId = "a".repeat(64);
  const destination = target.storage(target.id(account)); await destination.put("owner", account);
  await send(target, "target/begin", { steamId: account, sourceObjectId }, 409);
  const fresh = fixture("target-staging");
  const started = await send(fresh, "target/begin", { steamId: account, sourceObjectId });
  const page = [["owner", secondAccount]];
  await send(fresh, "target/page", { steamId: account, sourceObjectId, index: 0, previousHash: started.chainHash, entries: page, pageHash: sha(JSON.stringify(page)) }, 409);
  await send(fresh, "target/finish", { steamId: account, sourceObjectId, pageCount: 0, entryCount: 0, chainHash: started.chainHash }, 409);
  fresh.env.PDD_MIGRATION_TARGET_URL = "https://changed.example.test";
  await send(fresh, "target/status", { steamId: account }, 409);
  assert.equal(fresh.env.businessCalls + target.env.businessCalls, 0);
});

test("empty ownerless sources export as empty, while ownerless business data cannot silently migrate", async () => {
  const source = fixture("source-freeze"), objectId = "b".repeat(64);
  assert.equal((await send(source, "source/fence", { objectId })).owner, null);
  const empty = await send(source, "source/page", { objectId, after: null });
  assert.equal(empty.done, true); assert.deepEqual(empty.entries, []);
  await source.storage(objectId).put("active", "orphan");
  await send(source, "source/page", { objectId, after: null }, 409);
});

test("source forwarding preserves method, body, path, query, bearer, status and retry headers without admin headers", async () => {
  const source = fixture("source-forward"), previousFetch = globalThis.fetch;
  let seen;
  globalThis.fetch = async request => {
    seen = { url: request.url, method: request.method, body: await request.text(), headers: Object.fromEntries(request.headers), redirect: request.redirect };
    return new Response('{"code":"retry"}', { status: 503, headers: { "Retry-After": "123", "Content-Type": "application/json" } });
  };
  try {
    const response = await source.fetch(new Request(`${sourceUrl}/v1/pets/grant-batch?preserve=yes`, { method: "POST", body: '{"event":"frozen"}', headers: {
      Authorization: "Bearer old-session", "Content-Type": "application/json", "X-PDD-Migration-Time": "strip", "X-PDD-Migration-Signature": "strip",
    } }));
    assert.equal(response.status, 503); assert.equal(response.headers.get("Retry-After"), "123");
    assert.equal(seen.url, `${targetUrl}/v1/pets/grant-batch?preserve=yes`); assert.equal(seen.method, "POST"); assert.equal(seen.body, '{"event":"frozen"}');
    assert.equal(seen.headers.authorization, "Bearer old-session"); assert.equal(seen.redirect, "manual");
    assert.equal(seen.headers["x-pdd-migration-time"], undefined); assert.equal(seen.headers["x-pdd-migration-signature"], undefined);
    assert.equal(source.env.businessCalls, 0);
  } finally { globalThis.fetch = previousFetch; }
});

test("source forwarding refuses redirects without following or exposing their location", async () => {
  const source = fixture("source-forward"), previousFetch = globalThis.fetch;
  let calls = 0;
  globalThis.fetch = async request => {
    calls++; assert.equal(request.redirect, "manual");
    return new Response(null, { status: 302, headers: { Location: "https://other.example.test" } });
  };
  try {
    const response = await source.fetch(new Request(`${sourceUrl}/v1/inventory`, { headers: { Authorization: "Bearer old-session" } }));
    assert.equal(response.status, 503); assert.equal(response.headers.get("Location"), null); assert.equal(calls, 1);
  } finally { globalThis.fetch = previousFetch; }
});

test("old-host health forwards actual destination health after cutover", async () => {
  const source = fixture("source-forward"), previousFetch = globalThis.fetch;
  let seen;
  globalThis.fetch = async request => { seen = request; return Response.json({ status: "ok", appId: 4817200, petsItemDefId: 100 }); };
  try {
    const response = await source.fetch(new Request(`${sourceUrl}/health`));
    assert.deepEqual(await response.json(), { status: "ok", appId: 4817200, petsItemDefId: 100 });
    assert.equal(seen.url, `${targetUrl}/health`); assert.equal(seen.redirect, "manual");
  } finally { globalThis.fetch = previousFetch; }
});

test("health distinguishes staging; fresh players can use the live target and invalid configuration fails closed", async () => {
  const target = fixture("target-staging");
  assert.equal((await (await target.fetch(new Request(`${targetUrl}/health`))).json()).status, "migrating");
  target.restart("target-live");
  assert.equal((await (await target.fetch(new Request(`${targetUrl}/health`))).json()).status, "ok");
  assert.equal((await target.fetch(new Request(`${targetUrl}/v1/inventory`, { headers: { Authorization: "Bearer test-session" } }))).status, 200);
  target.env.PDD_MIGRATION_TARGET_URL = sourceUrl;
  assert.equal((await target.fetch(new Request(`${targetUrl}/v1/inventory`))).status, 503);
});
