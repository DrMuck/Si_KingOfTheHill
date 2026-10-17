/*
 Rewards.cs — commander faction reward (+ anti-snowball handicap) and the
 per-player zone-presence credit tick.

 Since v0.5.0 the per-player credit ledger, kill rewards, bounties, starter
 credits and /buy live in the separate mod Si_Buy. Zone presence pays through
 BuyBridge (reflection into Si_Buy.Buy.GrantExternalCredits); without Si_Buy the
 tick is a no-op and the capture game runs unchanged.

 Commander reward: credited to the TEAM treasury via Team.StoreResource, so it
 needs no economy mod.
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
        static long GetPlayerSteamId(Player p)
        {
            // NetworkID has m_ID (ulong) — same as the Steam ID bits.
            try { return (long)p.PlayerID.m_ID; }
            catch { return 0; }
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
                if (key == 0) return;
                // Paid through Si_Buy when it is installed; silently nothing otherwise.
                // No per-tick chat spam — credits silently accrue. /buy shows totals.
                BuyBridge.Grant(key, reward, "KoH zone presence");
            }
            catch { }
        }
    }
}
