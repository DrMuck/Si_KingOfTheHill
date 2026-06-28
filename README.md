# Si_KingOfTheHill (KGT)

> **King of the Galactic Teleport** — a capture-the-hill game-mode layer for [Silica](https://store.steampowered.com/app/504900/Silica/).

A neutral Galactic Teleporter (Ultra Heavy Factory) spawns at a map-configured location at round start. Whichever team holds enough weighted unit-cap inside the surrounding cylinder for long enough wins the round — even without destroying the enemy HQ. A live outpost ring around the capture zone fills in the leading team's faction colour as progress accumulates.

Bundles a per-player **credit economy** (`/buy` menu, kill rewards, zone-presence ticks, faction-treasury commander rewards) and an anti-snowball **handicap multiplier**.

## Features

- **Capture mechanic** — weighted unit-cap dominance, dominance-threshold smoothing, per-tick accumulation toward a configurable win threshold (per-map override supported)
- **Outpost ring** — 12 outposts at the capture perimeter visualize progress, incremental update keeps network cost low even on fast dominance changes
- **In-round purchase menu** (`/buy`) — players spend earned credits to spawn units ahead of their current position
- **Multi-source credit economy** — starter credits + kill rewards + zone presence + per-faction commander treasury rewards
- **Headhunter bounties** — admins maintain a wanted list; killing a bounty target pays the killer a bonus
- **Three operating modes** — Full KoH (default), BuyOnly (credits + `/buy` without the capture game), Disabled
- **KoH building damage immunity** — Harmony-patched across every known damage path, with an auto-respawn watchdog if a player slips through
- **AntiBuild zone** — refuses player constructions inside the exclusion radius
- **TTS voice intro** — split, network-friendly clips that survive AdminMod's UInt16 packet-offset wrap

## Installation

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader) on your Silica dedicated server (`MelonLoader 0.7.x`, net472 references — see notes)
2. Install the [Silica Admin Mod](https://github.com/data-bomb/Silica) (required dependency)
3. Install [Si_MapBalance](https://github.com/DrMuck/Silica-MapBalance) (required — provides the per-map KoH spawn data)
4. Recommended companion: [Si_UnitBalance](https://github.com/DrMuck/Silica-UnitBalance) (so the `/buy` prices and `/stats` inspector see the live modded numbers)
5. Copy `Si_KingOfTheHill.dll` to your server's `Mods/` folder
6. (Optional) Copy intro/milestone/win wavs to `UserData/sounds/` — see the [sound library](https://github.com/DrMuck/Si_AddOn_Packages) in the add-on packages release

On first start the mod auto-creates `UserData/KingOfTheHill_cfg/Si_KingOfTheHill_Config.json` and `UserData/KingOfTheHill_cfg/Bounties.json` with sensible defaults.

## Operating modes

Set via `Cfg.Mode` in JSON or live with `/koh mode N`:

| Mode | Intro / capture / immunity | AntiBuild | `/buy` | Kill rewards | Commander reward | Starter credits |
|--|:--:|:--:|:--:|:--:|:--:|:--:|
| **1 Full** (default) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **2 BuyOnly** | — | — | ✅ | ✅ | — | ✅ |
| **3 Disabled** | — | — | — | — | — | — |

In Mode 2, KGT also tells `Si_MapBalance` not to prefer `koh` layouts (via reflect-call on `KingOfTheHill.ModeKohActive`), so the map picker skips KoH spawn JSONs.

## Admin chat commands

All `/koh` subcommands gate on SilicaAdminMod's `CanAdminExecute(Power.Generic)`.

### Core configuration

| Command | Effect |
|--|--|
| `/koh on` / `/koh off` | Master toggle (`Cfg.Enabled`) |
| `/koh mode <1\|2\|3>` | Switch operating mode (takes full effect next round) |
| `/koh starter <N>` | `StarterCreditsPerPlayer` — credits granted to each player at round start |
| `/koh radius <m>` | Capture-zone radius (fallback only — usually defined by MapBalance per-map) |
| `/koh threshold <pts>` | Win threshold (points to win) |
| `/koh rate <pts/s>` | `MaxCapturePointsPerSecond` cap |
| `/koh reset` | Clear king + accumulation + milestones for the current round |
| `/koh status` (or just `/koh`) | Full status — mode, radii, current king, last tick gain, per-team handicap multipliers (also available to non-admins) |

### Headhunter bounties

Lives in `UserData/KingOfTheHill_cfg/Bounties.json`. Edit by hand or via these commands:

| Command | Effect |
|--|--|
| `/koh bounty` (or `list`) | Show all active bounties + the master ON/OFF state |
| `/koh bounty on` / `/koh bounty off` | Master switch for the bounty subsystem |
| `/koh bounty reload` | Re-read `Bounties.json` from disk (no server restart needed) |
| `/koh bounty add <steamId> <amount> [name]` | Add or overwrite an entry; saves the JSON |
| `/koh bounty remove <steamId>` | Delete an entry; saves the JSON |

When a bounty target connects, the server broadcasts: *"HEADHUNTER: 1000cr bonus to anyone who kills Talion!"* — once per session per target. Killing a bounty target pays the bonus to the killer on top of the normal kill reward.

## Player commands

These work for any connected player — no admin role required.

| Command | Effect |
|--|--|
| `/koh` (or `/koh status`) | Live capture status: current king, progress %, last tick gain, per-team handicap multipliers |
| `/buy` | Open the in-round unit-purchase menu. Categories listed at the top level; pick with `/1`-`/N`, drill in, then pick a unit |
| `/buy <N>` | Pick category or unit N directly |
| `/1`-`/N` | Navigate the currently-open `/buy` menu |
| `/back` | Up one level in `/buy` |
| `/0` | Close the `/buy` menu |

Purchased units spawn ~15m in front of you at ground level. Refused if too close to an enemy critical (`Cfg.BuyMinDistanceFromEnemyCritical`).

### How players earn credits

- **Starter credits** — flat grant once per round when you connect (server-configured)
- **Kill rewards** — percentage of victim unit/structure cost goes to the killer
- **Zone presence** — per-second tick proportional to unit cap value while one of your units is inside the capture zone (Mode 1 only)
- **Headhunter bounty** — bonus on top of the kill reward when you kill someone on the bounty list

## Configuration

`UserData/KingOfTheHill_cfg/Si_KingOfTheHill_Config.json` — the main config. A `Si_KingOfTheHill_Config.example.json` is shipped with this repo as a reference.

`UserData/KingOfTheHill_cfg/Bounties.json` — the bounty list. Auto-created with sample entries on first run.

`Cfg.WinThresholdPerMap` (dictionary, scene name → points) lets you scale game length to map size.

`Cfg.HandicapEnabled`, `Cfg.HandicapStrength`, `Cfg.HandicapFloorMult`, `Cfg.HandicapCeilingMult` control the anti-snowball commander-reward handicap.

## How it works

KGT does not spawn the capture point itself. **[Si_MapBalance](https://github.com/DrMuck/Silica-MapBalance) owns the spawn**: at round start, if KGT is loaded and in Mode 1, MapBalance prefers a layout JSON that carries a `koh` block (`UserData/Spawns/<MapName>/koh_*.json`), spawns the configured prefab on the configured team at the configured position, then fires its `OnSpecialsReady` event. KGT catches that event, grabs the spawned GameObject, builds the outpost ring, arms damage immunity, and starts ticking.

### Capture math (per tick, default `EvalIntervalSeconds = 1.0`)

```
for each unit in Unit.Units:
  if inside zone (2D cylinder around _kohCenter):
    weight = UnitCapValue × CategoryMultiplier (or PerUnitOverride if present)
    if player-controlled: weight ×= PlayerControlledMultiplier
    scores[team] += weight

dominator = team with scores[team] / total_in_zone ≥ DominanceThreshold (default 0.60)
king_progress += min(scores[king], MaxCapturePointsPerSecond × dt)
```

After sustained dominance for `OwnershipHandoverDelay` seconds, the candidate becomes king. First team to push `king_progress` to the win threshold wins.

### Commander handicap

```
score(team) = TotalResources × resW + Units.Count × 100 × milW
ratio       = score(team) / mean(scores)
mult        = clamp(ratio^(-strength), floor, ceiling)
```

The dominant team's commander reward shrinks, the trailing team's grows. Defaults: `floor 0.25` / `ceiling 3.0` / `strength 1.0` → 2× ahead = 0.5× reward, 0.5× behind = 2× reward.

## Building from source

```bash
cd Si_KingOfTheHill
dotnet build -c Release
```

Targets `netstandard2.1`. Reference DLLs in `include/netstandard2.1/`:
- `SilicaCore.dll`, `MelonLoader.dll`, `Si_AdminMod.dll`, `0Harmony.dll`, `Newtonsoft.Json.dll`
- `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.PhysicsModule.dll`, `UnityEngine.TerrainModule.dll`

Build output: `Si_KingOfTheHill/bin/Release/netstandard2.1/Si_KingOfTheHill.dll`.

## See also

- [ADMIN_MANUAL.md / ADMIN_MANUAL.pdf](ADMIN_MANUAL.md) — full cross-mod admin reference (all DrMuck mods)
- [PLAYER_MANUAL.md / PLAYER_MANUAL.pdf](PLAYER_MANUAL.md) — player-facing chat command reference
- [DESIGN.md](DESIGN.md) — internal design notes

## License

GPL-3.0 (matches Silica modding ecosystem).
