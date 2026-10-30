// Temporary account handoff around a preserved Worker module. This module
// never authenticates to Steam, reads a publisher key, or changes economy data.
// Keep the source fence in every later source deployment: rolling the source
// back to the original unwrapped module would discard the enforcement layer.
const PREFIX = "/__pdd_migration/v1/";
const SOURCE_KEY = "__pdd_account_migration_source_v1";
const TARGET_KEY = "__pdd_account_migration_target_v1";
const RESERVED = new Set([SOURCE_KEY, TARGET_KEY]);
const ROLES = new Set(["source-freeze", "source-forward", "target-staging", "target-live"]);
const LIMIT = 1024 * 1024;
const PAGE_SIZE = 128;
const encoder = new TextEncoder();

class MigrationError extends Error {
  constructor(status, code, message) { super(message); this.status = status; this.code = code; }
}
const invalid = message => new MigrationError(400, "migration_invalid_request", message);
const conflict = message => new MigrationError(409, "migration_conflict", message);
const unavailable = () => new MigrationError(503, "account_migration", "The server is moving accounts. Please retry shortly.");
function json(value, status = 200, headers = {}) {
  return new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json", "Cache-Control": "no-store", ...headers } });
}
function failure(error) {
  const known = error instanceof MigrationError;
  const status = known ? error.status : 503;
  return json({ code: known ? error.code : "migration_storage_unavailable", error: known ? error.message : "The account handoff could not be confirmed. Please retry shortly." }, status,
    status === 503 ? { "Retry-After": "60" } : {});
}
function origin(value) {
  try {
    const parsed = new URL(value);
    if (parsed.protocol !== "https:" || parsed.username || parsed.password || parsed.search || parsed.hash || parsed.pathname !== "/") throw new Error();
    return parsed.origin;
  } catch { throw new MigrationError(503, "migration_configuration", "The account migration configuration is incomplete."); }
}
function configuration(env) {
  const role = env.PDD_MIGRATION_ROLE, generation = env.PDD_MIGRATION_ID;
  if (!ROLES.has(role) || typeof generation !== "string" || !/^[A-Za-z0-9._:-]{1,128}$/.test(generation)) {
    throw new MigrationError(503, "migration_configuration", "The account migration configuration is incomplete.");
  }
  const sourceUrl = origin(env.PDD_MIGRATION_SOURCE_URL), targetUrl = origin(env.PDD_MIGRATION_TARGET_URL);
  if (sourceUrl === targetUrl || typeof env.PDD_MIGRATION_PUBLIC_KEY !== "string") {
    throw new MigrationError(503, "migration_configuration", "The account migration configuration is incomplete.");
  }
  return { role, generation, sourceUrl, targetUrl, publicKey: env.PDD_MIGRATION_PUBLIC_KEY };
}
function objectId(value) {
  if (typeof value !== "string" || !/^[0-9a-f]{64}$/.test(value)) throw invalid("A canonical source object ID is required.");
  return value;
}
function steamId(value) {
  if (typeof value !== "string" || !/^[1-9][0-9]{0,19}$/.test(value) || BigInt(value) > 18446744073709551615n) throw invalid("A canonical Steam account ID is required.");
  return value;
}
function base64(value, length) {
  if (typeof value !== "string" || !/^[A-Za-z0-9+/]+={0,2}$/.test(value)) throw invalid("Invalid migration signature encoding.");
  let bytes;
  try { bytes = Uint8Array.from(atob(value), character => character.charCodeAt(0)); } catch { throw invalid("Invalid migration signature encoding."); }
  if (bytes.byteLength !== length || btoa(String.fromCharCode(...bytes)) !== value) throw invalid("Invalid migration signature encoding.");
  return bytes;
}
async function hash(value) {
  return [...new Uint8Array(await crypto.subtle.digest("SHA-256", encoder.encode(value)))].map(value => value.toString(16).padStart(2, "0")).join("");
}
function hex(value) { return typeof value === "string" && /^[0-9a-f]{64}$/.test(value); }
function compareKeys(left, right) {
  const a = encoder.encode(left), b = encoder.encode(right);
  for (let i = 0; i < Math.min(a.length, b.length); i++) if (a[i] !== b[i]) return a[i] - b[i];
  return a.length - b.length;
}
async function readBody(request) {
  if (Number(request.headers.get("Content-Length")) > LIMIT) throw new MigrationError(413, "migration_body_too_large", "Migration requests are limited to one MiB.");
  const reader = request.body?.getReader();
  if (!reader) return "";
  let length = 0;
  const chunks = [];
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      length += value.byteLength;
      if (length > LIMIT) {
        // A cloned request is a tee. Awaiting cancellation of one branch can
        // wait forever while the untouched original branch remains readable.
        void reader.cancel().catch(() => {});
        throw new MigrationError(413, "migration_body_too_large", "Migration requests are limited to one MiB.");
      }
      chunks.push(value);
    }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  try { return new TextDecoder("utf-8", { fatal: true }).decode(bytes); } catch { throw invalid("Migration bodies must be UTF-8 JSON."); }
}
async function admin(request, config) {
  if (request.method !== "POST") throw new MigrationError(405, "migration_method", "Use a signed POST request.");
  const timestamp = request.headers.get("X-PDD-Migration-Time");
  if (!timestamp || !/^[0-9]{13}$/.test(timestamp) || Math.abs(Date.now() - Number(timestamp)) > 120000) throw new MigrationError(401, "migration_unauthorized", "A current migration signature is required.");
  let publicKey, signature;
  try {
    publicKey = await crypto.subtle.importKey("raw", base64(config.publicKey, 32), { name: "Ed25519" }, false, ["verify"]);
    signature = base64(request.headers.get("X-PDD-Migration-Signature"), 64);
  } catch { throw new MigrationError(401, "migration_unauthorized", "A valid migration signature is required."); }
  const body = await readBody(request.clone());
  const url = new URL(request.url);
  const signed = `PDD-MIGRATION-V1\n${config.generation}\n${timestamp}\n${request.method}\n${url.pathname}${url.search}\n${await hash(body)}`;
  if (!await crypto.subtle.verify({ name: "Ed25519" }, publicKey, signature, encoder.encode(signed))) throw new MigrationError(401, "migration_unauthorized", "A valid migration signature is required.");
  let parsed;
  try { parsed = JSON.parse(body); } catch { throw invalid("A JSON object is required."); }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw invalid("A JSON object is required.");
  return parsed;
}
function pinned(record, config, sourceObjectId, account) {
  if (!record || record.generation !== config.generation || record.sourceUrl !== config.sourceUrl || record.targetUrl !== config.targetUrl
    || (sourceObjectId !== undefined && record.sourceObjectId !== sourceObjectId) || (account !== undefined && record.steamId !== account)) {
    throw conflict("The journal is pinned to a different migration, account, source object, or destination.");
  }
}
function metadata(record) {
  return { generation: record.generation, sourceUrl: record.sourceUrl, targetUrl: record.targetUrl,
    sourceObjectId: record.sourceObjectId, steamId: record.steamId ?? null, phase: record.phase,
    nextIndex: record.nextIndex ?? 0, entryCount: record.entryCount ?? 0, chainHash: record.chainHash ?? null,
    lastKey: record.lastKey ?? null };
}
function entries(value) {
  if (!Array.isArray(value) || value.length > PAGE_SIZE) throw invalid("A migration page contains at most 128 entries.");
  let previous;
  for (const entry of value) {
    if (!Array.isArray(entry) || entry.length !== 2 || typeof entry[0] !== "string" || !entry[0].length || encoder.encode(entry[0]).byteLength > 2048 || RESERVED.has(entry[0])) throw invalid("Invalid or reserved migration key.");
    if (previous !== undefined && compareKeys(previous, entry[0]) >= 0) throw invalid("Migration keys must be unique and ordered.");
    previous = entry[0];
  }
  return value;
}

export function createAccountMigration(baseWorker, BasePlayerInventory) {
  class PlayerInventory extends BasePlayerInventory {
    async handle(request) {
      try {
        const config = configuration(this.env), path = new URL(request.url).pathname;
        if (path.startsWith(PREFIX)) {
          const body = await admin(request, config);
          if (path === `${PREFIX}source/fence` || path === `${PREFIX}source/page`) {
            if (!config.role.startsWith("source-")) throw conflict("This Worker is not a migration source.");
            const id = objectId(body.objectId);
            if (String(this.ctx.id) !== id) throw conflict("The source object does not match the requested journal.");
            return path.endsWith("/fence") ? await this.fence(config, id) : await this.exportPage(config, id, body);
          }
          if (path.startsWith(`${PREFIX}target/`)) {
            if (!config.role.startsWith("target-")) throw conflict("This Worker is not a migration target.");
            const account = steamId(body.steamId);
            if (String(this.ctx.id) !== String(this.env.PLAYER_INVENTORY.idFromName(account))) throw conflict("The destination object does not match the Steam account.");
            if (path === `${PREFIX}target/status`) {
              const record = await this.ctx.storage.get(TARGET_KEY);
              if (!record) return json({ phase: "empty", steamId: account, generation: config.generation, sourceUrl: config.sourceUrl, targetUrl: config.targetUrl });
              pinned(record, config, undefined, account); return json(metadata(record));
            }
            if (config.role !== "target-staging") throw conflict("Imports are closed while the destination is live.");
            const id = objectId(body.sourceObjectId);
            if (path === `${PREFIX}target/begin`) return await this.begin(config, id, account);
            if (path === `${PREFIX}target/page`) return await this.importPage(config, id, account, body);
            if (path === `${PREFIX}target/finish`) return await this.finish(config, id, account, body);
          }
          throw new MigrationError(404, "migration_not_found", "Migration endpoint not found.");
        }
        // Check inside the object as well as at the edge: an old edge Worker
        // can still route to this namespace during deployment propagation.
        if (config.role !== "target-live" || await this.ctx.storage.get(SOURCE_KEY)) throw unavailable();
        const imported = await this.ctx.storage.get(TARGET_KEY);
        if (imported) { pinned(imported, config); if (imported.phase !== "complete") throw unavailable(); }
        return await super.handle(request);
      } catch (error) { return failure(error); }
    }

    async fence(config, id) {
      const record = await this.ctx.storage.transaction(async txn => {
        const previous = await txn.get(SOURCE_KEY);
        if (previous) { pinned(previous, config, id); return previous; }
        const owner = await txn.get("owner");
        if (owner !== undefined) steamId(owner);
        const next = { generation: config.generation, sourceUrl: config.sourceUrl, targetUrl: config.targetUrl,
          sourceObjectId: id, steamId: owner ?? null, phase: "fenced", createdAt: Date.now() };
        await txn.put(SOURCE_KEY, next); return next;
      });
      return json({ ...metadata(record), owner: record.steamId });
    }

    async exportPage(config, id, body) {
      const record = await this.ctx.storage.get(SOURCE_KEY);
      pinned(record, config, id);
      if (record.phase !== "fenced") throw conflict("The source journal must be fenced before export.");
      if (body.after !== null && body.after !== undefined && (typeof body.after !== "string" || encoder.encode(body.after).byteLength > 2048)) throw invalid("Invalid export cursor.");
      const rows = await this.ctx.storage.list({ ...(body.after ? { startAfter: body.after } : {}), limit: PAGE_SIZE + 1 });
      const page = [];
      let bytes = 0, truncated = false;
      for (const [key, value] of rows) {
        if (RESERVED.has(key)) continue;
        if (page.length === PAGE_SIZE) { truncated = true; break; }
        const size = encoder.encode(JSON.stringify([key, value])).byteLength;
        if (size > LIMIT - 8192) throw conflict("A journal entry is too large for safe transfer.");
        if (page.length && bytes + size > LIMIT - 8192) { truncated = true; break; }
        page.push([key, value]); bytes += size + 1;
      }
      if (!record.steamId && page.length) throw conflict("An ownerless source contains business data and requires investigation.");
      const done = !truncated && rows.size < PAGE_SIZE + 1;
      const lastKey = page.at(-1)?.[0] ?? body.after ?? null;
      return json({ ...metadata(record), owner: record.steamId, entries: page,
        pageHash: await hash(JSON.stringify(page)), lastKey, cursor: done ? null : lastKey, done });
    }

    async begin(config, id, account) {
      const chainHash = await hash(`PDD-MIGRATION-V1\n${config.generation}\n${id}\n${account}`);
      const record = await this.ctx.storage.transaction(async txn => {
        const previous = await txn.get(TARGET_KEY);
        if (previous) { pinned(previous, config, id, account); return previous; }
        if ((await txn.list({ limit: 1 })).size) throw conflict("The destination journal is not empty.");
        const next = { generation: config.generation, sourceUrl: config.sourceUrl, targetUrl: config.targetUrl,
          sourceObjectId: id, steamId: account, phase: "importing", nextIndex: 0, entryCount: 0, chainHash, lastKey: null };
        await txn.put(TARGET_KEY, next); return next;
      });
      return json(metadata(record));
    }

    async importPage(config, id, account, body) {
      const page = entries(body.entries), pageHash = await hash(JSON.stringify(page));
      if (!Number.isSafeInteger(body.index) || body.index < 0 || !hex(body.previousHash) || body.pageHash !== pageHash) throw invalid("Invalid migration page index or digest.");
      const nextHash = await hash(`${body.previousHash}\n${pageHash}`);
      const result = await this.ctx.storage.transaction(async txn => {
        const record = await txn.get(TARGET_KEY); pinned(record, config, id, account);
        if (record.nextIndex === body.index + 1 && record.lastPageHash === pageHash && record.lastPreviousHash === body.previousHash) return { ...metadata(record), duplicate: true };
        if (record.phase !== "importing") throw conflict("The journal import has already completed.");
        if (record.nextIndex !== body.index || record.chainHash !== body.previousHash) throw conflict("The migration page is out of sequence.");
        if (page.length && record.lastKey !== null && compareKeys(record.lastKey, page[0][0]) >= 0) throw conflict("The migration page overlaps previously imported keys.");
        for (const [key, value] of page) {
          if (key === "owner" && value !== account) throw conflict("The source journal owner does not match the destination account.");
          if (await txn.get(key) !== undefined) throw conflict("The migration page would overwrite existing journal data.");
          await txn.put(key, value);
        }
        const next = { ...record, nextIndex: record.nextIndex + 1, entryCount: record.entryCount + page.length,
          chainHash: nextHash, lastKey: page.at(-1)?.[0] ?? record.lastKey, lastPageHash: pageHash, lastPreviousHash: body.previousHash };
        await txn.put(TARGET_KEY, next); return { ...metadata(next), duplicate: false };
      });
      return json(result);
    }

    async finish(config, id, account, body) {
      const record = await this.ctx.storage.transaction(async txn => {
        const previous = await txn.get(TARGET_KEY); pinned(previous, config, id, account);
        if (!Number.isSafeInteger(body.pageCount) || !Number.isSafeInteger(body.entryCount) || body.pageCount !== previous.nextIndex
          || body.entryCount !== previous.entryCount || !hex(body.chainHash) || body.chainHash !== previous.chainHash) throw conflict("The final migration counts or digest do not match.");
        if (await txn.get("owner") !== account) throw conflict("The imported journal has no matching owner.");
        if (previous.phase === "complete") return previous;
        const next = { ...previous, phase: "complete", completedAt: Date.now() };
        await txn.put(TARGET_KEY, next); return next;
      });
      return json(metadata(record));
    }
  }

  const worker = {
    async fetch(request, env, ctx) {
      try {
        const config = configuration(env), url = new URL(request.url), path = url.pathname;
        if (path === "/health" && request.method === "GET" && config.role !== "source-forward") {
          if (config.role === "target-live") return await baseWorker.fetch(request, env, ctx);
          return json({ status: "migrating", migrationRole: config.role, migrationId: config.generation });
        }
        if (path.startsWith(PREFIX)) {
          const body = await admin(request, config);
          if (path === `${PREFIX}status`) return json({ role: config.role, generation: config.generation, sourceUrl: config.sourceUrl, targetUrl: config.targetUrl });
          if (!env.PLAYER_INVENTORY) throw unavailable();
          let id;
          if ([`${PREFIX}source/fence`, `${PREFIX}source/page`].includes(path) && config.role.startsWith("source-")) id = env.PLAYER_INVENTORY.idFromString(objectId(body.objectId));
          else if ([`${PREFIX}target/begin`, `${PREFIX}target/page`, `${PREFIX}target/finish`, `${PREFIX}target/status`].includes(path) && config.role.startsWith("target-")) id = env.PLAYER_INVENTORY.idFromName(steamId(body.steamId));
          else throw new MigrationError(404, "migration_not_found", "Migration endpoint not found.");
          return await env.PLAYER_INVENTORY.get(id).fetch(request);
        }
        if (path.startsWith("/__pdd_migration/")) throw new MigrationError(404, "migration_not_found", "Migration endpoint not found.");
        if (config.role === "source-freeze" || config.role === "target-staging") throw unavailable();
        if (config.role === "source-forward") {
          if (!(path === "/health" && request.method === "GET") && !/^\/v[12](?:\/|$)/.test(path)) return json({ code: "not_found", error: "Endpoint not found." }, 404);
          const destination = new URL(config.targetUrl); destination.pathname = path; destination.search = url.search;
          const headers = new Headers(request.headers);
          for (const key of [...headers.keys()]) if (key.toLowerCase().startsWith("x-pdd-migration-")) headers.delete(key);
          // workerd supports manual/follow, but not redirect:error. Refuse
          // redirects explicitly so bearer tokens never follow another host.
          const forwarded = new Request(destination, new Request(request, { headers, redirect: "manual" }));
          const response = await fetch(forwarded);
          if (response.status >= 300 && response.status < 400) throw unavailable();
          return response;
        }
        return await baseWorker.fetch(request, env, ctx);
      } catch (error) { return failure(error); }
    },
  };
  return { worker, PlayerInventory };
}
