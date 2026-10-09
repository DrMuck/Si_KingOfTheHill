/*
 Si_KingOfTheHill - v0.1.0 (scaffold)

 3-team King of the Hill mode for Silica. A neutral tower in the middle of the
 map is captured by whichever team holds the highest weighted unit count inside
 a cylindrical zone. When the global capture timer expires, the current king
 wins the round.

 See DESIGN.md (in repo root) for full spec.

 Server-only mod (no client component required).
 Deploy: E:\Steam\steamapps\common\Silica Dedicated Server\Mods\Si_KingOfTheHill.dll
 Config: UserData\KingOfTheHill_cfg\Si_KingOfTheHill_Config.json
*/

using MelonLoader;
using SilicaAdminMod;
using System;
using System.IO;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_KingOfTheHill.KingOfTheHill), "King of the Galactic Teleport", "0.3.0", "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonOptionalDependencies("Admin Mod", "Si_UnitBalance")]

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill : MelonMod
    {
        // Tick gating — re-evaluate zone occupancy at this interval.
        static float _evalTimer = 0f;

        // Time.time stamp of the current round's start. Used to gate time-locked
        // purchases (Shrimp buy unlock). 0 = no round started yet (fail-open).
        internal static float _roundStartTime = 0f;

        // === Operating mode helpers (read Cfg.Mode + Cfg.Enabled) ===
        // ModeKohActive  → full KoH gameplay (intro, capture, immunity, AntiBuild, rewards)
        // ModeBuyActive  → /buy command works (Mode 1 or 2)
        // ModeAnyActive  → any subsystem allowed (Mode 1, 2, or anything other than Disabled)
        public static bool ModeKohActive => Cfg != null && Cfg.Enabled && Cfg.Mode == 1;
        public static bool ModeBuyActive => Cfg != null && Cfg.Enabled && (Cfg.Mode == 1 || Cfg.Mode == 2);
        public static bool ModeAnyActive => Cfg != null && Cfg.Enabled && Cfg.Mode != 3;

        // Starter credits tracking (per Steam ID, per round). Cleared on game end.
        static readonly System.Collections.Generic.HashSet<long> _starterCreditsGranted
            = new System.Collections.Generic.HashSet<long>();
        // Periodic check timer for granting starter credits to mid-round joiners.
        static float _starterCreditsCheckTimer = 0f;
        const float STARTER_CREDITS_CHECK_INTERVAL = 5.0f;

        // Startup-grace window after each game start. If no KoH has been registered
        // (i.e. Si_MapBalance didn't spawn one because the active spawn config has
        // no "koh" entry), broadcast a one-time "mod not active" notice in chat.
        // Delay covers the case where Si_MapBalance's Harmony patch fires AFTER
        // GameEvents.OnGameStarted.
        const float STARTUP_CHECK_DELAY = 3.0f;
        static float _startupCheckTimer = 0f;
        static bool _startupCheckPending = false;

        // Per-line scripted intro: chat broadcast → IntroSoundLeadSeconds → spoken
        // wavs (chained back-to-back) → IntroLinePauseSeconds → next chat line.
        //
        // Each clip MUST stay BELOW 5.46 seconds — AdminMod's audio streamer packs
        // the sample offset as a UInt16 and the value wraps at 65536 samples
        // (5.46s @ 12 kHz). Wrapped offsets land back near zero on the client,
        // corrupting the second half of any wav longer than that → stutter.
        //
        // To preserve the full spoken explanation despite the limit, long lines are
        // split into multiple wavs that play in sequence (intro_NNa.wav, intro_NNb.wav,
        // ...). The state machine plays each clip with a tiny inter-clip gap so the
        // transition is seamless.
        static readonly (string chat, (string sound, float dur)[] clips)[] _introScript = new (string, (string, float)[])[]
        {
            ("[KGT] <color=#39ff14>=== KING OF THE GALACTIC TELEPORT ===</color>",
                new (string, float)[] { ("sounds/intro_01a.wav", 2.76f) }),
            ("[KGT] A neutral Galactic Teleporter has appeared somewhere on the map. Capture it for your team.",
                new (string, float)[] { ("sounds/intro_02a.wav", 4.34f), ("sounds/intro_02b.wav", 2.50f) }),
            ("[KGT] Move units into the capture zone. The team with majority presence holds the Teleporter.",
                new (string, float)[] { ("sounds/intro_03a.wav", 3.29f), ("sounds/intro_03b.wav", 4.34f) }),
            ("[KGT] Capture progress is shown in your team's colour. First team to fully capture wins the round.",
                new (string, float)[] { ("sounds/intro_04a.wav", 3.60f), ("sounds/intro_04b.wav", 3.62f) }),
            ("[KGT] <color=#ffcc00>Commander reward:</color> while your team has units in the zone, resources tick into your faction treasury automatically.",
                new (string, float)[] { ("sounds/intro_05a.wav", 5.16f), ("sounds/intro_05b.wav", 4.13f) }),
            ("[KGT] <color=#ffcc00>Player reward:</color> earn personal credits for kills and for time spent in the zone.",
                new (string, float)[] { ("sounds/intro_06a.wav", 4.90f), ("sounds/intro_06b.wav", 2.66f) }),
            ("[KGT] Use <color=#39ff14>/buy</color> to spend credits on units. Type <color=#39ff14>/koh</color> for live capture status.",
                new (string, float)[] { ("sounds/intro_07a.wav", 3.70f), ("sounds/intro_07b.wav", 3.53f) }),
        };
        // Tiny gap inserted between consecutive clips of the same line so the audio
        // streamer (and client buffer) finishes the prior packet cleanly before the
        // next PlaySound triggers a new stream.
        const float INTRO_INTER_CLIP_GAP = 0.15f;
        // Stage machine state:
        //   0 = ready to broadcast chat for _introIdx
        //   1 = chat broadcast; playing clips of _introScript[_introIdx] sequentially
        //   2 = all clips played; waiting IntroLinePauseSeconds before next line
        static int   _introIdx     = -1;  // -1 = inactive; 0..N-1 = current line
        static int   _introClipIdx = 0;   // which clip within current line is next
        static int   _introStage   = 0;
        static float _introTimer   = 0f;

        public override void OnInitializeMelon()
        {
            _configPath = Path.Combine("UserData", "KingOfTheHill_cfg", "Si_KingOfTheHill_Config.json");
            LoadConfig();
            MelonLogger.Msg("Si_KingOfTheHill v0.1.0 loaded (scaffold — game logic not yet implemented).");

            // Lifecycle hooks. GameEvents.* clears across scene transitions; re-hook in OnSceneWasLoaded.
            GameEvents.OnGameStarted += OnGameStarted;
            GameEvents.OnGameEnded += OnGameEnded;

            // Damage immunity for the KoH building.
            InstallDamagePatch(HarmonyInstance);

            // Reward system (kill events).
            HookRewards();

            // /buy menu /1-/N chat navigation.
            HookBuyChat();

            // Headhunter bounty list (separate JSON in UserData/KingOfTheHill_cfg/Bounties.json).
            LoadBountyConfig();
        }

        public override void OnLateInitializeMelon()
        {
            // /koh registered as a player command (hidden from chat). Admin-only sub-actions
            // are gated inside the handler via caller.CanAdminExecute(Power.Generic).
            PlayerMethods.RegisterPlayerCommand("koh", OnKohCommand, true);
            PlayerMethods.RegisterPlayerCommand("buy", OnBuyCommand, true);
            MelonLogger.Msg("Si_KingOfTheHill: Registered /koh and /buy commands.");

            // Try to subscribe to Si_MapBalance now; if it loaded after us, we'll retry on first
            // game start. Subscription is the channel by which we learn the per-map KoH position.
            TrySubscribeMapBalance();

            // Anti-build via SilicaAdminMod.Event_Construction.OnRequestBuildStructure.
            HookAntiBuild();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // GameEvents.* gets cleared across scene transitions — re-hook.
            GameEvents.OnGameStarted -= OnGameStarted;
            GameEvents.OnGameStarted += OnGameStarted;
            GameEvents.OnGameEnded -= OnGameEnded;
            GameEvents.OnGameEnded += OnGameEnded;
            // Rewards too.
            ReHookRewards();
            ReHookBuyChat();
        }

        static void OnGameStarted(GameMode mode)
        {
            if (!ModeAnyActive) return;  // Mode 3 (Disabled) or master toggle off

            // Stamp round start for time-locked purchases (Shrimp unlock, etc.).
            _roundStartTime = Time.time;

            // NOTE: do NOT ResetKohState() here. Silica's GameEvents.OnGameStarted fires
            // SEVERAL SECONDS after MusicJukeboxHandler.OnGameStarted (which is the hook
            // MapBalance's Harmony patch uses to spawn the KoH and fire OnSpecialsReady).
            // Resetting here would clobber the state MapBalance just populated.
            // State reset happens in OnGameEnded; OnMapBalanceSpecialsReady also clears+repopulates.

            // Retry MapBalance subscription only when KoH gameplay is active.
            if (ModeKohActive) TrySubscribeMapBalance();

            // Always load the dump — /buy needs it regardless of mode.
            LoadUnitDump();
            // Apply Si_UnitBalance's cost_mult overrides on top of the vanilla dump snapshot.
            // Reading ConstructionData.ResourceCost would return vanilla (OM is a runtime
            // overlay, not a field mutation), so we parse Si_UnitBalance_Config.json instead.
            // Re-applied each round start so live config edits take effect on next round.
            ApplyUnitBalanceCostOverrides();

            if (ModeKohActive)
            {
                // Arm the startup-grace check (handled in OnUpdate).
                _startupCheckTimer = STARTUP_CHECK_DELAY;
                _startupCheckPending = true;
            }

            MelonLogger.Msg($"[KGT] OnGameStarted — map={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} (_hasKoh={_hasKoh}) Mode={Cfg.Mode}");

            // Game-mode intro: only in Mode 1 + IntroEnabled + KoH actually spawned this round.
            if (ModeKohActive && _hasKoh && Cfg.IntroEnabled) BroadcastIntro();

            // Starter credits: grant to all currently connected players (Mode 1 & 2).
            _starterCreditsGranted.Clear();
            _starterCreditsCheckTimer = STARTER_CREDITS_CHECK_INTERVAL;
            if (ModeBuyActive && Cfg.StarterCreditsPerPlayer > 0)
            {
                GrantStarterCreditsToAll();
            }
        }

        /// <summary>
        /// Multi-line chat intro + optional voice-over at round start. Explains the
        /// capture mechanics, /koh status command, and the two reward systems (team
        /// treasury for the commander, personal credits for players).
        /// </summary>
        static void BroadcastIntro()
        {
            // Kick the state machine. OnUpdate / TickIntroQueue does the rest.
            // Delay the first chat by IntroStartDelaySeconds so the round-start
            // burst of join/spawn/scene-load messages finishes first.
            _introIdx     = 0;
            _introClipIdx = 0;
            _introStage   = 0;
            _introTimer   = Mathf.Max(0f, Cfg.IntroStartDelaySeconds);
        }

        /// <summary>
        /// Per-line intro state machine. Called from OnUpdate every tick.
        ///   Stage 0: broadcast chat for _introIdx, then wait IntroSoundLeadSeconds.
        ///   Stage 1: play the next clip of the current line. When clips remain,
        ///            schedule the next one after clip.duration + INTRO_INTER_CLIP_GAP
        ///            (stays in stage 1). When the last clip has been started, move
        ///            to stage 2 after the same delay.
        ///   Stage 2: wait IntroLinePauseSeconds, then advance to the next line.
        /// </summary>
        static void TickIntroQueue(float dt)
        {
            if (_introIdx < 0 || _introIdx >= _introScript.Length) return;

            _introTimer -= dt;
            if (_introTimer > 0f) return;

            var line = _introScript[_introIdx];

            if (_introStage == 0)
            {
                BroadcastAllChat(line.chat);
                _introClipIdx = 0;
                _introTimer = Mathf.Max(0f, Cfg.IntroSoundLeadSeconds);
                _introStage = 1;
            }
            else if (_introStage == 1)
            {
                // More clips to play?
                if (_introClipIdx < line.clips.Length)
                {
                    var clip = line.clips[_introClipIdx];
                    if (!string.IsNullOrEmpty(clip.sound)) PlaySound(clip.sound);
                    _introClipIdx++;
                    _introTimer = clip.dur + INTRO_INTER_CLIP_GAP;
                }
                else
                {
                    // Last clip finished; pause before next line.
                    _introTimer = Mathf.Max(0f, Cfg.IntroLinePauseSeconds);
                    _introStage = 2;
                }
            }
            else // stage 2: advance to next line
            {
                _introIdx++;
                _introClipIdx = 0;
                _introStage = 0;
                _introTimer = 0f;
                if (_introIdx >= _introScript.Length) _introIdx = -1; // done
            }
        }

        static void ResetIntroState()
        {
            _introIdx     = -1;
            _introClipIdx = 0;
            _introStage   = 0;
            _introTimer   = 0f;
        }

        static void OnGameEnded(GameMode mode, Team winner)
        {
            // Clear KoH state at end of round. Old GameObject reference becomes invalid on
            // scene transition; clearing here means we start the next round with _hasKoh=false
            // and rely on OnMapBalanceSpecialsReady (or the startup-grace broadcast) for the
            // next state transition.
            ResetKohState();
            _startupCheckPending = false;
            // Stop any in-flight intro so it doesn't bleed into the next round.
            ResetIntroState();
            // Reset credits between rounds if configured.
            if (Cfg.RewardResetOnGameEnd) ResetAllCredits();
            // Also clear any active buy menus so they don't carry stale state across rounds.
            ResetBuyState();
            // Re-announce bounties at the start of the next round.
            _announcedBounties.Clear();
            MelonLogger.Msg("[KGT] OnGameEnded — KoH state cleared.");
        }

        public override void OnUpdate()
        {
            if (!NetworkGameServer.GetServerStarted()) return;
            if (!ModeAnyActive) return;

            // Starter-credits + bounty-announcement poll (Mode 1 & 2). Both walk
            // Player.Players, so we cycle them on the same 5-second timer.
            if (ModeBuyActive)
            {
                _starterCreditsCheckTimer -= Time.deltaTime;
                if (_starterCreditsCheckTimer <= 0f)
                {
                    _starterCreditsCheckTimer = STARTER_CREDITS_CHECK_INTERVAL;
                    if (Cfg.StarterCreditsPerPlayer > 0) GrantStarterCreditsToAll();
                    AnnounceBountiesIfNeeded();
                }
            }

            // Everything below is Mode 1 (full KoH) only.
            if (!ModeKohActive) return;

            // Startup-grace check: after STARTUP_CHECK_DELAY, if MapBalance never registered
            // a KoH for this map, announce that the mod is inactive this round.
            if (_startupCheckPending)
            {
                _startupCheckTimer -= Time.deltaTime;
                if (_startupCheckTimer <= 0f)
                {
                    _startupCheckPending = false;
                    if (!_hasKoh)
                        BroadcastAllChat("[KGT] mod not active, spawn cfg missing");
                    else
                        MelonLogger.Msg("[KGT] startup check OK — KoH active this round.");
                }
            }

            // Drain the round-start intro one line at a time — driven by real-time
            // delta, not the EvalInterval gate, so the pacing stays consistent even
            // if EvalIntervalSeconds is tuned high.
            TickIntroQueue(Time.deltaTime);

            // KoH auto-respawn watchdog — runs independent of the eval gate so it
            // recovers fast if a player slipped damage past the immunity patches.
            TickKohAliveCheck(Time.deltaTime);

            _evalTimer -= Time.deltaTime;
            if (_evalTimer > 0f) return;
            float dt = Cfg.EvalIntervalSeconds;
            _evalTimer = dt;

            // Per-tick capture pipeline:
            //   Zone score (+ player set) → King dominance + accumulation → status broadcast → player enter/exit msgs
            try
            {
                RecomputeHandicap(dt);  // cheap; only does real work every Cfg.HandicapRecomputeIntervalSeconds
                var scores = EvaluateScores();
                UpdateOwnership(scores, dt);
                GrantCommanderRewards(scores, dt);
                TickTimer(dt);
                ProcessPlayerNotifications();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] tick failed: {ex.Message}");
            }
        }
    }
}
