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
5. Attach jobs directly via `DynamicSQLiteController.UpdatePlayerShipIDFromJob`.
6. Set `DontLoadJobs` so native AI does not add blocked cargo.
7. Dispatch with the native cast-out/movement path.

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

