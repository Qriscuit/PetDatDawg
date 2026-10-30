// Explicit, resumable account handoff. This utility never calls Steam or billing.
// Private signing material and player journals stay in the ignored .wrangler tree.
import { readFile, writeFile, mkdir, access } from 'node:fs/promises';
import { createHash, generateKeyPairSync, sign, randomUUID } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const directory = path.join(root, '.wrangler/account-migration-20261030');
const sourceAccount = '70452b69328eb240eeb225f7476554e2';
const targetAccount = 'b92850b8277b83eb68aad13a7f8a2d82';
const namespace = '66edfacca3da491b8ab38ddd376557be';
const workerName = 'petdadawg';
const sourceUrl = 'https://petdadawg.americlasagna.workers.dev';
const targetUrl = 'https://petdadawg.petthatdog.workers.dev';
const expectedBundleHash = '664d37f87605b8da84aff8abf52c86f65e4e41ce93005faa82839083e37cfba8';
const candidate = path.join(root, '.wrangler/batching-release-20261029/candidate/index.js');
const hash = value => createHash('sha256').update(value).digest('hex');
const json = file => readFile(file, 'utf8').then(JSON.parse);
const exists = async file => { try { await access(file); return true; } catch { return false; } };
const require = (ok, message) => { if (!ok) throw new Error(message); };
function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  return value;
}
function preservedSettings(settings) {
  const { annotations, ...rest } = settings;
  return { ...rest, bindings: settings.bindings.filter(binding => !binding.name.startsWith('PDD_MIGRATION_')).sort((a, b) => a.name.localeCompare(b.name)) };
}
const save = (file, value) => writeFile(file, JSON.stringify(value, null, 2) + '\n');
async function pin(file, value) {
  const text = JSON.stringify(value, null, 2) + '\n';
  if (await exists(file)) require(await readFile(file, 'utf8') === text, 'An immutable migration snapshot changed. Investigate before continuing.');
  else await writeFile(file, text, { flag: 'wx' });
}
function migrationVars(session, role) {
  return {
    PDD_MIGRATION_ID: session.generation,
    PDD_MIGRATION_ROLE: role,
    PDD_MIGRATION_PUBLIC_KEY: session.publicKey,
    PDD_MIGRATION_SOURCE_URL: sourceUrl,
    PDD_MIGRATION_TARGET_URL: targetUrl,
  };
}
async function sourceApi(suffix, options = {}) {
  const config = await readFile(path.join(root, '.wrangler/config/.wrangler/config/default.toml'), 'utf8');
  const token = /^oauth_token\s*=\s*"([^"]+)"/m.exec(config)?.[1];
  require(token, 'Source Wrangler authorization is unavailable.');
  let response;
  try {
    response = await fetch(`https://api.cloudflare.com/client/v4/accounts/${sourceAccount}${suffix}`, {
      ...options, headers: { Authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(30000), redirect: 'error',
    });
  } catch { throw new Error('Source management request outcome unknown. Inspect state; never blindly repeat a deployment.'); }
  require(response.ok, `Source management request failed: HTTP ${response.status}.`);
  return response;
}
async function sourceJson(suffix) {
  const body = await (await sourceApi(suffix)).json();
  require(body.success, 'Source management response was unsuccessful.');
  return body;
}
async function listObjects() {
  let cursor, objects = [];
  for (let page = 0; page < 1000; page++) {
    const query = new URLSearchParams({ limit: '1000', ...(cursor ? { cursor } : {}) });
    const result = await sourceJson(`/workers/durable_objects/namespaces/${namespace}/objects?${query}`);
    require(Array.isArray(result.result), 'Source object listing was invalid.');
    if (!result.result.length) return objects;
    objects.push(...result.result.filter(object => object.hasStoredData));
    const next = result.result_info?.cursor;
    if (!next) return objects;
    require(next !== cursor, 'Source object listing did not advance.');
    cursor = next;
  }
  throw new Error('Source object listing exceeded its safety bound.');
}
async function modules() {
  const original = await readFile(candidate, 'utf8');
  require(hash(original) === expectedBundleHash, 'Preserved v1 bundle hash changed.');
  const wrapper = await readFile(path.join(root, 'src/account-migration.js'), 'utf8');
  const entry = `import base, { PlayerInventory as BasePlayerInventory } from './index.js';\nimport { createAccountMigration } from './account-migration.js';\nconst migration = createAccountMigration(base, BasePlayerInventory);\nexport const PlayerInventory = migration.PlayerInventory;\nexport default migration.worker;\n`;
  return { 'migration-entry.js': entry, 'index.js': original, 'account-migration.js': wrapper };
}
async function remoteSourceModules() {
  const form = await (await sourceApi(`/workers/scripts/${workerName}/content/v2`)).formData();
  const result = {};
  for (const [name, file] of form) if (typeof file !== 'string') result[name] = await file.text();
  return result;
}
async function admin(base, route, body, session) {
  const url = new URL(`/__pdd_migration/v1/${route}`, base);
  const text = JSON.stringify(body);
  const timestamp = Date.now().toString();
  const message = `PDD-MIGRATION-V1\n${session.generation}\n${timestamp}\nPOST\n${url.pathname}${url.search}\n${hash(text)}`;
  const privateKey = await readFile(path.join(directory, 'signing-private.pem'), 'utf8');
  const signature = sign(null, Buffer.from(message), privateKey).toString('base64');
  let response;
  try {
    response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json',
      'X-PDD-Migration-Time': timestamp, 'X-PDD-Migration-Signature': signature },
      body: text, redirect: 'error', signal: AbortSignal.timeout(30000) });
  } catch { throw new Error('Migration request outcome unknown. Resume using the saved generation and page index.'); }
  let result;
  try { result = await response.json(); } catch { throw new Error(`Migration returned non-JSON HTTP ${response.status}.`); }
  require(response.ok, `Migration ${route} failed: HTTP ${response.status}, code ${/^[a-z_]+$/.test(result.code) ? result.code : 'unknown'}.`);
  return result;
}
async function prepare() {
  await mkdir(directory, { recursive: true });
  const sessionFile = path.join(directory, 'session.json');
  if (await exists(sessionFile)) {
    const session = await json(sessionFile);
    console.log(JSON.stringify({ status: 'prepared', generation: session.generation, publicKey: session.publicKey, sourceUrl, targetUrl, directory }));
    return;
  }
  require(!(await exists(path.join(directory, 'signing-private.pem'))), 'An incomplete signing-key setup exists; preserve and investigate it.');
  require(hash(await readFile(candidate)) === expectedBundleHash, 'Preserved v1 release differs from the pin.');
  const { publicKey, privateKey } = generateKeyPairSync('ed25519');
  const jwk = publicKey.export({ format: 'jwk' });
  const session = { generation: randomUUID(), sourceAccount, targetAccount, namespace, workerName, sourceUrl, targetUrl,
    publicKey: Buffer.from(jwk.x, 'base64url').toString('base64'), expectedBundleHash, createdAt: new Date().toISOString() };
  await writeFile(path.join(directory, 'signing-private.pem'), privateKey.export({ type: 'pkcs8', format: 'pem' }), { flag: 'wx', mode: 0o600 });
  await writeFile(sessionFile, JSON.stringify(session, null, 2) + '\n', { flag: 'wx' });
  console.log(JSON.stringify({ status: 'prepared', generation: session.generation, publicKey: session.publicKey, sourceUrl, targetUrl, directory }));
}
async function snapshot() {
  const [content, settings, deployments, objects] = await Promise.all([
    remoteSourceModules(), sourceJson(`/workers/scripts/${workerName}/settings`),
    sourceJson(`/workers/scripts/${workerName}/deployments`), listObjects(),
  ]);
  require(Object.keys(content).length === 1 && hash(content['index.js']) === expectedBundleHash, 'Source is no longer the exact preserved v1 release.');
  require(settings.result.bindings.some(b => b.name === 'PLAYER_INVENTORY' && b.namespace_id === namespace), 'Source namespace changed.');
  await pin(path.join(directory, 'source-settings.json'), settings.result);
  await pin(path.join(directory, 'source-deployment.json'), deployments.result);
  await pin(path.join(directory, 'source-objects-before.json'), objects);
  console.log(JSON.stringify({ status: 'source_snapshot_saved', objects: objects.length, bundleSha256: expectedBundleHash }));
}
async function sourceDeploy(role, session) {
  require(['source-freeze', 'source-forward'].includes(role), 'Unknown source role.');
  const local = await modules();
  const remote = await remoteSourceModules();
  const settings = (await sourceJson(`/workers/scripts/${workerName}/settings`)).result;
  const originalSettings = await json(path.join(directory, 'source-settings.json'));
  require(JSON.stringify(canonical(preservedSettings(settings))) === JSON.stringify(canonical(preservedSettings(originalSettings))), 'Source settings or economy flags changed outside the migration.');
  require(settings.bindings.some(b => b.name === 'PLAYER_INVENTORY' && b.namespace_id === namespace), 'Source namespace changed.');
  if (role === 'source-freeze') {
    require(Object.keys(remote).length === 1 && hash(remote['index.js']) === expectedBundleHash, 'Freeze requires the original v1 release. Inspect uncertain prior deployments.');
  } else {
    require(Object.keys(local).every(name => remote[name] === local[name]) && Object.keys(remote).length === Object.keys(local).length, 'Source migration modules changed.');
    const report = await json(path.join(directory, 'import-report.json'));
    const objects = await listObjects();
    require(report.complete && JSON.stringify(objects.map(o => o.id).sort()) === JSON.stringify(report.objectIds.slice().sort()), 'Not every source object has a completed transfer.');
    const health = await fetch(`${targetUrl}/health`, { redirect: 'error', signal: AbortSignal.timeout(15000) });
    require(health.ok && (await health.json()).status === 'ok', 'Target is not live and healthy.');
    const auth = await json(path.join(directory, 'auth-inventory-verification.json'));
    require(auth.generation === session.generation && auth.targetUrl === targetUrl && auth.result === 'READ_ONLY_AUTH_INVENTORY_PASS'
      && Date.now() - Date.parse(auth.verifiedAt) < 15 * 60 * 1000 && auth.grants === 0 && auth.purchases === 0,
    'Fresh zero-grant Steam authentication and inventory verification is required before forwarding old clients.');
  }
  const plain = Object.fromEntries(settings.bindings.filter(b => b.type === 'plain_text').map(b => [b.name, b.text]));
  const vars = { ...plain, ...migrationVars(session, role) };
  const metadata = { ...settings, main_module: 'migration-entry.js',
    bindings: Object.entries(vars).map(([name, text]) => ({ type: 'plain_text', name, text })),
    keep_bindings: ['secret_text', 'assets', 'durable_object_namespace'], keep_assets: true,
    annotations: { 'workers/message': `Account migration ${session.generation}: ${role}` } };
  const form = new FormData();
  form.set('metadata', JSON.stringify(metadata));
  for (const [name, text] of Object.entries(local)) form.set(name, new Blob([text], { type: 'application/javascript+module' }), name);
  const intent = path.join(directory, `${role}-intent.json`);
  require(!(await exists(intent)), 'A deployment intent already exists. Verify its result instead of repeating the PUT.');
  await pin(intent, { role, generation: session.generation, startedAt: new Date().toISOString(), modules: Object.fromEntries(Object.entries(local).map(([name, text]) => [name, hash(text)])) });
  const body = await (await sourceApi(`/workers/scripts/${workerName}`, { method: 'PUT', body: form })).json();
  require(body.success, 'Source upload outcome was unsuccessful. Inspect the intent before retrying.');
  const after = await remoteSourceModules();
  require(Object.keys(local).every(name => after[name] === local[name]), 'Source readback differs from migration modules.');
  const updated = (await sourceJson(`/workers/scripts/${workerName}/settings`)).result;
  require(updated.bindings.some(b => b.name === 'PLAYER_INVENTORY' && b.namespace_id === namespace), 'Source namespace was not retained.');
  require(updated.bindings.some(b => b.name === 'PDD_MIGRATION_ROLE' && b.text === role), 'Source role did not persist.');
  await save(path.join(directory, `${role}-result.json`), { role, completedAt: new Date().toISOString(), sourceNamespace: namespace, deploymentId: body.result.deployment_id });
  console.log(JSON.stringify({ status: 'source_deployed_and_verified', role, namespacePreserved: true, billingWrites: 0 }));
}
async function exportState(session) {
  const objects = await listObjects();
  await pin(path.join(directory, 'source-objects-frozen.json'), objects);
  const summaries = [];
  for (const object of objects) {
    const folder = path.join(directory, 'journals', object.id);
    await mkdir(folder, { recursive: true });
    const fence = await admin(sourceUrl, 'source/fence', { objectId: object.id }, session);
    await pin(path.join(folder, 'fence.json'), fence);
    let after = null, page = 0, entries = 0, lastKey;
    do {
      const body = await admin(sourceUrl, 'source/page', { objectId: object.id, after }, session);
      require(Array.isArray(body.entries) && body.entries.length <= 128, 'Invalid source page.');
      require(body.steamId === fence.steamId, 'Source owner changed during export.');
      for (const [key] of body.entries) { require(typeof key === 'string' && (!lastKey || key > lastKey), 'Source keys did not increase.'); lastKey = key; }
      await pin(path.join(folder, `page-${String(page).padStart(6, '0')}.json`), body);
      entries += body.entries.length; page++;
      if (body.done) break;
      require(body.cursor && body.cursor !== after, 'Source export cursor did not advance.');
      after = body.cursor;
    } while (page < 100000);
    require(page < 100000, 'Source export exceeded its page bound.');
    require(fence.steamId || entries === 0, 'Ownerless source has business state.');
    const summary = { objectId: object.id, steamId: fence.steamId, pageCount: page, entryCount: entries };
    await pin(path.join(folder, 'summary.json'), summary);
    summaries.push(summary);
    console.log(JSON.stringify({ status: 'player_journal_exported', player: summaries.length, pageCount: page, entryCount: entries }));
  }
  const afterObjects = await listObjects();
  require(JSON.stringify(afterObjects) === JSON.stringify(objects), 'Source object list changed while exporting.');
  await pin(path.join(directory, 'export-report.json'), { generation: session.generation, objectIds: objects.map(o => o.id), players: summaries,
    totalEntries: summaries.reduce((sum, p) => sum + p.entryCount, 0), totalPages: summaries.reduce((sum, p) => sum + p.pageCount, 0), complete: true });
  console.log(JSON.stringify({ status: 'all_journals_exported', objects: summaries.length, entries: summaries.reduce((sum, p) => sum + p.entryCount, 0) }));
}
async function importState(session) {
  const report = await json(path.join(directory, 'export-report.json'));
  require(report.complete && report.generation === session.generation, 'Export is incomplete or belongs to another generation.');
  require(report.totalEntries + report.totalPages * 3 < 90000, 'Import may exhaust the destination daily storage allowance. Stop and choose a capacity plan before writes.');
  let players = 0;
  for (const player of report.players) {
    if (!player.steamId) { require(player.entryCount === 0, 'Cannot skip nonempty ownerless journal.'); continue; }
    const identity = { steamId: player.steamId, sourceObjectId: player.objectId };
    let status = await admin(targetUrl, 'target/begin', identity, session);
    let chainHash = hash(`PDD-MIGRATION-V1\n${session.generation}\n${player.objectId}\n${player.steamId}`);
    let count = 0;
    for (let index = 0; index < player.pageCount; index++) {
      const source = await json(path.join(directory, 'journals', player.objectId, `page-${String(index).padStart(6, '0')}.json`));
      const nextHash = hash(`${chainHash}\n${source.pageHash}`);
      if (status.phase !== 'complete' && index >= status.nextIndex) {
        status = await admin(targetUrl, 'target/page', { ...identity, index, previousHash: chainHash, entries: source.entries, pageHash: source.pageHash }, session);
        require(status.chainHash === nextHash && status.nextIndex === index + 1, 'Destination page receipt differs from source.');
      }
      chainHash = nextHash;
      count += source.entries.length;
    }
    require(count === player.entryCount, 'Saved source entry count differs.');
    status = await admin(targetUrl, 'target/finish', { ...identity, pageCount: player.pageCount, entryCount: count, chainHash }, session);
    require(status.phase === 'complete' && status.chainHash === chainHash && status.entryCount === count, 'Destination import did not finalize.');
    await save(path.join(directory, 'journals', player.objectId, 'target-complete.json'), status);
    players++;
    console.log(JSON.stringify({ status: 'player_journal_imported', player: players, entries: count, pages: player.pageCount }));
  }
  await save(path.join(directory, 'import-report.json'), { ...report, importedPlayers: players, completedAt: new Date().toISOString() });
  console.log(JSON.stringify({ status: 'all_journals_imported', players, entries: report.totalEntries, complete: true }));
}

async function verifyTarget(session) {
  const report = await json(path.join(directory, 'import-report.json'));
  require(report.complete && report.generation === session.generation, 'No complete import report for this migration.');
  const [objects, source, target] = await Promise.all([
    listObjects(), admin(sourceUrl, 'status', {}, session), admin(targetUrl, 'status', {}, session),
  ]);
  require(source.role === 'source-freeze' && target.role === 'target-staging', 'Activation requires a frozen source and staged destination.');
  require(source.generation === session.generation && target.generation === session.generation, 'Migration generation changed.');
  require(JSON.stringify(objects.map(o => o.id).sort()) === JSON.stringify(report.objectIds.slice().sort()), 'The frozen source object set changed.');
  const checked = [];
  for (const player of report.players) {
    if (!player.steamId) { require(player.entryCount === 0, 'A nonempty source has no owner.'); continue; }
    let chainHash = hash(`PDD-MIGRATION-V1\n${session.generation}\n${player.objectId}\n${player.steamId}`);
    let count = 0;
    for (let index = 0; index < player.pageCount; index++) {
      const page = await json(path.join(directory, 'journals', player.objectId, `page-${String(index).padStart(6, '0')}.json`));
      require(hash(JSON.stringify(page.entries)) === page.pageHash, 'Saved source page digest changed.');
      chainHash = hash(`${chainHash}\n${page.pageHash}`);
      count += page.entries.length;
    }
    const status = await admin(targetUrl, 'target/status', { steamId: player.steamId }, session);
    require(status.phase === 'complete' && status.sourceObjectId === player.objectId && status.generation === session.generation
      && status.sourceUrl === sourceUrl && status.targetUrl === targetUrl && status.entryCount === count
      && status.nextIndex === player.pageCount && status.chainHash === chainHash, 'A remote destination journal does not match its frozen source.');
    checked.push({ sourceObjectId: player.objectId, pages: player.pageCount, entries: count, chainHash });
  }
  const local = await modules();
  await save(path.join(directory, 'activation-permit.json'), { generation: session.generation, verifiedAt: new Date().toISOString(),
    sourceUrl, targetUrl, sourceRole: source.role, targetRole: target.role, allSourceObjectsAccountedFor: true,
    objects: report.objectIds, checked, modules: Object.fromEntries(Object.entries(local).map(([name, text]) => [name, hash(text)])) });
  console.log(JSON.stringify({ status: 'target_activation_verified', objects: objects.length, players: checked.length, entries: report.totalEntries }));
}

async function stageFiles(session) {
  const local = await modules();
  const folder = path.join(directory, 'modules');
  await mkdir(folder, { recursive: true });
  for (const [name, text] of Object.entries(local)) await writeFile(path.join(folder, name), text);
  const vars = { PDD_STEAM_APP_ID: '4817200', PDD_STEAM_PETS_ITEMDEF_ID: '100', PDD_GRANTS_PER_MINUTE: '300',
    PDD_SHOP_ENABLED: '1', PDD_LEGACY_PURCHASES_ENABLED: '0', PDD_CASH_PURCHASES_ENABLED: '0', PDD_MARKET_ENABLED: '0',
    ...migrationVars(session, 'target-staging') };
  const config = { name: workerName, account_id: targetAccount, main: './modules/migration-entry.js',
    compatibility_date: '2026-10-10', no_bundle: true, find_additional_modules: true,
    rules: [{ type: 'ESModule', globs: ['**/*.js'], fallthrough: true }],
    workers_dev: true, preview_urls: false, vars, durable_objects: { bindings: [{ name: 'PLAYER_INVENTORY', class_name: 'PlayerInventory' }] },
    migrations: [{ tag: 'v1', new_sqlite_classes: ['PlayerInventory'] }],
    observability: { enabled: true, logs: { enabled: true }, traces: { enabled: true, head_sampling_rate: 0.1 } } };
  await save(path.join(directory, 'wrangler.target-staging.json'), config);
  console.log(JSON.stringify({ status: 'migration_modules_staged', modules: Object.keys(local), originalBundleHash: hash(local['index.js']) }));
}

async function verifyStaged(session) {
  const status = await admin(targetUrl, 'status', {}, session);
  require(status.role === 'target-staging' && status.generation === session.generation && status.sourceUrl === sourceUrl && status.targetUrl === targetUrl,
    'Destination staging configuration differs from the pinned migration.');
  const denied = await fetch(`${targetUrl}/__pdd_migration/v1/status`, { method: 'POST', body: '{}', redirect: 'error', signal: AbortSignal.timeout(15000) });
  require(denied.status === 401, 'Destination accepts unsigned migration requests.');
  const blocked = await fetch(`${targetUrl}/v1/inventory`, { redirect: 'error', signal: AbortSignal.timeout(15000) });
  require(blocked.status === 503, 'Destination public operations are not staged.');
  await save(path.join(directory, 'staging-verification.json'), { verifiedAt: new Date().toISOString(), generation: session.generation,
    targetUrl, signatureVerified: true, unsignedRejected: true, publicOperationsBlocked: true, journalWrites: 0, steamCalls: 0 });
  console.log(JSON.stringify({ status: 'remote_staging_verified', signatureVerified: true, unsignedRejected: true, publicOperationsBlocked: true }));
}

try {
  const command = process.argv[2];
  if (command === 'prepare') await prepare();
  else {
    const session = await json(path.join(directory, 'session.json'));
    require(session.sourceAccount === sourceAccount && session.targetAccount === targetAccount && session.sourceUrl === sourceUrl && session.targetUrl === targetUrl, 'Pinned migration targets changed.');
    if (command === 'snapshot') await snapshot();
    else if (command === 'source-freeze' || command === 'source-forward') await sourceDeploy(command, session);
    else if (command === 'export') await exportState(session);
    else if (command === 'import') await importState(session);
    else if (command === 'verify-target') await verifyTarget(session);
    else if (command === 'stage-files') await stageFiles(session);
    else if (command === 'verify-staged') await verifyStaged(session);
    else if (command === 'modules') console.log(JSON.stringify({ modules: await modules(), vars: migrationVars(session, 'target-staging'), session: { generation: session.generation, publicKey: session.publicKey } }));
    else throw new Error('Use prepare, snapshot, modules, stage-files, verify-staged, source-freeze, export, import, verify-target, or source-forward.');
  }
} catch (error) {
  // Only messages authored above may be printed; network errors can hold data.
  const message = error instanceof Error && error.stack?.includes('account-migration.mjs') ? error.message : 'Local or remote migration check failed. Preserve artifacts and inspect the current phase.';
  console.error(JSON.stringify({ status: 'stopped', reason: message, automaticRetry: false }));
  process.exitCode = 1;
}
