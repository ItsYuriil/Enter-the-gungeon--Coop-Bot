# Enter the Gungeon – Co-op Bot

A BepInEx mod that adds an AI-controlled second player to *Enter the Gungeon*. The bot follows you, fights, dodges, and does what you order with hotkeys. It uses the game's own co-op systems (second player, ghost/revive, co-op camera and UI).

## Features

- **Summon / dismiss** a bot in any run; choose which character it plays (Cultist, Pilot, Convict, Marine, Hunter, Robot, Bullet, Paradox, Gunslinger, ...).
- **Combat:** targets and shoots enemies with lead, slight human-like aim error, reloads smartly, handles charge weapons, drops empty weapons, can use blanks and active items.
- **Dodging:** predicts incoming projectiles and weaves or rolls (with varied reaction times); avoids pits, harmful goop/fire and telegraphed danger zones (e.g. sky rockets).
- **Cover:** takes cover when under fire, reloading or hurt, and sometimes by preference. It flips nearby tables to make cover and peeks out from behind it to shoot, then hides again. A higher CoverPreference hides longer and peeks shorter.
- **Orders (H):** open chests, interact with objects, pick up guns, smash breakables, regroup, or kill the enemy under the cursor.
- **Co-op rules:** works with doors and elevators, becomes a ghost when killed (blocks bullets until revived), camera follows the bot if you are down.
- **Save / continue:** the bot is part of the run's save. Quit mid-run, come back with *Continue* and the bot is back with its character and gear. A bot that is dismissed before the next save is simply not in that save.
- **Gear persistence:** a dismissed bot's loadout (guns, ammo, items, health) is written to the save folder, so it survives quitting the game and is restored when summoned again. It is cleared when a new run starts.
- **Weapon list (Y):** the bot keeps every weapon it picks up, up to the inventory limit, and you choose which one it holds. It only drops its current weapon to pick a new one when the inventory is full. When its weapon runs dry, it switches to a loaded one before dropping anything.
- **Trading:** stand next to the bot and press **T** to swap active items or **G** to swap your equipped weapon. Starting weapons and passive items never move.
- If you die outside a boss room, the run ends as in single player.
- **Pause-menu "Coop Bot Menu"** to rebind keys and set cover preference.

## Requirements

- *Enter the Gungeon* (Steam, Windows, 64-bit)
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (x86/x64 matching the game) installed in the game folder
- The **Mod the Gungeon** API / loader for BepInEx
- To build from source: .NET SDK (any recent version; targets `net35` via reference assemblies)

## Install

1. Install BepInEx and Mod the Gungeon into the game folder and run the game once.
2. Copy `CoopBot.dll` to `<game>\BepInEx\plugins\CoopBot\`.
3. Start a run and press **H**.

## Build from source

```
dotnet build -c Release -p:GamePath="C:\path\to\Enter the Gungeon"
```

The csproj references the game's `Assembly-CSharp.dll`, Unity and BepInEx/Harmony DLLs from `GamePath` (default `G:\SteamLibrary\steamapps\common\Enter the Gungeon`) and copies the result to `BepInEx\plugins\CoopBot\` automatically. Close the game before building (the DLL is locked while it runs).

## Default controls (rebindable in the pause menu or the BepInEx config)

| Key | Action |
|-----|--------|
| H | Summon the bot; afterwards order it (interact / pick up / smash / attack the enemy under the cursor / regroup) |
| Shift+H | Dismiss the bot (saves its loadout) |
| J | Cycle bot character (only while the bot is not active) |
| N | Bot drops its equipped weapon |
| U | Bot uses its active item |
| B | Bot drops its active item |
| T | Trade: swap active items with the bot (stand within 3.5 tiles) |
| X | Trade: swap your equipped weapon with the bot's (stand within 3.5 tiles) |
| Y | Open the bot's weapon list (the screen dims and the bot's weapon HUD stays lit); click a weapon to hold it. The player cannot shoot while it is open |

Config file: `BepInEx\config\` (Controls, Character, CoverPreference, ShowHints).

Save files: the bot's run record is `coopbot_run_<guid>.json` and its dismissed loadout is `coopbot_loadout.json`, both in the game's save folder (`%USERPROFILE%\AppData\LocalLow\Dodge Roll\Enter the Gungeon`).

## Notes

- Not affiliated with Dodge Roll or Devolver Digital. No game files are included in this repository.
- Behaviour is tuned by playtesting; expect rough edges. Bug reports with `BepInEx\LogOutput.log` are welcome.

## License

Copyright (c) 2026 ItsYuriil. All rights reserved.

This code and its source are not open source. No one may use, copy, modify, merge, publish, distribute, sublicense, sell, or create derivative works from this mod, its code, or its source without the express written authorization of the author, ItsYuriil.
