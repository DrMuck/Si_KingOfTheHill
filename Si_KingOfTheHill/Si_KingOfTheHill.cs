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

        // Startup-grace window after each game start. If no KoH has been registered
        // (i.e. Si_MapBalance didn't spawn one because the active spawn config has
        // no "koh" entry), broadcast a one-time "mod not active" notice in chat.
        // Delay covers the case where Si_MapBalance's Harmony patch fires AFTER
        // GameEvents.OnGameStarted.
        const float STARTUP_CHECK_DELAY = 3.0f;
        static float _startupCheckTimer = 0f;
        static bool _startupCheckPending = false;

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
            if (!Cfg.Enabled) return;

            // NOTE: do NOT ResetKohState() here. Silica's GameEvents.OnGameStarted fires
            // SEVERAL SECONDS after MusicJukeboxHandler.OnGameStarted (which is the hook
            // MapBalance's Harmony patch uses to spawn the KoH and fire OnSpecialsReady).
            // Resetting here would clobber the state MapBalance just populated.
            // State reset happens in OnGameEnded; OnMapBalanceSpecialsReady also clears+repopulates.

            // Retry MapBalance subscription in case it loaded after us.
            TrySubscribeMapBalance();

            // Load the Si_UnitBalance dump on the first game start so unit classification
            // is ready for scoring. Single-shot — guarded inside LoadUnitDump.
            LoadUnitDump();
            // Apply Si_UnitBalance's cost_mult overrides on top of the vanilla dump snapshot.
            // Reading ConstructionData.ResourceCost would return vanilla (OM is a runtime
            // overlay, not a field mutation), so we parse Si_UnitBalance_Config.json instead.
            // Re-applied each round start so live config edits take effect on next round.
            ApplyUnitBalanceCostOverrides();

            // Arm the startup-grace check (handled in OnUpdate).
            _startupCheckTimer = STARTUP_CHECK_DELAY;
            _startupCheckPending = true;

            MelonLogger.Msg($"[KGT] OnGameStarted — map={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} (_hasKoh={_hasKoh})");

            // Game-mode intro for players. Only when KoH is actually active this round.
            if (_hasKoh) BroadcastIntro();
        }

        /// <summary>Multi-line chat intro that explains the game mode at round start.</summary>
        static void BroadcastIntro()
        {
            BroadcastAllChat("[KGT] <color=#39ff14>=== KING OF THE GALACTIC TELEPORT ===</color>");
            BroadcastAllChat("[KGT] A neutral Galactic Teleporter has appeared at the centre of the map. Capture it for your team.");
            BroadcastAllChat("[KGT] Move units into the capture zone (the buried outpost ring marks the perimeter). The team with majority presence holds the Teleporter.");
            BroadcastAllChat("[KGT] The ring fills in your team's colour as you progress. First team to fully capture wins the round.");
            BroadcastAllChat("[KGT] Type /koh for live status.");
        }

        static void OnGameEnded(GameMode mode, Team winner)
        {
            // Clear KoH state at end of round. Old GameObject reference becomes invalid on
            // scene transition; clearing here means we start the next round with _hasKoh=false
            // and rely on OnMapBalanceSpecialsReady (or the startup-grace broadcast) for the
            // next state transition.
            ResetKohState();
            _startupCheckPending = false;
            // Reset credits between rounds if configured.
            if (Cfg.RewardResetOnGameEnd) ResetAllCredits();
            // Also clear any active buy menus so they don't carry stale state across rounds.
            ResetBuyState();
            MelonLogger.Msg("[KGT] OnGameEnded — KoH state cleared.");
        }

        public override void OnUpdate()
        {
            if (!NetworkGameServer.GetServerStarted()) return;
            if (!Cfg.Enabled) return;

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

            _evalTimer -= Time.deltaTime;
            if (_evalTimer > 0f) return;
            float dt = Cfg.EvalIntervalSeconds;
            _evalTimer = dt;

            // Per-tick capture pipeline:
            //   Zone score (+ player set) → King dominance + accumulation → status broadcast → player enter/exit msgs
            try
            {
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
