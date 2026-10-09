# Silica Server — Admin Chat Manual

Covers the four DrMuck mods loaded on this server:

| Mod | Chat prefix |
|--|--|
| [Si_CrabCannon](#si_crabcannon) | `/cc` |
| [Si_HeavyTeleporter](#si_heavyteleporter) | `/st` |
| [Si_UnitBalance + UI](#si_unitbalance) | `!rebalance`, `!b`, `/stats` |
| [Si_KingOfTheHill](#si_kingofthehill-kgt) | `/koh`, `/buy` |

All "admin" commands gate on **SilicaAdminMod's `CanAdminExecute(Power.Generic)`** — you need at least the `Generic` power in `admins.json`. Player commands work for everyone.

Most commands save back to their JSON config in `UserData/<ModName>_cfg/` automatically.

---

## Player commands quick reference

Everything in this section works for **any connected player** — no admin powers required. Type these into normal chat; commands with a `/` prefix are hidden from other players (handled as player commands by SilicaAdminMod).

### From Si_CrabCannon — set your own super-weapon aim

Available to **commanders** and **regular players** only when admins enable it (via `/cc commanderaim` and `/cc playeraim`). Admins can always use it.

| Command | What it does |
|--|--|
| `/ccaim` | Show your current personal aim (or the cannon + super defaults if you haven't set one) |
| `/ccaim <angle> <speed>` | Set aim manually. Angle 1–89°, speed 1–800. Range capped at server's `MAX_RANGE`. Commander = sets super-weapon defaults team-wide and broadcasts to team chat. Player = personal override (only affects your own launches) |
| `/ccaim range <meters>` | Compute the speed needed to land at `<meters>` using your current angle. Convenience for "I want a 1800 m shot" — speed is auto-computed and rejected if it would exceed limits |

### From Si_HeavyTeleporter — battlefield teleport (Sol & Centauri only)

| Command | What it does |
|--|--|
| `/st` | Request a heavy vehicle teleport to you. Opens a menu of nearby teleportable vehicles |
| `/st <N>` | Pick vehicle N from the `/st` menu (e.g. `/st 1`) |
| `/st status` | Read-only summary: enabled/disabled, your team's tech tier requirement, charges left, cooldown |

Requirements: your team's tech tier must be at least the configured threshold, and your team must have at least one charge available. Recharges automatically.

### From Si_UnitBalance — inspect any unit's stats

| Command | What it does |
|--|--|
| `/stats` | Open a read-only inspector for the unit you are currently controlling. Browse parameter groups with `/1`–`/N`. `/back` goes up, `/0` closes |

You must be controlling a unit. Useful to see actual HP, damage, range, speed numbers (which can differ from vanilla because of live balance overrides).

### From Si_KingOfTheHill — capture status + unit purchase

| Command | What it does |
|--|--|
| `/koh` | Live status — current king team, capture % progress, last tick gain, per-team handicap multipliers |
| `/koh status` | Same as above |
| `/buy` | Open the unit-purchase menu (modes 1 & 2; falls back to *"disabled"* in mode 3 or if the server enabled `BuyAdminOnly`). Categories listed; pick with `/1`–`/N` |
| `/buy <N>` | Pick category or unit N directly |
| `/1`–`/N` | Navigate the current `/buy` menu level |
| `/back` | One level up in `/buy` |
| `/0` | Close the `/buy` menu |

Purchased units spawn ~15 m in front of you at ground level. Spawn is refused if it would land inside the configured no-build distance of an enemy HQ / Nest / Queen (configured via `BuyMinDistanceFromEnemyCritical`, typically ~400–800 m), or **further than `BuyMaxDistanceFromOwnCritical` (800 m default) from your own nearest HQ / Nest** — `/buy` reinforces your base, it doesn't teleport an army across the map.

Credits come from three sources while you play:
- **Starter credits** — flat grant once per round (server-configured; often a few thousand)
- **Kill rewards** — a percentage of the victim unit / structure's cost goes to the killer
- **Zone presence** — if KGT is in full mode (Mode 1) you accrue credits per second while one of your units is inside the capture zone

---

## Si_CrabCannon

Long-range super-weapon for the **Alien faction**. Once the Crab Cannon is unlocked (team reaches the configured tech tier), an alien player walks a Crab (or any enabled creature) into the Nest area and triggers a ballistic launch — the Crab becomes a projectile that arcs across the map, lands at the configured range, and unleashes the creature on the target zone. Combined with the optional **Super Weapon** mode it becomes a per-team strategic strike with charges, recharge time, and team-wide aiming.

Acts as the alien equivalent of an artillery / nuke. Default cannon parameters (speed, angle, payload) are server-wide; the Super Weapon adds per-team charges + a separate, team-aimable trajectory.

All `/cc` subcommands are **admin only**. Players who are allowed to participate use [`/ccaim`](#ccaim--companion-player-command-commanders--players-gated-by-admin) instead.

### Quick reference

| Command | What it does |
|--|--|
| `/cc` | Toggle ON / OFF |
| `/cc on` / `/cc off` | Explicit on/off |
| `/cc status` | One-line summary: mode, speed, angle, peak/range, cooldown, unit toggles, super state |
| `/cc speed <m/s>` | Launch speed (0–800). Adjusts → previews new peak + range. Rejected if range exceeds map limit |
| `/cc angle <0–90>` | Launch angle in degrees. Same range guard |
| `/cc range <m>` | Sets speed so range hits this value at current angle. Convenience for "I want a 1500 m shot" |
| `/cc cooldown <s>` | Reload between shots |
| `/cc radius <m>` | Trigger radius around aim point |
| `/cc tier <N>` | Minimum team tech tier before cannon is available |
| `/cc crab \| goliath \| behemoth \| scorpion \| hunter` | Toggle that creature type on/off as a payload |
| `/cc playeraim` | Toggle: can regular players aim the cannon? |
| `/cc commanderaim` | Toggle: can the commander aim the cannon? |
| `/cc countdown <s>` | Pre-fire warning countdown broadcast in chat |

### Super weapon (separate sub-system)

| Command | What it does |
|--|--|
| `/cc super` | Toggle super weapon ON / OFF |
| `/cc superrange <m>` | Sets the super-weapon's speed for a target range (uses super's angle) |
| `/cc supercharges <N>` | Max simultaneous super-weapon charges per team |
| `/cc supercd <s>` | Recharge time between super shots |

`/cc status` includes the super line: `SUPER: <used>/<max> charges, cd=<s>, aim=<angle>/<speed>`.

### `/ccaim` — companion player command (commanders / players, gated by admin)

| Command | What it does |
|--|--|
| `/ccaim` | Show personal aim or fall back to cannon + super defaults |
| `/ccaim <angle> <speed>` | Commander → updates super-weapon defaults team-wide. Regular player → personal aim override. Range checks honor `MAX_RANGE` |
| `/ccaim range <meters>` | Computes required speed for target range at current angle |

Admins can always use `/ccaim`. Commanders need `CommanderAimAllowed` ON (toggle with `/cc commanderaim`). Regular players need `PlayerAimAllowed` ON (toggle with `/cc playeraim`). Both default to OFF in stock config.

---

## Si_HeavyTeleporter

A **vehicle-teleport tool for human factions** (Sol & Centauri). Once a team reaches the configured tech tier, any soldier on that team can `/st` to call a team vehicle to their current location — useful for getting a Hover Tank or Siege Tank across the map without driving the whole way. Commanders and admins gain extra forms (`/st <playername>`, `/st <x> <z>`) for tactical positioning of teammates or themselves.

> **Pickup constraint**: only vehicles **standing in front of the team's Ultra Heavy Factory** are eligible for teleport (i.e. lined up on the UHF's spawn pad / staging area). The `/st` menu only lists those vehicles.

Each team has a shared charge pool that recharges over time, so it can't be spammed. Min-distance and radius rules prevent teleport-stomping enemy bases.

All player / commander / admin variants of `/st` share the same prefix; access scales with role.

### Player commands (anyone, Sol or Centauri only)

| Command | What it does |
|--|--|
| `/st` | Open a menu listing nearby teleportable vehicles. Next call `/st <N>` picks one |
| `/st <N>` | Confirm vehicle selection after `/st` |

Requires team tech tier ≥ `TechTier` config and at least one charge.

### Commander / admin commands

| Command | What it does |
|--|--|
| `/st <playername>` | Teleport that player's controlled unit |
| `/st <x> <z>` | Teleport caller to map coordinates (admin or commander) |

### Admin-only configuration

| Command | What it does |
|--|--|
| `/st on` / `/st off` | Master switch |
| `/st status` | Live summary + per-team charge/cooldown |
| `/st charges <N>` | Max charges per team |
| `/st cd <s>` | Recharge time |
| `/st tier <N>` | Minimum tech tier required to use teleporter |
| `/st countdown <s>` | Pre-teleport delay (broadcast warning) |
| `/st radius <m>` | Teleport target radius (target unit area) |
| `/st mindist <m>` | Minimum distance from enemy HQ/Nest to allow teleport |

---

## Si_UnitBalance

The server's **live unit-balance system** — overrides vanilla numbers (HP, cost, build time, weapon damage, ranges, projectile speeds, movement, FOW radii, etc.) on every Silica unit and structure per faction. The values live in `Si_UnitBalance_Config.json`; the mod applies them as a runtime overlay via the game's OverrideManager without touching prefab assets (so they're cleanly reverted at game end / map change).

The accompanying **UnitBalance UI** layer exposes three chat surfaces:
- **`!rebalance`** — hot-reload the JSON after hand-editing it
- **`!b`** — interactive admin editor: drill into Faction → Category → Unit → Parameter and set a new value live, mid-round
- **`/stats`** — read-only inspector available to any player (lets soldiers see the actual modded numbers for whatever they're piloting)

There's also a built-in **round-transition watchdog** that auto-recovers stalled end-of-round states. Recommended kept ON.

### `!rebalance` (admin)

| Command | What it does |
|--|--|
| `!rebalance` | Re-read `Si_UnitBalance_Config.json` from disk and re-apply every override (cost_mult, health_mult, range_mult, etc.) to the live game. Takes effect immediately. Use after editing JSON by hand |

### `!b` — Balance Editor (admin, interactive menu)

| Command | What it does |
|--|--|
| `!b` | Open the editor. Top-level menu: `1. Sol`  `2. Centauri`  `3. Alien`  `4. JSON`  `5. HTP` |
| `!b <N>` | Pick item N from current menu without typing `!b` first then `/N` |
| `!b exit` | Close the editor (also closes on second `!b` with no args) |

Navigate using `/1`–`/20`, `/0` (close), `/back` (one level up). Dotted form also works: `.1`, `.2`, ... and `!b 1 1.5` sets the visible parameter directly.

Menu structure:
1. **Sol / Centauri / Alien** → faction → category (Barracks/Light/Heavy/UHF/Air, or Cysts/Nest) → unit → parameter group (base/primary weapon/secondary weapon/etc) → parameter (set numeric value or 0/1 for bools)
2. **JSON** → 1. Reset to blank (vanilla)  · 2. Save current config  · 3. Load saved config
3. **HTP** (HitPoint Tier) → Time / Cost split → tech-tier values

> ⚠️ **Conflict with `/buy`**: keep only one of `!b` or `/buy` open at a time. If you have `!b` open and try `/buy`, KGT will refuse: *"You have a balance editor (/b) session open. Close it first (type /b again), then run /buy."*

### `/stats` — Read-only inspector (any player)

| Command | What it does |
|--|--|
| `/stats` | Open the stats view for whatever unit you are currently controlling. Browse parameter groups with `/1`–`/N`, `/back`, `/0` to close |

`/stats` and `!b` are mutually exclusive per player — opening `!b` drops any active `/stats` session automatically.

### Watchdog (server safety)

| Command | What it does |
|--|--|
| `!bal watchdog_enabled true \| false` | Per-server flag. When ON, monitors round-end transitions: if no new round starts within 60 s, forces `GameMode.EndRound()` so the server self-heals from stalls (e.g. the `TrueShadow` shader exception bug) |

Recommended setting: **`true`**.

---

## Si_KingOfTheHill (KGT)

The headline **"King of the Galactic Teleport"** game-mode layer. Drops a neutral capture point (an UltraHeavyFactory built by the Wildlife team, configured per map via Si_MapBalance) somewhere on the map. The first team to push enough weighted unit-cap into the capture cylinder and hold dominance for long enough wins the round — even without destroying the enemy HQ. Capture progress is visualized live by a ring of 12 outposts that fill in the leading team's faction colour.

KGT also bundles a **per-player credit economy** (`/buy` menu, kill rewards, zone-presence ticks, faction-treasury commander rewards) and an anti-snowball **handicap multiplier** so a dominant team's commander reward shrinks while a trailing team's grows.

Has two chat surfaces:
- **`/koh`** — admin configuration + live status (status itself is player-visible)
- **`/buy`** — in-round unit purchase menu, available to any player (configurable to admin-only)

A three-tier **Mode system** lets you run the full KGT experience, only the `/buy` economy, or disable everything per round.

> **Per-map radii**: `CaptureRadius` and `ExclusionRadius` (the no-build zone) are normally **defined in the MapBalance spawn config** — the `koh` block of `UserData/Spawns/<MapName>/koh_*.json`. KGT reads them from MapBalance at round start; the matching `Cfg.CaptureRadius` / `Cfg.BuildExclusionRadius` fields in `Si_KingOfTheHill_Config.json` act only as fallbacks when the spawn config doesn't provide a value. **To change a map's capture/no-build radius, edit that map's MapBalance JSON, not the KGT config.**

### `/koh` — config + status

`/koh` alone prints the status line. Sub-commands are admin-only.

| Command | What it does |
|--|--|
| `/koh` or `/koh status` | One-line summary: ON/OFF, mode, capture radius, exclusion radius, king + progress, last tick gain/cap, per-team handicap multipliers |
| `/koh on` / `/koh off` | Master toggle (`Cfg.Enabled`) |
| `/koh mode <1\|2\|3>` | Operating mode (see [§ Mode system](#mode-system)) |
| `/koh starter <N>` | Set `StarterCreditsPerPlayer` — credits granted to each player once per round (modes 1 & 2 only). `0` disables |
| `/koh radius <m>` | Capture-zone radius |
| `/koh threshold <pts>` | Win threshold (points to win) |
| `/koh rate <pts/s>` | `MaxCapturePointsPerSecond` cap |
| `/koh reset` | Clear king + accumulation + milestones for the current round (doesn't end the round) |

### `/buy` — in-round unit purchase

Available in modes 1 & 2. Anyone can use it unless `Cfg.BuyAdminOnly: true` is set.

| Command | What it does |
|--|--|
| `/buy` | Open buy menu. Shows categories: Barracks, Light Factory, Heavy Factory, Ultra Heavy Factory, Air Factory (humans) — or Lesser/Greater/Grand/Colossal Spawning Cyst (alien) |
| `/buy <N>` | Pick category N (or unit N once in a sub-list) |
| `/1`–`/N` | Same as `/buy N` while the menu is open |
| `/back` | Back to category list |
| `/0` | Close menu |

Units spawn ≈15 m in front of your controlled unit at ground level. Refused if too close to any enemy critical (HQ / Nest / Queen) — distance gate is `Cfg.BuyMinDistanceFromEnemyCritical` — or too far from your own nearest HQ / Nest (`Cfg.BuyMaxDistanceFromOwnCritical`, 800 m default; 0 = unlimited). The own-base leash anchors on critical **structures** only, and is skipped entirely if your team owns none.

### Mode system

`Cfg.Mode` (1 / 2 / 3) — change via `/koh mode N`:

| Mode | Intro chat + voice | KoH spawn / capture / immunity | AntiBuild zone | `/buy` | Kill rewards | Zone-presence reward | Commander reward | Starter credits |
|--|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| **1 Full** (default) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **2 BuyOnly** | — | — | — | ✅ | ✅ | — | — | ✅ |
| **3 Disabled** | — | — | — | — | — | — | — | — |

In **Mode 2**, `MapBalance` is told KGT doesn't want KoH layouts (`IsKohModLoaded()` reflects `ModeKohActive` → false), so the map picker skips `koh_*` JSONs even though KGT is loaded.

Mode changes take effect at the next round start.

---

## How KGT works — mechanics & MapBalance integration

KGT itself does **not** spawn the capture point. **`Si_MapBalance` owns the spawn**; KGT just consumes it.

### Spawn pipeline

```
new round starts
  ↓
Si_MapBalance picks a layout JSON for the map
  ↓
  IF (KGT loaded AND KGT.ModeKohActive)
    → prefers a layout that has a "koh" block (e.g. UserData/Spawns/Citadel/koh_test_citadel.json)
    → spawns the koh.PrefabName (e.g. Sol_UltraHeavyFactory) at koh.Position on team koh.Team (default Wildlife)
  ↓
Si_MapBalance fires its public OnSpecialsReady event
  ↓
KGT.OnMapBalanceSpecialsReady reads MapBalanceSpecials.Koh.{Obj, CaptureRadius, ExclusionRadius}
  ↓
KGT caches: _kohTower (transform), _kohCenter (Vector3), _captureRadius, _exclusionRadius, _kohTeam (Wildlife)
  ↓
KGT spawns 12 outpost ring markers around _kohCenter at _captureRadius
KGT arms damage immunity (ApplyDamage / SetHealth / SetHealth01 / OnReceiveClientDamageHitPacket transpiler)
KGT arms AntiBuild via SilicaAdminMod.Event_Construction.OnRequestBuildStructure
KGT arms auto-respawn watchdog (3 s interval — rebuilds the KoH if a player slips damage past immunity)
```

If the active spawn-config JSON for the current map has **no `koh` block**, MapBalance picks a non-KoH layout. KGT broadcasts *"[KGT] mod not active, spawn cfg missing"* in chat 3 seconds after round start.

### Capture math (per tick, default `EvalIntervalSeconds = 1.0`)

For every unit in `Unit.Units`:
1. **Inside zone?** `(dx² + dz²) ≤ _captureRadius²` (2D cylinder, Y unbounded)
2. **Eligible?** Skip if `UnitCapValue ≤ 0` (harvesters, queens, etc.)
3. **Compute weight per unit**:
   ```
   mult  = Cfg.PerUnitOverride[name]                              ← e.g. Crab = 0.25
          ?? Cfg.CategoryMultiplier[Cfg.BuildingToCategory[built_at]]   ← Sol_Heavy → "HeavyVehicle"
          ?? 1.0
   weight = UnitCapValue × mult
   if (player-controlled) weight ×= Cfg.PlayerControlledMultiplier  (default 2.0)
   ```
4. **Accumulate per team**: `scores[team] += weight`

### King ownership

After scoring:
```
total_in_zone = sum(scores.values())
for team in scores:
  if (scores[team] / total_in_zone) ≥ Cfg.DominanceThreshold  (default 0.60):
    candidate_king = team
```

The candidate becomes the king after `Cfg.OwnershipHandoverDelay` seconds of sustained dominance (smoothing — prevents flicker when units flow through).

### Accumulation toward win

Each tick the king accumulates:
```
this_tick_gain = min(scores[king], Cfg.MaxCapturePointsPerSecond × EvalIntervalSeconds)
king_progress += this_tick_gain
if (king_progress ≥ effective_threshold) → KGT TriggerWin
```

`effective_threshold` = `Cfg.WinThresholdPerMap[<scene>]` if present, else `Cfg.WinThreshold`.

If `Cfg.CumulativeAccumulation = true` (default), there's **one shared accumulator** — when a new team becomes king they continue from where the previous king left off. If false, each team has their own bucket.

Outpost ring fill: `kingN = round(OutpostCount × king_progress / threshold)` — the first N outposts (clockwise from north) show the king's faction colour; the rest stay neutral. Ring updates incrementally — only slots whose owner changes are despawned + respawned, so a +1 progress tick costs ≈ 2 networked ops instead of 24.

### Capture-progress milestones

`king_progress / threshold` thresholds: `25%`, `50%`, `75%`, `95%`. Each crossing fires a chat broadcast + a milestone wav (`SoundMilestone25.wav`, etc.). At `100%` the round ends — `kgt_win` log line, finishing-force units spawn (`8/16/24` Goliath/Tank/SiegeTank around each losing critical), per-team win wav plays, then `GameMode.EndRound()` is called.

### Reward systems

Three orthogonal credit sources running in parallel:

| Source | Beneficiary | Formula | Modes |
|--|--|--|--|
| **Kill reward** | Killing player | `round(victim.cost × Cfg.RewardKillFraction)` per kill (units AND structures) | 1, 2 |
| **Zone-presence reward** | Player whose unit is in zone | `round(EvalIntervalSeconds × unit.cap × Cfg.RewardZonePresenceMultiplier)` per tick per controlled unit | 1 only |
| **Commander reward** | Faction treasury | `round(min(team_score, MaxCapturePointsPerSecond × dt) × Cfg.CommanderRewardMultiplier × handicap_mult)` per tick per team. Goes via `Team.StoreResource` (overflow → `Team.StartingResources`) | 1 only |
| **Starter credits** | Player | Flat `Cfg.StarterCreditsPerPlayer` granted once per round at game start (+ 5 s poll for late joiners) | 1, 2 |

**Costs honor live `Si_UnitBalance` `cost_mult`** — KGT reads `Si_UnitBalance_Config.json` directly at each round start and multiplies the vanilla dump cost by the per-unit `cost_mult` field. (Reading `ConstructionData.ResourceCost` would return vanilla because UnitBalance's OverrideManager is a runtime overlay, not a field mutation.)

### Commander-reward handicap (anti-snowball)

Every `HandicapRecomputeIntervalSeconds` (default 5 s):

```
score(team) = TotalResources × HandicapResourceWeight + Units.Count × 100 × HandicapMilitaryWeight
ratio       = score(team) / avg(score across non-neutral teams)
mult        = clamp( ratio^(-HandicapStrength), HandicapFloorMult, HandicapCeilingMult )
```

Defaults give: dominant team at 2× avg → 0.50× reward; trailing team at 0.5× avg → 2.0× reward. Floor `0.25`, ceiling `3.0` (live config currently `5.0`). Display per-team multipliers via `/koh status` (green when boost, red when handicap).

### What MapBalance does for KGT specifically

1. **Layout selection bias** — `Si_MapBalance.IsKohModLoaded()` reflect-calls `Si_KingOfTheHill.KingOfTheHill.ModeKohActive`. When `true`, the layout picker prefers JSONs that carry a `koh` block. When `false` (Mode 2 / 3), the picker treats KGT as not loaded.
2. **Per-map spawn data** — `koh.PrefabName`, `koh.Position`, `koh.Team`, `koh.CaptureRadius`, `koh.ExclusionRadius` all live in the spawn-config JSON under `UserData/Spawns/<MapName>/koh_*.json`. Tune them per map.
3. **The publish handoff** — MapBalance writes `MapBalanceSpecials.Koh = { Obj, CaptureRadius, ExclusionRadius }` after spawning and fires `OnSpecialsReady`. KGT's only knowledge of *where* the capture point is comes from that public field.

To **disable KGT on a single map**, remove the `koh` block from that map's spawn-config JSON. KGT will broadcast the *"mod not active, spawn cfg missing"* notice and the round runs as normal Strategy.

To **change win threshold per map**, set `Cfg.WinThresholdPerMap` entries in `Si_KingOfTheHill_Config.json` keyed by Unity scene name (e.g. `"NorthPolarCap": 30000`).
