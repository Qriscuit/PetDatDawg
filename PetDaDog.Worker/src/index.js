// Steam remains the inventory authority. Durable storage contains operation
// receipts, frozen exchange inputs and rate limits, never a writable balance.
const APP_ID = 4817200;
const PETS = 100;
const IDENTITY = "petdadog-backend";
const STEAM = "https://partner.steam-api.com";
const STEAM_ENDPOINT_NAMES = Object.freeze({
  "ISteamUserAuth/AuthenticateUserTicket/v1/": "ISteamUserAuth/AuthenticateUserTicket",
  "ISteamUser/CheckAppOwnership/v4/": "ISteamUser/CheckAppOwnership",
  "IInventoryService/GetInventory/v1/": "IInventoryService/GetInventory",
  "IInventoryService/AddItem/v1/": "IInventoryService/AddItem",
  "IInventoryService/ExchangeItem/v1/": "IInventoryService/ExchangeItem",
});
const STEAM_FAILURE_CATEGORIES = new Set([
  "item_properties_error", "item_definition_error", "inventory_disabled", "permission_denied", "invalid_parameters",
  "steam_rejected", "missing_response", "invalid_success_flag", "missing_item_json", "invalid_item_json",
  "invalid_item_data", "invalid_response_json", "invalid_response_document", "request_timeout", "connection_failed",
  "missing_pets_receipt", "invalid_replayed_flag",
]);
const MAX_UINT64 = 18446744073709551615n;
const BOXES = Object.freeze({
  dog: { itemDefId: 1001, cost: 1, generator: 1101, firstReward: 3000, lastReward: 3044 },
  accessory: { itemDefId: 1000, cost: 2, generator: 1100, firstReward: 2000, lastReward: 2042 },
});
const encoder = new TextEncoder();

export class ApiError extends Error {
  constructor(status, code, message, extra = {}) {
    super(message);
    this.status = status;
    this.code = code;
    this.extra = extra;
  }
}

class SteamHttpError extends Error {
  constructor(status, path) {
    super("Steam HTTP failure.");
    this.httpStatus = status;
    this.endpoint = Object.hasOwn(STEAM_ENDPOINT_NAMES, path) ? STEAM_ENDPOINT_NAMES[path] : "unknown";
  }
}

function json(value, status = 200, headers = {}) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store", ...headers },
  });
}

function errorResponse(error) {
  if (error instanceof SteamHttpError) {
    const denied = [401, 403].includes(error.httpStatus);
    return json({
      error: denied
        ? "Steam denied the backend API request. Check the publisher key's app access, permissions, and IP whitelist."
        : "Steam did not accept the backend request. Please retry.",
      code: denied ? "steam_access_denied" : "steam_unavailable",
      steamEndpoint: error.endpoint,
      steamHttpStatus: error.httpStatus,
    }, 503);
  }
  if (error instanceof ApiError) {
    return json({ error: error.message, code: error.code, ...error.extra }, error.status,
      error.status === 429 ? { "Retry-After": "60" } : {});
  }
  // Do not log raw errors/URLs: Steam authentication URLs contain the ticket.
  return json({ error: "The service is temporarily unavailable. Please retry.", code: "service_unavailable" }, 503);
}

function unavailable() {
  return new ApiError(503, "steam_unavailable", "Steam could not confirm the request. Please retry.");
}

function steamFailure(path, category, response) {
  // Only fixed endpoint/category names and bounded result codes can cross the
  // API boundary. Never expose Steam's error text or returned item properties.
  const extra = { steamEndpoint: Object.hasOwn(STEAM_ENDPOINT_NAMES, path) ? STEAM_ENDPOINT_NAMES[path] : "unknown",
    steamFailureCategory: category };
  for (const value of [response?.eresult, response?.result, response?.errorcode, response?.error?.errorcode]) {
    if ((typeof value === "number" || (typeof value === "string" && /^[0-9]{1,5}$/.test(value)))
      && Number.isInteger(Number(value)) && Number(value) >= 0 && Number(value) <= 65535) {
      extra.steamResult = Number(value);
      break;
    }
  }
  return new ApiError(503, "steam_unavailable", "Steam could not confirm the request. Please retry.", extra);
}

function steamRejectionCategory(response) {
  // These labels describe keywords in a rejection, not proof of its cause.
  const text = typeof response.error === "string" ? response.error.slice(0, 512).toLowerCase() : "";
  if (/itemprops|item propert|dynamic propert/.test(text)) return "item_properties_error";
  if (/itemdef|item definition/.test(text)) return "item_definition_error";
  if (/inventory.*(?:disabled|not enabled)|(?:disabled|not enabled).*inventory/.test(text)) return "inventory_disabled";
  if (/permission|access denied|not permitted/.test(text)) return "permission_denied";
  if (/parameter/.test(text)) return "invalid_parameters";
  return "steam_rejected";
}

function validateSteamHeaders(response, path) {
  // Steam service requests can return HTTP 200 while x-eresult reports failure.
  // Missing headers retain the documented JSON validation below. A present
  // malformed/combined result cannot establish success and must fail closed.
  const result = response.headers.get("x-eresult");
  if (result !== null && (!/^(?:0|[1-9][0-9]{0,4})$/.test(result) || Number(result) > 65535)) {
    throw steamFailure(path, "invalid_success_flag");
  }
  const error = response.headers.get("x-error_message");
  if ((result !== null && result !== "1") || error?.trim()) {
    throw steamFailure(path, steamRejectionCategory({ error }), result === null ? undefined : { eresult: Number(result) });
  }
}

function logSteamFailure(path, error, httpStatus) {
  // One record per failed upstream call. Never log the request, account, raw
  // headers/body, exception text, ticket, key or operation ID.
  const record = { event: "steam_request_failed",
    steamEndpoint: Object.hasOwn(STEAM_ENDPOINT_NAMES, path) ? STEAM_ENDPOINT_NAMES[path] : "unknown" };
  if (Number.isInteger(httpStatus) && httpStatus >= 100 && httpStatus <= 599) record.steamHttpStatus = httpStatus;
  if (error instanceof SteamHttpError) {
    if (Number.isInteger(error.httpStatus) && error.httpStatus >= 100 && error.httpStatus <= 599) {
      record.steamHttpStatus = error.httpStatus;
    }
    record.steamFailureCategory = [401, 403].includes(error.httpStatus) ? "permission_denied" : "steam_rejected";
  } else {
    const category = error.extra?.steamFailureCategory;
    record.steamFailureCategory = STEAM_FAILURE_CATEGORIES.has(category) ? category : "steam_rejected";
    const result = error.extra?.steamResult;
    if (Number.isInteger(result) && result >= 0 && result <= 65535) record.steamResult = result;
  }
  console.warn(record);
}

export function configuration(env) {
  if (String(env.PDD_STEAM_APP_ID) !== String(APP_ID) || String(env.PDD_STEAM_PETS_ITEMDEF_ID) !== String(PETS)
    || typeof env.PDD_STEAM_PUBLISHER_KEY !== "string" || !env.PDD_STEAM_PUBLISHER_KEY.trim()
    || typeof env.PDD_BACKEND_SESSION_SECRET !== "string" || encoder.encode(env.PDD_BACKEND_SESSION_SECRET).length < 32) {
    throw new ApiError(503, "configuration_required", "The backend configuration is incomplete.");
  }
  const grantsPerMinute = Number(env.PDD_GRANTS_PER_MINUTE ?? "300");
  if (!Number.isSafeInteger(grantsPerMinute) || grantsPerMinute < 1 || grantsPerMinute > 3600) throw unavailable();
  return { appId: APP_ID, petsItemDefId: PETS, key: env.PDD_STEAM_PUBLISHER_KEY.trim(),
    secret: env.PDD_BACKEND_SESSION_SECRET, grantsPerMinute };
}

export function canonicalGuid(value) {
  if (typeof value !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
    || value === "00000000-0000-0000-0000-000000000000") {
    throw new ApiError(400, "invalid_request", "clientEventId must be a non-empty GUID.");
  }
  return value.toLowerCase();
}

function uint64(value) {
  // Never accept an unsafe JS number as an item ID or SteamID.
  if (typeof value === "number") {
    if (!Number.isSafeInteger(value)) throw unavailable();
    value = String(value);
  }
  if (typeof value !== "string" || !/^[1-9][0-9]{0,19}$/.test(value) || BigInt(value) > MAX_UINT64) throw unavailable();
  return value;
}

function uint(value, max = 4294967295) {
  if (typeof value === "string" && !/^(0|[1-9][0-9]*)$/.test(value)) throw unavailable();
  if (typeof value !== "string" && typeof value !== "number") throw unavailable();
  const number = Number(value);
  if (!Number.isSafeInteger(number) || number < 0 || number > max) throw unavailable();
  return number;
}

function boolean(value) {
  if ([true, "true", 1, "1"].includes(value)) return true;
  if ([false, "false", 0, "0"].includes(value)) return false;
  throw unavailable();
}

function base64url(bytes) {
  return btoa(String.fromCharCode(...bytes)).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

function unbase64url(text) {
  if (!/^[A-Za-z0-9_-]+$/.test(text)) throw new Error("Invalid base64url.");
  return Uint8Array.from(atob(text.replaceAll("-", "+").replaceAll("_", "/")), c => c.charCodeAt(0));
}

async function hmacKey(secret) {
  return crypto.subtle.importKey("raw", encoder.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign", "verify"]);
}

export async function createSession(steamId, secret, now = Date.now()) {
  const payload = base64url(encoder.encode(JSON.stringify({ steamId: uint64(steamId), appId: APP_ID,
    expiresAtUnixSeconds: Math.floor(now / 1000) + 3600 })));
  const signature = await crypto.subtle.sign("HMAC", await hmacKey(secret), encoder.encode(payload));
  return `${payload}.${base64url(new Uint8Array(signature))}`;
}

export async function validateSession(token, secret, now = Date.now()) {
  try {
    if (typeof token !== "string" || token.length > 2048) return null;
    const parts = token.split(".");
    if (parts.length !== 2 || !await crypto.subtle.verify("HMAC", await hmacKey(secret), unbase64url(parts[1]), encoder.encode(parts[0]))) return null;
    const payload = JSON.parse(new TextDecoder().decode(unbase64url(parts[0])));
    const seconds = Math.floor(now / 1000);
    if (payload.appId !== APP_ID || !Number.isSafeInteger(payload.expiresAtUnixSeconds)
      || payload.expiresAtUnixSeconds <= seconds || payload.expiresAtUnixSeconds > seconds + 3600) return null;
    return uint64(payload.steamId);
  } catch { return null; }
}

export async function steamRequestId(steamId, clientEventId) {
  const hash = await crypto.subtle.digest("SHA-256", encoder.encode(`${uint64(steamId)}:${canonicalGuid(clientEventId)}`));
  // Matches BitConverter.ToUInt64(hash, 0) on the Windows ASP.NET backend.
  return new DataView(hash).getBigUint64(0, true).toString();
}

async function readJson(request) {
  if (!request.headers.get("Content-Type")?.toLowerCase().startsWith("application/json")) {
    throw new ApiError(415, "invalid_request", "Send an application/json request body.");
  }
  const reader = request.body?.getReader();
  if (!reader) throw new ApiError(400, "invalid_request", "A JSON request body is required.");
  let total = 0;
  const chunks = [];
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.length;
    if (total > 32768) {
      await reader.cancel();
      throw new ApiError(413, "invalid_request", "The request body is too large.");
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  try {
    const value = JSON.parse(new TextDecoder().decode(bytes));
    if (value === null || Array.isArray(value) || typeof value !== "object") throw new Error();
    return value;
  } catch { throw new ApiError(400, "invalid_request", "The request body must be a JSON object."); }
}

export function parseItems(response, path = "IInventoryService/GetInventory/v1/") {
  if (!response || typeof response !== "object" || Array.isArray(response)) throw steamFailure(path, "missing_response");
  if (response.error) throw steamFailure(path, steamRejectionCategory(response), response);
  if (response.success !== undefined) {
    let success;
    try { success = boolean(response.success); } catch { throw steamFailure(path, "invalid_success_flag", response); }
    if (!success) throw steamFailure(path, "steam_rejected", response);
  }
  if (typeof response.item_json !== "string") throw steamFailure(path, "missing_item_json", response);
  let rows;
  try { rows = JSON.parse(response.item_json); } catch { throw steamFailure(path, "invalid_item_json", response); }
  if (!Array.isArray(rows)) throw steamFailure(path, "invalid_item_json", response);
  const seen = new Set();
  return rows.map(row => {
    try {
      if (!row || typeof row !== "object" || (row.appid !== undefined && uint(row.appid) !== APP_ID)) throw unavailable();
      const itemId = uint64(row.itemid);
      if (seen.has(itemId)) throw unavailable();
      seen.add(itemId);
      const itemDefId = uint(row.itemdefid);
      if (itemDefId === 0) throw unavailable();
      const state = row.state ?? "";
      if (typeof state !== "string" || !["", "removed", "consumed"].includes(state.toLowerCase())) throw unavailable();
      const removed = state.toLowerCase() === "removed" || state.toLowerCase() === "consumed";
      const quantity = row.quantity === undefined && removed ? 0 : uint(row.quantity);
      return { itemId, itemDefId, quantity: removed ? 0 : quantity, removed };
    } catch { throw steamFailure(path, "invalid_item_data", response); }
  });
}

function inventoryView(rows) {
  const items = rows.filter(row => !row.removed && row.quantity > 0)
    .map(({ itemId, itemDefId, quantity }) => ({ itemId, itemDefId, quantity }));
  const pets = items.filter(row => row.itemDefId === PETS).reduce((total, row) => total + row.quantity, 0);
  if (!Number.isSafeInteger(pets)) throw unavailable();
  return { pets, items };
}

export class SteamClient {
  constructor(config, fetcher = fetch, timeoutMs = 15000) {
    this.config = config;
    this.fetcher = (...args) => fetcher(...args);
    this.timeoutMs = timeoutMs;
  }

  async call(method, path, values) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);
    let httpStatus;
    try {
      const params = new URLSearchParams({ appid: String(APP_ID), ...values });
      const headers = { "x-webapi-key": this.config.key };
      // workerd supports manual/follow; manual prevents sending the key to a
      // redirect destination. Redirect responses fail the status check below.
      const options = { method, headers, signal: controller.signal, redirect: "manual" };
      let url = `${STEAM}/${path}`;
      if (method === "GET") url += `?${params}`;
      else { headers["Content-Type"] = "application/x-www-form-urlencoded"; options.body = params; }
      const response = await this.fetcher(url, options);
      httpStatus = response.status;
      if (!response.ok) throw new SteamHttpError(response.status, path);
      validateSteamHeaders(response, path);
      let document;
      try { document = await response.json(); } catch { throw steamFailure(path, "invalid_response_json"); }
      if (!document || typeof document !== "object" || Array.isArray(document)) throw steamFailure(path, "invalid_response_document");
      return document;
    } catch (error) {
      const failure = error instanceof SteamHttpError || error instanceof ApiError ? error
        : steamFailure(path, controller.signal.aborted ? "request_timeout" : "connection_failed");
      logSteamFailure(path, failure, httpStatus);
      throw failure;
    }
    finally { clearTimeout(timer); }
  }

  async authenticate(ticketHex, identity) {
    if (identity !== IDENTITY || typeof ticketHex !== "string" || !/^(?:[0-9a-f]{2}){1,8192}$/i.test(ticketHex)) {
      throw new ApiError(400, "invalid_request", "A Steam Web API ticket for petdadog-backend is required.");
    }
    const result = await this.call("GET", "ISteamUserAuth/AuthenticateUserTicket/v1/", { ticket: ticketHex, identity });
    const params = result.response?.params;
    if (!params || result.response.error || params.result !== "OK") throw new ApiError(401, "invalid_ticket", "Steam could not authenticate this session.");
    const steamId = uint64(params.steamid);
    const ownership = await this.call("GET", "ISteamUser/CheckAppOwnership/v4/", { steamid: steamId });
    if (!ownership.appownership || !boolean(ownership.appownership.ownsapp)
      || (ownership.appownership.usercanceled !== undefined && boolean(ownership.appownership.usercanceled))) {
      throw new ApiError(403, "app_not_owned", "This Steam account does not have an active Pet Da Dog license.");
    }
    return steamId;
  }

  async inventory(steamId) {
    return inventoryView(parseItems((await this.call("GET", "IInventoryService/GetInventory/v1/", { steamid: steamId })).response));
  }

  async grant(steamId, requestId) {
    const path = "IInventoryService/AddItem/v1/";
    const result = (await this.call("POST", path, {
      steamid: steamId, "itemdefid[0]": String(PETS), itempropsjson: "{}", notify: "0", requestid: requestId,
    })).response;
    const affected = parseItems(result, path);
    // Replayed receipts can describe a Pets stack that has since been spent.
    if (!affected.some(item => item.itemDefId === PETS)) throw steamFailure(path, "missing_pets_receipt", result);
    let replayed;
    try { replayed = result.replayed === undefined ? false : boolean(result.replayed); }
    catch { throw steamFailure(path, "invalid_replayed_flag", result); }
    return { replayed };
  }

  async exchange(steamId, intent) {
    const values = { steamid: steamId, outputitemdefid: String(intent.outputItemDefId) };
    intent.materials.forEach((item, index) => {
      values[`materialsitemid[${index}]`] = item.itemId;
      values[`materialsquantity[${index}]`] = String(item.consume);
    });
    // ExchangeItem has NO documented requestid. Never retry this call blindly.
    try {
      const result = (await this.call("POST", "IInventoryService/ExchangeItem/v1/", values)).response;
      return parseItems(result, "IInventoryService/ExchangeItem/v1/");
    } catch (error) {
      // Steam documents these as access denied / missing method / wrong HTTP
      // method: the mutation never ran. Timeouts, 5xx, 400, 429 and malformed
      // success responses remain ambiguous and must retain the durable lock.
      if (error instanceof SteamHttpError && [401, 403, 404, 405].includes(error.httpStatus)) return [];
      throw error;
    }
  }
}

export function exchangeIntent(kind, input, inventory) {
  if (kind === "buy") {
    const box = BOXES[input.boxType];
    if (!box) throw new ApiError(400, "invalid_request", "boxType must be dog or accessory.");
    if (inventory.pets < box.cost) throw new ApiError(409, "insufficient_pets", "You need more confirmed Pets to buy this box.");
    let remaining = box.cost;
    const materials = [];
    for (const item of inventory.items.filter(item => item.itemDefId === PETS)) {
      const consume = Math.min(remaining, item.quantity);
      materials.push({ ...item, consume });
      remaining -= consume;
      if (remaining === 0) break;
    }
    if (remaining !== 0) throw unavailable();
    return { materials, outputItemDefId: box.itemDefId, firstReward: box.itemDefId, lastReward: box.itemDefId };
  }
  const owned = inventory.items.find(item => item.itemId === input.boxItemId);
  if (!owned) throw new ApiError(404, "box_not_found", "That box is not in your confirmed Steam inventory.");
  const box = Object.values(BOXES).find(box => box.itemDefId === owned.itemDefId);
  if (!box) throw new ApiError(400, "invalid_request", "Only Dog Boxes and Accessories Boxes can be opened.");
  return { materials: [{ ...owned, consume: 1 }], outputItemDefId: box.generator,
    firstReward: box.firstReward, lastReward: box.lastReward };
}

export function exchangeReceipt(intent, before, affected) {
  if (affected.length === 0) return null; // Documented rejection; no inventory changes.
  for (const material of intent.materials) {
    const changed = affected.find(item => item.itemId === material.itemId);
    if (!changed || changed.itemDefId !== material.itemDefId
      || !Number.isSafeInteger(changed.quantity) || changed.quantity < 0 || changed.quantity > 4294967295
      || typeof changed.removed !== "boolean" || (changed.removed && changed.quantity !== 0)) throw unavailable();
    if (material.itemDefId !== PETS && changed.quantity !== material.quantity - material.consume) throw unavailable();
    // The preceding inventory read is not an atomic snapshot of ExchangeItem.
    // A previously timed-out AddItem can finish in between and increase this
    // Pets stack. The definitive exchange receipt identifies the requested material;
    // Steam enforces its recipe and the exact consume quantity we submitted.
    // Comparing the returned balance to snapshot-minus-cost would falsely lock
    // a purchase that Steam successfully completed.
  }
  const received = [];
  for (const row of affected) {
    if (intent.materials.some(item => item.itemId === row.itemId)) continue;
    const previous = before.items.find(item => item.itemId === row.itemId);
    if (previous && previous.itemDefId !== row.itemDefId) throw unavailable();
    const delta = row.quantity - (previous?.quantity ?? 0);
    if (row.removed || row.itemDefId < intent.firstReward || row.itemDefId > intent.lastReward || delta <= 0) throw unavailable();
    received.push({ itemId: row.itemId, itemDefId: row.itemDefId, quantity: delta });
  }
  if (received.reduce((total, row) => total + row.quantity, 0) !== 1) throw unavailable();
  return received;
}

function pendingOperation(operation) {
  const result = { clientEventId: operation.id, kind: operation.kind };
  if (operation.kind === "buy") result.boxType = operation.input.boxType;
  if (operation.kind === "open") result.boxItemId = operation.input.boxItemId;
  return result;
}

// The SQLite-backed DO's transactional KV API is used intentionally: it makes
// durable operation records atomic without moving balances out of Steam.
export class PlayerService {
  constructor(storage, steam, config, now = () => Date.now()) {
    this.storage = storage; this.steam = steam; this.config = config; this.now = now;
  }

  async active() {
    const id = await this.storage.get("active");
    return id ? await this.storage.get(`op:${id}`) : null;
  }

  async save(operation, active = false) {
    await this.storage.transaction(async txn => {
      await txn.put(`op:${operation.id}`, operation);
      if (active) await txn.put("active", operation.id);
      else if (await txn.get("active") === operation.id) await txn.delete("active");
    });
  }

  async readInventory(steamId) {
    const result = await this.steam.inventory(steamId);
    const active = await this.active();
    if (active && active.kind !== "grant") result.pendingOperation = pendingOperation(active);
    return result;
  }

  async ensureUnblocked(id) {
    const active = await this.active();
    if (!active || active.id === id) return;
    if (active.kind === "grant") throw new ApiError(503, "grant_pending", "A previous pet is still being confirmed. Please retry it first.");
    throw new ApiError(409, "exchange_pending", "A previous box action is awaiting Steam confirmation. New actions are paused.", {
      status: "pending", pendingOperation: pendingOperation(active),
    });
  }

  async grant(steamId, input) {
    const id = canonicalGuid(input.clientEventId);
    let operation = await this.storage.get(`op:${id}`);
    if (operation && operation.kind !== "grant") throw new ApiError(409, "operation_conflict", "This operation ID was already used for a different action.");
    if (operation?.state === "complete") return { ...await this.steam.inventory(steamId), granted: 1, replayed: true };
    await this.ensureUnblocked(id);
    if (!operation) {
      operation = { id, kind: "grant", state: "pending", requestId: await steamRequestId(steamId, id), createdAt: this.now() };
      // A zero requestid disables Steam idempotency. Do not send this astronomically unlikely hash.
      if (operation.requestId === "0") throw new ApiError(400, "invalid_request", "Please generate another operation ID.");
      await this.storage.transaction(async txn => {
        const minute = Math.floor(this.now() / 60000);
        const previous = await txn.get("grant-rate");
        const count = previous?.minute === minute ? previous.count : 0;
        if (count >= this.config.grantsPerMinute) throw new ApiError(429, "rate_limited", "Pets are arriving too quickly. Your pending pets will retry shortly.");
        await txn.put("grant-rate", { minute, count: count + 1 });
        await txn.put(`op:${id}`, operation);
      });
    }
    const receipt = await this.steam.grant(steamId, operation.requestId);
    operation.state = "complete";
    await this.save(operation);
    return { ...await this.steam.inventory(steamId), granted: 1, replayed: receipt.replayed };
  }

  async exchange(steamId, kind, input) {
    const id = canonicalGuid(input.clientEventId);
    let intentInput;
    if (kind === "buy") {
      if (!Object.hasOwn(BOXES, input.boxType)) throw new ApiError(400, "invalid_request", "boxType must be dog or accessory.");
      intentInput = { boxType: input.boxType };
    } else {
      try { intentInput = { boxItemId: uint64(input.boxItemId) }; }
      catch { throw new ApiError(400, "invalid_request", "boxItemId must be a Steam inventory item ID string."); }
    }
    let operation = await this.storage.get(`op:${id}`);
    if (operation && (operation.kind !== kind || JSON.stringify(operation.input) !== JSON.stringify(intentInput))) {
      throw new ApiError(409, "operation_conflict", "This operation ID was already used for a different action.");
    }
    if (operation?.state === "rejected") throw new ApiError(409, "exchange_rejected", "Steam rejected this exchange. Refresh your inventory before trying again.");
    if (operation?.state === "complete") return this.completedExchange(steamId, operation);
    await this.ensureUnblocked(id);
    if (operation) {
      if (operation.state === "applied") return this.finishExchange(steamId, operation);
      // A prior attempt may still complete on Steam. Inventory deltas cannot
      // prove which operation caused a change, so never infer success/retry.
      return this.pendingExchange(steamId, operation);
    }
    const before = await this.steam.inventory(steamId);
    const intent = exchangeIntent(kind, intentInput, before);
    operation = { id, kind, input: intentInput, state: "pending", before, intent, createdAt: this.now() };
    await this.save(operation, true); // Must commit BEFORE issuing a non-idempotent call.
    let receipt;
    try {
      const affected = await this.steam.exchange(steamId, intent);
      receipt = exchangeReceipt(intent, before, affected);
    } catch {
      return this.pendingExchange(steamId, operation);
    }
    if (receipt === null) {
      operation.state = "rejected";
      delete operation.before;
      await this.save(operation);
      throw new ApiError(409, "exchange_rejected", "Steam rejected this exchange. Refresh your inventory before trying again.");
    }
    operation.state = "applied";
    operation.receivedItems = receipt;
    // Once a definitive receipt exists, keep the inputs/receipt but discard the
    // large pre-exchange snapshot. Unknown outcomes retain it for investigation.
    delete operation.before;
    await this.save(operation, true);
    return this.finishExchange(steamId, operation);
  }

  async completedExchange(steamId, operation) {
    return { ...await this.steam.inventory(steamId), receivedItems: operation.receivedItems, status: "complete" };
  }

  async finishExchange(steamId, operation) {
    let result;
    try { result = await this.completedExchange(steamId, operation); }
    catch { return this.pendingExchange(steamId, operation); }
    operation.state = "complete";
    // The receipt is retained indefinitely, preventing a duplicate exchange on
    // restart or when a client loses the success response.
    await this.save(operation);
    return result;
  }

  async pendingExchange(steamId, operation) {
    let inventory = {};
    try { inventory = await this.steam.inventory(steamId); } catch { /* Never substitute zero. */ }
    return { ...inventory, status: "pending", operationId: operation.id, code: "exchange_pending",
      pendingOperation: pendingOperation(operation),
      error: operation.state === "applied"
        ? "Steam processed this box action. Waiting for a confirmed inventory refresh."
        : "Steam has not confirmed this box action. New purchases and pet grants are paused; contact support if it does not resolve." };
  }
}

export class PlayerInventory {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    this.tail = Promise.resolve();
  }

  fetch(request) {
    // Per-instance queue covers external I/O, unlike the DO's storage input
    // gates alone. Durable 'active' records preserve the lock after restarts.
    const result = this.tail.then(() => this.handle(request));
    this.tail = result.catch(() => {});
    return result;
  }

  async handle(request) {
    try {
      const config = configuration(this.env);
      const token = request.headers.get("Authorization")?.replace(/^Bearer\s+/i, "");
      const steamId = await validateSession(token, config.secret);
      if (!steamId) throw new ApiError(401, "session_expired", "Please reconnect to Steam.");
      const owner = await this.ctx.storage.get("owner");
      if (owner && owner !== steamId) throw new ApiError(403, "invalid_session", "This session cannot access that inventory.");
      if (!owner) await this.ctx.storage.put("owner", steamId);
      const service = new PlayerService(this.ctx.storage, new SteamClient(config), config);
      const path = new URL(request.url).pathname;
      let result;
      if (request.method === "GET" && ["/v1/pets", "/v1/inventory"].includes(path)) result = await service.readInventory(steamId);
      else if (request.method === "POST" && path === "/v1/pets/grant") result = await service.grant(steamId, await readJson(request));
      else if (request.method === "POST" && path === "/v1/boxes/buy") result = await service.exchange(steamId, "buy", await readJson(request));
      else if (request.method === "POST" && path === "/v1/boxes/open") result = await service.exchange(steamId, "open", await readJson(request));
      else throw new ApiError(404, "not_found", "Endpoint not found.");
      return json(result, result.status === "pending" ? 202 : 200);
    } catch (error) { return errorResponse(error); }
  }
}

export default {
  async fetch(request, env) {
    try {
      const config = configuration(env);
      const path = new URL(request.url).pathname;
      if (request.method === "GET" && path === "/health") return json({ status: "ok", appId: APP_ID, petsItemDefId: PETS });
      if (request.method === "POST" && path === "/v1/auth/steam") {
        const body = await readJson(request);
        const steamId = await new SteamClient(config).authenticate(body.ticketHex, body.identity);
        return json({ steamId, sessionToken: await createSession(steamId, config.secret) });
      }
      const read = request.method === "GET" && ["/v1/pets", "/v1/inventory"].includes(path);
      const write = request.method === "POST" && ["/v1/pets/grant", "/v1/boxes/buy", "/v1/boxes/open"].includes(path);
      if (!read && !write) throw new ApiError(404, "not_found", "Endpoint not found.");
      const authorization = request.headers.get("Authorization");
      if (!authorization || !/^Bearer\s+/i.test(authorization)) throw new ApiError(401, "session_expired", "Please reconnect to Steam.");
      const steamId = await validateSession(authorization.replace(/^Bearer\s+/i, ""), config.secret);
      if (!steamId) throw new ApiError(401, "session_expired", "Please reconnect to Steam.");
      if (!env.PLAYER_INVENTORY) throw unavailable();
      const id = env.PLAYER_INVENTORY.idFromName(steamId);
      return await env.PLAYER_INVENTORY.get(id).fetch(request);
    } catch (error) { return errorResponse(error); }
  },
};
