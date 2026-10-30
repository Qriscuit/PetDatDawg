// Account handoff verification in real workerd + SQLite. The preserved live
// module is copied byte-for-byte; all Steam calls use one local fake backend.
// The seed/snapshot hooks below exist only in this temporary test entrypoint.
import assert from "node:assert/strict";
import { createHash, webcrypto } from "node:crypto";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { Miniflare, convertV4MiniflareOptions, Response } from "miniflare";

const workerRoot = fileURLToPath(new URL("../", import.meta.url));
const directory = path.resolve(process.argv[2] ?? path.join(workerRoot, ".wrangler/batching-release-20261029/candidate"));
const manifest = JSON.parse(await readFile(path.join(directory, "manifest.json"), "utf8"));
const candidateBytes = await readFile(manifest.candidatePath);
const hash = value => createHash("sha256").update(value).digest("hex");
assert.equal(hash(candidateBytes), "664d37f87605b8da84aff8abf52c86f65e4e41ce93005faa82839083e37cfba8");
assert.equal(hash(candidateBytes), manifest.candidateSha256);
const { createSession } = await import(pathToFileURL(manifest.candidatePath));
const { FakeSteamBackend } = await import(pathToFileURL(path.join(directory, "fixture/fake-steam.js")));
const fake = new FakeSteamBackend();
const generation = "runtime-account-migration-v1";
const sourceUrl = "https://source.example.test", targetUrl = "https://target.example.test";
const secret = "runtime-account-handoff-only-session-secret-never-live";
const account = "76561198000000000", uncertainAccount = "76561198000000001";
const guid = number => `${String(number).padStart(8, "0")}-89ab-cdef-0123-456789abcdef`;
const prefix = "/__pdd_migration/v1/";
const sourceKey = "__pdd_account_migration_source_v1", targetKey = "__pdd_account_migration_target_v1";
const keys = await webcrypto.subtle.generateKey({ name: "Ed25519" }, true, ["sign", "verify"]);
const publicKey = Buffer.from(await webcrypto.subtle.exportKey("raw", keys.publicKey)).toString("base64");
const temporaryRoot = await mkdtemp(path.join(os.tmpdir(), "pdd-account-migration-test-"));
const runtimes = [];
const calls = [];
let unknownExchange = true, targetRuntime;
const checks = [];

for (const id of [account, uncertainAccount]) {
  fake.add(fake.account(id), 100, 10000);
  fake.add(fake.account(id), 101, 10000);
}

// The test subclass exposes journal state through the real object's serialized
// fetch queue. It is never bundled with the production handoff module.
const entrySource = `
import baseWorker, { PlayerInventory as BasePlayerInventory } from "./preserved.js";
import { createAccountMigration } from "./migration.js";
const wrapped = createAccountMigration(baseWorker, BasePlayerInventory);
export class PlayerInventory extends wrapped.PlayerInventory {
  async handle(request) {
    const url = new URL(request.url);
    if (url.pathname === "/__test/seed" && request.method === "POST") {
      const entries = await request.json();
      await this.ctx.storage.transaction(async txn => {
        for (const [key, value] of entries) await txn.put(key, value);
      });
      return Response.json({ seeded: entries.length });
    }
    if (url.pathname === "/__test/snapshot") {
      return Response.json([...await this.ctx.storage.list()]);
    }
    if (this.env.PDD_TEST_BASELINE === "1") return BasePlayerInventory.prototype.handle.call(this, request);
    return super.handle(request);
  }
}
export default {
  fetch(request, env, ctx) {
    return env.PDD_TEST_BASELINE === "1" ? baseWorker.fetch(request, env, ctx) : wrapped.worker.fetch(request, env, ctx);
  }
};
`;
await Promise.all([
  writeFile(path.join(temporaryRoot, "entry.js"), entrySource),
  writeFile(path.join(temporaryRoot, "preserved.js"), candidateBytes),
  writeFile(path.join(temporaryRoot, "migration.js"), await readFile(path.join(workerRoot, "src/account-migration.js"))),
]);

async function steam(request) {
  const body = request.method === "GET" ? undefined : await request.text();
  const url = new URL(request.url);
  const parameters = request.method === "GET" ? url.searchParams : new URLSearchParams(body);
  const id = parameters.get("steamid") ?? account;
  calls.push({ path: url.pathname, account: id });
  const response = await fake.fetch(request.url, {
    method: request.method, headers: Object.fromEntries(request.headers),
    ...(body === undefined ? {} : { body }),
  });
  // Steam applied this exchange, but the caller never gets a definitive
  // receipt. Migration must retain the pending lock and never repeat it.
  if (unknownExchange && id === uncertainAccount && url.pathname.includes("ExchangeItem")) {
    unknownExchange = false;
    throw new Error("Deliberate local fake-Steam exchange uncertainty");
  }
  return new Response(await response.text(), { status: response.status, headers: Object.fromEntries(response.headers) });
}

function options(name, role, baseline = false) {
  return convertV4MiniflareOptions({
    name, compatibilityDate: "2026-10-10", cf: false,
    modules: ["entry.js", "preserved.js", "migration.js"].map(file => ({ type: "ESModule", path: path.join(temporaryRoot, file) })),
    resourcePersistencePath: path.join(temporaryRoot, `${name}-storage`),
    bindings: {
      PDD_TEST_BASELINE: baseline ? "1" : "0",
      PDD_STEAM_APP_ID: "4817200", PDD_STEAM_PETS_ITEMDEF_ID: "100", PDD_GRANTS_PER_MINUTE: "300",
      PDD_SHOP_ENABLED: "1", PDD_LEGACY_PURCHASES_ENABLED: "0", PDD_CASH_PURCHASES_ENABLED: "0", PDD_MARKET_ENABLED: "0",
      PDD_STEAM_PUBLISHER_KEY: "test-only-publisher-key", PDD_BACKEND_SESSION_SECRET: secret,
      PDD_ECONOMY_ENVIRONMENT: "isolated-test", PDD_MIGRATION_ID: generation,
      PDD_MIGRATION_ROLE: role, PDD_MIGRATION_SOURCE_URL: sourceUrl,
      PDD_MIGRATION_TARGET_URL: targetUrl, PDD_MIGRATION_PUBLIC_KEY: publicKey,
    },
    durableObjects: { PLAYER_INVENTORY: { className: "PlayerInventory", useSQLite: true } },
    serviceBindings: { PDD_FAKE_STEAM: steam },
    outboundService: async request => {
      // The only permitted outbound path is the old host forwarding into this
      // process's destination runtime. Everything else is a test failure.
      if (new URL(request.url).origin !== targetUrl || !targetRuntime) throw new Error("Account migration verification must never contact the network");
      if (new URL(request.url).pathname === "/v1/redirect-test") return new Response(null, { status: 302, headers: { Location: "https://offsite.invalid/" } });
      const response = await targetRuntime.dispatchFetch(request.url, {
        method: request.method, headers: Object.fromEntries(request.headers),
        ...(["GET", "HEAD"].includes(request.method) ? {} : { body: await request.arrayBuffer() }),
      });
      return new Response(await response.arrayBuffer(), { status: response.status, headers: Object.fromEntries(response.headers) });
    },
  });
}
async function start(name, role, baseline = false) {
  const runtime = new Miniflare(options(name, role, baseline));
  runtimes.push(runtime); await runtime.ready; return runtime;
}
async function send(runtime, route, body, status = 200, id = account, base = sourceUrl) {
  const response = await runtime.dispatchFetch(new URL(route, base), {
    method: body === undefined ? "GET" : "POST",
    headers: { Authorization: `Bearer ${await createSession(id, secret)}`, "Content-Type": "application/json" },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  const result = await response.json(); assert.equal(response.status, status, `${route}: ${JSON.stringify(result)}`); return result;
}
async function signed(runtime, route, body, status = 200, { tamper = false, timestamp = String(Date.now()), search = "" } = {}) {
  const rawBody = JSON.stringify(body), pathname = `${prefix}${route}${search}`;
  const signedText = `PDD-MIGRATION-V1\n${generation}\n${timestamp}\nPOST\n${pathname}\n${hash(rawBody)}`;
  const signature = Buffer.from(await webcrypto.subtle.sign("Ed25519", keys.privateKey, new TextEncoder().encode(signedText))).toString("base64");
  const response = await runtime.dispatchFetch(new URL(pathname, runtime === targetRuntime ? targetUrl : sourceUrl), {
    method: "POST", headers: { "Content-Type": "application/json", "X-PDD-Migration-Time": timestamp, "X-PDD-Migration-Signature": signature },
    body: tamper ? `${rawBody} ` : rawBody,
  });
  const result = await response.json(); assert.equal(response.status, status, `${route}: ${JSON.stringify(result)}`); return result;
}
async function namespace(runtime) { return runtime.getDurableObjectNamespace("PLAYER_INVENTORY"); }
async function local(runtime, id, route, body) {
  const ns = await namespace(runtime), stub = ns.get(ns.idFromName(id));
  const response = await stub.fetch(`https://internal.test${route}`, {
    method: body === undefined ? "GET" : "POST", ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    headers: { Authorization: `Bearer ${await createSession(id, secret)}` },
  });
  return { status: response.status, result: await response.json() };
}
const business = snapshot => snapshot.filter(([key]) => ![sourceKey, targetKey].includes(key));
const exchanges = id => calls.filter(call => call.account === id && call.path.includes("ExchangeItem")).length;

try {
  const source = await start("migration-source", "source-freeze", true);
  const batch = { clientEventId: guid(1), runId: guid(2), firstSequence: 1, count: 37 };
  assert.equal((await send(source, "/v1/pets/grant-batch", batch)).pets, 10037);
  const initialShop = await send(source, "/v2/shop");
  assert.equal(initialShop.economyVersion, "golden-bones-v1");
  const dig = { clientEventId: guid(3), offerId: "dig", economyVersion: "golden-bones-v1" };
  const reward = await send(source, "/v2/shop/purchase", dig);
  assert.equal(reward.status, "complete"); assert.equal(reward.pets, 9537);
  const pendingPurchase = { clientEventId: guid(4), offerId: "dog:3000", economyVersion: "golden-bones-v1" };
  const pending = await send(source, "/v2/shop/purchase", pendingPurchase, 202, uncertainAccount);
  assert.equal(pending.status, "pending"); assert.equal(exchanges(uncertainAccount), 1);
  const filler = Array.from({ length: 269 }, (_, i) => [`opaque:${String(i).padStart(4, "0")}`, { futureJournal: true, value: i }]);
  assert.equal((await local(source, account, "/__test/seed", filler)).result.seeded, filler.length);
  const sourceSnapshot = (await local(source, account, "/__test/snapshot")).result;
  const pendingSnapshot = (await local(source, uncertainAccount, "/__test/snapshot")).result;
  assert.ok(sourceSnapshot.some(([key]) => key === "grant-rate"));
  assert.ok(sourceSnapshot.some(([key]) => key === `grant-run:${batch.runId}`));
  assert.ok(sourceSnapshot.some(([key]) => key === "next-dig-at"));
  assert.ok(sourceSnapshot.some(([key]) => key === "unacknowledged"));
  assert.equal(pendingSnapshot.find(([key]) => key === "active")[1], pendingPurchase.clientEventId);
  checks.push("real v1 batch, receipt, cooldown and uncertain exchange seeded in SQLite");

  await source.setOptions(options("migration-source", "source-freeze")); await source.ready;
  targetRuntime = await start("migration-target", "target-staging");
  assert.equal((await send(source, "/v1/inventory", undefined, 503)).code, "account_migration");
  assert.equal((await send(targetRuntime, "/v1/inventory", undefined, 503)).code, "account_migration");
  assert.equal((await local(source, account, "/v1/inventory")).status, 503);
  assert.equal((await local(targetRuntime, account, "/v1/inventory")).status, 503);
  await signed(source, "status", {}, 401, { tamper: true });
  await signed(source, "status", {}, 401, { timestamp: String(Date.now() - 180000) });
  await signed(source, "status", {}, 200, { search: "?test=signature-path" });
  checks.push("source and staging edge/object gates plus signed admin authorization");

  const sourceNamespace = await namespace(source);
  const migrations = [
    { id: account, snapshot: sourceSnapshot }, { id: uncertainAccount, snapshot: pendingSnapshot },
  ];
  let exportedPages = 0;
  for (const migration of migrations) {
    const objectId = String(sourceNamespace.idFromName(migration.id));
    const fenced = await signed(source, "source/fence", { objectId });
    assert.equal(fenced.owner, migration.id); assert.equal(fenced.phase, "fenced");
    assert.deepEqual(await signed(source, "source/fence", { objectId }), fenced);
    assert.equal((await local(source, migration.id, "/v1/inventory")).status, 503);
    const common = { steamId: migration.id, sourceObjectId: objectId };
    let state = await signed(targetRuntime, "target/begin", common);
    assert.equal(state.phase, "importing");
    assert.deepEqual(await signed(targetRuntime, "target/begin", common), state);
    let cursor = null;
    const exported = [];
    for (;;) {
      const page = await signed(source, "source/page", { objectId, after: cursor });
      assert.equal(page.pageHash, hash(JSON.stringify(page.entries)));
      exported.push(...page.entries); exportedPages++;
      const payload = { ...common, index: state.nextIndex, previousHash: state.chainHash, pageHash: page.pageHash, entries: page.entries };
      await signed(targetRuntime, "target/page", { ...payload, index: state.nextIndex + 1 }, 409);
      state = await signed(targetRuntime, "target/page", payload);
      const duplicate = await signed(targetRuntime, "target/page", payload);
      assert.equal(duplicate.duplicate, true); assert.equal(duplicate.entryCount, state.entryCount);
      assert.equal(state.chainHash, hash(`${payload.previousHash}\n${payload.pageHash}`));
      if (page.done) break;
      assert.equal(typeof page.cursor, "string"); assert.notEqual(page.cursor, cursor); cursor = page.cursor;
    }
    assert.deepEqual(exported, migration.snapshot);
    await signed(targetRuntime, "target/finish", { ...common, pageCount: state.nextIndex, entryCount: state.entryCount + 1, chainHash: state.chainHash }, 409);
    const finish = { ...common, pageCount: state.nextIndex, entryCount: state.entryCount, chainHash: state.chainHash };
    const finished = await signed(targetRuntime, "target/finish", finish);
    assert.equal(finished.phase, "complete");
    assert.deepEqual(await signed(targetRuntime, "target/finish", finish), finished);
    assert.deepEqual(business((await local(targetRuntime, migration.id, "/__test/snapshot")).result), migration.snapshot);
    assert.equal((await signed(targetRuntime, "target/status", { steamId: migration.id })).phase, "complete");
  }
  assert.ok(exportedPages >= 4);
  checks.push("paginated exact journal transfer, duplicate replay, order and final-digest checks");

  // A partial import remains unavailable even if an operator enables the
  // target role early. That lock also survives a real runtime restart.
  const partialAccount = "76561198000000002";
  const partialSourceId = String(sourceNamespace.idFromName(partialAccount));
  await signed(targetRuntime, "target/begin", { steamId: partialAccount, sourceObjectId: partialSourceId });
  await targetRuntime.setOptions(options("migration-target", "target-live")); await targetRuntime.ready;
  assert.equal((await send(targetRuntime, "/v1/inventory", undefined, 503, partialAccount)).code, "account_migration");
  assert.equal((await local(source, account, "/v1/inventory")).status, 503);
  const replay = await send(targetRuntime, "/v1/pets/grant-batch", batch);
  assert.equal(replay.pets, 9537); assert.equal(replay.replayed, true); assert.equal(replay.granted, 37);
  assert.equal((await send(targetRuntime, "/v1/pets/grant-batch", { ...batch, count: 36 }, 409)).code, "operation_conflict");
  assert.equal((await send(targetRuntime, "/v1/pets/grant-batch", { ...batch, clientEventId: guid(5) }, 409)).code, "sequence_conflict");
  assert.equal((await send(targetRuntime, "/v2/shop/purchase", { ...dig, clientEventId: guid(6) }, 409)).code, "dig_cooldown");
  const after = await send(targetRuntime, "/v2/shop");
  assert.deepEqual(after.catalog, initialShop.catalog);
  assert.equal(after.receipts.length, 1); assert.equal(after.receipts[0].clientEventId, dig.clientEventId);
  const uncertainBefore = exchanges(uncertainAccount);
  const stillPending = await send(targetRuntime, `/v2/shop/operations/${pendingPurchase.clientEventId}`, undefined, 202, uncertainAccount);
  assert.equal(stillPending.status, "pending");
  assert.equal(stillPending.pendingOperation.clientEventId, pendingPurchase.clientEventId);
  await send(targetRuntime, "/v2/shop/purchase", pendingPurchase, 202, uncertainAccount);
  assert.equal(exchanges(uncertainAccount), uncertainBefore, "An uncertain ExchangeItem must never be repeated after migration");
  await signed(targetRuntime, "target/begin", { steamId: account, sourceObjectId: String(sourceNamespace.idFromName(account)) }, 409);
  checks.push("live restart retains exact catalog, grant deduplication, cooldown, receipts and unknown-exchange lock");

  await source.setOptions(options("migration-source", "source-forward")); await source.ready;
  assert.equal((await local(source, account, "/v1/inventory")).status, 503);
  const throughOldHost = await send(source, "/v1/inventory");
  assert.equal(throughOldHost.pets, 9537);
  const throughOldReplay = await send(source, "/v1/pets/grant-batch", batch);
  assert.equal(throughOldReplay.replayed, true); assert.equal(throughOldReplay.pets, 9537);
  const redirectRejected = await source.dispatchFetch(`${sourceUrl}/v1/redirect-test`);
  assert.equal(redirectRejected.status, 503); assert.equal(redirectRejected.headers.has("Location"), false);
  checks.push("old API host forwards to the single new authority while source objects remain fenced");
  console.log(JSON.stringify({ status: "passed", economyVersion: "golden-bones-v1", preservedBundleSha256: hash(candidateBytes),
    migratedAccounts: migrations.length, exportedPages, checks, network: "local fake Steam and in-process destination proxy only" }, null, 2));
} finally {
  await Promise.all(runtimes.map(runtime => runtime.dispose()));
  // Only remove the exact directory returned by mkdtemp under the system temp
  // directory; do not infer or recursively remove any repository path.
  assert.equal(path.dirname(path.resolve(temporaryRoot)), path.resolve(os.tmpdir()));
  assert.ok(path.basename(temporaryRoot).startsWith("pdd-account-migration-test-"));
  await rm(temporaryRoot, { recursive: true, force: true });
}
