# Steam checkout sandbox export

This is a separate diagnostic export for the unreleased app's validated Steamworks
developer account supplied at build time. It loads the actual transparent desktop dog
and an opaque diagnostic window. It does not change the normal game build or the
Worker's public cash flag. Global input earning and the tray are disabled.

Build with Godot 4.6.3 Mono:

```powershell
node scripts/economy-generate.mjs --sandbox-prices
.\Tests\SteamCheckoutSandbox\Build.ps1 -ValidatedTestSteamId '<developer SteamID64>'
```

Generating the candidate does not publish it. Verify that the app is unreleased
and the test account is on its Steamworks development/publishing team before
temporarily publishing prices. Keep the public Shop cash capability disabled.

The generated project and executable stay under the ignored
`Pet_Da_Dog_CSharp/.godot/steam-checkout-sandbox` directory, outside `Builds`.
`latest-package.txt` in that directory identifies the latest package. A launch
smoke test uses the real dog scene with Steam disabled; it never starts checkout.

## Optional launch GameID comparison

If Shift+Tab works but `IsOverlayEnabled()` remains false, build a controlled
comparison with `-PreserveLaunchGameId`. The ordinary client assigns AppID 4817200
to both `SteamAppId` and `SteamGameId` immediately before SDK initialization. This
option changes only the generated sandbox copy of `SteamAppId.cs`: it still sets
the real `SteamAppId`, but keeps a nonempty `SteamGameId` inherited from Steam's
launcher. If no GameID was inherited, it uses the ordinary AppID fallback.
`SteamOverlayGameId` is never changed. The builder fails if the expected source
method is no longer present, rather than silently applying a different patch.

```powershell
.\Tests\SteamCheckoutSandbox\Build.ps1 -ValidatedTestSteamId '<developer SteamID64>' -PreserveLaunchGameId
```

This is an unproven diagnostic for a possible shortcut/SDK identity mismatch,
not a production fix. Compare launches through the same Steam shortcut and keep
the window/rendering setup unchanged. Logs record only the three named launch
identifiers before configuration and after configuration immediately before SDK
initialization, plus overlay availability transitions. Checkout still requires
the correct SDK AppID/account, authentication, a current approval marker, a
Steam price, a true SDK overlay check, and the public cash capability remaining
off. No checkout is started automatically. The generated package uses the same
test profile and executable name; the builder never replaces a running package.

## Manual Steam test

The non-Steam shortcut test reached `StartPurchase` success, but only the normal
overlay appeared; the checkout page was unavailable. Price lookup and a successful
checkout-start callback do not establish that payment UI works. Continue that
case with an installed private Steam build under the real AppID.

Prepare its complete depot ZIP with `Prepare-SteamDepot.ps1 -PackagePath
'<validated sandbox package>'`. The script stages the existing validated export,
renames its EXE/PCK to `PetDaDogCSharp`, verifies every archived file against the
source, and runs an isolated offline launch. It excludes `steam_appid.txt`, the
local checkout approval, debug symbols, and credential files. It never uploads
or promotes a build. The generated `upload-instructions.txt` explains depot
4817201, the Windows launch option, and a password-protected test branch. The
diagnostic build belongs only on that test branch.

For a local non-Steam shortcut diagnostic, the original steps are:

1. In the desktop Steam client, choose **Games → Add a Non-Steam Game → Browse**.
   Select `PetDaDogCheckoutSandbox.exe` in the generated package and add it.
2. Launch that entry **from Steam**. Leave `steam_appid.txt` beside the executable.
3. Confirm the diagnostic window reports **Steam AppID 4817200**, the validated
   Steam account, and **Overlay enabled: True**. A different AppID/account or an
   unavailable overlay keeps checkout disabled.
4. Click **Load price from Steam**. The displayed amount and currency come from
   Steam. No estimated USD price is substituted.
5. Checkout remains disabled until the published priced schema is independently
   verified and a short-lived `checkout-approval.json` is supplied beside the EXE.
6. Once unlocked, click **Start Steam sandbox checkout** and **cancel** in Steam.
   Return to the diagnostic window and refresh. The confirmed balance should
   remain unchanged. Report whether the overlay is fully visible and clickable.
7. Start a second checkout and complete **one** 100-Golden-Bones pack. Observe
   **Worker-confirmed Golden Bones**. Starting checkout alone is not payment
   confirmation; only the backend inventory response updates the counter.
8. Close and relaunch the test through Steam, then refresh. The confirmed balance
   and saved checkout baseline allow an independent restart comparison.

The real transparent dog window is not enlarged or replaced for this test. Its
size, transparency, and focus flag are displayed so overlay clipping can be
identified rather than hidden by a different presentation.

Logs and the Steam-ID-scoped confirmed balance baseline are stored in
`%APPDATA%/PetDaDogCheckoutSandbox/checkout-sandbox.log` and
`checkout-session.json`. They contain no tickets, backend tokens, or publisher
credentials. This fixture does not claim a balance change proves a particular
order; it reports the authoritative inventory difference for the test account.

The approval marker must match AppID, pack ID, Steam ID, and the priced schema's
SHA256 embedded by the build. UTC issuance/expiry must be valid and no more than
30 minutes apart. Remove the marker and restore the unpriced Steam schema after
testing. The normal game's public purchase capability remains disabled throughout.
