// Stage the existing preview artwork unchanged for Steam's public icon URLs.
import { mkdirSync, readFileSync, copyFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const economy = JSON.parse(readFileSync(resolve(root, 'SteamInventory/economy.json'), 'utf8'));
if (!/^[a-z0-9-]+$/.test(economy.version)) throw new Error('Invalid icon version.');
const destination = resolve(root, 'PetDaDog.Worker/public/icons', economy.version);
mkdirSync(destination, { recursive: true });
for (const cosmetic of economy.cosmetics) {
  if (!cosmetic.uiAssetPath.startsWith('res://Art/UI/') || cosmetic.uiAssetPath.includes('..')) throw new Error('Invalid icon source.');
  copyFileSync(resolve(root, 'Pet_Da_Dog_CSharp', cosmetic.uiAssetPath.slice(6)), resolve(destination, `${cosmetic.itemDefId}.png`));
}
writeFileSync(resolve(root, 'PetDaDog.Worker/public/_headers'), '/icons/*\n  Cache-Control: public, max-age=3600\n  X-Content-Type-Options: nosniff\n');
console.log(`Staged ${economy.cosmetics.length} original cosmetic icons under /icons/${economy.version}/.`);
