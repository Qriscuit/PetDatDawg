# Cloudflare setup for Pet Da Dog

This guide connects the Godot client to the API in `PetDaDog.Worker`, using
Steam AppID **4817200**. Steam Inventory holds the spendable Pets balance, boxes,
and owned cosmetics. Cloudflare verifies Steam sign-ins and forwards accepted
operations to Steam.

Verified deployment: `https://petdadawg.americlasagna.workers.dev`, with
`/health` returning HTTP 200, AppID 4817200, and Pets itemdef 100. Cloudflare
contains both secret bindings and the `PLAYER_INVENTORY` storage binding. The
Godot project is configured for that URL. Real Steam sign-in, grants, purchases,
and box openings still require the live checks in section 7.

## 1. Finish the Steamworks configuration

Upload the current [item definitions](../SteamInventory/itemdefs.json) in
[Inventory Service for this app](https://partner.steamgames.com/apps/inventoryservice/4817200/),
check the upload results, and enable Inventory Service. The schema includes:

| Item | Definition ID | Use |
| --- | --- | --- |
| Pets | 100 | One accepted dog click earns one unit |
| Dog Box | 1001 | Costs 1 Pet; opens into one of 45 dog variants |
| Accessories Box | 1000 | Costs 2 Pets; opens into one of 43 accessories |

Use a Steamworks partner-group account with access to this app for testing
while Inventory visibility is Private. See [Steam's inventory setup](https://partner.steamgames.com/doc/features/inventory).

Create a publisher key through **Users & Permissions → Manage Groups**. Choose
a dedicated group containing app **4817200**, select **Create WebAPI Key**,
enable **General** and **Economy**, and save. General permits ticket validation;
Economy permits inventory operations. Enter the key directly into Cloudflare
when instructed below. Keep it out of chat, client files, and source control.
[Steam publisher-key instructions](https://partner.steamgames.com/doc/webapi_overview/auth)

The optional Steam key IP whitelist refers to the server calling Steam. Your
PC's address does not describe a deployed Cloudflare Worker's outgoing traffic.
Leave the whitelist unset for this setup unless you have deliberately arranged
a suitable server egress restriction.

## 2. Create the Worker on the Cloudflare website

1. Sign in to the [Cloudflare dashboard](https://dash.cloudflare.com/) and choose
   the account where the game API should live.
2. Open **Workers & Pages**, then **Create application**.
3. Choose the simple **Hello World** Worker option or template if offered. Name
   it **petdadawg**, matching `PetDaDog.Worker/wrangler.jsonc`, and deploy the
   starter. Dashboard labels can vary; if your screen only offers repository
   templates, use the command-line creation path in step 4 below instead.
4. Save the provided HTTPS address, usually
   `https://petdadawg.americlasagna.workers.dev`.

The starter only confirms that Cloudflare is reachable. Step 4 deploys the
actual game API. A custom domain is optional for initial testing.
[Cloudflare dashboard setup](https://developers.cloudflare.com/workers/get-started/dashboard/)

## 3. Add the two secrets

Open **Workers & Pages → petdadawg → Settings → Variables and Secrets → Add**.
Select type **Secret** for both entries:

| Secret name | Value |
| --- | --- |
| `PDD_STEAM_PUBLISHER_KEY` | The Steamworks publisher key |
| `PDD_BACKEND_SESSION_SECRET` | A new random signing secret, at least 32 UTF-8 bytes |

To generate the session secret locally, run this in PowerShell. It creates 32
random bytes and places their base64 representation on your clipboard:

```powershell
$pddSecretBytes = New-Object byte[] 32
$pddSecretGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$pddSecretGenerator.GetBytes($pddSecretBytes)
$pddSecretGenerator.Dispose()
[Convert]::ToBase64String($pddSecretBytes) | Set-Clipboard
```

Paste that clipboard value into `PDD_BACKEND_SESSION_SECRET`, then use
Cloudflare's **Deploy** control to apply the settings. This secret signs the
client's short-lived API sessions. It is separate from the Steam key.

If you created the Worker with the command line, add the secrets using these
interactive prompts from `PetDaDog.Worker` after its first deployment:

```powershell
npx.cmd wrangler secret put PDD_STEAM_PUBLISHER_KEY
npx.cmd wrangler secret put PDD_BACKEND_SESSION_SECRET
```

Plain configuration values are already in `wrangler.jsonc`: AppID `4817200`,
Pets item definition `100`, and grant limit `300` per player per minute. The two
secret values must never be placed in its `vars` object.
[Cloudflare secrets](https://developers.cloudflare.com/workers/configuration/secrets/)

## 4. Deploy the code from this repository

Node.js runs the local deployment tool; Cloudflare runs the deployed Worker,
and players do not need Node. This development machine already has Node 24
bundled with Codex at
`C:\Users\Hurshuuuuuu\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe`.
With the existing project dependencies, it can run Wrangler directly:

```powershell
Set-Location 'S:\CodexProjects\PetDaDog\PetDaDog.Worker'
$pddNode = 'C:\Users\Hurshuuuuuu\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
$env:WRANGLER_SEND_METRICS = 'false'
$env:XDG_CONFIG_HOME = Join-Path (Get-Location).Path '.wrangler/config'
$env:WRANGLER_LOG_PATH = Join-Path (Get-Location).Path '.wrangler/logs'
& $pddNode node_modules/wrangler/bin/wrangler.js login --scopes account:read user:read workers_scripts:write workers:write
& $pddNode node_modules/wrangler/bin/wrangler.js whoami
& $pddNode node_modules/wrangler/bin/wrangler.js deploy
```

Use this path to apply the existing `wrangler.jsonc` without installing another
copy of Node. Cloudflare sign-in is required before deployment. The login above
uses a workspace-local ignored configuration folder.

For a separate machine or a fresh dependency installation, install
[Node.js](https://nodejs.org/en/download) **22.13 or newer** with npm;
the local tests use Node's built-in SQLite module. Choose a currently supported
LTS release, then open a new PowerShell window. The commands below use `.cmd`
so they work with Windows' default script execution restrictions:

```powershell
Set-Location 'S:\CodexProjects\PetDaDog\PetDaDog.Worker'
npm.cmd ci
npm.cmd test
npm.cmd run test:runtime
npm.cmd run check
npx.cmd wrangler login
npx.cmd wrangler whoami
```

`login` opens a Cloudflare sign-in page. Select the same account used above.
`whoami` lets you check the account before deployment. The tests and dry run
check local code; they do not contact Steam with a real player account.

Confirm `name` in `wrangler.jsonc` matches the Worker you created, then deploy:

```powershell
npm.cmd run deploy
```

For the command-line creation path, this command creates **petdadawg**. Add
the secrets using the prompts in step 3. The API needs those secrets before
player sign-ins or grants can work. If the Worker already exists, Wrangler
replaces its starter code with this project's API.
[Wrangler deployment guide](https://developers.cloudflare.com/workers/get-started/guide/)

Wrangler also provisions the `PLAYER_INVENTORY` Durable Object binding, using
the exported `PlayerInventory` class and the checked-in `v1` SQLite migration.
It stores operation records and serializes each player's transactions. Steam
remains the authority for balances and item ownership. Deploy the whole project
with its configuration so that this binding is created along with the code.

SQLite Durable Objects work on Cloudflare's Workers Free plan, subject to its
usage limits. A small live test can start there; check usage as player traffic
grows. [Durable Object plans](https://developers.cloudflare.com/durable-objects/platform/pricing/)

The configured public URL is `https://petdadawg.americlasagna.workers.dev`.
Confirm deployment prints that same address, then check:

```powershell
Invoke-RestMethod 'https://petdadawg.americlasagna.workers.dev/health'
```

A configured API returns:

```json
{"status":"ok","appId":4817200,"petsItemDefId":100}
```

This verifies the deployed API is reachable and its configuration is present.
It does not contact Steam. The player tests below verify authentication and
real Steam inventory operations.

## 5. Connect the Godot client

Start Steam and sign in with your development account. In PowerShell:

```powershell
Set-Location 'S:\CodexProjects\PetDaDog\Pet_Da_Dog_CSharp'
$env:PDD_BACKEND_URL = 'https://petdadawg.americlasagna.workers.dev'
$env:PDD_STEAM_APP_ID = '4817200'
Remove-Item Env:PDD_DISABLE_STEAM -ErrorAction SilentlyContinue
dotnet build
& 'W:\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe' --path 'S:\CodexProjects\PetDaDog\Pet_Da_Dog_CSharp' --scene 'res://Main.tscn' --no-header
```

Run the game from that same PowerShell session so it receives the environment
variables. The development `steam_appid.txt` must also contain **4817200**;
check for an older `480` file beside an exported executable if initialization
uses the wrong app.

For the packaged game, set the production address in
`Pet_Da_Dog_CSharp/project.godot` using `pdd/backend_url` before exporting:

```ini
[pdd]

backend_url="https://petdadawg.americlasagna.workers.dev"
```

If the `[pdd]` section already exists, edit its `backend_url` entry. The
`PDD_BACKEND_URL` environment variable overrides this project setting during
development. The client uses AppID **4817200** by default. Players should
receive a build configured for your deployed URL.

## 6. Understand what the game confirms

The client uses these routes automatically:

| Route | Purpose |
| --- | --- |
| `POST /v1/auth/steam` | Verify a Steam ticket and create an API session |
| `POST /v1/pets/grant` | Confirm one queued click and grant one Pets unit |
| `GET /v1/inventory` | Read the Pets balance, boxes, and owned cosmetics |
| `POST /v1/boxes/buy` | Exchange Pets for an unopened Dog or Accessories Box |
| `POST /v1/boxes/open` | Consume a specific box and return its random reward |

One visible dog click creates one GUID in the client's current-run pending
queue. The Worker validates the player's Steam ticket for identity
`petdadog-backend`, issues a signed session, and grants one Pets unit through
Steam `AddItem`. Retried click grants reuse the same Steam request ID.

The displayed Pets balance comes from Steam. Pending click grants are shown
separately and retry while this run remains open. Session expiry triggers
reauthentication. Offline clicks do not become a locally authoritative balance,
and an unsent pending queue is not carried across a restart.

Buying a Dog Box spends **1 Pet**; buying an Accessories Box spends **2 Pets**.
Steam exchanges currency for an unopened box atomically. Opening exchanges one
specific owned box for its corresponding generator; Steam chooses one random
cosmetic and consumes the box in the same operation.

The Shop shows owned boxes and confirmed cosmetics. Owned dog variants can be
selected in the dog selector, and owned inventory accessories become available
in the Items shelf. The default dog and existing starter accessories remain
available. Duplicate cosmetic drops increase their Steam inventory quantity.

The Worker limits accepted grants to **300 per player per minute** by default.
Steam authentication proves the player's identity, while a desktop client
cannot prove a human physically clicked. This limit controls accepted request
volume; it is not a physical-click detector.

If Steam's response to an exchange is uncertain, the API reports a pending
operation rather than automatically submitting another purchase or opening.
New grants, purchases, and openings for that player remain blocked until
the operation is resolved; inventory reads remain available. A saved successful
Steam response can finish processing on a later request. A lost response needs
support investigation against Steam's operation history: a changed balance or
cosmetic quantity alone does not establish which request succeeded. Do not
delete its operation record or submit a replacement purchase to force progress.

## 7. Check the full live workflow

The opt-in connection/grant runner uses the production Steam and HTTP clients,
compares totals with Steam's native inventory API, and performs repeated fresh
inventory reads. It defaults to **zero grants**. If the independent native API
is unavailable, explicit `-WorkerOnly` mode checks the Steam inventory supplied
by the trusted Worker and reports `nativeVerified=false`:

```powershell
.\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamSmoke.ps1 -AllowLiveSteam
# Exercise the dog's visible-pixel click handler and earn exactly three real Pets:
.\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamSmoke.ps1 -AllowLiveSteam -GrantCount 3 -UseDogInput
# Check the Worker path separately when the independent native API is unavailable:
.\Pet_Da_Dog_CSharp\Tests\Run-LiveSteamSmoke.ps1 -AllowLiveSteam -WorkerOnly -GrantCount 0 -UseDogInput
```

Close other pet clients while checking exact increments. Logs are written under
the client's `.godot` directory; credentials are not printed. A timeout after
sending a click can mean Steam accepted it, so read inventory before rerunning.
For interactive checks, launch the client with `-- --status --validate-pets` to
log confirmed totals and pending clicks separately.

Live validation on October 10, 2026 confirmed Steam client initialization for
AppID 4817200 and Worker health. The replacement publisher key was deployed at
9:09 PM EDT, but Steam sign-in still returned **HTTP 403 from
ISteamUserAuth/AuthenticateUserTicket**. A temporary remote preview then tested
the deployed key without a player ticket, using both the supported request
header and the `key` parameter:

| Steam request | Header key | Parameter key |
| --- | --- | --- |
| Publisher app list | 403 | 403 |
| App ownership check | 403 | 403 |
| Inventory read for AppID 4817200 | 200, inventory JSON parsed; result headers not checked | 200, inventory JSON parsed; result headers not checked |

The developer confirmed that the key had Economy and Microtransactions enabled,
but **General was disabled**. That explains the sign-in and ownership denials.
Enable General on the exact group whose key is stored in Cloudflare, retain
Economy, and save the key permissions. A permissions-only change does not
require replacing the same key in Cloudflare. Microtransactions does not replace
General. After General was enabled, both request formats returned **200** for
the app list, ownership, and inventory. The real client authenticated and six
fresh inventory responses parsed as zero Pets. Those early reads did not inspect
Steam's result headers, so they did not establish an authoritative zero balance.
[Steam key permissions](https://partner.steamgames.com/doc/webapi_overview/auth)

The preview sent no grants, exposed no keys or tickets, and was terminated
without changing production. A denied app-list or ownership result does not
prove the app is absent or the account lacks a license. The Worker reports an
allowlisted Steam endpoint and HTTP status to distinguish these upstream errors
from its own `app_not_owned` response.

The native Steam `GetAllItems` call returned `k_EResultFail`, while a publisher
API request returned all 93 definitions, including Pets item 100 of type `item`.
A dog-input grant received HTTP 200 but no Pets in its receipt
(`missing_pets_receipt`). Isolated retries of that same click tested
`input_json`, the flat sample without `itempropsjson`, and the identical
`input_json` payload with the key in the form body. All returned empty receipts;
the final control also had no success/replayed flags. Their inventory JSON
parsed as zero, but result headers were not captured, so those balances were
not confirmed. Same-ID controls alone could not rule out a cached empty receipt.
All previews were terminated, and no speculative transport change was deployed.

One separately authorized fresh request then returned an empty receipt with
**HTTP 200 and `x-eresult: 2`**, plus an `x-error_message` header. Steam's shared
enum defines result 2 as generic failure. HTTP status alone therefore did not
establish success. The local Steam `inventory_service_log.txt` supplied the
specific reason for AppID 4817200: **Inventory Service is not enabled for this
app**. The developer confirmed the Inventory Service checkbox was checked and
the displayed Economy Asset Server URL was
`http://api.steampowered.com/IGameInventory`, then published the Steamworks
configuration. A checked dashboard setting alone had not established the live
service state; the contents of the pending policies change were not reviewed.
[Steam result codes](https://partner.steamgames.com/doc/api/steam_api?l=english#EResult)

After publishing, a read-only probe returned **`x-eresult: 1` with valid empty
inventory**. The strict native/Worker zero-grant test passed **54 assertions**:
native `GetAllItems` succeeded with zero Pets, the Worker also returned zero,
and six fresh comparisons agreed. This establishes the post-publish read
baseline. Retrying the recorded fresh failed click then confirmed **0 → 1 Pet**,
cleared its pending queue entry, and returned one Pet on three fresh Worker
reads. That recovery test passed **39 assertions** in Worker-only mode.
The following strict test passed **81 assertions** with two further grants:
**1 → 2 → 3 Pets**, exactly one per accepted click, with pending entries clearing
to zero each time. The test called the real `DesktopPet._Input` handler at opaque
texture-alpha pixels in headless Godot; release events and clicks outside the dog
were ignored. This verifies the input handler, not physical Windows pointer input.
Native Steam initially returned its cached total of one, then converged to three
after about ten seconds; fresh Worker reads also confirmed three. This establishes
real grant addition and agreement with independent native Steam inventory after
the refresh delay. The runner can retry a recorded test GUID without generating
another click; see `LIVE_STEAM_SMOKE.md`.
Replaying the last confirmed click passed **40 assertions** and left the total
at **3 → 3**, with no pending entry and three stable fresh Worker reads. The
packaged interactive client was then reopened and restored **3 confirmed Pets,
0 pending** from Steam through the Worker.

Production Worker version `e34a7a97-2a84-48c6-bbb7-98ae8a9da6b2`, deployed
around 10:20 PM EDT, now checks Steam's result headers before accepting any
response body. A present non-success, malformed, combined or out-of-range
`x-eresult`, or a nonempty `x-error_message`, fails closed. Missing headers retain
the existing JSON validation. Grants keep their original request ID for retry;
uncertain exchanges retain their durable lock and are not repeated. The Worker
passed **31 unit tests**, the actual workerd/SQLite smoke including header-failure
rejection, and the deployment dry run. The legacy ASP.NET backend's matching
guard passed **323 harness checks**. Uploaded definitions or `/health` success
alone do not prove real grants work.

Use these logs for diagnosis:

- Steam's local `logs/inventory_service_log.txt`, normally under
  `C:\Program Files (x86)\Steam`, gives native Inventory failures. Match the
  timestamp and AppID 4817200.
- Cloudflare **Observability → Logs** receives `steam_request_failed` records
  for failed upstream calls. These contain only a fixed endpoint, available HTTP
  status, fixed failure category and optional bounded numeric Steam result.
  They contain no raw Steam error text, account, ticket, key or click ID.
- The live client validation stdout files under the client's `.godot` directory
  show the test assertions and confirmed/pending state; the post-publish baseline
  is `live-steam-smoke-20261010-221828`, and the successful same-click recovery is
  `live-steam-smoke-20261010-222101`. The strict two-grant/native comparison is
  `live-steam-smoke-20261010-222154`; duplicate replay is
  `live-steam-smoke-20261010-222246`, and the reopened packaged client is
  `interactive-validation-20261010-222358`.
- Steamworks **Economy → Asset Server Error Logs** can show the documented
  `response.error` messages. Valve's public Inventory documentation does not
  guarantee that header-only failures appear there.
  [Inventory response and error-log documentation](https://partner.steamgames.com/doc/webapi/IInventoryService?l=english)

The transparent dog window clips rendering to its moving visible region, which
also clips Steam's injected overlay. Handle this for every player through the
app's Steamworks Installation → General Installation overlay configuration;
requiring each player to change their Library preferences is not the intended
solution. Steam documents that Software app types have the overlay disabled by
default. There is no overlay-disabling API in the documented client interface;
a process environment hint did not disable it in this Windows test. The app-wide
metadata change remains to be verified and published in Steamworks.
[Steam overlay documentation](https://partner.steamgames.com/doc/features/overlay)

Use a fresh test account balance or record the starting balance before each
check. Inspect both the game display and Steam Inventory.

1. Launch the game with Steam running. Confirm it signs in and reads inventory.
2. Click visible dog pixels once. Pending grants should clear and Pets increase
   by exactly one. Clicking transparent overlay space should grant nothing.
3. Earn enough Pets and buy a Dog Box. Pets should decrease by one and the
   owned Dog Box count increase by one.
4. Open that Dog Box. Its count should decrease by one, and one of the 45 dog
   variants should appear as owned and selectable.
5. Buy an Accessories Box for two Pets and open it. Confirm one of the 43
   accessories becomes owned and usable.
6. Relaunch. The Pets balance and cosmetic ownership should restore from Steam.
7. Briefly interrupt connectivity after a click, then reconnect without closing
   the game. Confirm the queued click completes once.
8. Leave the game running past the session lifetime and click again. Confirm
   reauthentication allows grants to continue. The duplicate-grant API tests
   verify that retry code reuses the same click ID; a live retried click must
   still add only one Pets unit.

These checks establish real configuration and player behavior. Local tests,
dry-run deployment, and `/health` alone do not prove Steam accepted a grant.

## Troubleshooting

A 403 can originate at different stages. Check the response code and the
allowlisted `steamEndpoint` before changing credentials:

| Stage | What can deny access | Fix |
| --- | --- | --- |
| Steam ticket validation / ownership | General permission disabled, wrong publisher key, missing AppID access, IP allowlist | Correct the exact publisher group's key permissions and scope; if replacing its value, deploy the Cloudflare secret |
| Steam inventory / grants | Economy permission missing, wrong app scope or IP allowlist | Enable Economy on the same publisher key; inspect the reported Steam endpoint |
| Worker's ownership decision | Valid ticket but no active app license (`app_not_owned`) | Give the test account access to AppID 4817200 |
| Cloudflare edge before the Worker | A configured access or firewall rule | Inspect the matching Cloudflare request; a Steam publisher-key change does not repair an edge rule |

An expired Worker session normally returns 401; rate limiting returns 429.
An upstream Steam 403 is reported by this Worker as HTTP 503 with
`code: steam_access_denied`, `steamEndpoint`, and `steamHttpStatus: 403`.
An HTTP 200 empty grant receipt is a different failure and must not advance
the client's confirmed balance or remove its pending click.

| Symptom | Check |
| --- | --- |
| Still returns Hello World | Deploy `PetDaDog.Worker` to the same Worker name and account |
| Steam initialization fails | Steam running, correct AppID/dev file, and account access to the app |
| Steam sign-in rejected | Publisher group includes 4817200, General permission, and matching ticket identity |
| Grants or inventory fail | Economy permission, current uploaded schema, Inventory enabled, Private visibility account access |
| Worker configuration error | Both secrets present and `PLAYER_INVENTORY` binding deployed |
| Rate limit reached | Stop clicking briefly and allow this run's pending queue to drain |
| Purchase/open remains pending | Reconcile the saved operation with Steam; do not create another charge |

Use the Worker's logs in Cloudflare or `npx.cmd wrangler tail` while investigating.
Keep tickets, session tokens, and publisher keys out of diagnostic output you
share. Never put the publisher key in the Godot client.

