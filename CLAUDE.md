# Claude Handoff Notes

## Project

TransOcean 2 Fleet Automation is an experimental live mod for Steam TransOcean 2.

- Game root: `C:\Program Files (x86)\Steam\steamapps\common\TransOcean2`
- Source repo: `C:\Users\daogl\dev\transocean2-fleet-automation`
- Public repo: `https://github.com/dan-oglesby/transocean2-fleet-automation`

The source repo intentionally lives outside Proton Drive.

## Active Loading Path

Use the direct managed patch path, not BepInEx.

- `doorstop_config.ini` should remain `enabled = false`.
- The game assembly has a one-call bootstrap patch in `Cargo.InitGameStart.Awake()`.
- Original assembly backup: `TransOcean2_Data\Managed\Assembly-CSharp.dll.to2fa-original`
- Runtime DLL: `TransOcean2_Data\Managed\TransOcean2FleetAutomation.Direct.dll`
- Restore helper: `scripts\restore-direct-patch.ps1`

Install current direct build with:

```powershell
.\scripts\install-direct-patch.ps1 -GameRoot 'C:\Program Files (x86)\Steam\steamapps\common\TransOcean2'
```

Build only:

```powershell
.\scripts\build-direct.ps1 -GameRoot 'C:\Program Files (x86)\Steam\steamapps\common\TransOcean2'
```

## Current Mod Behavior

- F9 toggles the in-game TO2 Fleet Captain panel.
- F8 evaluates enabled ships.
- F10 refreshes the fleet list.
- `Live actions` defaults on.
- `Auto repairs` defaults on.
- `Minimum condition to sail` defaults to 85%.
- `Repair target condition` defaults to 100%.
- `Allow contraband` defaults off.

Ships below the sail threshold are held for maintenance, routed to safe reachable repair docks, or repaired when already in a repair-capable harbor.

Cargo automation is intended to:

1. Find available jobs from the current harbor.
2. Exclude contraband freight by default using the game's `StaticTableFreightAttributes.Smugglersware`.
3. Rank jobs by pay per day, relationship points, and penalty risk.
4. Bundle same-destination jobs that fit.
5. Add a discounted one-hop chain opportunity bonus for destinations whose arrival harbor has another strong legal bundle available.
6. Attach immediate-destination jobs directly via `DynamicSQLiteController.UpdatePlayerShipIDFromJob`.
7. Set `DontLoadJobs` so native AI does not add blocked cargo.
8. Dispatch with the native cast-out/movement path.

The chain scoring is intentionally advisory. It does not accept future-harbor jobs early and it does not mix cargo for multiple destinations into one native route. It only nudges the first destination toward harbors with better follow-on work, then lets the next automation tick re-evaluate after arrival.

## Recent Contract Bug

The native job pool marks available normal jobs with `DynamicTableJobs.PlayerShipID == 1`, not `0`.

The direct planner previously rejected all nonzero `PlayerShipID` values, so it logged `no matching legal unreserved jobs` even when the native UI would show available contracts.

Current fix:

```csharp
int playerShipId = ReadInt(rawJob, "PlayerShipID");
if (playerShipId != 0 && playerShipId != 1 && playerShipId != ship.PlayerShipId)
{
    return null;
}
```

The user asked to install this fix. After installing, verify in the next run that TO2FA logs lines like:

- `best route ...`
- `loaded X legal job(s) for ...`

If it still does not visibly accept contracts, inspect whether `UpdatePlayerShipIDFromJob(jobId, playerShipId)` is enough for the commission UI/state, or whether we also need to mirror parts of `Cargo.MenuCommissions.OnAcceptCommissions` / `CommissionFactory.LoadContractCommissionOnShip`.

## Upgrade API Findings

Auto-upgrade automation is feasible but not enabled yet.

Relevant native methods and fields:

- `Cargo.ShipFactory.UpgradePlayerShip(int playerShipID, int upgradeId, int slot, int upgradeDockOwner, long upgradeDockOwnerShares)`
- `Cargo.ShipFactory.GetUpgradeDockOwner(int regionNumber)`
- `Cargo.ShipFactory.SetUpgradeValues(int playerShipID, int upgradeId)`
- `DynamicSQLiteController.SetUpgradeToShip(int playerShipID, int upgradeID, int slot)`
- `DynamicSQLiteController.UpdatePlayerShipUpgradeTime(int playerShipID, DateTime upgradeFinish)`
- `StaticSQLiteController.GetUpgradeDockHarbors()`
- `StaticSQLiteController.GetUpgradeDockHarbor(int region)`
- `StaticSQLiteController.GetAvailableHarborUpgrades()`
- `DynamicTablePlayerShips` has `Upgrade1` through `Upgrade5`, `UpgradeFinish`, `Upgraded`, and `Status`.

Upgrade templates live under `Cargo.ShipUpgrades`:

- IDs 1-6 are general upgrades. Observed base prices: 1,500,000 for IDs 1-2, 2,000,000 for IDs 3-4, and 2,500,000 for IDs 5-6.
- ID 1 increases speed, ID 2 shortens repair duration, ID 3 lowers tug fees, ID 4 lowers fuel consumption, ID 5 enables waypoint behavior used by `HasWaypointUpgrade`, and ID 6 extends distance/range.
- IDs 7-15 are freight-specific export/revenue upgrades. Base price is 500,000. Container IDs 7-9, bulk IDs 10-12, tank IDs 13-15. Their `Freights` list determines which cargo types get the factor bonus.

Suggested first auto-upgrade policy:

- Add an `Auto upgrades` toggle defaulting off.
- Only consider enabled ships that are idle in a harbor with `UpgradeDock`, above maintenance threshold, not repairing/upgrading, and with treasury above a larger reserve.
- Prefer cheap freight-specific upgrades matching the ship freight type and recent/high-value cargo, then fuel/speed/waypoint upgrades for larger classes.
- Use `GetUpgradeDockOwner(region)` plus the native `UpgradePlayerShip` path rather than direct database writes.
- Log the chosen upgrade ID, slot, estimated price, and days before spending credits.

## Useful Logs

Primary Unity log:

```text
C:\Program Files (x86)\Steam\steamapps\common\TransOcean2\TransOcean2_Data\output_log.txt
```

Useful search pattern:

```powershell
Select-String -Path 'C:\Program Files (x86)\Steam\steamapps\common\TransOcean2\TransOcean2_Data\output_log.txt' -Pattern '\[TO2FA\.Direct\]|best route|loaded .*job|no matching|direct dispatch|Exception|Error|failed' -CaseSensitive:$false
```

## Safety Notes

- Do not use BepInEx for normal play yet; it caused Unity serialization/missing-script warnings in prior tests.
- Do not overwrite the managed DLL while the game is running.
- Avoid multiplayer testing while the direct patch is installed.
- Be careful with the user's dirty worktree. Do not revert user changes.
