// Pure comparison shared by the offline CLI and a temporary publisher preview.
// No credentials, network operations, inventory reads, or inventory mutations.
const booleanFields = new Set(["auto_stack", "marketable", "tradable", "hidden", "store_hidden", "use_bundle_price"]);
const sensitiveSchemaFields = ["exchange", "bundle", "price", "price_category", "promo", "drop_start_time", "drop_interval",
  "container_contents_generator", "auto_stack", "marketable", "tradable", "hidden", "store_hidden", "use_bundle_price", "purchase_bundle_discount", "icon_url", "icon_url_large"];
const absent = value => value === undefined || value === null || value === "";
function normalize(property, value) {
  if (booleanFields.has(property)) {
    if (absent(value) || [false, "false", 0, "0"].includes(value)) return false;
    if ([true, "true", 1, "1"].includes(value)) return true;
    return `invalid:${String(value).slice(0, 100)}`;
  }
  if (absent(value)) return "";
  return String(value);
}
function bounded(value) {
  if (value === undefined || value === null) return null;
  if (["string", "number", "boolean"].includes(typeof value)) return typeof value === "string" ? value.slice(0, 4096) : value;
  return "[invalid property type]";
}
export function extractDefinitions(document) {
  let rows;
  if (Array.isArray(document)) rows = document;
  else if (Array.isArray(document?.items)) rows = document.items;
  else {
    const response = document?.response;
    if (!response || response.error || (response.success !== undefined && ![true, "true", 1, "1"].includes(response.success)) || typeof response.itemdef_json !== "string") throw new Error("Publisher definition response is missing or rejected.");
    rows = JSON.parse(response.itemdef_json);
  }
  if (!Array.isArray(rows)) throw new Error("Definitions must be an array.");
  return rows.map(row => typeof row === "string" ? JSON.parse(row) : row);
}
export function compareDefinitions(expected, document) {
  const rows = extractDefinitions(document);
  const actual = new Map(), duplicateIds = [], invalidRows = [];
  for (const [index, row] of rows.entries()) {
    const id = Number(row?.itemdefid);
    if (!row || typeof row !== "object" || !Number.isSafeInteger(id) || id <= 0 || (row.appid !== undefined && Number(row.appid) !== expected.appid)) { invalidRows.push(index); continue; }
    if (actual.has(id)) duplicateIds.push(id);
    actual.set(id, row);
  }
  if (document?.appid !== undefined && Number(document.appid) !== expected.appid) invalidRows.push("wrong_appid");
  const expectedById = new Map(expected.items.map(item => [item.itemdefid, item]));
  const missing = [...expectedById.keys()].filter(id => !actual.has(id));
  const unexpected = [...actual.keys()].filter(id => !expectedById.has(id));
  const differences = [];
  for (const item of expected.items) {
    const live = actual.get(item.itemdefid);
    if (!live) continue;
    // Check missing expected fields AND unexpected live activation of price,
    // cheap recipes, promotions, Market, permit sources, and bundle contents.
    for (const field of new Set([...Object.keys(item), ...sensitiveSchemaFields])) {
      if (normalize(field, item[field]) !== normalize(field, live[field])) differences.push({ itemDefId: item.itemdefid, property: field, expected: bounded(item[field]), actual: bounded(live[field]) });
    }
  }
  const pools = expected.items.filter(item => item.type === "generator").map(item => {
    const live = actual.get(item.itemdefid);
    const weights = typeof live?.bundle === "string" && live.bundle ? live.bundle.split(";") : [];
    return { generator: item.itemdefid, outcomes: weights.length, expectedOutcomes: item.bundle.split(";").length,
      exactMatch: live?.bundle === item.bundle, allEqualWeight: weights.length > 0 && weights.every(entry => /^\d+x1$/.test(entry)), exchange: bounded(live?.exchange) };
  });
  return { appId: expected.appid, passed: rows.length === expected.items.length && !missing.length && !unexpected.length && !duplicateIds.length && !invalidRows.length && !differences.length,
    expectedDefinitionCount: expected.items.length, definitionCount: rows.length, missing, unexpected, duplicateIds, invalidRows, differences, pools,
    pricedItemDefIds: [...actual].filter(([, value]) => !absent(value.price) || !absent(value.price_category)).map(([id]) => id),
    marketableItemDefIds: [...actual].filter(([, value]) => normalize("marketable", value.marketable) === true).map(([id]) => id),
    matchingCosmeticIcons: expected.items.filter(item => ["dog", "accessory"].includes(item.pdd_kind) && actual.get(item.itemdefid)?.icon_url === item.icon_url && actual.get(item.itemdefid)?.icon_url_large === item.icon_url_large).length,
    legacyBoxRecipes: [1000, 1001].map(id => ({ itemDefId: id, exchange: bounded(actual.get(id)?.exchange) })),
    liveInventoryMutations: 0 };
}
