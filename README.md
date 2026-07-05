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
- Logs a heartbeat every 30 seconds.
- Press `F8` in-game for a smoke-test probe log.

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

The current direct mod only logs and does not write game state. Avoid multiplayer testing while any loader, plugin, or direct patch is installed.
