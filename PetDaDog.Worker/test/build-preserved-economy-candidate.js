// Build the batching release without publishing a staged economy. This is a
// local-only artifact builder: it does not read credentials or deploy anything.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const workerRoot = fileURLToPath(new URL("../", import.meta.url));
const indexMarker = "// src/index.js\n";
const shopMarker = "// src/shop.js\n";
const economyMarker = "// src/economy.generated.js\n";
export const sha256 = text => createHash("sha256").update(text).digest("hex");

export function inspectBundle(text) {
  // Refuse an unrecognized build rather than guessing at executable boundaries.
  const normalized = text.replaceAll("\r\n", "\n");
  for (const marker of [economyMarker, shopMarker, indexMarker])
    assert.equal(normalized.split(marker).length, 2, `Expected one ${marker.trim()} boundary`);
  const economyStart = normalized.indexOf(economyMarker);
  const shopStart = normalized.indexOf(shopMarker);
  const indexStart = normalized.indexOf(indexMarker);
  assert.ok(economyStart < shopStart && shopStart < indexStart);
  const declaration = normalized.slice(economyStart + economyMarker.length, shopStart).trim();
  assert.ok(declaration.startsWith("var economy_generated_default = ") && declaration.endsWith(";"));
  const economy = JSON.parse(declaration.slice("var economy_generated_default = ".length, -1));
  // Preserve original bytes, including line endings, in the deployed prefix.
  const originalIndexMarker = text.includes("// src/index.js\r\n") ? "// src/index.js\r\n" : indexMarker;
  const originalIndexStart = text.indexOf(originalIndexMarker);
  return {
    prefix: text.slice(0, originalIndexStart),
    core: text.slice(originalIndexStart),
    helpers: normalized.slice(0, economyStart),
    shop: normalized.slice(shopStart, indexStart),
    economy,
  };
}

export async function buildCandidate(livePath, outputDirectory) {
  livePath = path.resolve(livePath);
  outputDirectory = path.resolve(outputDirectory);
  const liveText = await readFile(livePath, "utf8");
  const live = inspectBundle(liveText);
  assert.equal(live.economy.version, "golden-bones-v1", "This release is explicitly restricted to the current v1 economy");
  assert.equal(live.economy.appId, 4817200);
  const built = await build({
    absWorkingDir: workerRoot, entryPoints: ["src/index.js"], bundle: true,
    format: "esm", platform: "browser", target: "es2022", keepNames: true,
    write: false, outfile: "index.js", logLevel: "silent",
  });
  assert.equal(built.outputFiles.length, 1);
  const sourceText = built.outputFiles[0].text;
  const source = inspectBundle(sourceText);
  assert.equal(source.helpers, live.helpers, "Bundler helpers changed; inspect before combining modules");
  assert.equal(source.shop, live.shop, "Shop implementation changed; preserving economy alone would be insufficient");
  assert.match(source.core, /async grantBatch\(steamId, input\)/);
  assert.match(source.core, /maxGrantBatchSize: Math\.min\(MAX_GRANT_BATCH, config\.grantsPerMinute\)/);
  // The legacy reward bounds also live in index.js. Keep that separate economy
  // constant verbatim: the staged source includes four unpublished dog IDs.
  const boxesPattern = /var BOXES = Object\.freeze\(\{[\s\S]*?\n\}\);/g;
  const liveBoxes = [...live.core.matchAll(boxesPattern)];
  const sourceBoxes = [...source.core.matchAll(boxesPattern)];
  assert.equal(liveBoxes.length, 1); assert.equal(sourceBoxes.length, 1);
  const releaseCore = source.core.replace(sourceBoxes[0][0], liveBoxes[0][0]);
  const candidateText = live.prefix + releaseCore;
  const candidate = inspectBundle(candidateText);
  assert.equal(candidate.prefix, live.prefix);
  assert.equal(candidate.core, releaseCore);
  assert.deepEqual(candidate.economy, live.economy);
  assert.equal(candidate.shop, live.shop);
  await mkdir(outputDirectory, { recursive: true });
  const candidatePath = path.join(outputDirectory, "index.js");
  await writeFile(candidatePath, candidateText);
  // Ignored local fixtures allow the fake Steam recipe implementation to use
  // exactly the live economy too. They are never part of the candidate bundle.
  const fixtureDirectory = path.join(outputDirectory, "fixture");
  await mkdir(fixtureDirectory, { recursive: true });
  await writeFile(path.join(fixtureDirectory, "economy.generated.js"), `export default ${JSON.stringify(live.economy, null, 2)};\n`);
  for (const name of ["shop.js", "fake-steam.js"])
    await writeFile(path.join(fixtureDirectory, name), await readFile(path.join(workerRoot, "src", name), "utf8"));
  await writeFile(path.join(outputDirectory, "source-bundle.js"), sourceText);
  await writeFile(path.join(outputDirectory, "live-core.js"), live.core);
  await writeFile(path.join(outputDirectory, "candidate-core.js"), releaseCore);
  const manifest = {
    livePath, candidatePath, economyVersion: live.economy.version,
    cosmeticCount: live.economy.cosmetics.length,
    liveSha256: sha256(liveText), candidateSha256: sha256(candidateText),
    preservedPrefixSha256: sha256(live.prefix), shopSha256: sha256(live.shop),
    economySha256: sha256(JSON.stringify(live.economy)), sourceCoreSha256: sha256(source.core),
    candidateCoreSha256: sha256(releaseCore), preservedLegacyBoxesSha256: sha256(liveBoxes[0][0]),
    sourceIndexSha256: sha256(await readFile(path.join(workerRoot, "src/index.js"))),
    sourceShopSha256: sha256(await readFile(path.join(workerRoot, "src/shop.js"))),
    networkMutations: false,
  };
  await writeFile(path.join(outputDirectory, "manifest.json"), JSON.stringify(manifest, null, 2) + "\n");
  return manifest;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const [livePath, outputDirectory] = process.argv.slice(2);
  assert.ok(livePath && outputDirectory, "Usage: node test/build-preserved-economy-candidate.js <downloaded-live-index.js> <ignored-output-directory>");
  console.log(JSON.stringify(await buildCandidate(livePath, outputDirectory), null, 2));
}
