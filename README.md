# Si_KingOfTheHill (KGT)

> **King of the Galactic Teleport** — a capture-the-hill game-mode layer for [Silica](https://store.steampowered.com/app/504900/Silica/).

A neutral Galactic Teleporter (Ultra Heavy Factory) spawns at a map-configured location at round start. Whichever team holds enough weighted unit-cap inside the surrounding cylinder for long enough wins the round — even without destroying the enemy HQ. A live outpost ring around the capture zone fills in the leading team's faction colour as progress accumulates.

Rewards the commander's **faction treasury** per tick (with an anti-snowball **handicap multiplier**) and pays players a **zone-presence credit** into [Si_Buy](https://github.com/DrMuck/Si_Buy)'s economy when that mod is installed. Since v0.5.0 the `/buy` menu, kill rewards, starter credits and headhunter bounties live in Si_Buy.

## Features

- **Capture mechanic** — weighted unit-cap dominance, dominance-threshold smoothing, per-tick accumulation toward a configurable win threshold (per-map override supported)
- **Outpost ring** — 12 outposts at the capture perimeter visualize progress, incremental update keeps network cost low even on fast dominance changes
- **Commander reward** — per-tick faction-treasury income for every team with units in the zone, scaled by the anti-snowball handicap
- **Zone-presence credits** — players controlling units in the zone earn personal credits, paid through [Si_Buy](https://github.com/DrMuck/Si_Buy) (optional)
- **KoH building damage immunity** — Harmony-patched across every known damage path, with an auto-respawn watchdog if a player slips through
- **AntiBuild zone** — refuses player constructions inside the exclusion radius
- **TTS voice intro** — split, network-friendly clips that survive AdminMod's UInt16 packet-offset wrap

## Installation

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader) on your Silica dedicated server (`MelonLoader 0.7.x`, net472 references — see notes)
2. Install the [Silica Admin Mod](https://github.com/data-bomb/Silica) (required dependency)
3. Install [Si_MapBalance](https://github.com/DrMuck/Silica-MapBalance) (required — provides the per-map KoH spawn data)
4. Recommended companion: [Si_Buy](https://github.com/DrMuck/Si_Buy) (the `/buy` economy; KGT pays zone-presence credits into it) and [Si_UnitBalance](https://github.com/DrMuck/Silica-UnitBalance) (the unit dump KGT reads for category weights)
5. Copy `Si_KingOfTheHill.dll` to your server's `Mods/` folder
6. (Optional) Copy intro/milestone/win wavs to `UserData/sounds/` — see the [sound library](https://github.com/DrMuck/Si_AddOn_Packages) in the add-on packages release

On first start the mod auto-creates `UserData/cfg/KingOfTheHill/Si_KingOfTheHill_Config.json` with sensible defaults.

## Credit economy (Si_Buy)

The economy that used to be part of this mod is [Si_Buy](https://github.com/DrMuck/Si_Buy). KGT links to it at runtime by
reflection (`BuyBridge.cs`, type `Si_Buy.Buy`) and pays `round(EvalIntervalSeconds × unit_cap ×
RewardZonePresenceMultiplier)` credits per tick to every player controlling a unit inside the zone.
Without Si_Buy the capture game runs unchanged and that tick is a no-op.

For consumers that still resolve the old location, `Si_KingOfTheHill.KingOfTheHill` keeps
`GrantExternalCredits`, `GetCreditsBySteamId`, `GetCredits` and `ModeBuyActive` as forwarders to
Si_Buy. New code should call `Si_Buy.Buy` directly.

## Enabling

`Cfg.Enabled` (or `/koh on` / `/koh off`) switches the whole KoH game. While off, KGT also tells
`Si_MapBalance` not to prefer `koh` layouts (reflect-call on `KingOfTheHill.ModeKohActive`), so the
map picker skips KoH spawn JSONs. Pre-0.5.0 configs with `Mode` 2 (BuyOnly) or 3 (Disabled) load as
`Enabled=false`; the economy half of the old Mode 2 is Si_Buy's own `Enabled` switch now.

## Admin chat commands

All `/koh` subcommands gate on SilicaAdminMod's `CanAdminExecute(Power.Generic)`.

### Core configuration

| Command | Effect |
|--|--|
| `/koh on` / `/koh off` | Master toggle (`Cfg.Enabled`) |
| `/koh enabled` + `on` or `off` | Same toggle as one sub-command (used by the Si_ModUI switch; without argument prints the state) |
| `/koh radius <m>` | Capture-zone radius (fallback only — usually defined by MapBalance per-map) |
| `/koh threshold <pts>` | Win threshold (points to win) |
| `/koh rate <pts/s>` | `MaxCapturePointsPerSecond` cap |
| `/koh reset` | Clear king + accumulation + milestones for the current round |
| `/koh status` (or just `/koh`) | Full status — on/off, Si_Buy link, radii, current king, last tick gain, per-team handicap multipliers (also available to non-admins) |

## Player commands

These work for any connected player — no admin role required.

| Command | Effect |
|--|--|
| `/koh` (or `/koh status`) | Live capture status: current king, progress %, last tick gain, per-team handicap multipliers |

`/buy`, kill rewards, starter credits and bounties: see [Si_Buy](https://github.com/DrMuck/Si_Buy). KGT's contribution is the
**zone-presence** credit — a per-second tick proportional to unit cap value while one of your units is
inside the capture zone.

## Configuration

`UserData/cfg/KingOfTheHill/Si_KingOfTheHill_Config.json` — the main config. A `Si_KingOfTheHill_Config.example.json` is shipped with this repo as a reference.

`Cfg.WinThresholdPerMap` (dictionary, scene name → points) lets you scale game length to map size.

`Cfg.HandicapEnabled`, `Cfg.HandicapStrength`, `Cfg.HandicapFloorMult`, `Cfg.HandicapCeilingMult` control the anti-snowball commander-reward handicap.

## How it works

KGT does not spawn the capture point itself. **[Si_MapBalance](https://github.com/DrMuck/Silica-MapBalance) owns the spawn**: at round start, if KGT is loaded and enabled, MapBalance prefers a layout JSON that carries a `koh` block (`UserData/cfg/Spawns/<MapName>/koh_*.json`), spawns the configured prefab on the configured team at the configured position, then fires its `OnSpecialsReady` event. KGT catches that event, grabs the spawned GameObject, builds the outpost ring, arms damage immunity, and starts ticking.

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
