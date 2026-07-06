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
- Evaluates enabled ships every 30 seconds.
- Live actions can flip opted-in player ships into the game's native `IsAI` ship state and trigger the built-in AI cast-out path when a ship is idle in harbor.
- Holds enabled ships below the configured minimum condition-to-sail threshold instead of sending them back out.
- Scores available jobs from each idle ship's current harbor using payment per travel day, relationship points, and penalty risk.
- Flags low-condition ships for repair consideration using a simple treasury reserve check.
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

The current direct mod can perform live actions when the panel's `Live actions` toggle is on. It uses the game's own player-ship AI state and ship cast-out event path, so enabled ships may accept work, depart, refuel, repair, upgrade, and spend credits according to the base-game AI logic. The mod adds an extra minimum condition-to-sail gate, defaulting to 85%, before it triggers a departure. Turning `Live actions` off restores manual AI state for currently enabled ships. Avoid multiplayer testing while any loader, plugin, or direct patch is installed.
