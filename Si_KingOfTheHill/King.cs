/*
 King.cs — dominance-based king + per-team capture-point accumulation.

 Each tick:
   1. From scores: compute totalScore. Dominant team = the one with
      share >= Cfg.DominanceThreshold (default 0.60). null = contested.
   2. If dominant team differs from _provisionalKing, restart handover timer.
      If dominant stays for OwnershipHandoverDelay, commit as new king.
   3. If a committed king exists this tick: add min(team_score, MaxRate * dt)
      to that team's accumulated bucket.
   4. Check milestone crossings (25/75/90/95%) and win threshold.
   5. Win → TriggerWin which destroys all OTHER teams' critical structures
      so Silica's natural end-of-round flow resolves.

 ResetKingState clears everything between rounds.
*/

using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        static Team? _currentKing;
        static Team? _provisionalKing;
        static float _handoverElapsed;

        // Accumulated capture points per team (used in per-team mode).
        static readonly Dictionary<Team, float> _accumulated = new Dictionary<Team, float>();

        // Cumulative-mode shared bucket. The current king accumulates into this single value;
        // a new king continues from where the previous king left off.
        static float _totalAccumulated;

        // Diagnostic: print one log line every accumulation bucket of 1000 pts so we can
        // verify the rate cap is being applied (at default 100 pts/s, that's ~10 ticks per log).
        static int _lastLoggedAccumulationBucket = -1;

        // Last-tick rate snapshot (exposed via /koh status so admins can see if we're
        // currently rate-capped or running below cap due to low/contested presence).
        static float _lastTickGain;       // points actually added this tick (or 0 if contested)
        static float _lastTickKingScore;  // dominant team's raw weighted score
        static float _lastTickTotalScore; // sum of all teams' scores in zone
        static float _lastTickMaxAllowed; // MaxCapturePointsPerSecond × tickInterval (the cap)

        // Milestone tracking. Per-team mode: percent -> set of teams that already triggered.
        // Cumulative mode: uses _milestonesPlayedCumulative (a single set of percents reached).
        static readonly Dictionary<int, HashSet<Team>> _milestonesPlayed = new Dictionary<int, HashSet<Team>>();
        static readonly HashSet<int> _milestonesPlayedCumulative = new HashSet<int>();
        static bool _firstCapturePlayed;
        static bool _winTriggered;

        // Status-broadcast pacing.
        static float _statusAnnounceTimer;

        /// <summary>Full reset — called on game end and on new KoH spawn.</summary>
        static void ResetKingState()
        {
            _currentKing = null;
            _provisionalKing = null;
            _handoverElapsed = 0f;
            _accumulated.Clear();
            _totalAccumulated = 0f;
            _lastLoggedAccumulationBucket = -1;
            _lastTickGain = 0f;
            _lastTickKingScore = 0f;
            _lastTickTotalScore = 0f;
            _lastTickMaxAllowed = 0f;
            _milestonesPlayed.Clear();
            _milestonesPlayedCumulative.Clear();
            _firstCapturePlayed = false;
            _winTriggered = false;
            _statusAnnounceTimer = 0f;
            ResetTimerState();
        }

        /// <summary>
        /// Drive the ownership state machine + accumulation per tick.
        /// </summary>
        static void UpdateOwnership(Dictionary<Team, float> scores, float deltaSeconds)
        {
            if (!_hasKoh || _winTriggered) return;

            // === 1. Find dominant team. ===
            float total = 0f;
            foreach (var v in scores.Values) total += v;

            Team? dominant = null;
            if (total > 0f)
            {
                float threshold = total * Cfg.DominanceThreshold;
                foreach (var kv in scores)
                {
                    if (kv.Value >= threshold) { dominant = kv.Key; break; }
                }
            }

            // === 2. Smoothed king transition. ===
            if (dominant == null)
            {
                // Contested or empty — clear pending handover but keep current king.
                _provisionalKing = null;
                _handoverElapsed = 0f;
            }
            else if (ReferenceEquals(dominant, _currentKing))
            {
                _provisionalKing = null;
                _handoverElapsed = 0f;
            }
            else if (ReferenceEquals(dominant, _provisionalKing))
            {
                _handoverElapsed += deltaSeconds;
                if (_handoverElapsed >= Cfg.OwnershipHandoverDelay)
                    CommitNewKing(dominant);
            }
            else
            {
                _provisionalKing = dominant;
                _handoverElapsed = 0f;
                // Allow zero-delay flip via OwnershipHandoverDelay=0.
                if (Cfg.OwnershipHandoverDelay <= 0f) CommitNewKing(dominant);
            }

            // === 3. Accumulation (only when there IS a committed king AND they're still dominant). ===
            // Note: _currentKing alone is not sufficient — they need to be the dominant team THIS tick.
            // This prevents a king from gaining points while contested.
            // Always snapshot the totals for /koh visibility (even when contested).
            _lastTickTotalScore = total;
            _lastTickMaxAllowed = Cfg.MaxCapturePointsPerSecond * deltaSeconds;

            if (_currentKing != null && ReferenceEquals(_currentKing, dominant))
            {
                float kingScore = scores[_currentKing];
                float maxThisTick = Cfg.MaxCapturePointsPerSecond * deltaSeconds;
                float gain = Mathf.Min(kingScore, maxThisTick);
                _lastTickKingScore = kingScore;
                _lastTickGain = gain;

                // Optional first-capture sound (once per round, before milestones).
                if (!_firstCapturePlayed)
                {
                    _firstCapturePlayed = true;
                    PlaySound(Cfg.SoundFirstCapture);
                }

                if (Cfg.CumulativeAccumulation)
                {
                    // Shared bucket. New king picks up where previous king left off.
                    float prevTotal = _totalAccumulated;
                    _totalAccumulated = prevTotal + gain;

                    // Diagnostic: log when crossing each 1000-pt bucket so user can verify
                    // the cap is being applied. Format: gain/tick (cap, raw kingScore), total.
                    int bucket = (int)(_totalAccumulated / 1000f);
                    if (bucket != _lastLoggedAccumulationBucket)
                    {
                        _lastLoggedAccumulationBucket = bucket;
                        MelonLogger.Msg($"[KGT/DBG] accumulation: this-tick gain={gain:F1}  (cap={maxThisTick:F1}, raw kingScore={kingScore:F1})  total={_totalAccumulated:F0}/{_winThreshold:F0}");
                    }

                    CheckMilestonesCumulative(_currentKing, prevTotal, _totalAccumulated);
                }
                else
                {
                    // Per-team bucket. Each team has its own progress.
                    if (!_accumulated.TryGetValue(_currentKing, out float prev)) prev = 0f;
                    float now = prev + gain;
                    _accumulated[_currentKing] = now;
                    CheckMilestones(_currentKing, prev, now);
                }
            }
            else
            {
                // Contested or no king dominant this tick — record no gain.
                _lastTickGain = 0f;
                _lastTickKingScore = 0f;
            }
        }

        /// <summary>Get the current king's progress in the active accumulation model.</summary>
        static float GetCurrentProgress(Team king)
        {
            if (Cfg.CumulativeAccumulation) return _totalAccumulated;
            return _accumulated.TryGetValue(king, out float v) ? v : 0f;
        }

        static void CheckMilestonesCumulative(Team king, float prev, float now)
        {
            float threshold = _winThreshold;
            if (threshold <= 0f) return;
            if (now >= threshold) { TriggerWin(king); return; }
            TryMilestoneCumulative(25, Cfg.SoundMilestone25, prev, now, threshold, king);
            TryMilestoneCumulative(50, Cfg.SoundMilestone50, prev, now, threshold, king);
            TryMilestoneCumulative(75, Cfg.SoundMilestone75, prev, now, threshold, king);
            TryMilestoneCumulative(95, Cfg.SoundMilestone95, prev, now, threshold, king);

            // Update the outpost-ring clock to reflect new progress (only respawns when count changes).
            UpdateOutpostClock(king, now / threshold);
        }

        static void TryMilestoneCumulative(int percent, string sound, float prev, float now, float threshold, Team king)
        {
            float boundary = threshold * (percent / 100f);
            if (prev >= boundary || now < boundary) return;
            if (!_milestonesPlayedCumulative.Add(percent)) return; // already fired this round
            BroadcastMilestoneChat(king, percent);
            PlaySound(sound);
        }

        static void BroadcastMilestoneChat(Team king, int percent)
        {
            string name = ColoredTeamName(king);
            string pctStr = ColoredPercent(percent);
            string msg;
            switch (percent)
            {
                case 25: msg = $"[KGT] {name} have claimed a quarter of the hill — {pctStr}."; break;
                case 50: msg = $"[KGT] {name} hold half the prize — {pctStr}."; break;
                case 75: msg = $"[KGT] {name} command three quarters of the hill — {pctStr}."; break;
                case 95: msg = $"[KGT] {name} stand a hair's breadth from triumph — {pctStr}."; break;
                default: msg = $"[KGT] {name} hold {pctStr} of the hill."; break;
            }
            BroadcastAllChat(msg);
        }

        static void CommitNewKing(Team newKing)
        {
            var old = _currentKing;
            _currentKing = newKing;
            _provisionalKing = null;
            _handoverElapsed = 0f;

            string newName = ColoredTeamName(newKing);
            if (old == null)
                BroadcastAllChat($"[KGT] The contest for the King of the Galactic Teleporter has commenced — {newName} have asserted their claim.");
            else
                BroadcastAllChat($"[KGT] {newName} have wrested the Galactic Teleporter from {ColoredTeamName(old)}.");

            // Re-paint the outpost-clock ring on the new king's team, preserving the current
            // progress fraction (cumulative mode: king inherits prior progress).
            float frac = _winThreshold > 0f ? GetCurrentProgress(newKing) / _winThreshold : 0f;
            UpdateOutpostClock(newKing, frac);
        }

        static void CheckMilestones(Team king, float prev, float now)
        {
            float threshold = _winThreshold;
            if (threshold <= 0f) return;

            // Threshold reached → win.
            if (now >= threshold)
            {
                TriggerWin(king);
                return;
            }

            // 25 / 50 / 75 / 95 % milestone crossings.
            TryMilestone(king, 25, Cfg.SoundMilestone25, prev, now, threshold);
            TryMilestone(king, 50, Cfg.SoundMilestone50, prev, now, threshold);
            TryMilestone(king, 75, Cfg.SoundMilestone75, prev, now, threshold);
            TryMilestone(king, 95, Cfg.SoundMilestone95, prev, now, threshold);
        }

        static void TryMilestone(Team king, int percent, string sound, float prev, float now, float threshold)
        {
            float boundary = threshold * (percent / 100f);
            if (prev >= boundary || now < boundary) return;

            if (!_milestonesPlayed.TryGetValue(percent, out var set))
            {
                set = new HashSet<Team>();
                _milestonesPlayed[percent] = set;
            }
            if (!set.Add(king)) return; // already triggered for this team

            BroadcastMilestoneChat(king, percent);
            PlaySound(sound);
        }

        static string SafeTeamName(Team t)
        {
            // Prefer ShortName — it's clean ("Sol" / "Cent" / "Alien") whereas TeamName
            // returns "Human Sol" / "Human Centauri" / etc.
            try
            {
                var s = t.GetTeamShortName();
                if (!string.IsNullOrEmpty(s)) return s;
                var n = t.GetTeamName();
                if (!string.IsNullOrEmpty(n)) return n;
                return t.name ?? "?";
            }
            catch { return "?"; }
        }

        // === Chat color helpers ===
        const string COLOR_SOL      = "#328cff"; // blue
        const string COLOR_CENT     = "#eb4646"; // red
        const string COLOR_ALIEN    = "#50c832"; // green
        const string COLOR_WILDLIFE = "#c8963c"; // brown
        const string COLOR_URANIUM  = "#39ff14"; // uranium green (capture %)
        const string COLOR_DEFAULT  = "#cccccc"; // neutral gray fallback

        static string GetTeamColor(Team t)
        {
            try
            {
                string s = t.GetTeamShortName() ?? "";
                if (s.IndexOf("Sol",   StringComparison.OrdinalIgnoreCase) >= 0) return COLOR_SOL;
                if (s.IndexOf("Cent",  StringComparison.OrdinalIgnoreCase) >= 0) return COLOR_CENT;
                if (s.IndexOf("Alien", StringComparison.OrdinalIgnoreCase) >= 0) return COLOR_ALIEN;
                if (s.IndexOf("Worm",  StringComparison.OrdinalIgnoreCase) >= 0) return COLOR_WILDLIFE;
                if (s.IndexOf("Wild",  StringComparison.OrdinalIgnoreCase) >= 0) return COLOR_WILDLIFE;
            }
            catch { }
            return COLOR_DEFAULT;
        }

        /// <summary>Team name wrapped in a Unity color tag for chat display.</summary>
        static string ColoredTeamName(Team t)
            => $"<color={GetTeamColor(t)}>{SafeTeamName(t)}</color>";

        /// <summary>Percentage wrapped in uranium-green for chat display.</summary>
        static string ColoredPercent(float pct)
            => $"<color={COLOR_URANIUM}>{pct:F0}%</color>";

        static string ColoredPercent(int pct)
            => $"<color={COLOR_URANIUM}>{pct}%</color>";
    }
}
