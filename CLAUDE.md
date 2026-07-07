# Claude Handoff Notes

## Project

TransOcean 2 Fleet Automation is an experimental live mod for Steam TransOcean 2.

- Game root: `C:\Program Files (x86)\Steam\steamapps\common\TransOcean2`
- Source repo: `C:\Users\daogl\dev\transocean2-fleet-automation`
- Public repo: `https://github.com/dan-oglesby/transocean2-fleet-automation`

The source repo intentionally lives outside Proton Drive.

## Changelog — 2026-07-06 (Claude session)

Handoff from the prior Codex session. That session's uncommitted work (game-speed-scaled automation tick + a sink-risk repair-travel floor) is still in the tree and was left intact. On top of it, this session added, all in `src/TransOcean2FleetAutomationDirect.cs`:

1. **Repair threshold slider now spans 1%–99%** (was 50%–100%). Constants `MinSailConditionFloor`/`MinSailConditionCeiling`; `minimumSailCondition` clamp updated in `Awake` and `SetMinimumSailCondition`; slider relabeled "Repair threshold (min condition to sail)".
2. **`Auto-pilot all: ON/OFF` master toggle** replacing the two enable/disable buttons; reflects `AreAllVisibleShipsEnabled()` and calls `RefreshFleet(true)` on change.
3. **Near-opaque panel + click-through blocking** (`GetPanelBackgroundTexture`, `BlockClickThrough`).
4. **Upgrade cushion is now `max(20M/ship, 200M total)`** (`GetUpgradeTreasuryCushion`, new const `UpgradeTreasuryFloorTotal`).
5. **Idle-ship repositioning** (`TryRepositionIdleShip` / `FindBestRepositionHarbor` / `TryDeadheadToHarbor`, `Move idle ships to work` toggle + idle-days slider) so contraband-only / dried-up ports stop stranding the fleet.
6. Documented that native routing is single-destination, so true "cargo on the way" multi-stop is deliberately not implemented (see the contract-pickup note below).

Build verified with `scripts\build-direct.ps1` (csc v3.5, references only `UnityEngine.dll` — keep the code C# 3.5-compatible: no LINQ, no optional/named args, no `var`-only patterns beyond what's already there). Not yet live-tested in-game as of this write-up.

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
- `Repair threshold (minimum condition to sail)` defaults to 85% and is now adjustable across the full **1%–99%** range.
- `Repair target condition` defaults to 100% (slider floor follows the repair threshold).
- `Allow contraband` defaults off.
- `Move idle ships to work` (idle repositioning) defaults on, with an idle-days slider defaulting to **7 in-game days** (range 1–30).

### Panel UI notes (July 2026 update)

- The panel now paints a near-opaque backing texture (`GetPanelBackgroundTexture`, alpha ~0.97) so the game world no longer bleeds through it.
- `BlockClickThrough(panelRect)` consumes `MouseDown/MouseUp/MouseDrag/ScrollWheel` events that land inside the panel so clicks/scrolls do not fall through to the game view. This only stops IMGUI-level event propagation; it is best-effort against any uGUI/`EventSystem` raycasts the base game runs. If click-through is still observed on some controls, the next step is a full-screen invisible `GUI.Box` behind the panel or a native input-lock hook.
- The old `Enable all` / `Disable all` buttons are replaced by a single **`Auto-pilot all: ON/OFF`** toggle. Its label reflects `AreAllVisibleShipsEnabled()`, and flipping it calls `SetAllVisibleShips(...)` then `RefreshFleet(true)` so the ship rows immediately reflect the change.

Ships below the sail threshold are held for maintenance, routed to safe reachable repair docks, or repaired when already in a repair-capable harbor. Repair routing derives the native sink-risk threshold by probing `Cargo.ShipFactory.GetShipSinkChance(playerID, condition)` and requires projected arrival condition to be at least 10% above that threshold.

Scheduled automation uses a 30-second real-time base interval divided by `Deck13HH.Timer.CurrentInGameSpeed`, clamped to a 2-second minimum at the fastest speed.

### Idle repositioning (fixes the stalled-fleet / contraband problem)

Previously, an enabled ship with no legal work at its harbor (e.g. a port whose only exports were contraband while `Allow contraband` was off) would log `no matching legal available jobs found` and sit forever. The July 2026 run showed most of the fleet stuck this way. Contraband is still just a filter — `Allow contraband` enables/disables it — but the filter no longer strands ships, because idle ships now move on:

- When an enabled, in-harbor ship finds no legal job plan, the mod starts a per-ship idle timer keyed by `(playerShipId, currentHarbor)` using the in-game clock (`Deck13HH.Timer.CurrentDateTime`). Changing ports resets the timer.
- Once the ship has been idle at that harbor for at least `idleRepositionDays` (default 7), `TryRepositionIdleShip` picks a destination via `FindBestRepositionHarbor`:
  - `StaticSQLiteController.GetHarborsFrom(currentHarbor, 0, float.MaxValue, shipClass)` yields class-reachable harbors.
  - Candidates are sorted by `GetHarborDistance`, then the nearest ones (capped at `IdleRepositionMaxHarborsToProbe = 10`) are probed with `FindBestJobPlanFromHarbor` to confirm they actually have legal work. Arrival condition must keep `GetShipSinkChance` at 0.
  - The ship deadheads there via `TryDeadheadToHarbor` (native `ShipFactory.SendShipToDestination`) and loads real jobs on arrival at the next tick.
- The probe count is bounded so one idle ship cannot fan job queries across the whole map. Repositioning is gated behind the `Move idle ships to work` toggle.

### Contract pickup at ports (same-destination and onward work)

Re-evaluation already happens each time a ship is idle in a harbor (scheduled tick, F8, or the per-ship Plan button), so ships take on fresh contracts every time they dock. Same-destination jobs are bundled into one native route (`BuildJobPlanForDestination`), and the one-hop chain bonus steers the chosen first leg toward harbors that have strong follow-on bundles (see "chain scoring" note below).

Note on "cargo that is on the way": the native routing model is **single-destination** — a ship dispatched to harbor B cannot drop cargo at an intermediate harbor A en route. So true multi-stop / short-leg unloading is not implemented; doing it safely would require driving the native waypoint system (upgrade ID 5, `HasWaypointUpgrade`) and per-leg cargo assignment, which risks corrupting native route state. Until that is proven safe, "on the way" is approximated by (a) bundling everything bound for the same destination and (b) chain-scoring toward job-rich onward harbors, then re-planning on arrival.

Repair and upgrade repositioning tries to pay for itself: before sending a ship to the chosen service port, the mod looks for legal same-destination jobs ending at that repair/upgrade harbor. If matching cargo fits, it dispatches with those jobs attached; otherwise it deadheads to the service port.

Cargo automation is intended to:

1. Find available jobs from the current harbor.
2. Exclude contraband freight by default using the game's `StaticTableFreightAttributes.Smugglersware`.
3. Rank jobs by pay per day, relationship points, and penalty risk.
4. Bundle same-destination jobs that fit.
5. Add a discounted one-hop chain opportunity bonus for destinations whose arrival harbor has another strong legal bundle available.
6. Attach immediate-destination jobs directly via `DynamicSQLiteController.UpdatePlayerShipIDFromJob`.
7. Set `DontLoadJobs` so native AI does not add blocked cargo.
8. Keep native `IsAI` off, write `DestinationHarbor`, and dispatch with `ShipFactory.SendShipToDestination` when available.
9. Use the native cast-out event only as a fallback if the direct movement bridge is unavailable.

After any dispatch or service-dock routing, the mod applies a short per-ship settle cooldown before it will issue another action for that ship. This is meant to avoid double-dispatching while the native database is still updating harbor and destination fields.

If an enabled ship disappears from `GetALLPlayerShips`, the next active fleet refresh logs `enabled ship is missing from the native player ship list`. The July 2026 short run showed `#1976 Skaald` drop out this way after native logs said `GetPlayerIDFromPlayerShip - PlayerShip in playerShips database not found. PlayerShipID: 1976`; an earlier run showed the same signature for `#3201 Lorken`.

The chain scoring is intentionally advisory. It does not accept future-harbor jobs early and it does not mix cargo for multiple destinations into one native route. It only nudges the first destination toward harbors with better follow-on work, then lets the next automation tick re-evaluate after arrival.

Upgrade automation is now experimental but active behind the panel's `Auto upgrades` toggle.

- Repairs still have priority.
- Upgrade work is only considered for idle enabled ships that are at the fleet's current lowest installed-upgrade count.
- This low-water-mark rule prevents one ship in a large fleet from consuming all upgrade turns while other enabled ships have fewer upgrades.
- If the ship is already at a real upgrade dock and an affordable candidate exists, the mod calls the native `ShipFactory.UpgradePlayerShip(...)`.
- If it needs to travel, the mod chooses a reachable safe upgrade dock with an affordable candidate and tries to carry legal cargo to that exact dock first.
- If no upgrades are left, the ship is above the fleet upgrade low-water mark, the fleet cushion has not been reached, or spendable treasury above that cushion is too low, the ship falls through to normal contract automation.
- Upgrade reserve (`GetUpgradeTreasuryCushion`) is now `max(20,000,000 * visible ship count, 200,000,000)` — that is, **20M per ship for a small company, or a 200M total floor, whichever is higher**. Until treasury is above that reserve, automation only repairs and hauls cargo.
- Upgrade selection prefers freight-specific revenue upgrades when harbor/export/job data supports them, then fuel consumption, speed, waypoint, repair-duration, tug-fee, and range upgrades.

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

Auto-upgrade automation uses the native upgrade bridge, but should still be treated as experimental until more live runs confirm the economy behavior.

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

Current policy:

- `Auto upgrades` defaults on for testing and can be toggled in the panel.
- Only considers enabled ships that are idle, above maintenance threshold, not repairing/upgrading, and not ahead of the enabled fleet's minimum upgrade count.
- Uses `GetUpgradeDockOwner(region)` plus the native `UpgradePlayerShip` path rather than direct database writes.
- Logs the chosen upgrade ID, slot, and estimated base price when spending credits.

## Useful Logs

Primary Unity log:

```text
C:\Program Files (x86)\Steam\steamapps\common\TransOcean2\TransOcean2_Data\output_log.txt
```

Useful search pattern:

```powershell
Select-String -Path 'C:\Program Files (x86)\Steam\steamapps\common\TransOcean2\TransOcean2_Data\output_log.txt' -Pattern '\[TO2FA\.Direct\]|best route|loaded .*job|missing from the native player ship list|playerShips database not found|no matching|direct dispatch|Exception|Error|failed' -CaseSensitive:$false
```

## Safety Notes

- Do not use BepInEx for normal play yet; it caused Unity serialization/missing-script warnings in prior tests.
- Do not overwrite the managed DLL while the game is running.
- Avoid multiplayer testing while the direct patch is installed.
- Be careful with the user's dirty worktree. Do not revert user changes.
