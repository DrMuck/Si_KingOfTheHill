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
                if (!Cfg.Enabled || unit == null || unit.ObjectInfo == null || instigator == null) return;

                // Walk instigator -> BaseGameObject -> OwnerPlayer
                BaseGameObject? attacker = GameFuncs.GetBaseGameObject(instigator);
                Player? killer = attacker?.NetworkComponent?.OwnerPlayer;
                if (killer == null) return;

                // Skip self-kill / friendly-fire reward (still rewards for player vs player on
                // *enemy* team — we use Silica's enemy check for clarity).
                if (unit.Team != null && killer.Team != null && unit.Team == killer.Team) return;

                string victimName = unit.ObjectInfo.DisplayName ?? "";
                if (!_unitCost.TryGetValue(victimName, out int cost) || cost <= 0) return;

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
                if (!Cfg.Enabled || structure == null || structure.ObjectInfo == null || instigator == null) return;

                BaseGameObject? attacker = GameFuncs.GetBaseGameObject(instigator);
                Player? killer = attacker?.NetworkComponent?.OwnerPlayer;
                if (killer == null) return;

                // No reward for destroying own/allied structure.
                if (structure.Team != null && killer.Team != null && structure.Team == killer.Team) return;

                string victimName = structure.ObjectInfo.DisplayName ?? "";
                if (!_unitCost.TryGetValue(victimName, out int cost) || cost <= 0) return;

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
