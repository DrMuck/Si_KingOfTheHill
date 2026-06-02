# Si_KingOfTheHill — King of the Galactic Teleport (KGT)

**Status:** v0.3.0 (released)
**Game:** Silica (3-team RTS/FPS — Sol / Centauri / Alien)
**Runtime:** MelonLoader 0.7.3+ on Mono dedicated server (references `MelonLoader\net472\` DLLs; target `netstandard2.1`)
**Server-only** mod. No client component required.

---

## 1. Overview

A neutral structure (the "Galactic Teleporter") spawns at the centre of each map. Teams compete to control a cylindrical zone around it; whichever team holds the majority of weighted force in the zone fills a shared capture meter. When the meter hits a configurable threshold, the dominating team wins the round — losing teams' critical structures are destroyed by a cinematic finishing force so Silica's natural end-of-round flow resolves.

Players accrue **credits** for kills + zone presence and spend them via a `/buy` chat menu that spawns purchased units on their team.

---

## 2. Building & spawn

- **Prefab:** `Sol_UltraHeavyFactory` (a networked structure — replicates to clients). Earlier prototype tried `Fortress_LargeTower_01` (decorative-only, no NetworkComponent → couldn't replicate).
- **Spawn team:** `Wildlife` (resolved at runtime — see §6 for the team-priority logic). Keeps the AI hostile-target check happy when paired with the GetTeamsAreEnemy bypass.
- **Placement:** Si_MapBalance is the spawner. It reads `koh: { x, z, capture_radius, exclusion_radius }` from the per-map layout JSON in `UserData/Spawns/{Map}/*.json` and instantiates the prefab during its `MusicJukeboxHandler.OnGameStarted` Harmony postfix.
- **Public API:** `Si_MapBalance.MapBalanceSpecials.Koh` exposes `{ Obj, CaptureRadius, ExclusionRadius }`. Si_KingOfTheHill subscribes via reflection to `MapBalanceSpecials.OnSpecialsReady` to receive the reference (no compile-time dependency between the two mods).

If no KoH is configured for the active map, KGT broadcasts `[KGT] mod not active, spawn cfg missing` at round start (3s grace) and stays inert.

---

## 3. Capture zone

- **Shape:** vertical cylinder centred on the KoH position. X/Z radius = `Cfg.CaptureRadius` (default 50m; web-tool-configured per map). Y unbounded — air units count.
- **No-build exclusion:** larger radius (`Cfg.BuildExclusionRadius`, default 75m) defined but not yet enforced.

---

## 4. Capture mechanics (accumulation model)

Each tick (default 1s, `Cfg.EvalIntervalSeconds`):

1. **Enumerate units in the cylinder** (`Unit.Units` filter). For each live, non-destroyed, controlled or AI unit:
   - `weight = unit_cap_value × CategoryMultiplier × PlayerControlledMultiplier`
   - `unit_cap_value` comes from `Si_UnitBalance_Dump.json` (Si_UnitBalance dependency)
   - `CategoryMultiplier` is keyed by the unit's production building (`built_at` field — Barracks, Light Factory, Lesser Spawning Cyst, …). All default to 1.0; admins can tune per-category. Crab gets `PerUnitOverride = 0.25`.
   - `PlayerControlledMultiplier` = 2.0 when `unit.ControlledBy != null`
2. **Sum per team** → `Dictionary<Team, float> scores`.
3. **Dominance check:** team with `score >= total * Cfg.DominanceThreshold` (default 0.60) is the **dominant team** this tick. If no team hits the threshold → **contested** (no accumulation).
4. **Smoothed king transition:** dominant team must hold dominance for `Cfg.OwnershipHandoverDelay` (default 3s) to become the committed king. Prevents flicker on close fights.
5. **Accumulation:** while dominant team == current king:
   - `gain = min(king_score, MaxCapturePointsPerSecond × tickInterval)` — capped at e.g. 100/tick
   - **Cumulative mode (default):** added to a single shared bucket `_totalAccumulated`. A new king continues where the previous king left off.
   - **Per-team mode** (`CumulativeAccumulation = false`): each team has their own bucket; only the current king's bucket fills.
6. **Win check:** `_totalAccumulated >= WinThreshold` → `TriggerWin(currentKing)`.

### Win threshold

- Default: 50,000 points (`Cfg.WinThreshold`)
- Per-map override: `Cfg.WinThresholdPerMap[sceneName]` — populated for all 13 stock maps; admins tune by map size

---

## 5. Outpost ring (capture clock)

- **N = `Cfg.OutpostCount` outposts** (default 12) spawn around the capture-radius perimeter at angles `i * 360°/N`, **buried `Cfg.OutpostBuryDepth` metres** (default 30) below the raycast-true surface — visible on radar only.
- **Acts as a clock:** the first M outposts (M = round(N × progress%)) are spawned on the **current king's team**; the rest stay on the Wildlife team. As capture progresses, more outposts flip to the king's colour — a slow radial radar fill.
- **Lifecycle:** initial spawn on KoH acquisition (0 king, all neutral). Re-spawned only when (kingTeam, kingCount) changes. Cleaned up on round end.

---

## 6. AI targeting + damage immunity

### AI targeting bypass

Harmony postfix on `GameMode.GetTeamsAreEnemy(Team, Team)`. If either team is identified as neutral (TeamShortName or GO name contains "Master" / "Wildlife" / "Worm"), result is forced to `false` ("not enemies"). Mirrors Si_4way's alliance pattern.

- Cached per-team in a `Dictionary<object, bool>` so hot-path cost is ~50ns/call (the game fires this method 100K+ times/sec).
- Short-circuits when `!_hasKoh` so non-KoH rounds pay zero overhead.

### Damage immunity (4 patches)

Player + AI weapons + sell actions all eventually call `DamageManager.SomeMethod` on the target. Each path needed a separate Harmony prefix:

| Patch target | Path it blocks |
|---|---|
| `DamageManager.ApplyDamage(Collider, float, EDamageType, GameObject, Vector3)` | Server-side AI projectile / melee |
| `DamageManager.SetHealth(float, GameObject, bool)` | Direct health writes (network sync, debug) |
| `DamageManager.SetHealth01(float)` | Player sell/demolish (`StrategyMode.PerformDestroyStructure` calls this with 0f) |
| `DamageManager.OnReceiveClientDamageHitPacket(GameByteStreamReader)` | **Player-controlled-unit damage** — client computes hit, sends packet, server does `Health -= value` (bypasses ApplyDamage) |

All 4 walk the parent chain from the DamageManager's transform looking for the cached KoH GameObject; if found, the call is short-circuited (no health change). Diagnostic counters log first 5 HIT + 5 MISS per round.

---

## 7. Chat narration (British English, team-coloured)

All user-facing messages use `[KGT]` prefix. Team names render in faction colour:

| Team | Hex |
|---|---|
| Sol | `#328cff` (blue) |
| Centauri | `#eb4646` (red) |
| Alien | `#50c832` (green) |
| Wildlife | `#c8963c` (brown) |
| Percentage (uranium-green) | `#39ff14` |

### Event messages

- **Round intro** (5-line block, fires on first KoH acquisition each round): explains capture mechanic + `/koh`
- **First king commit:** `The contest for the King of the Galactic Teleporter has commenced — Sol have asserted their claim.`
- **King transfer:** `Cent have wrested the Galactic Teleporter from Sol.`
- **Milestones** (25/50/75/95%): e.g. `Sol have claimed a quarter of the hill — 25%.`
- **Periodic status** (every `StatusAnnounceIntervalSeconds`, default 60s): `Sol presently hold 45% of the hill.`
- **Win:** per-team — `Sol Wins - Sol lives forever` / `Centauri Wins - Get outta here` / `Alien wins - Praise the Queen motherfuckers` (all editable in config)
- **Player enter/exit zone** (private to that player): `You have entered the king's domain.` / `…departed…`

---

## 8. Sound system (SilicaAdminMod AudioHelper)

All WAVs at 12 kHz / mono / 8-bit PCM (AudioHelper voice-packet format). Generated via `edge-tts` (British voices: SoniaNeural for milestones, RyanNeural for Sol, ThomasNeural for Centauri, US GuyNeural for Alien).

| Event | Sound |
|---|---|
| First capture | `first_capture.wav` — "The contest for the King of the Galactic Teleporter has commenced." |
| 25% / 50% / 75% / 95% milestones | `milestone_25.wav`, `milestone_50.wav`, `milestone_75.wav`, `milestone_95.wav` |
| Win (Sol) | `sol_wins.wav` — "Sol wins. Sol lives forever." |
| Win (Centauri) | `centauri_wins.wav` — "Centauri wins. Get outta here." |
| Win (Alien) | `alien_wins.wav` — "Alien wins. Praise the queen, motherfuckers." |
| Finishing force spawn | `cannon_boom.wav` (Si_CrabCannon's existing sound) |

Win sounds are re-encoded at +6 dB so they punch through ambient game audio. Cannon boom + finishing-force spawn is deferred by `Cfg.WinFinishingForceDelaySeconds` (default 4s) so it doesn't overlap the win voice line.

---

## 9. End-game finishing force

At `TriggerWin(winner)`:

1. Win chat + voice line fire immediately.
2. After `WinFinishingForceDelaySeconds`: cannon boom + spawn the winner's signature unit at every losing team's critical:

| Winner | Per human HQ | Per Alien Queen | Drop style |
|---|---|---|---|
| **Alien** | 8 Goliaths drop from +80m above the HQ (gravity + jitter) | 8 Goliaths on Queen | Vertical fall |
| **Sol** | 8 Siege Tanks in a ring (40m radius, facing inward) | 16 Siege Tanks | Ground ring |
| **Centauri** | 8 Crimson Tanks in a ring | 16 Crimson Tanks | Ground ring |

3. Units engage naturally → destroy the criticals → game's natural "only one team has criticals" win flow resolves.
4. **Safety fallback** after `WIN_FORCE_FALLBACK_SECONDS` (60s): if any losing critical is still alive (terrain blocked the units, etc.), force-destroy via `SetHealth01(0)`.

Critical detection covers both `team.Structures` (Sol/Cent HQs) and `team.Units` (Alien Queen).

---

## 10. Reward system

### Sources

| Trigger | Formula | Default |
|---|---|---|
| Kill enemy unit (`GameEvents.OnUnitDestroyed`) | `round(unit_cost × RewardKillFraction)` | 10% of cost |
| Destroy enemy structure (`GameEvents.OnStructureDestroyed`) | `round(structure_cost × RewardKillFraction)` | 10% of cost |
| Hold a controlled unit in zone (per tick) | `round(EvalInterval × unit_cap × RewardZonePresenceMultiplier)` | tick × cap × 2 |

Crab's 0.25 scoring penalty is **NOT** applied to reward calc (`RewardZoneCrabSkipPenalty = true`). Friendly-fire kills earn no reward.

### Cost source

`Si_UnitBalance_Dump.json` provides baseline costs. At each round start, `RefreshLiveUnitCosts()` scans `Resources.FindObjectsOfTypeAll<ConstructionData>()` and overwrites `_unitCost[name]` with the live `ResourceCost` value — so Si_UnitBalance's runtime cost overrides flow through to reward calc.

### Persistence

Credits live in `Dictionary<long /*SteamID*/, int>`. Reset between rounds when `RewardResetOnGameEnd = true` (default).

---

## 11. /buy command (multi-step chat menu)

### Flow

```
/buy           → open menu at Categories level (Barracks / Light / Heavy / Ultra / Air,
                 or Lesser/Greater/Grand/Colossal Spawning Cyst for Alien)
/1 ... /N      → select option (drill into category, or buy unit when in unit list)
/back          → up one level
/0             → close
/buy 1, /buy back, /buy exit ← legacy/fallback forms; all still work
```

### Chat routing

`/1`-`/N` are owned by Si_UnitBalanceUI for its own menus. KGT subscribes to `SilicaAdminMod.Event_Chat.OnRequestPlayerChat` (fires BEFORE the chat-block decision) instead of registering its own `/1`-`/N` commands. Only acts when the calling player has an open `/buy` menu, sets `args.Block = true` to suppress the chat line. No registration conflict with UB.

### Purchase rules

- Must be **controlling a unit** (refused otherwise — that's the spawn anchor).
- Spawn position = `Cfg.BuySpawnDistance` (15m) in front of player's controlled unit's facing.
- Refused if spawn would land within `Cfg.BuyMinDistanceFromEnemyCritical` (400m default) of any enemy HQ / Nest / Queen — anti-rush guard. Checks both `team.Structures` and `team.Units` with `ObjectInfo.Critical == true`.
- Unit appears on **player's team** (passed to `Game.SpawnPrefab` so it's baked into the SendNetSpawn packet — see [[project_silica_team_networking]] memory).

---

## 12. Admin/player commands

| Command | Visibility | Effect |
|---|---|---|
| `/koh` | All players | Status: on/off, radii, current king + progress %, last-tick gain vs cap (CAPPED / below cap / contested) |
| `/koh status` | Same as `/koh` | — |
| `/koh on` / `/koh off` | Admin (Power.Generic) | Master toggle |
| `/koh radius <m>` | Admin | Set capture radius |
| `/koh threshold <pts>` | Admin | Set WinThreshold (global default) |
| `/koh rate <pts/s>` | Admin | Set MaxCapturePointsPerSecond |
| `/koh reset` | Admin | Clear king state mid-round (testing) |
| `/buy` | All players | Open buy menu / refresh current view |
| `/1` … `/N`, `/back`, `/0` | All players (state-gated) | Buy menu navigation when menu is open |

---

## 13. Dependencies

- **Si_MapBalance** (required): spawns the KoH building from per-map layout JSON, exposes `MapBalanceSpecials.Koh` to KGT.
- **Si_UnitBalance** (required for unit categorisation + cost lookup): KGT reads `Si_UnitBalance_Dump.json` for unit→category mapping + base costs. Live cost refresh uses `ConstructionData.ResourceCost` (which Si_UnitBalance's OverrideManager modifies).
- **SilicaAdminMod** (required): `AudioHelper.PlaySoundFile` for sound playback; `HelperMethods.SendChatMessageToPlayer` for chat; `Event_Chat.OnRequestPlayerChat` for /1-/N nav hook.
- **MelonLoader** + **Newtonsoft.Json** + **0Harmony** + Unity refs (CoreModule, TerrainModule, PhysicsModule).

---

## 14. Config layout

`UserData/KingOfTheHill_cfg/Si_KingOfTheHill_Config.json` — auto-generated on first load; auto-resaved on every load so new fields appear with their defaults while preserving customisations.

Key sections (full schema in `Config.cs`):

```jsonc
{
  "Enabled": true,
  "OnlyInStrategyMode": true,
  "CaptureRadius": 50.0,
  "BuildExclusionRadius": 75.0,
  "EvalIntervalSeconds": 1.0,
  "OwnershipHandoverDelay": 3.0,

  "WinThreshold": 50000.0,
  "MaxCapturePointsPerSecond": 100.0,
  "DominanceThreshold": 0.60,
  "CumulativeAccumulation": true,

  "WinThresholdPerMap": { "Badlands": 50000, "BlackIsle": 50000, … },

  "PlayerControlledMultiplier": 2.0,
  "BuildingToCategory": { "Barracks": "Infantry", "Light Factory": "LightVehicle", … },
  "CategoryMultiplier": { "Infantry": 1.0, … },
  "PerUnitOverride": { "Crab": 0.25 },

  "OutpostCount": 12,
  "OutpostBuryDepth": 30.0,

  "EnterExitNotifications": true,
  "StatusAnnounceIntervalSeconds": 60.0,

  "RewardKillFraction": 0.10,
  "RewardZonePresenceMultiplier": 2.0,
  "RewardZoneCrabSkipPenalty": true,
  "RewardResetOnGameEnd": true,
  "BuySpawnDistance": 15.0,
  "BuyMinDistanceFromEnemyCritical": 400.0,

  "WinFinishingForceDelaySeconds": 4.0,
  "SoundFirstCapture": "sounds/first_capture.wav",
  "SoundMilestone25":  "sounds/milestone_25.wav",
  "SoundMilestone50":  "sounds/milestone_50.wav",
  "SoundMilestone75":  "sounds/milestone_75.wav",
  "SoundMilestone95":  "sounds/milestone_95.wav",
  "SoundFinishingForce": "sounds/cannon_boom.wav",
  "SoundWinSol":       "sounds/sol_wins.wav",
  "SoundWinCentauri":  "sounds/centauri_wins.wav",
  "SoundWinAlien":     "sounds/alien_wins.wav",

  "WinMessageSol":      "Sol Wins - Sol lives forever",
  "WinMessageCentauri": "Centauri Wins - Get outta here",
  "WinMessageAlien":    "Alien wins - Praise the Queen motherfuckers",
  "WinMessageDefault":  "{name} have conquered the hill — a most resounding victory!"
}
```

---

## 15. File layout

```
Si_KingOfTheHill/
├── DESIGN.md                          ← this file
├── include/netstandard2.1/            ← reference DLLs (gitignored)
└── Si_KingOfTheHill/
    ├── Si_KingOfTheHill.csproj
    ├── Si_KingOfTheHill.cs            ← MelonMod entry, lifecycle, OnUpdate pipeline
    ├── Config.cs                      ← KohConfig + load/save (auto-save on load)
    ├── Zone.cs                        ← MapBalance subscription, dump loader, EvaluateScores, player-in-zone tracking
    ├── King.cs                        ← dominance state machine, accumulation, milestones, colour helpers
    ├── Timer.cs                       ← periodic status, win trigger, finishing-force delay, sound dispatch
    ├── Outposts.cs                    ← outpost-clock ring spawn/despawn
    ├── Patches.cs                     ← 4 damage-immunity Harmony patches + GetTeamsAreEnemy bypass
    ├── Rewards.cs                     ← credit tracking, kill/structure-kill rewards, zone-presence reward
    ├── Buy.cs                         ← /buy menu state machine, /1-/N chat hook, purchase + spawn
    └── Commands.cs                    ← /koh command + Reply/BroadcastAllChat helpers
```

---

## 16. Known limitations / future work

- **No anti-build patch yet** — `BuildExclusionRadius` is configured but not enforced in code. A Harmony prefix on `Structure.Construct` would block enemy constructions inside the exclusion radius.
- **No world-space UI billboard** — capture progress is communicated via chat + outpost clock. A `Canvas WorldSpace` above the KoH would be nicer but requires per-client rendering work.
- **Outposts above ground on cliffs** — `SampleSurfaceY` uses raycast from terrain+200m, which handles most rocks. Map-specific edge cases may still poke through; bump `OutpostBuryDepth` per map.
- **Auxiliary units excluded** — units with `unit_cap_value <= 0` (Hover Bike, harvester, etc.) don't contribute to score or rewards. By design.
- **Credit persistence** — round-scoped only (`RewardResetOnGameEnd = true`). Cross-round persistence would need a save file.
