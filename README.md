# TransOcean 2 Fleet Automation Probe

Experimental BepInEx modding probe for TransOcean 2. The current plugin is read-only: it logs the game's fleet and route automation state so we can verify that a safe modding path exists before adding behavior.

## Current behavior

- Loads as a BepInEx 5 plugin.
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

## Safety notes

This probe does not write game state. Avoid multiplayer testing while any loader or plugin is installed.
