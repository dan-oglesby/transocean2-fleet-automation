# TransOcean 2 Fleet Automation

Experimental live modding work for TransOcean 2 fleet automation.

## Current status

The direct managed patch path is the current live-mod track. A smoke test patched `Cargo.InitGameStart.Awake()` in `Assembly-CSharp.dll` to call `TransOcean2FleetAutomation.Direct.Loader.Bootstrap()`. The game launched through Steam, stayed running, and logged the direct mod bootstrap without the BepInEx serialization-layout warnings.

The BepInEx loader path is not safe to leave enabled yet. In local smoke tests, BepInEx 5.4.21.0 and 5.4.23.5 both injected successfully, but Unity then reported many missing-script and serialization-layout warnings while loading TransOcean 2 UI scenes. With Doorstop disabled, the game launches cleanly through Steam.

Keep `doorstop_config.ini` set to `enabled = false` for normal play until the loader compatibility issue is solved.

## Direct patch behavior

- Builds `TransOcean2FleetAutomation.Direct.dll` without any BepInEx runtime dependency.
- Copies the direct mod DLL to `TransOcean2_Data/Managed/`.
- Backs up the original game assembly as `Assembly-CSharp.dll.to2fa-original`.
- Injects a single call into `Cargo.InitGameStart.Awake()`.
- Attaches a live Unity `MonoBehaviour`.
- Adds the first in-game TO2 Fleet Captain panel.
- Keeps the panel hidden until an active player fleet is loaded.
- Supports ship-by-ship opt-in toggles.
- Evaluates enabled ships on a 30-second base interval scaled by the game's current speed.
- Live actions keep opted-in player ships out of the game's native `IsAI` job loader and dispatch idle ships from harbor through controlled direct moves.
- Cargo automation defaults to legal cargo only. Freights marked `Smugglersware` by the game's freight attributes are excluded unless `Allow contraband` is enabled in the panel.
- Direct dispatch attaches the selected jobs itself, blocks the native AI loader from adding extra cargo, sets the destination explicitly, and then uses the built-in movement path.
- When multiple legal jobs fit for the same destination, the planner can load them together as same-port easy wins.
- Route scoring also looks one harbor ahead: destinations get a discounted follow-on opportunity bonus when the arrival harbor has another strong legal job bundle available for the same ship type.
- The chain bonus affects which first-leg route is chosen; the mod still only accepts and loads the immediate destination's jobs, then re-evaluates after arrival.
- Holds enabled ships below the configured repair threshold (minimum condition to sail) instead of sending them back out for normal jobs. That threshold is adjustable across the full 1%–99% range.
- A single `Auto-pilot all + new` toggle enables/disables TO2 Captain mode for every visible ship at once and shows its current on/off state; toggling it refreshes the ship list immediately. While it is on, ships added to the fleet later are automatically enrolled too (unless you had explicitly turned a specific ship off).
- The panel paints a near-opaque background and swallows mouse clicks/scrolls that land on it so they do not fall through to the game world.
- If an enabled ship is idle in a harbor with no legal work for a configurable number of in-game days (default 7), it repositions to the nearest class-reachable harbor that has legal work rather than waiting indefinitely. This keeps contraband-only or dried-up ports from stranding the fleet; contraband remains a simple on/off filter via `Allow contraband`.
- Auto repairs can send low-condition ships to the nearest safe reachable repair dock, requiring arrival condition to stay at least 10% above the native sink-risk threshold.
- Repair and upgrade routing first checks for legal same-destination cargo to the service port and carries it if it fits, offsetting otherwise-empty repositioning trips.
- Maintenance routing runs before the normal idle-in-harbor check, so just-arrived ships with transitional statuses still get an active repair decision when they have a real current harbor.
- Auto upgrades can route idle ships to real upgrade docks and start the game's native upgrade flow only after treasury is above the fleet cushion, which is the lesser of 10,000,000 per visible ship or a 50,000,000 total cap.
- Upgrade turns are balanced across enabled ships by installed-upgrade count; ships above the fleet's current low-water mark keep running contracts until the rest catch up.
- Scores available jobs from each idle ship's current harbor using payment per travel day, relationship points, and penalty risk.
- Uses a simple treasury reserve check before starting automated repairs.
- Press `F8` in-game to run enabled ships.
- Press `F9` in-game to show or hide the panel.
- Press `F10` in-game to refresh the visible fleet list.

## Direct patch install

Close the game, keep Doorstop disabled, then run:

```powershell
.\scripts\install-direct-patch.ps1
```

Restore the original game assembly with:

```powershell
.\scripts\restore-direct-patch.ps1
```

## BepInEx probe behavior

- Loads as a BepInEx 5 plugin.
- Avoids compile-time references to TransOcean 2 game assemblies; it waits and reflects over game types after Unity loads them.
- Waits for TransOcean 2 runtime controllers.
- Logs visible ships and their automation fields.
- Press `F8` in-game to dump fleet state again.

## Local paths used during development

- Game root: `C:\Program Files (x86)\Steam\steamapps\common\TransOcean2`
- Source root: this repository, intentionally outside `C:\Users\daogl\Proton Drive`

## BepInEx probe build

Install BepInEx 5 x64 into the game root first, then run:

```powershell
.\scripts\build.ps1
.\scripts\install-plugin.ps1
```

The compiled DLL is written to `dist/` and copied to `BepInEx/plugins/`.

## Recovery

If the game will not start after installing BepInEx, disable the loader without deleting files:

```powershell
.\scripts\disable-loader.ps1
```

Re-enable it for a test run with:

```powershell
.\scripts\enable-loader.ps1
```

Only enable the loader for controlled smoke tests right now.

## Safety notes

The current direct mod can perform live actions when the panel's `Live actions` toggle is on. It keeps the game's native `IsAI` job loader disabled for enabled ships and uses direct destination routing, the ship movement bridge, repair methods, and upgrade methods, so enabled ships may accept work, depart, refuel, repair, upgrade, and spend credits. Normal cargo dispatch is not handed fully to the native job loader: the mod chooses jobs, excludes contraband by default, attaches same-destination jobs that fit, marks the ship as `DontLoadJobs`, writes the target destination, and then moves the ship. A short post-dispatch settle window prevents the same ship from being re-dispatched while native state catches up. The mod adds an extra minimum condition-to-sail gate, defaulting to 85%, before it triggers a normal job departure. Below that gate, auto repairs run before normal idle-state checks: ships in a real current harbor can be routed to a repair dock when the native sink-risk curve says arrival condition will stay at least 10% above the danger threshold, then repaired up to the configured repair target when treasury reserves allow. Ships underway are held until they reach a harbor. Turning `Live actions` off restores manual AI state for currently enabled ships. Avoid multiplayer testing while any loader, plugin, or direct patch is installed.

Auto-upgrade automation is experimental. It uses the native upgrade bridge, keeps a fleet cushion untouched (the lesser of 10,000,000 credits per visible ship or a 50,000,000 total cap), and only starts one upgrade per shipyard visit. Until that cushion is reached, ships may still repair and haul contracts, but they will not start or route toward upgrades. If no affordable upgrade is available above the cushion, or if another enabled ship has fewer installed upgrades, the ship continues normal contract automation.
