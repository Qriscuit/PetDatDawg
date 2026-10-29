// Isolated local development transport. This module is never imported by the
// production Worker and has no network client or publisher credentials.
import economy from "./economy.generated.js";
import { shopCatalog } from "./shop.js";

const DEFAULT_ACCOUNT = "76561198000000000";
export class FakeSteamBackend {
  constructor() { this.accounts = new Map(); this.nextId = 9007199254740993n; }
  account(id = DEFAULT_ACCOUNT) {
    if (!this.accounts.has(id)) this.accounts.set(id, { items: [], requests: new Map() });
    return this.accounts.get(id);
  }
  add(account, def, quantity) {
    let item = def !== economy.ids.digPermit && account.items.find(i => i.itemdefid === def);
    if (!item) account.items.push(item = { appid: economy.appId, itemid: String(this.nextId++), itemdefid: def, quantity: 0, state: "" });
    item.quantity += quantity; return item;
  }
  result(rows, replayed = false) { return Response.json({ response: { success: true, replayed, item_json: JSON.stringify(rows) } }); }
  async fetch(input, options) {
    const request = input instanceof Request ? input : new Request(input, options);
    const url = new URL(request.url);
    if (url.pathname === "/seed") {
      const data = await request.json(); const account = this.account(data.steamId);
      if (!account.requests.has(`seed:${data.requestId}`)) {
        if (data.pets) this.add(account, economy.ids.pets, data.pets);
        if (data.goldenBones) this.add(account, economy.ids.goldenBones, data.goldenBones);
        account.requests.set(`seed:${data.requestId}`, true);
      }
      return Response.json({ testOnly: true });
    }
    if (url.pathname.includes("AuthenticateUserTicket")) return Response.json({ response: { params: { result: "OK", steamid: DEFAULT_ACCOUNT } } });
    if (url.pathname.includes("CheckAppOwnership")) return Response.json({ appownership: { ownsapp: true } });
    const params = request.method === "GET" ? url.searchParams : new URLSearchParams(await request.text());
    const account = this.account(params.get("steamid") || DEFAULT_ACCOUNT);
    if (url.pathname.includes("GetInventory")) return this.result(account.items);
    if (url.pathname.includes("AddItem")) {
      const key = `add:${params.get("requestid")}`;
      if (account.requests.has(key)) return this.result(account.requests.get(key).map(original =>
        account.items.find(item => item.itemid === original.itemid) ?? { ...original, quantity: 0, state: "removed" }), true);
      const affected = [];
      for (let i = 0; params.has(`itemdefid[${i}]`); i++) {
        const item = this.add(account, Number(params.get(`itemdefid[${i}]`)), 1);
        // Real AddItem repeats the final stack snapshot for repeated itemdefs.
        affected.push(item);
      }
      const result = structuredClone(affected);
      account.requests.set(key, result); return this.result(result);
    }
    if (url.pathname.includes("ConsumeItem")) {
      const key = `consume:${params.get("requestid")}`;
      if (account.requests.has(key)) return this.result(account.requests.get(key), true);
      const item = account.items.find(i => i.itemid === params.get("itemid"));
      const quantity = Number(params.get("quantity"));
      if (!item || item.quantity < quantity) return new Response("Rejected", { status: 403 });
      item.quantity -= quantity;
      const result = structuredClone([{ ...item, state: item.quantity ? "" : "removed" }]);
      account.items = account.items.filter(i => i.quantity > 0);
      account.requests.set(key, result); return this.result(result);
    }
    if (url.pathname.includes("ExchangeItem")) {
      const output = Number(params.get("outputitemdefid"));
      const materials = [];
      for (let i = 0; params.has(`materialsitemid[${i}]`); i++) {
        const item = account.items.find(row => row.itemid === params.get(`materialsitemid[${i}]`));
        const quantity = Number(params.get(`materialsquantity[${i}]`));
        if (!item || !Number.isInteger(quantity) || quantity < 1 || item.quantity < quantity || materials.some(m => m.item === item)) return new Response("Rejected", { status: 403 });
        materials.push({ item, quantity });
      }
      const offer = shopCatalog.offers.find(o => o.outputItemDefId === output);
      let recipe, reward, rewardQuantity = 1;
      if (offer) {
        recipe = [[offer.currencyItemDefId, offer.price]];
        if (offer.id === "dig") recipe.push([economy.ids.digPermit, 1]);
        reward = offer.rewardItemDefIds[Math.floor(Math.random() * offer.rewardItemDefIds.length)];
      } else if ([1100, 1101].includes(output)) {
        recipe = [[output === 1100 ? 1000 : 1001, 1]];
        reward = economy.cosmetics.find(c => c.kind === (output === 1100 ? "accessory" : "dog")).itemDefId;
      } else if (output === economy.ids.recycleBundle && materials.length === 1 && economy.cosmetics.some(c => c.itemDefId === materials[0].item.itemdefid)) {
        recipe = [[materials[0].item.itemdefid, 1]]; reward = economy.ids.goldenBones; rewardQuantity = economy.recycleValue;
      }
      if (!recipe || materials.some(m => !recipe.some(([def]) => def === m.item.itemdefid)) || recipe.some(([def, quantity]) => materials.filter(m => m.item.itemdefid === def).reduce((n, m) => n + m.quantity, 0) !== quantity)) return new Response("Rejected", { status: 403 });
      const rows = [];
      for (const material of materials) {
        material.item.quantity -= material.quantity;
        rows.push({ ...material.item, state: material.item.quantity ? "" : "removed" });
      }
      account.items = account.items.filter(i => i.quantity > 0);
      rows.push({ ...this.add(account, reward, rewardQuantity) }); return this.result(rows);
    }
    return new Response("Not found", { status: 404 });
  }
}
const backend = new FakeSteamBackend();
export default { fetch: (request) => backend.fetch(request) };
