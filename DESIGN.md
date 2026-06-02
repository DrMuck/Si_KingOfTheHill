# Si_KingOfTheHill — Design Document

**Status:** design draft v0.1 — not yet implemented.
**Game:** Silica (3-team RTS/FPS — Sol/Centauri/Alien).
**Runtime:** Mono server, MelonLoader 0.7.3.2524, references `net472/` (see Si_UnitBalance memory).
**Target:** `netstandard2.1`.

---

## 1. Concept

A neutral "King of the Hill" (KoH) structure spawns in the middle of the map. Teams compete to control a cylindrical zone around it. The team with the **highest weighted unit count** inside the zone is the current king. When a global capture timer expires, the current king wins the round — overriding the usual "last HQ standing" condition.

Original win condition is **not** removed: a team that destroys all enemy HQ/Queen structures before the KoH timer expires still wins normally. KoH is an *additional* path to victory.

---

## 2. KoH Building

- **Prefab:** `Fortress_LargeTower_01` — confirmed to exist at `Assets/GameObject/Fortress_LargeTower_01.prefab`. It is a pure visual hierarchy of concrete walls + meshes with **no `DamageManager`, no `ObjectInfo`, no `Structure` script**. Treat it as a static decorative prop — no native gameplay behavior.
- **Damage immunity:** N/A — the building has no damage system to begin with. Harmony patches not needed for this aspect.
- **Ownership:** the building stays neutral (no Team assignment possible without a Structure component). King ownership is expressed entirely through the custom overlay UI and chat broadcasts.
- **Progress display — custom overlay:** since there is no native health bar, render our own. Two layers:
  - **World-space billboard** above the tower (Unity `Canvas` set to `WorldSpace`) with a horizontal fill bar tinted to the current king's team color, plus countdown text.
  - **Periodic chat broadcasts** as a fallback / low-tech audit trail (every minute + last 10s).
- **Visual flair (optional):** colored point light or particle effect at the tower base tinted to the king's team color. Falls back to neutral white when no king is held.

**Spawning:** runtime `GameObject.Instantiate` at the configured per-map position (see §10). On `OnGameStarted` / map-load, the mod looks up the prefab in `GameDatabase.Database` (or via `Resources.FindObjectsOfTypeAll<GameObject>()` matching on name as a fallback) and instantiates it server-side. Network replication of a static-prop type is uncertain — see §11 risk.

---

## 3. Capture Zone

- **Shape:** vertical cylinder (a.k.a. "tube" / column) centered on the KoH building.
  - X/Z check uses 2D distance.
  - Y is unbounded — air units count.
- **Radius:** configurable (default suggestion: 40-60m, tuned in playtest).
- **Build-exclusion radius:** larger than capture radius (default: 1.5× capture radius). Enemies (non-neutral teams) cannot construct any structure inside this radius. Own team's units / pre-existing structures unaffected.

---

## 4. Ownership & King Logic

- **Tick rate:** evaluated every N seconds (default 1s; configurable as `EvalIntervalSeconds`).
- Each tick:
  1. Enumerate all live units in the cylinder.
  2. Group by team.
  3. Sum weighted points per team (see §5).
  4. The team with the **highest non-zero sum** is the **provisional king**.
  5. If the provisional king == current king → reset handover timer.
  6. If the provisional king ≠ current king → start/continue handover timer.
  7. When handover timer reaches `OwnershipHandoverDelay` (default: 5s), commit the new king.
- **Empty zone:** no team has any units inside.
  - Capture countdown **freezes** (does not rewind) — debatable, see Open Questions §11.
  - Current king is retained (no neutral state once captured).
- **Single team in zone:** that team is king after handover delay (even if only 1 unit).
- **Pre-capture state:** before any team has ever held the building, ownership is `None`. The global capture timer does NOT tick during `None`.

---

## 5. Unit Weighting

**Base weight = unit's `unit_cap_value`** (the slot count the unit occupies in its team's unit cap — same field Si_UnitBalance reads via `ObjectInfo`). The more cap a unit eats, the more it's worth as a KoH presence. Self-balances against the game's existing cost/cap economy.

**Final weight formula:**

```
weight = unit_cap_value × CategoryMultiplier × (player_controlled ? PlayerControlledMultiplier : 1)
```

### Auto-classifier from production building (`built_at`)

The Si_UnitBalance dump exposes `built_at` per unit — the production building name. That IS the natural category. Extracted from `Si_UnitBalance_Dump.json`:

| `built_at` value | Category | Faction(s) |
|---|---|---|
| `Barracks` | **Infantry** | Sol, Centauri |
| `Light Factory` | **LightVehicle** | Sol, Centauri |
| `Heavy Factory` | **HeavyVehicle** | Sol, Centauri |
| `Ultra Heavy Factory` | **UltraHeavyVehicle** | Sol, Centauri |
| `Air Factory` | **Air** | Sol, Centauri |
| `Lesser Spawning Cyst` | **LesserAlien** | Alien |
| `Greater Spawning Cyst` | **GreaterAlien** | Alien |
| `Grand Spawning Cyst` | **GrandSpawner** | Alien |
| `Colossal Spawning Cyst` | **ColossalAlien** | Alien |
| *(empty)* | **Auxiliary** | Harvesters, Hover Bike, Sports Car, Queen, Worms |

All multipliers default to **1.0** so weight = unit_cap_value directly.

### Default CategoryMultiplier table

| Category | Multiplier | Notes |
|---|---|---|
| Infantry | 1.0 | |
| LightVehicle | 1.0 | |
| HeavyVehicle | 1.0 | |
| UltraHeavyVehicle | 1.0 | Siege Tank, Crimson Tank (cap 6) |
| Air | 1.0 | Cap already differentiates Bomber/Freighter (5) from Fighter (3) |
| LesserAlien | 1.0 | |
| GreaterAlien | 1.0 | |
| GrandSpawner | 1.0 | Goliath only |
| ColossalAlien | 1.0 | Colossus (cap 15), Defiler (cap 8) — flag, see §11 |
| Auxiliary | 0.0 | Harvesters etc. — don't count toward KoH |

### Computed weights at default multipliers (auditable preview)

Drawn from the current `Si_UnitBalance_Dump.json`. Numbers = `cap × 1.0`.

**Sol Infantry:** Rifleman 1, Scout 1, Heavy 2, Sniper 2, Commando 2
**Sol Light:** Light Quad 1, Light Striker 2, Heavy Quad 2, Platoon Hauler 2, Heavy Striker 3, AA Truck 3
**Sol Heavy:** Hover Tank 3, Pulse Truck 3, Barrage Truck 3, Railgun Tank 4
**Sol Ultra:** Siege Tank 6
**Sol Air:** Gunship 2, Fighter 3, Dropship 4, **Bomber 5**

**Centauri Infantry:** Militia 1, Trooper 1, Juggernaut 2, Marksman 2, Templar 2
**Centauri Light:** Light Raider 1, Heavy Raider 2, Assault Car 2, Squad Transport 2, Flak Car 3, Strike Tank 3
**Centauri Heavy:** Combat Tank 3, Pyro Tank 3, Heavy Tank 4, Rocket Tank 4
**Centauri Ultra:** Crimson Tank 6
**Centauri Air:** Interceptor 2, Dreadnought 3, Shuttle 4, **Freighter 5**

**Alien Lesser:** Crab → **0.25** (special), Shrimp 1, Squid 1, Wasp 1, Dragonfly 2, Shocker 2
**Alien Greater:** Hunter 1, Behemoth 2, Horned Crab 2 (or 0.5 — see §11 Q7), Firebug 3, Scorpion 3
**Alien Grand:** Goliath 4
**Alien Colossal:** Defiler 8, **Colossus 15** (see §11 Q6)

### Per-unit override (escape hatch for Crab + edge cases)

```jsonc
"PerUnitOverride": {
  "Crab": 0.25            // applied to the unit's cap (so weight = 1 × 0.25 = 0.25)
  // "Horned Crab": 0.25  // TBD — see §11 Q7
  // "Colossus": 0.5      // example: cap Colossus down if 15 too high in practice
}
```

Lookup precedence: `PerUnitOverride[unit_name]` (multiplier) → `CategoryMultiplier[category]` → `1.0`.

### Player-controlled multiplier

`PlayerControlledMultiplier`: default **2.0**. Applied when `unit == player.ControlledUnit` for some live `Player`.

### Dead / invalid units

Filter `unit.IsDestroyed == false` and `unit.ObjectInfo != null` before counting.

### Why unit-cap-based works well

- The game already balances `unit_cap_value` carefully — Goliath at cap 4 vs Rifleman at cap 1 is a deliberate ratio. We inherit that for free.
- The original spec's hand-picked weights (1 / 2 / 2.5 / 3 / 3.5 / 5) map close to actual cap values, just without the conceptual lookup table — cap IS the lookup.
- Future / modded units are auto-classified by `built_at`. Any new unit with an unrecognized `built_at` falls back to the `Infantry` category multiplier (configurable).

---

## 6. Capture Countdown

- **Duration:** `CaptureSeconds` (default 600s = 10 minutes — configurable).
- Starts ticking down the first time any team becomes the king.
- Does **not** reset on ownership transfer — same global timer keeps counting down.
- Pauses on `None` ownership (zone empty).
- Periodic chat broadcasts:
  - On capture start: "[KoH] First contact — capture begins (10:00 to king's victory)"
  - On ownership change: "[KoH] {Team} is now king of the hill"
  - Every minute: "[KoH] {RemainingMin}m to {Team} victory"
  - Last 10s: per-second countdown
  - At zero: trigger KoH win path (see §8).

---

## 7. Anti-Build Rule

- Inside the build-exclusion radius, any **non-owning-team** construction attempt is denied.
- "Non-owning-team" = any team that is NOT the current king.
- Implementation: Harmony prefix on the structure-construction entry point (likely `Structure.Construct` or the placement-request RPC). Check the target position vs KoH center; if inside exclusion radius AND building team != current king, deny placement and send chat feedback ("[KoH] Cannot build inside enemy hill zone").
- If `current king == None`, all teams are blocked from building in the zone.
- Already-placed structures are untouched (no retroactive removal).

---

## 8. Win Conditions

Two parallel win paths:

1. **Original (HQ destruction):** if a team destroys all opposing HQ/Queen structures before the KoH timer expires, original game logic resolves the round normally. The KoH timer is irrelevant.
2. **KoH timer expiry:** when the capture countdown reaches zero AND a team is the current king, that team wins immediately, regardless of HQ status.

**Tie-handling:** if the timer hits zero on a `None` ownership (shouldn't happen because timer is paused) → no KoH win, original logic continues.

**Implementation:** locate the existing round-end / victory-decision entry point (likely `StrategyMode.OnTeamVictory` or `GameMode.SetWinningTeam` — to be confirmed during prototyping). Call that with the KoH-king team. Verify with the unit-balance HQ-decay logic which already touches end-of-round flow.

---

## 9. Config JSON Schema

File: `UserData/KingOfTheHill_cfg/Si_KingOfTheHill_Config.json` (created on first load with defaults).

```jsonc
{
  "Enabled": true,
  "BuildingPrefab": "Fortress_LargeTower_01",
  "CaptureRadius": 50.0,
  "BuildExclusionRadius": 75.0,
  "EvalIntervalSeconds": 1.0,
  "OwnershipHandoverDelay": 5.0,
  "CaptureSeconds": 600.0,
  "PlayerControlledMultiplier": 2.0,

  // Base weight per unit = ObjectInfo.unit_cap_value (read at runtime).
  // Category is derived from the unit's production building ("built_at" in Si_UnitBalance dump).
  // Final weight = unit_cap_value * Multiplier
  // Multiplier = PerUnitOverride[name]  if present
  //            else CategoryMultiplier[ BuildingToCategory[built_at] ]
  //            else CategoryMultiplier["Infantry"]
  // Then × PlayerControlledMultiplier if the unit is currently controlled by a Player.

  "BuildingToCategory": {
    "Barracks":               "Infantry",
    "Light Factory":          "LightVehicle",
    "Heavy Factory":          "HeavyVehicle",
    "Ultra Heavy Factory":    "UltraHeavyVehicle",
    "Air Factory":            "Air",
    "Lesser Spawning Cyst":   "LesserAlien",
    "Greater Spawning Cyst":  "GreaterAlien",
    "Grand Spawning Cyst":    "GrandSpawner",
    "Colossal Spawning Cyst": "ColossalAlien"
    // unmatched / empty built_at -> "Auxiliary" (harvesters, hover bike, etc.)
  },

  "CategoryMultiplier": {
    "Infantry":          1.0,
    "LightVehicle":      1.0,
    "HeavyVehicle":      1.0,
    "UltraHeavyVehicle": 1.0,
    "Air":               1.0,
    "LesserAlien":       1.0,
    "GreaterAlien":      1.0,
    "GrandSpawner":      1.0,
    "ColossalAlien":     1.0,
    "Auxiliary":         0.0
  },

  "PerUnitOverride": {
    "Crab": 0.25
    // optional escape hatch — add unit-name -> multiplier here.
    // Wins over CategoryMultiplier.
  },

  "DefaultCategoryForUnknown": "Infantry",

  "AnnounceEveryMinute": true,
  "AnnounceLast10s": true
}
```

Plus per-map override for KoH spawn position (since map center varies):

```jsonc
"MapPositions": {
  "Badlands":           { "x": 0,   "y": 50,  "z": 0 },
  "IndustrialQuarter":  { "x": 12,  "y": 45,  "z": -8 }
}
```

If a map isn't in the table, fall back to map center via terrain bounds OR disable the mod for that map (configurable).

---

## 10. Admin / Chat Commands (TBD)

Tentative — confirm naming with DrMuck before implementing:

| Command | Power | Effect |
|---|---|---|
| `/koh` | none / player | Show current status: king, time remaining, weighted scores per team |
| `/koh on` / `/koh off` | Generic | Master toggle (round-scoped) |
| `/koh status` | Generic | Detailed dump for current map |
| `/koh time <seconds>` | Generic | Set capture duration |
| `/koh radius <m>` | Generic | Set capture radius |
| `/koh reset` | Generic | Reset capture timer to full, clear king |
| `/koh weight <category> <value>` | Generic | Adjust a weight at runtime |

---

## 11. Decisions Made & Remaining Open Questions

### Decided (v0.2)

1. **Building:** `Fortress_LargeTower_01` confirmed present at `Assets/GameObject/Fortress_LargeTower_01.prefab`. Pure static visual prop — no `DamageManager` / `ObjectInfo` / `Structure` components. Treated as decoration only.
2. **Progress display:** custom overlay — world-space billboard above tower + chat broadcasts. No native healthbar to drive.
3. **Empty zone behavior:** **freeze timer**. Current king retained until someone re-enters.
4. **Placement:** **runtime instantiation** at configured per-map position via `MapPositions` JSON.
5. **Weighting model:** `unit_cap_value × CategoryMultiplier × PlayerControlledMultiplier` (no flat weight table). Category auto-derived from `built_at`.
6. **Crab:** `PerUnitOverride["Crab"] = 0.25` (effective weight 0.25).

### Still open (need answers / research during implementation)

1. **(?) Prefab loading** — best way to obtain `Fortress_LargeTower_01` at runtime. Try (a) `GameDatabase.Database` lookup, (b) `Resources.FindObjectsOfTypeAll<GameObject>()` filtered by name, (c) `AssetBundle`. Prototype during impl.
2. **(?) Network replication of a static prop** — instantiated server-side, will clients see it? Static props normally come from the map file. May need a `NetworkObject` / `BaseGameObject` wrapper to force replication. Risk: clients see nothing while server sees the tower. Fallback: server-only logic + chat-broadcast progress (no visible tower, just zone gameplay).
3. **(?) Round-end hook** — confirm API for forcing a team win mid-round. The HQ-decay system already destroys HQs to trigger natural loss — may piggyback (on KoH win, force-destroy losing teams' HQs to use the existing win flow). Inspect `StrategyMode`, `GameMode`, `MusicJukeboxHandler.OnGameEnded` during impl.
4. **(?) Build-exclusion enforcement point** — `Structure.Construct`, build-request RPC, or placement-validate? Whichever runs server-side first. Likely the same hook Si_BuildLimits uses.
5. **(?) `unit_cap_value` field/property location** — confirm exact accessor on `ObjectInfo` under Il2Cpp (Si_UnitBalance already reads it; reuse that path).
6. **(?) Colossus cap-15 outlier** — Colossus has `unit_cap_value=15`, so default weight 15 (3× Goliath). May dominate KoH if 2-3 are in zone. Options: cap multiplier (e.g. PerUnitOverride 0.5 → 7.5), or accept — Colossus is meant to be game-deciding. Flag for playtest.
7. **(?) Horned Crab classification** — `Horned Crab` is in `Greater Spawning Cyst` (cap 2), not Lesser. User spec said "crabs = 0.25". Does that include Horned Crab? Current draft: only `Crab` gets 0.25; Horned Crab defaults to GreaterAlien×1.0 = weight 2. Confirm.
8. **(?) Pre-capture timer visibility** — show grayed-out timer + "waiting for first contact" before anyone captures, or hide? Default: show.
9. **(?) Air unit positions** — verify air units expose accurate world-position via `unit.transform.position`. Should be fine; flag during testing.
10. **(?) Game-mode scope** — KoH only makes sense in Strategy mode. Add `OnlyInStrategyMode = true` config flag (default on).
11. **(?) Multi-tower / multi-KoH** — single tower per map only, or support multiple capture points (best-of-N)? Default: single. Future extension if requested.

---

## 12. Implementation Plan (post-decision)

Once §11 questions are answered:

1. **Skeleton** — `Si_KingOfTheHill.cs` MelonMod with `OnInitializeMelon`, config load, command registration.
2. **Spawn / locate KoH** — on `GameEvents.OnGameStarted` or `OnLevelLoaded`, instantiate or find the KoH building, cache its transform.
3. **Damage immunity** — Harmony patch on DamageManager.
4. **Zone evaluator** — per-tick `Update()` (gated to `EvalIntervalSeconds`) that walks unit lists, computes weighted scores, updates ownership state machine.
5. **Capture timer** — global float, decremented per tick when a king is held.
6. **Visual indicator** — drive health bar or send broadcast text.
7. **Anti-build patch** — Harmony prefix on construction entry.
8. **Win trigger** — at timer zero, call the round-end forcing function.
9. **Commands** — `/koh` family.
10. **Config persistence** — load + save JSON (use `root.ToString()` NOT `ToString(Formatting.Indented)` — Mono Newtonsoft doesn't have the overload, per Si_UnitBalance fix).

---

## 13. Risks

- **Crash risk:** runtime instantiation of a prefab not designed to spawn dynamically may NRE or fail to network-replicate. Mitigation: prefer map-placed if Fortress_LargeTower_01 is finicky.
- **Game-mode interaction:** Silica has multiple game modes (Strategy, etc.). KoH may only make sense in Strategy mode. Add `OnlyInStrategyMode` config flag.
- **Win-condition double-fire:** if a team destroys all enemy HQs in the same tick the KoH timer expires, the original win path and KoH path could both fire. Add precedence rule: whichever fires first wins; second is suppressed.
- **AdminMod 2.13.36 transpiler regression** (per memory) — DrMuck rolled back to 2.13.19. Build/test against 2.13.19 specifically.

---

## 14. File Layout (planned)

```
Si_KingOfTheHill/
├── DESIGN.md                          (this file)
├── include/
│   └── netstandard2.1/                (reference DLLs — copy from Si_CrabCannon/include)
└── Si_KingOfTheHill/
    ├── Si_KingOfTheHill.csproj
    ├── Si_KingOfTheHill.cs            (MelonMod entry, lifecycle)
    ├── Config.cs                      (JSON model, load/save)
    ├── Zone.cs                        (cylinder check, unit enumeration, weighting)
    ├── King.cs                        (ownership state machine, handover)
    ├── Timer.cs                       (capture countdown, announcements)
    ├── Patches.cs                     (Harmony: damage immunity, anti-build, win trigger)
    └── Commands.cs                    (/koh family)
```
