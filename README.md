# TransOcean 2 Fleet Automation Probe

Experimental BepInEx modding probe for TransOcean 2. The current plugin is read-only: it logs the game's fleet and route automation state so we can verify that a safe modding path exists before adding behavior.

## Current status

The probe DLL builds, but the BepInEx loader path is not safe to leave enabled yet. In local smoke tests, BepInEx 5.4.21.0 and 5.4.23.5 both injected successfully, but Unity then reported many missing-script and serialization-layout warnings while loading TransOcean 2 UI scenes. With Doorstop disabled, the game launches cleanly through Steam.

Keep `doorstop_config.ini` set to `enabled = false` for normal play until the loader compatibility issue is solved.

## Current behavior

- Loads as a BepInEx 5 plugin.
- Avoids compile-time references to TransOcean 2 game assemblies; it waits and reflects over game types after Unity loads them.
- Waits for TransOcean 2 runtime controllers.
- Logs visible ships and their automation fields.
- Press `F8` in-game to dump fleet state again.

## Local paths used during development

- Game root: `C:\Program Files (x86)\Steam\steamapps\common\TransOcean2`
- Source root: this repository, intentionally outside `C:\Users\daogl\Proton Drive`

## Build

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

This probe does not write game state. Avoid multiplayer testing while any loader or plugin is installed.
