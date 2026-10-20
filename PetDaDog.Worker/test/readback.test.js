import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync } from "node:fs";
import { compareDefinitions } from "../../scripts/economy-compare.mjs";
const schema = JSON.parse(readFileSync(new URL("../../SteamInventory/itemdefs.json", import.meta.url), "utf8"));
const wire = items => ({ response: { success: true, itemdef_json: JSON.stringify(items) } });

test("publisher readback compares all definitions, pools, prices and icons with Steam scalar types/defaults", () => {
  const items = structuredClone(schema.items);
  for (const row of items) {
    row.appid = "4817200";
    for (const [key, value] of Object.entries(row)) if (["number", "boolean"].includes(typeof value)) row[key] = String(value);
    if (row.auto_stack === "false") delete row.auto_stack;
    row.price = ""; row.price_category = ""; row.promo = "";
  }
  const report = compareDefinitions(schema, wire(items));
  assert.equal(report.passed, true); assert.equal(report.definitionCount, 150);
  assert.deepEqual(report.pools.map(p => p.outcomes), [63, 73, 20, 63, 53]);
  assert.equal(report.matchingCosmeticIcons, 136); assert.deepEqual(report.pricedItemDefIds, []);
});
test("readback fails for missing/duplicate IDs, stale cheap recipes, prices, changed pool weights, wrong app and icons", () => {
  for (const modify of [
    rows => rows.pop(), rows => rows.push(structuredClone(rows[0])),
    rows => { rows.find(r => r.itemdefid === 1001).exchange = "100x1"; },
    rows => { rows.find(r => r.itemdefid === 1200).price = "1;USD99"; },
    rows => { rows.find(r => r.itemdefid === 1110).bundle = "3000x2"; },
    rows => { rows.find(r => r.itemdefid === 3072).icon_url = "https://wrong.test/icon.png"; },
    rows => { rows.find(r => r.itemdefid === 102).promo = "manual"; },
    rows => { rows[0].appid = 480; },
  ]) {
    const rows = structuredClone(schema.items); modify(rows);
    assert.equal(compareDefinitions(schema, wire(rows)).passed, false);
  }
  assert.throws(() => compareDefinitions(schema, { response: { success: false, itemdef_json: "[]" } }));
  assert.throws(() => compareDefinitions(schema, { response: { itemdef_json: "not json" } }));
});
