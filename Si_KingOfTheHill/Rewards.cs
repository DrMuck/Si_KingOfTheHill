/*
 Rewards.cs — per-player credit tracking + kill rewards + zone presence rewards.

 Credit sources:
   1. Kill reward: when a player kills a unit, +round(victim.cost * RewardKillFraction)
   2. Zone presence: each tick while controlling a unit in the capture zone,
      +round(EvalIntervalSeconds * cap * RewardZonePresenceMultiplier).
      Crab keeps cap=1 (no 0.25 penalty applied to rewards).

 Credit sink: /buy (see Buy.cs).

 Credits are keyed by Steam ID (long) for stable identity across reconnects.
 Reset between rounds when Cfg.RewardResetOnGameEnd = true (default).
*/

using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        // Steam ID -> credit balance. Cleared on round end if Cfg.RewardResetOnGameEnd.
        static readonly Dictionary<long, int> _playerCredits = new Dictionary<long, int>();

        static bool _rewardsHooked;

        // ----- Public accessors -----

        public static int GetCredits(Player player)
        {
            if (player == null) return 0;
            long key = GetPlayerSteamId(player);
            return _playerCredits.TryGetValue(key, out int c) ? c : 0;
        }

        static int GetCredits(long steamId)
            => _playerCredits.TryGetValue(steamId, out int c) ? c : 0;

        static void AddCredits(long steamId, int delta)
        {
            if (delta == 0) return;
            int now = GetCredits(steamId) + delta;
            if (now < 0) now = 0;
            _playerCredits[steamId] = now;
        }

        // ----- Cross-mod API (reflection-friendly) -----
        // Other mods (e.g. Si_Logistics delivery bonus) call this via reflection:
        //   asm.GetType("Si_KingOfTheHill.KingOfTheHill")
        //      .GetMethod("GrantExternalCredits", Public|Static, null, new[]{typeof(long),typeof(int),typeof(string)}, null)
        // Primitive parameters keep the reflected signature independent of game types.
        // Returns false (and credits nothing) while the /buy economy is inactive (Mode 3 or Cfg.Enabled = false).
        public static bool GrantExternalCredits(long steamId, int amount, string reason)
        {
            if (!ModeBuyActive || steamId == 0 || amount <= 0) return false;
            AddCredits(steamId, amount);
            MelonLogger.Msg($"[KGT] +{amount}cr -> {steamId} ({reason ?? "external"}). Total: {GetCredits(steamId)}cr");
            return true;
        }

        public static int GetCreditsBySteamId(long steamId)
            => ModeBuyActive ? GetCredits(steamId) : 0;

        static long GetPlayerSteamId(Player p)
        {
            // NetworkID has m_ID (ulong) — same as the Steam ID bits.
            try { return (long)p.PlayerID.m_ID; }
            catch { return 0; }
        }

        static void ResetAllCredits()
        {
            _playerCredits.Clear();
        }

        /// <summary>
        /// Grant Cfg.StarterCreditsPerPlayer to each connected player who hasn't yet
        /// received it this round. Single-shot per Steam ID (tracked in
        /// _starterCreditsGranted), so calling repeatedly from OnUpdate is safe.
        /// </summary>
        static void GrantStarterCreditsToAll()
        {
            int amount = Cfg.StarterCreditsPerPlayer;
            if (amount <= 0) return;
            try
            {
                var players = Player.Players;
                if (players == null) return;
                int granted = 0;
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p == null) continue;
                    // Skip the server's ghost player.
                    if (p == NetworkGameServer.GetServerPlayer()) continue;
                    long sid = GetPlayerSteamId(p);
                    if (sid == 0) continue;
                    if (!_starterCreditsGranted.Add(sid)) continue;  // already received
                    AddCredits(sid, amount);
                    granted++;
                    try
                    {
                        HelperMethods.SendChatMessageToPlayer(p,
                            $"[KGT] Welcome — starter credits: <color=#39ff14>{amount}</color>cr. Type /buy to spend.");
                    }
                    catch { }
                }
                if (granted > 0)
                    MelonLogger.Msg($"[KGT] Starter credits: granted {amount}cr to {granted} player(s)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] GrantStarterCreditsToAll threw: {ex.Message}");
            }
        }

        // ----- Event hookup -----

        static void HookRewards()
        {
            if (_rewardsHooked) return;
            try
            {
                GameEvents.OnUnitDestroyed -= OnUnitDestroyed_Reward;
                GameEvents.OnUnitDestroyed += OnUnitDestroyed_Reward;
                GameEvents.OnStructureDestroyed -= OnStructureDestroyed_Reward;
                GameEvents.OnStructureDestroyed += OnStructureDestroyed_Reward;
                _rewardsHooked = true;
                MelonLogger.Msg("[KGT] Rewards: subscribed to OnUnitDestroyed + OnStructureDestroyed");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] HookRewards failed: {ex.Message}");
            }
        }

        // GameEvents clear on scene transition — call from OnSceneWasLoaded too.
        static void ReHookRewards()
        {
            _rewardsHooked = false;
            HookRewards();
        }

        // ----- Kill reward -----

        static void OnUnitDestroyed_Reward(Unit unit, GameObject instigator)
        {
            try
            {
                if (!ModeBuyActive || unit == null || unit.ObjectInfo == null || instigator == null) return;

                // Walk instigator -> BaseGameObject -> OwnerPlayer
                BaseGameObject? attacker = GameFuncs.GetBaseGameObject(instigator);
                Player? killer = attacker?.NetworkComponent?.OwnerPlayer;
                if (killer == null) return;

                // Skip self-kill / friendly-fire reward (still rewards for player vs player on
                // *enemy* team — we use Silica's enemy check for clarity).
                if (unit.Team != null && killer.Team != null && unit.Team == killer.Team) return;

                if (!TryGetEffectiveCost(unit.ObjectInfo, out int cost, out string victimName)) return;

                int reward = Mathf.RoundToInt(cost * Cfg.RewardKillFraction);
                if (reward <= 0) return;

                long key = GetPlayerSteamId(killer);
                AddCredits(key, reward);
                try
                {
                    HelperMethods.SendChatMessageToPlayer(killer,
                        $"[KGT] +<color=#39ff14>{reward}</color>cr (killed {victimName}). Total: {GetCredits(key)}cr");
                }
                catch { }

                // Headhunter bounty bonus — paid on top of the normal kill reward.
                PayBountyIfApplicable(unit, killer);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] OnUnitDestroyed_Reward threw: {ex.Message}");
            }
        }

        // ----- Structure-kill reward (mirror of unit-kill, separate event) -----

        static void OnStructureDestroyed_Reward(Structure structure, GameObject instigator)
        {
            try
            {
                if (!ModeBuyActive || !Cfg.StructureKillRewardsEnabled || structure == null || structure.ObjectInfo == null || instigator == null) return;

                BaseGameObject? attacker = GameFuncs.GetBaseGameObject(instigator);
                Player? killer = attacker?.NetworkComponent?.OwnerPlayer;
                if (killer == null) return;

                // No reward for destroying own/allied structure.
                if (structure.Team != null && killer.Team != null && structure.Team == killer.Team) return;

                if (!TryGetEffectiveCost(structure.ObjectInfo, out int cost, out string victimName)) return;

                int reward = Mathf.RoundToInt(cost * Cfg.RewardKillFraction);
                if (reward <= 0) return;

                long key = GetPlayerSteamId(killer);
                AddCredits(key, reward);
                try
                {
                    HelperMethods.SendChatMessageToPlayer(killer,
                        $"[KGT] +<color=#39ff14>{reward}</color>cr (destroyed {victimName}). Total: {GetCredits(key)}cr");
                }
                catch { }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] OnStructureDestroyed_Reward threw: {ex.Message}");
            }
        }

        // ----- Commander handicap (anti-snowball) -----
        //
        // Periodically scores every non-neutral team by resources + unit count and
        // assigns each a reward multiplier so the trailing team earns more per
        // tick than the dominant one. Cheap: a handful of property reads every
        // HandicapRecomputeIntervalSeconds, cached in a dict for O(1) lookup at
        // reward time.

        static readonly Dictionary<Team, float> _handicapMult = new Dictionary<Team, float>();
        static float _handicapTimer = 0f;

        /// <summary>
        /// Recompute per-team handicap multipliers. Called from OnUpdate every tick;
        /// only does real work every Cfg.HandicapRecomputeIntervalSeconds.
        /// </summary>
        static void RecomputeHandicap(float dt)
        {
            if (!Cfg.HandicapEnabled)
            {
                if (_handicapMult.Count > 0) _handicapMult.Clear();
                return;
            }

            _handicapTimer -= dt;
            if (_handicapTimer > 0f) return;
            _handicapTimer = Mathf.Max(1f, Cfg.HandicapRecomputeIntervalSeconds);

            try
            {
                var teams = Team.Teams;
                if (teams == null || teams.Count < 2) { _handicapMult.Clear(); return; }

                // Two-pass: first build raw scores, then derive multipliers from the mean.
                var raw = new Dictionary<Team, float>();
                float total = 0f;
                int active = 0;

                for (int i = 0; i < teams.Count; i++)
                {
                    var team = teams[i];
                    if (team == null) continue;
                    if (IsGamemasterTeam(team)) continue;  // skip Wildlife / GM / Worm

                    float resources = 0f;
                    int unitCount = 0;
                    try { resources = team.TotalResources; } catch { }
                    try { unitCount = team.Units?.Count ?? 0; } catch { }

                    float score = resources * Cfg.HandicapResourceWeight
                                + unitCount * 100f * Cfg.HandicapMilitaryWeight;
                    raw[team] = score;
                    total += score;
                    active++;
                }

                _handicapMult.Clear();
                if (active < 2 || total <= 0f) return;

                float avg = total / active;
                float k = Cfg.HandicapStrength;
                float floor = Mathf.Max(0.01f, Cfg.HandicapFloorMult);
                float ceiling = Mathf.Max(floor, Cfg.HandicapCeilingMult);

                foreach (var kv in raw)
                {
                    float ratio = kv.Value / avg;
                    if (ratio <= 0f) ratio = 0.001f;  // avoid divide-by-zero / NaN
                    // mult = ratio^(-k). Default k=1 gives mult = 1/ratio.
                    float mult = Mathf.Pow(ratio, -k);
                    mult = Mathf.Clamp(mult, floor, ceiling);
                    _handicapMult[kv.Key] = mult;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] RecomputeHandicap threw: {ex.Message}");
            }
        }

        /// <summary>O(1) lookup. Returns 1.0 when handicap is disabled or team isn't tracked yet.</summary>
        static float GetHandicapMult(Team team)
        {
            if (!Cfg.HandicapEnabled || team == null) return 1f;
            return _handicapMult.TryGetValue(team, out float m) ? m : 1f;
        }

        // ----- Commander faction reward (called per-tick from OnUpdate, per team in zone) -----
        //
        // Mirrors the capture-progress cap: a team's effective per-tick contribution
        // is min(team_score, MaxCapturePointsPerSecond * dt). Reward = effective *
        // CommanderRewardMultiplier, rounded. Credited to the team treasury via
        // Team.StoreResource; any overflow (storage full) goes to Team.StartingResources
        // so the budget rises even with full silos.
        static void GrantCommanderRewards(Dictionary<Team, float> scores, float deltaSeconds)
        {
            try
            {
                if (!ModeKohActive || !Cfg.CommanderRewardEnabled) return;
                if (Cfg.CommanderRewardMultiplier <= 0f) return;
                if (scores == null || scores.Count == 0) return;

                float capPerTick = Cfg.MaxCapturePointsPerSecond * deltaSeconds;
                if (capPerTick <= 0f) return;

                foreach (var kvp in scores)
                {
                    var team = kvp.Key;
                    if (team == null) continue;
                    if (IsGamemasterTeam(team)) continue;

                    float effective = Mathf.Min(kvp.Value, capPerTick);
                    float handicap = GetHandicapMult(team);
                    int reward = Mathf.RoundToInt(effective * Cfg.CommanderRewardMultiplier * handicap);
                    if (reward <= 0) continue;

                    int leftover = team.StoreResource(reward);
                    if (leftover > 0)
                    {
                        try { team.StartingResources = team.StartingResources + leftover; }
                        catch { /* setter fires RPC sync — swallow if context not ready */ }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] GrantCommanderRewards threw: {ex.Message}");
            }
        }

        // ----- Zone-presence reward (called from Zone.EvaluateScores per controlled-unit-in-zone) -----

        static void GrantZonePresenceReward(Player player, int unitCap, string unitName)
        {
            try
            {
                if (player == null || unitCap <= 0) return;
                if (Cfg.RewardZonePresenceMultiplier <= 0f) return;

                // Crab override: when configured, ignore the scoring penalty in the reward calc.
                // (Our formula doesn't use the scoring multiplier — this clause is a no-op
                //  unless we later change the formula. Kept for the explicit user spec.)
                float capForReward = unitCap;
                if (Cfg.RewardZoneCrabSkipPenalty && unitName == "Crab" && capForReward < 1f)
                    capForReward = 1f;

                float rewardF = Cfg.EvalIntervalSeconds * capForReward * Cfg.RewardZonePresenceMultiplier;
                int reward = Mathf.RoundToInt(rewardF);
                if (reward <= 0) return;

                long key = GetPlayerSteamId(player);
                AddCredits(key, reward);
                // No per-tick chat spam — credits silently accrue. /buy or status shows totals.
            }
            catch { }
        }
    }
}
