import economy from "./economy.generated.js";

export { economy };
export const GOLDEN_BONES = economy.ids.goldenBones;
const cosmeticIds = new Set(economy.cosmetics.map(c => c.itemDefId));
const pools = kind => economy.cosmetics.filter(c => c.kind === kind && !economy.starterPool.includes(c.itemDefId)).map(c => c.itemDefId);
export const shopCatalog = Object.freeze({
  recycleValue: economy.recycleValue, maxRecycleQuantity: 1,
  offers: [
    { id: "dig", name: "Dig Box", kind: "box", currencyItemDefId: economy.ids.pets, price: economy.prices.dig, rewardItemDefIds: economy.starterPool, cooldownSeconds: economy.digCooldownSeconds, outputItemDefId: economy.ids.digGenerator },
    { id: "dog", name: "Premium Dog Box", kind: "box", currencyItemDefId: GOLDEN_BONES, price: economy.prices.dogBox, rewardItemDefIds: pools("dog"), cooldownSeconds: 0, outputItemDefId: economy.ids.dogGenerator },
    { id: "accessory", name: "Premium Accessory Box", kind: "box", currencyItemDefId: GOLDEN_BONES, price: economy.prices.accessoryBox, rewardItemDefIds: pools("accessory"), cooldownSeconds: 0, outputItemDefId: economy.ids.accessoryGenerator },
    ...economy.cosmetics.map(c => ({ id: `${c.kind}:${c.itemDefId}`, name: c.name, kind: c.kind, currencyItemDefId: GOLDEN_BONES, price: economy.prices[c.kind], rewardItemDefIds: [c.itemDefId], cooldownSeconds: 0, outputItemDefId: c.itemDefId })),
  ],
  packs: economy.packs.map(p => ({ ...p, testPriceUsd: p.testPriceUsdCents / 100 })),
});

const quantityOf = (inventory, itemDefId) => inventory.items.filter(i => i.itemDefId === itemDefId).reduce((n, i) => n + i.quantity, 0);
const publicPending = op => ({ clientEventId: op.id, kind: op.kind, offerId: op.input?.offerId, boxItemId: op.input?.boxItemId, phase: op.state });
const publicReceipt = op => ({ clientEventId: op.id, kind: op.kind, offerId: op.input?.offerId,
  receivedItems: op.receivedItems, completedAt: new Date(op.completedAt).toISOString(), isDuplicate: op.isDuplicate ?? false });

export function createShopService(Base, { ApiError, canonicalGuid, steamRequestId }) {
  const invalid = message => new ApiError(400, "invalid_request", message);
  const pendingError = () => new ApiError(503, "steam_unavailable", "Steam has not confirmed the transaction.");
  function materials(inventory, itemDefId, amount) {
    let remaining = amount;
    const result = [];
    for (const row of inventory.items.filter(i => i.itemDefId === itemDefId && i.quantity > 0)) {
      const consume = Math.min(row.quantity, remaining);
      result.push({ ...row, consume }); remaining -= consume;
      if (!remaining) break;
    }
    if (remaining) throw new ApiError(409, itemDefId === economy.ids.pets ? "insufficient_pets" : "insufficient_items", "You do not have enough confirmed items or currency for this action.");
    return result;
  }
  function validateReceipt(intent, before, affected) {
    if (affected === null) return null;
    if (!Array.isArray(affected) || !affected.length) throw pendingError();
    for (const material of intent.materials) {
      const row = affected.find(i => i.itemId === material.itemId);
      if (!row || row.itemDefId !== material.itemDefId || !Number.isSafeInteger(row.quantity) || row.quantity < 0 || row.quantity > 4294967295 || typeof row.removed !== "boolean" || (row.removed && row.quantity)) throw pendingError();
      // Steam validates frozen material identities and quantities atomically.
      // Wallet purchases or Market acquisitions can increase a currency or
      // cosmetic stack between read and receipt. Only the unique, nonstackable
      // permit has an exact expected remainder; snapshot deltas prove nothing.
      if (material.itemDefId === economy.ids.digPermit && row.quantity !== 0) throw pendingError();
    }
    const rewards = affected.filter(row => !intent.materials.some(m => m.itemId === row.itemId));
    if (rewards.length !== 1) throw pendingError();
    const row = rewards[0];
    if (!intent.rewardItemDefIds.includes(row.itemDefId) || row.removed || !Number.isSafeInteger(row.quantity) || row.quantity <= 0 || row.quantity > 4294967295) throw pendingError();
    const previous = before.items.find(i => i.itemId === row.itemId);
    if (previous && previous.itemDefId !== row.itemDefId) throw pendingError();
    // The returned stack can also receive another transaction concurrently.
    // The validated frozen recipe, not a snapshot difference, defines its award.
    return [{ itemId: row.itemId, itemDefId: row.itemDefId, quantity: intent.rewardQuantity }];
  }
  return class ShopPlayerService extends Base {
    async save(operation, active = false) {
      await this.storage.transaction(async txn => {
        await txn.put(`op:${operation.id}`, operation);
        if (active) await txn.put("active", operation.id);
        else if (await txn.get("active") === operation.id) await txn.delete("active");
        if (["applied", "complete"].includes(operation.state) && operation.input?.offerId === "dig") {
          // Persist cooldown and the definitive receipt in one transaction.
          const previous = await txn.get("next-dig-at") ?? 0;
          await txn.put("next-dig-at", Math.max(previous, operation.completedAt + operation.intent.cooldownSeconds * 1000));
        }
        if (operation.state === "complete" && ["purchase", "recycle", "open"].includes(operation.kind) && !operation.acknowledged) {
          const ids = await txn.get("unacknowledged") ?? [];
          if (!ids.includes(operation.id)) await txn.put("unacknowledged", [...ids, operation.id]);
        }
      });
    }

    async finishExchange(steamId, operation) {
      // Existing legacy open transactions also participate in restart reveals.
      if (!operation.completedAt) operation.completedAt = this.now();
      return super.finishExchange(steamId, operation);
    }

    async shopState(steamId, resume = true) {
      let active = await this.active();
      if (resume && active && ["purchase", "recycle"].includes(active.kind)) {
        await this.resumeShop(steamId, active);
        active = await this.active();
      } else if (resume && active?.kind === "open" && active.state === "applied") {
        await this.finishExchange(steamId, active); active = await this.active();
      }
      const inventory = await this.steam.inventory(steamId);
      const next = await this.storage.get("next-dig-at");
      const ids = await this.storage.get("unacknowledged") ?? [];
      const operations = await Promise.all(ids.map(id => this.storage.get(`op:${id}`)));
      return { ...inventory, goldenBones: inventory.goldenBones ?? quantityOf(inventory, GOLDEN_BONES),
        economyVersion: economy.version, serverTime: new Date(this.now()).toISOString(), nextDigAt: next ? new Date(next).toISOString() : null,
        catalog: shopCatalog, capabilities: { shopEnabled: this.config.shopEnabled === true,
          cashPurchasesEnabled: this.config.cashPurchasesEnabled === true, marketEnabled: this.config.marketEnabled === true,
          developerControls: this.config.testEconomy === true },
        ...(active && active.kind !== "grant" ? { pendingOperation: publicPending(active) } : {}),
        receipts: operations.filter(op => op?.state === "complete" && !op.acknowledged).map(publicReceipt) };
    }

    async acknowledge(steamId, input) {
      const id = canonicalGuid(input.clientEventId);
      await this.storage.transaction(async txn => {
        const op = await txn.get(`op:${id}`);
        if (!op || op.state !== "complete" || !["purchase", "recycle", "open"].includes(op.kind)) throw new ApiError(409, "receipt_unavailable", "That reward is not confirmed yet.");
        op.acknowledged = true; await txn.put(`op:${id}`, op);
        const ids = await txn.get("unacknowledged") ?? [];
        await txn.put("unacknowledged", ids.filter(value => value !== id));
      });
      return this.shopState(steamId);
    }

    async shopOperation(steamId, id) {
      id = canonicalGuid(id);
      const state = await this.shopState(steamId);
      const op = await this.storage.get(`op:${id}`);
      if (op && !["purchase", "recycle", "open"].includes(op.kind)) throw new ApiError(409, "operation_conflict", "That ID belongs to a different action.");
      return { ...state, operationId: id, status: !op ? "not_found" : op.state === "complete" ? "complete" : op.state === "rejected" ? "rejected" : "pending",
        ...(op?.receivedItems ? { receivedItems: op.receivedItems } : {}) };
    }

    async purchase(steamId, kind, input) {
      const id = canonicalGuid(input.clientEventId);
      let normalized;
      if (kind === "purchase") {
        if (typeof input.offerId !== "string" || input.offerId.length > 64) throw invalid("Choose a valid Shop offer.");
        normalized = { offerId: input.offerId };
      } else {
        if (!Number.isSafeInteger(input.itemDefId) || !cosmeticIds.has(input.itemDefId) || input.quantity !== 1 || !Number.isSafeInteger(input.equippedQuantity) || input.equippedQuantity < 0 || input.equippedQuantity > 100000) throw invalid("Recycle one owned Steam cosmetic at a time and include its equipped quantity.");
        normalized = { itemDefId: input.itemDefId, quantity: 1, equippedQuantity: input.equippedQuantity };
      }
      let op = await this.storage.get(`op:${id}`);
      if (op && (op.kind !== kind || JSON.stringify(op.input) !== JSON.stringify(normalized))) throw new ApiError(409, "operation_conflict", "This operation ID belongs to a different action.");
      if (!op) {
        if (!this.config.shopEnabled) throw new ApiError(503, "shop_unavailable", "The new Shop is being prepared. Please try again later.");
        if (input.economyVersion !== economy.version) throw new ApiError(409, "update_required", "Refresh the Shop before purchasing.");
        await this.ensureUnblocked(id);
        const before = await this.steam.inventory(steamId);
        let intent;
        if (kind === "purchase") {
          const offer = shopCatalog.offers.find(o => o.id === input.offerId);
          if (!offer) throw invalid("Choose a valid Shop offer.");
          const next = await this.storage.get("next-dig-at") ?? 0;
          if (offer.id === "dig" && this.now() < next) throw new ApiError(409, "dig_cooldown", "Your next Dig Box is not available yet.", { nextDigAt: new Date(next).toISOString() });
          intent = { ...structuredClone(offer), materials: materials(before, offer.currencyItemDefId, offer.price), rewardQuantity: 1, economyVersion: economy.version };
        } else {
          if (quantityOf(before, input.itemDefId) - Math.max(1, input.equippedQuantity) < 1) throw new ApiError(409, "no_spare_copy", "Keep your last copy and copies in your current outfit.");
          intent = { materials: materials(before, input.itemDefId, 1), outputItemDefId: economy.ids.recycleBundle,
            rewardItemDefIds: [GOLDEN_BONES], rewardQuantity: economy.recycleValue, economyVersion: economy.version, cooldownSeconds: 0 };
        }
        op = { id, kind, input: normalized, intent, before, state: normalized.offerId === "dig" ? "permit_pending" : "prepared", createdAt: this.now() };
        if (normalized.offerId === "dig") {
          op.permitRequestId = await this.phaseRequestId(steamId, id, "permit");
          op.cleanupRequestId = await this.phaseRequestId(steamId, id, "cleanup");
        }
        await this.save(op, true);
      }
      await this.resumeShop(steamId, op);
      return this.shopOperation(steamId, id);
    }

    async phaseRequestId(steamId, id, phase) {
      // Stable, independent 64-bit idempotency namespaces without client control.
      const hash = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(`${id}:${phase}`));
      const bytes = [...new Uint8Array(hash).slice(0, 16)].map(b => b.toString(16).padStart(2, "0")).join("");
      const guid = `${bytes.slice(0, 8)}-${bytes.slice(8, 12)}-${bytes.slice(12, 16)}-${bytes.slice(16, 20)}-${bytes.slice(20)}`;
      const result = await steamRequestId(steamId, guid);
      if (result === "0") throw invalid("Generate a new operation ID.");
      return result;
    }

    async resumeShop(steamId, op) {
      if (["complete", "rejected", "pending"].includes(op.state)) return;
      await this.ensureUnblocked(op.id);
      if (op.state === "permit_pending") {
        let rows;
        try { rows = await this.steam.addItem(steamId, economy.ids.digPermit, op.permitRequestId); } catch { return; }
        const permit = rows.find(i => i.itemDefId === economy.ids.digPermit && !i.removed && i.quantity === 1);
        if (!permit || rows.filter(i => i.itemDefId === economy.ids.digPermit).length !== 1) return;
        op.permitItemId = permit.itemId;
        op.intent.materials.push({ itemId: permit.itemId, itemDefId: economy.ids.digPermit, quantity: 1, consume: 1 });
        op.state = "prepared"; await this.save(op, true);
      }
      if (op.state === "prepared") {
        op.state = "pending";
        await this.save(op, true); // Crash after this point is intentionally never retried.
        let receipt;
        try { receipt = validateReceipt(op.intent, op.before, await this.steam.exchange(steamId, op.intent)); }
        catch { return; }
        if (receipt === null) {
          op.state = op.permitItemId ? "cleanup_pending" : "rejected";
          await this.save(op, Boolean(op.permitItemId));
        } else {
          op.receivedItems = receipt; op.state = "applied"; op.completedAt = this.now();
          op.isDuplicate = receipt.some(row => cosmeticIds.has(row.itemDefId) && quantityOf(op.before, row.itemDefId) > 0);
          delete op.before; await this.save(op, true);
        }
      }
      if (op.state === "cleanup_pending") {
        try { await this.steam.consumeItem(steamId, op.permitItemId, 1, op.cleanupRequestId); }
        catch { return; }
        op.state = "rejected"; delete op.before; await this.save(op);
      }
      if (op.state === "applied") {
        try { await this.steam.inventory(steamId); } catch { return; }
        op.state = "complete"; await this.save(op);
      }
    }

    async developerAction(steamId, action, input) {
      if (!this.config.testEconomy) throw new ApiError(404, "not_found", "Endpoint not found.");
      const id = canonicalGuid(input.clientEventId);
      await this.ensureUnblocked(id);
      if (action === "seed" && (!Number.isSafeInteger(input.pets) || input.pets < 0 || input.pets > 100000 || !Number.isSafeInteger(input.goldenBones) || input.goldenBones < 0 || input.goldenBones > 100000)) throw invalid("Test seed amounts must be between 0 and 100000.");
      const previous = await this.storage.get(`dev:${id}`);
      const signature = JSON.stringify({ action, pets: input.pets, goldenBones: input.goldenBones });
      if (previous && previous !== signature) throw new ApiError(409, "operation_conflict", "Test action ID was already used.");
      if (!previous) {
        if (action === "seed") await this.steam.seedTestInventory(steamId, id, input.pets, input.goldenBones);
        else await this.storage.delete("next-dig-at");
        await this.storage.put(`dev:${id}`, signature);
      }
      return this.shopState(steamId);
    }
  };
}
