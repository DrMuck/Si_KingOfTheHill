# Silica Server — Player Chat Manual

What you can do with chat commands while playing on this server. Everything here works for **any connected player** — no admin role needed (a few are gated by the active commander or by an admin permission, noted where it applies).

Type commands into normal chat. Anything that starts with `/` is a hidden command (other players won't see what you typed).

| Mod | Player commands |
|--|--|
| [Si_KingOfTheHill](#si_kingofthehill-kgt) | `/koh`, `/buy` |
| [Si_UnitBalance](#si_unitbalance) | `/stats` |
| [Si_HeavyTeleporter](#si_heavyteleporter) | `/st` |
| [Si_CrabCannon](#si_crabcannon) | `/ccaim` *(when allowed by admin)* |

---

## Si_KingOfTheHill (KGT)

The server runs a **"King of the Galactic Teleport"** game-mode layer. A neutral Galactic Teleporter (an UltraHeavyFactory under the Wildlife team) spawns somewhere on the map at round start. The first team to push enough weighted units into the capture cylinder around it and hold dominance for long enough wins the round — even without destroying the enemy HQ.

A ring of 12 outposts marks the capture perimeter. As the leading team accumulates progress, more of those outposts switch to that team's faction colour. Top of the screen / chat broadcasts call out 25/50/75/95% milestones and the winner.

The size of the capture zone and the no-build radius around it are **map-dependent** (configured per map by the server). The zone you see on one map may be tighter or wider on the next.

Alongside the capture game, KGT runs a **per-player credit economy**: you earn credits, you spend them with `/buy` to instantly reinforce wherever you're standing.

### Capture status

| Command | What it does |
|--|--|
| `/koh` | Show live status: current king team, capture % progress, last tick gain, per-team handicap multipliers |
| `/koh status` | Same as above |

### Buy menu

| Command | What it does |
|--|--|
| `/buy` | Open the purchase menu. Top level shows categories (Barracks, Light Factory, Heavy Factory, Ultra Heavy Factory, Air Factory for humans — or Lesser/Greater/Grand/Colossal Spawning Cyst for aliens) |
| `/buy <N>` | Pick category or unit N directly (e.g. `/buy 3`) |
| `/1`–`/N` | Navigate the current `/buy` menu level once it's open |
| `/back` | Back one level |
| `/0` | Close the menu |

Purchased units spawn **~15 m in front of you** at ground level. The buy will be refused if the spawn would land inside an enemy critical's no-build radius (HQ / Nest / Queen). Move farther away and try again.

If `/buy` says *"You have a balance editor (/b) session open. Close it first..."* — that's a collision with the admin balance editor. Only admins ever see this; type `/b` again to close that menu, then retry `/buy`.

### How you earn credits

Three sources run in parallel:

1. **Starter credits** — flat amount granted once per round when you connect. Server-configured (commonly a few thousand cr).
2. **Kill rewards** — when you destroy an enemy unit or structure, you receive a percentage of its cost (default 20%). Works for direct kills and structure demolitions.
3. **Zone presence** — while one of your units is inside the capture zone, you receive a per-second tick proportional to the unit's cap value (only active in the full KoH mode).

The commander's **faction treasury** also earns credits per-tick while team units are in the zone (with a handicap multiplier favouring the trailing team). That's separate from your personal `/buy` credits.

### Anti-build inside the zone

If you try to construct a structure inside the visible exclusion radius around the Galactic Teleporter, the server will reject it and warn the team. Build outside the ring.

---

## Si_UnitBalance

The server runs a **live unit-balance overlay** that adjusts vanilla numbers (HP, cost, build time, damage, range, projectile speeds, FOW, movement, etc.) per faction. You can inspect the actual values for whatever you're piloting.

| Command | What it does |
|--|--|
| `/stats` | Open a read-only inspector for the unit you are currently controlling. Browse parameter groups with `/1`–`/N`. `/back` goes up a level, `/0` closes |

Useful to confirm "how much HP does the Pyro Tank actually have on this server?" or "what's the real range of the modded Scorpion?". You must be controlling a unit when you type `/stats`.

`/stats` is independent of any other menu — opening it doesn't affect `/buy` or admin menus. Closes automatically when you exit (`/0` or `/back` from the top level).

---

## Si_HeavyTeleporter

A **vehicle-teleport tool for the human factions** (Sol & Centauri). Once your team reaches the configured tech tier (server-specific), any soldier on your team can call a team vehicle to their current position — useful for getting a Hover Tank, Siege Tank, or other heavy unit across the map without driving it the whole way.

> **Only vehicles standing in front of your team's Ultra Heavy Factory** are teleportable. Park the vehicle you want delivered on the UHF's staging pad first; the `/st` menu only lists vehicles in that pickup area.

| Command | What it does |
|--|--|
| `/st` | Request a heavy vehicle teleport to you. Opens a menu listing nearby teleportable vehicles |
| `/st <N>` | Pick vehicle N from the menu shown by `/st` (e.g. `/st 1`) |
| `/st status` | Read-only: enabled state, tech tier required, your team's remaining charges, recharge timer |

Limits:
- **Faction**: Sol or Centauri only (the alien faction has its own movement options).
- **Tech tier**: your team must be at or above the configured threshold. The reply will tell you what's required if you're under-tier.
- **Charges**: each team has a shared pool of teleport charges that recharge over time. If empty, you'll see a *"Recharging... Xs remaining"* message.
- **Distance from enemy base**: blocked if too close to enemy HQ / Nest (anti-rush guard).

Commanders and admins have two additional forms (`/st <playername>` to teleport someone, `/st <x> <z>` to teleport themselves to coordinates) — regular players don't need to remember those.

---

## Si_CrabCannon

The Alien faction has a long-range **super-weapon**: a Crab walked into the Nest area becomes a ballistic projectile and arcs across the map. Default cannon parameters (range, payload type) are server-controlled, but the **Super Weapon** mode lets the commander — and optionally individual players — aim their own shots.

### `/ccaim`

Available depending on what the admin allows:
- **Admins** — always
- **Commanders** — only if `CommanderAimAllowed` is ON (admin toggles it with `/cc commanderaim`)
- **Regular players** — only if `PlayerAimAllowed` is ON (admin toggles it with `/cc playeraim`)

If neither is enabled for you, `/ccaim` will tell you the aim is locked.

| Command | What it does |
|--|--|
| `/ccaim` | Show your current personal aim (or, if you haven't set one, the cannon + super defaults) |
| `/ccaim <angle> <speed>` | Set aim manually. Angle 1–89°, speed 1–800. Range is capped at the server's `MAX_RANGE`. Commanders set the **team-wide super-weapon defaults** (broadcast in team chat). Regular players set a **personal override** that only affects their own launches |
| `/ccaim range <meters>` | Convenience: compute the speed needed to land at `<meters>` using your current angle (e.g. `/ccaim range 1800`). Auto-rejected if it would exceed limits |

When the cannon is unlocked, the server announces it in chat with a hint: *"[CANNON] Crab Cannon unlocked! Walk a crab to the Nest to launch."*

---

## Tips & common gotchas

- **`/1`, `/2`, ... `/0`, `/back`** are shared navigation across all the mods. They route to whichever menu you have open. If two menus are open at once they fight — close one first (`/0`) before opening another.
- **`/buy` and `/stats`** coexist fine. **`/buy` and `/b` (admin balance editor)** don't — `/buy` will refuse and ask you to close `/b` first (only admins ever hit this).
- **Credits don't persist between rounds** unless the server has the reset-on-end option off. Treat each round as a fresh wallet (apart from your starter credits grant).
- **Buy spawn refused?** Either you're too close to an enemy critical, or you don't have enough credits. The reply will say which.
- **You see "mod not active, spawn cfg missing"** at round start → KGT is disabled for this particular map (no capture zone configured). Normal Strategy rules apply for the round.

Have fun. If something looks broken, ping an admin in chat or on Discord.
