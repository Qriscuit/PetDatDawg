import { readFileSync, writeFileSync, mkdirSync, copyFileSync } from "node:fs";
import { dirname, resolve, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
import { compareDefinitions } from "./economy-compare.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const args = process.argv.slice(2);
const option = name => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
const expectedPath = resolve(option("--expected") || resolve(root, "SteamInventory/itemdefs.json"));
const expectedText = readFileSync(expectedPath, "utf8"), expected = JSON.parse(expectedText);
const hash = createHash("sha256").update(expectedText).digest("hex");

if (args.includes("--prepare-preview")) {
  const directory = resolve(root, "PetDaDog.Worker/.wrangler/economy-readback");
  mkdirSync(directory, { recursive: true });
  writeFileSync(resolve(directory, "expected.json"), expectedText);
  copyFileSync(resolve(root, "scripts/economy-compare.mjs"), resolve(directory, "compare.mjs"));
  writeFileSync(resolve(directory, "wrangler.json"), JSON.stringify({ name: "petdadawg", main: "worker.js", compatibility_date: "2026-10-10", workers_dev: false, preview_urls: false, observability: { enabled: false } }, null, 2) + "\n");
  const source = `import expected from './expected.json';
import { compareDefinitions } from './compare.mjs';
export default {
  async fetch(request, env) {
    if (request.method !== 'GET' || new URL(request.url).pathname !== '/verify') return new Response('Not found', {status:404});
    if (!env.PDD_STEAM_PUBLISHER_KEY) return Response.json({passed:false,reason:'publisher_binding_unavailable'}, {status:503});
    try {
      const url = new URL('https://partner.steam-api.com/IInventoryService/GetItemDefs/v1/');
      url.searchParams.set('input_json', JSON.stringify({appid:4817200,cache_max_age_seconds:0}));
      const response = await fetch(url, {headers:{'x-webapi-key':env.PDD_STEAM_PUBLISHER_KEY},redirect:'manual',signal:AbortSignal.timeout(20000)});
      const result = response.headers.get('x-eresult');
      if (!response.ok || (result !== null && result !== '1') || response.headers.get('x-error_message')) return Response.json({passed:false,reason:'steam_response_rejected',httpStatus:response.status}, {status:502});
      const report = compareDefinitions(expected, await response.json());
      return Response.json({...report,mode:'remote-publisher-item-definitions-only',verifiedUtc:new Date().toISOString(),cacheMaxAgeSeconds:0,httpStatus:response.status,steamResult:result,expectedSha256:'${hash}'}, {headers:{'cache-control':'no-store'}});
    } catch { return Response.json({passed:false,reason:'definition_probe_failed'}, {status:502}); }
  }
};
`;
  writeFileSync(resolve(directory, "worker.js"), source);
  console.log(`Prepared read-only preview for ${expected.items.length} definitions in ${relative(root, directory)}.`);
  console.log("Use wrangler dev --remote --config .wrangler/economy-readback/wrangler.json --port 8791 from PetDaDog.Worker, GET http://127.0.0.1:8791/verify, then stop the preview. Do not deploy this diagnostic script.");
  process.exit(0);
}
if (!option("--input")) {
  console.error("Usage: node scripts/economy-readback.mjs --input fetched-itemdefs.json [--expected SteamInventory/itemdefs.json] [--report report.json]\nOr: node scripts/economy-readback.mjs --prepare-preview");
  process.exit(2);
}
try {
  const inputPath = resolve(option("--input"));
  if (inputPath === expectedPath) throw new Error("Input must be a separately fetched Steam readback, not the expected schema itself.");
  const report = { ...compareDefinitions(expected, JSON.parse(readFileSync(inputPath, "utf8"))), mode: "file-comparison-not-live-publication-proof", comparedUtc: new Date().toISOString(), expectedSha256: hash };
  const text = JSON.stringify(report, null, 2) + "\n";
  if (option("--report")) writeFileSync(resolve(option("--report")), text);
  console.log(text); process.exitCode = report.passed ? 0 : 1;
} catch { console.error("Cannot verify: unreadable, rejected, or malformed definition response. No successful readback was established."); process.exitCode = 2; }
