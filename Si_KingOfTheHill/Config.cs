using MelonLoader;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        // === Config model — mirrors §9 of DESIGN.md ===
        public class KohConfig
        {
            public bool Enabled = true;
            public bool OnlyInStrategyMode = true;

            // Operating mode (gates which mod subsystems run):
            //   1 = Full KoH  → intro, capture zone, building protection, AntiBuild, /buy, rewards (default)
            //   2 = BuyOnly   → no intro, no capture zone, no KoH spawn / immunity / AntiBuild.
            //                   /buy still works, kill rewards still fire, starter credits granted.
            //   3 = Disabled  → mod loads but every subsystem is inert.
            // Change at runtime with `/koh mode <N>` (admin only).
            public int Mode = 1;

            // Starter credits granted to each player once per round (modes 1 & 2 only).
            // 0 disables. Players joining mid-round receive the grant on connect.
            public int StarterCreditsPerPlayer = 0;

            // When true, /buy is restricted to admins (anyone who passes
            // SilicaAdminMod's CanAdminExecute check). Default false = everyone can /buy.
            public bool BuyAdminOnly = false;

            // Master switch for the headhunter bounty system. When false, no announcements
            // fire and no kill payouts are made (even if Bounties.json has entries).
            // Default true so a populated Bounties.json "just works" out of the box.
            public bool BountiesEnabled = true;

            // Note: the KoH prefab is owned by Si_MapBalance (its KohEntry.PrefabName field).
            // KGT just consumes the spawned GameObject via MapBalanceSpecials.Koh.
            public float CaptureRadius = 50.0f;
            // 0 = use the runtime exclusion radius from MapBalance's per-map KoH entry
            // (the same value broadcast via kgt_koh_spawn and visualized in MapReplay).
            // Set > 0 to force a fixed radius regardless of the map's MapBalance config.
            public float BuildExclusionRadius = 0.0f;

            public float EvalIntervalSeconds = 1.0f;
            public float OwnershipHandoverDelay = 3.0f; // small smoothing to avoid edge-of-dominance king flicker

            // === Accumulation-based win model ===
            // Each tick, the team whose share of in-zone score >= DominanceThreshold "owns" the tick.
            // That team gains min(team_score, MaxCapturePointsPerSecond * tickSeconds) points.
            // First team to reach WinThreshold wins the round.
            public float WinThreshold = 50000.0f;
            public float MaxCapturePointsPerSecond = 100.0f;
            public float DominanceThreshold = 0.60f;

            // Per-map win threshold override. Key = Silica scene name (e.g. "NorthPolarCap",
            // "Badlands", "IndustrialQuarter"). Value = points. If the current map isn't in
            // this dict, WinThreshold (above) is used. Lets you scale game length to map size.
            public Dictionary<string, float> WinThresholdPerMap = new Dictionary<string, float>
            {
                // { "NorthPolarCap", 30000 },
                // { "Badlands",      80000 },
            };

            // When true (default): a single shared accumulator. New king continues where
            // previous king left off — no per-team progress.
            // When false: each team has their own bucket. King only progresses their own.
            public bool CumulativeAccumulation = true;

            public float PlayerControlledMultiplier = 2.0f;

            // === Outposts (perimeter visual ring) ===
            public int OutpostCount = 12;
            public float OutpostBuryDepth = 30.0f;   // metres below TRUE surface (raycast hit, accounts for rocks/structures)

            // === Notifications ===
            public bool EnterExitNotifications = true;
            public float StatusAnnounceIntervalSeconds = 60.0f;

            // === Reward system (v0.3.0) ===
            // Per-player credits earned from kills + presence in zone, spent via /buy.
            public float RewardKillFraction = 0.10f;                 // killer gets this % of victim's initial cost
            public float RewardZonePresenceMultiplier = 2.0f;        // tick * unit_cap * this = credits per tick
            public bool  RewardZoneCrabSkipPenalty = true;           // crab's 0.25 scoring penalty doesn't apply to reward calc
            public bool  RewardResetOnGameEnd = true;                // wipe credits between rounds
            public float BuySpawnDistance = 15.0f;                   // metres in front of player to place purchased unit
            public float BuyMinDistanceFromEnemyCritical = 400.0f;   // refuse purchase if spawn would land within this radius of any enemy HQ/Nest/Queen (0 = disabled)

            // === Commander reward (per-tick, capped by MaxCapturePointsPerSecond) ===
            // Each tick, every team with units in the zone earns resources for its
            // faction treasury (helping the commander). Cap rule mirrors capture
            // progress: a team's effective per-tick contribution is
            // min(team_score_in_zone, MaxCapturePointsPerSecond * EvalIntervalSeconds).
            // Reward = effective * CommanderRewardMultiplier (rounded), credited via
            // Team.StoreResource (overflow → Team.StartingResources).
            public bool  CommanderRewardEnabled    = true;
            public float CommanderRewardMultiplier = 1.0f;

            // === Commander reward handicap (anti-snowball) ===
            // Every HandicapRecomputeIntervalSeconds, score each team as
            //   score = TotalResources * HandicapResourceWeight
            //         + Units.Count * 100 * HandicapMilitaryWeight
            // and compute the team's ratio to the average. Their commander-reward
            // multiplier is then scaled by ratio^(-HandicapStrength), clamped to
            // [HandicapFloorMult, HandicapCeilingMult]. Result:
            //   dominant team (ratio > 1)  → multiplier shrinks (gets less reward)
            //   trailing team (ratio < 1)  → multiplier grows  (gets more reward)
            // Set HandicapEnabled = false to disable and use a flat multiplier.
            public bool  HandicapEnabled                   = true;
            public float HandicapResourceWeight            = 1.0f;
            public float HandicapMilitaryWeight            = 0.5f;
            public float HandicapStrength                  = 1.0f;
            public float HandicapFloorMult                 = 0.25f;
            public float HandicapCeilingMult               = 3.0f;
            public float HandicapRecomputeIntervalSeconds  = 5.0f;

            // === Round-start intro ===
            // Multi-line chat explanation + optional voice-over wav played server-wide.
            // Set IntroEnabled=false to suppress both chat lines and sound on round start.
            // Set SoundIntro to "" to play chat only.
            public bool   IntroEnabled = true;
            // Intro is paced as a per-line script: chat line → IntroSoundLeadSeconds
            // pause → spoken wav → IntroLinePauseSeconds pause → next line. Each wav
            // is short (~3–8s) to avoid the cumulative jitter that AdminMod's
            // Task.Delay-based audio streamer suffers on long files.
            public float  IntroStartDelaySeconds = 5.0f;  // game-start → first chat broadcast
            public float  IntroSoundLeadSeconds = 0.3f;   // chat broadcast → sound start
            public float  IntroLinePauseSeconds = 1.0f;   // sound end → next chat broadcast
            // Legacy fields. Ignored now that the intro is per-line scripted. Kept
            // so old configs deserialize without errors.
            public string SoundIntro                = "";    // single-file mode removed
            public float  IntroLineIntervalSeconds  = 10.0f; // superseded by per-line timing
            public float  IntroSoundDelaySeconds    = 0.0f;  // single-file deferred play, removed

            // === Sound files (relative to server cwd; "sounds/x.wav" → UserData/sounds/x.wav) ===
            // British TTS clips generated via edge-tts. Set any to "" to disable that one.
            public string SoundFirstCapture = "sounds/first_capture.wav";
            public string SoundMilestone25 = "sounds/milestone_25.wav";
            public string SoundMilestone50 = "sounds/milestone_50.wav";
            public string SoundMilestone75 = "sounds/milestone_75.wav";
            public string SoundMilestone95 = "sounds/milestone_95.wav";
            // Played when the finishing-force units appear at end of game.
            public string SoundFinishingForce = "sounds/cannon_boom.wav";
            // Seconds AFTER the win voice line before the cannon boom + finishing force spawn.
            // Keeps the two audio cues from overlapping.
            public float WinFinishingForceDelaySeconds = 4.0f;
            // Per-team win sounds (custom TTS via edge-tts, British voices for humans).
            public string SoundWinSol = "sounds/sol_wins.wav";
            public string SoundWinCentauri = "sounds/centauri_wins.wav";
            public string SoundWinAlien = "sounds/alien_wins.wav";

            // Per-team win chat messages. Picked by team short name (Sol / Cent / Alien).
            // Anything not matched falls back to WinMessageDefault.
            public string WinMessageSol = "Sol Wins - Sol lives forever";
            public string WinMessageCentauri = "Centauri Wins - Get outta here";
            public string WinMessageAlien = "Alien wins - Praise the Queen motherfuckers";
            public string WinMessageDefault = "{name} have conquered the hill — a most resounding victory!";

            public Dictionary<string, string> BuildingToCategory = new Dictionary<string, string>
            {
                { "Barracks",               "Infantry" },
                { "Light Factory",          "LightVehicle" },
                { "Heavy Factory",          "HeavyVehicle" },
                { "Ultra Heavy Factory",    "UltraHeavyVehicle" },
                { "Air Factory",            "Air" },
                { "Lesser Spawning Cyst",   "LesserAlien" },
                { "Greater Spawning Cyst",  "GreaterAlien" },
                { "Grand Spawning Cyst",    "GrandSpawner" },
                { "Colossal Spawning Cyst", "ColossalAlien" },
            };

            public Dictionary<string, float> CategoryMultiplier = new Dictionary<string, float>
            {
                { "Infantry",          1.0f },
                { "LightVehicle",      1.0f },
                { "HeavyVehicle",      1.0f },
                { "UltraHeavyVehicle", 1.0f },
                { "Air",               1.0f },
                { "LesserAlien",       1.0f },
                { "GreaterAlien",      1.0f },
                { "GrandSpawner",      1.0f },
                { "ColossalAlien",     1.0f },
                { "Auxiliary",         0.0f },
            };

            public Dictionary<string, float> PerUnitOverride = new Dictionary<string, float>
            {
                { "Crab", 0.25f },
            };

            public string DefaultCategoryForUnknown = "Infantry";

            // Per-map KoH tower position is read from Si_MapBalance (which spawns the tower
            // from its layout JSON). No MapPositions config needed here.
        }

        // === Runtime singleton + persistence ===

        public static KohConfig Cfg = new KohConfig();
        static string _configPath = "";

        static void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var loaded = JsonConvert.DeserializeObject<KohConfig>(File.ReadAllText(_configPath));
                    if (loaded != null)
                    {
                        Cfg = loaded;
                        MelonLogger.Msg("[KGT] Config loaded from " + _configPath);
                        // Re-save so newly-added fields appear in the JSON for tuning. Preserves
                        // user-customised values (Newtonsoft only overwrites fields it parsed).
                        SaveConfig();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[KGT] Failed to load config: " + ex.Message + " — writing defaults.");
            }
            SaveConfig();
            MelonLogger.Msg("[KGT] Default config written to " + _configPath);
        }

        static void SaveConfig()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                // JsonConvert.SerializeObject(_, Formatting.Indented) is safe on Mono Newtonsoft.
                // (JToken.ToString(Formatting) is NOT — see Si_UnitBalance memory.)
                File.WriteAllText(_configPath, JsonConvert.SerializeObject(Cfg, Formatting.Indented));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[KGT] Failed to save config: " + ex.Message);
            }
        }
    }
}
