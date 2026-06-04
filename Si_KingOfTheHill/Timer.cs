/*
 Timer.cs — periodic chat status broadcasts + sound playback + win trigger.

 Tick responsibilities:
   - Every Cfg.StatusAnnounceIntervalSeconds, broadcast a status line showing the
     current king + their progress % vs. WinThreshold. Skipped when no king.
   - PlaySound(path) wraps SilicaAdminMod.AudioHelper.PlaySoundFile defensively.
   - TriggerWin(team) marks the round won, announces, plays the team-specific
     win sound, and force-destroys other teams' critical structures so Silica's
     natural end-of-round flow resolves.
*/

using MelonLoader;
using SilicaAdminMod;
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        static void ResetTimerState()
        {
            _statusAnnounceTimer = 0f;
            _winFallbackTimer = 0f;
            _winningTeam = null;
            _winFinishingDelayTimer = 0f;
            _winFinishingPending = false;
        }

        /// <summary>Per-tick periodic status broadcast. Cheap; gated by Cfg.StatusAnnounceIntervalSeconds.</summary>
        static void TickTimer(float deltaSeconds)
        {
            // After a win is triggered, only the finishing-delay + fallback timer continue to run.
            if (_winTriggered)
            {
                TickWinFinishingDelay(deltaSeconds);
                TickWinFallback(deltaSeconds);
                return;
            }
            if (!_hasKoh) return;
            if (Cfg.StatusAnnounceIntervalSeconds <= 0f) return;

            _statusAnnounceTimer -= deltaSeconds;
            if (_statusAnnounceTimer > 0f) return;
            _statusAnnounceTimer = Cfg.StatusAnnounceIntervalSeconds;

            // Skip status announcement if there's no king yet.
            if (_currentKing == null) return;
            float acc = GetCurrentProgress(_currentKing);
            float pct = _winThreshold > 0f ? (acc / _winThreshold * 100f) : 0f;
            BroadcastAllChat($"[KGT] {ColoredTeamName(_currentKing)} presently hold {ColoredPercent(pct)} of the hill.");
        }

        // === Sound playback (SilicaAdminMod.AudioHelper). ===

        static void PlaySound(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                // PlaySoundFile returns a Task and broadcasts server-wide when target=null.
                Task _ = AudioHelper.PlaySoundFile(path);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] PlaySoundFile('{path}') failed: {ex.Message}");
            }
        }

        // === Win trigger. ===

        /// <summary>
        /// Trigger round-end in favor of <paramref name="winner"/>:
        ///   - Announce + play team-specific win sound.
        ///   - Spawn the winner's "finishing force" at each losing-team critical (cinematic):
        ///       Alien:    4 Goliaths drop from above onto each enemy HQ + Queen.
        ///       Sol:      4 Siege Tanks next to each human HQ; 8 around the Alien Queen.
        ///       Centauri: 4 Crimson Tanks next to each human HQ; 8 around the Alien Queen.
        ///   - LET THE UNITS DO THE KILLING. Silica detects round-end naturally when only
        ///     one team has critical structures/units standing.
        ///   - Safety fallback: if any losing-team critical is still alive after
        ///     WIN_FORCE_FALLBACK_SECONDS, force-destroy it. Handled in TickTimer.
        /// Idempotent (guarded by _winTriggered).
        /// </summary>
        const float WIN_FORCE_FALLBACK_SECONDS = 60f;
        static float _winFallbackTimer;
        static Team? _winningTeam;
        // Defer cannon boom + finishing-force spawn until the win voice line has finished
        // playing, otherwise the two audio cues overlap each other.
        static float _winFinishingDelayTimer;
        static bool _winFinishingPending;

        static void TriggerWin(Team winner)
        {
            if (_winTriggered) return;
            _winTriggered = true;
            _winningTeam = winner;
            _winFallbackTimer = WIN_FORCE_FALLBACK_SECONDS;

            string name = SafeTeamName(winner);
            BroadcastAllChat($"[KGT] {GetWinMessageForTeam(winner, name)}");
            PlaySound(GetWinSoundForTeam(winner));
            MelonLogger.Msg($"[KGT] WIN: {name}. Voice line playing; cannon + finishing force in {Cfg.WinFinishingForceDelaySeconds}s.");
            // MapReplay event.
            LogToReplay("kgt_win", ("winner", ReplayTeamTag(winner)));

            // Defer cannon + finishing force so they don't overlap the voice line.
            _winFinishingDelayTimer = Mathf.Max(0f, Cfg.WinFinishingForceDelaySeconds);
            _winFinishingPending = true;
        }

        static void TickWinFinishingDelay(float deltaSeconds)
        {
            if (!_winFinishingPending || _winningTeam == null) return;
            _winFinishingDelayTimer -= deltaSeconds;
            if (_winFinishingDelayTimer > 0f) return;
            _winFinishingPending = false;

            MelonLogger.Msg("[KGT] Finishing force time — cannon + unit spawn.");
            PlaySound(Cfg.SoundFinishingForce);
            try { SpawnFinishingForce(_winningTeam); }
            catch (Exception ex) { MelonLogger.Warning($"[KGT] SpawnFinishingForce threw: {ex.Message}"); }
        }

        /// <summary>
        /// After WIN_FORCE_FALLBACK_SECONDS, force-destroy any remaining critical structures
        /// or units of losing teams. Covers the case where spawned finishing-force units
        /// couldn't reach their target (terrain, pathing, attacked from defenders).
        /// Called once per tick after a win has been triggered.
        /// </summary>
        static void TickWinFallback(float deltaSeconds)
        {
            if (!_winTriggered || _winningTeam == null) return;
            _winFallbackTimer -= deltaSeconds;
            if (_winFallbackTimer > 0f) return;
            _winFallbackTimer = float.PositiveInfinity; // single-shot

            try
            {
                int killedS = 0, killedU = 0;
                foreach (var t in Team.Teams)
                {
                    if (t == null || ReferenceEquals(t, _winningTeam)) continue;
                    if (IsGamemasterTeam(t)) continue;

                    var structures = t.Structures;
                    if (structures != null)
                    {
                        for (int i = structures.Count - 1; i >= 0; i--)
                        {
                            var s = structures[i];
                            if (s == null || s.IsDestroyed) continue;
                            if (s.ObjectInfo == null || !s.ObjectInfo.Critical) continue;
                            try { s.DamageManager?.SetHealth01(0f); killedS++; } catch { }
                        }
                    }
                    var units = t.Units;
                    if (units != null)
                    {
                        for (int i = units.Count - 1; i >= 0; i--)
                        {
                            var u = units[i];
                            if (u == null || u.IsDestroyed) continue;
                            if (u.ObjectInfo == null || !u.ObjectInfo.Critical) continue;
                            try { u.DamageManager?.SetHealth01(0f); killedU++; } catch { }
                        }
                    }
                }
                if (killedS + killedU > 0)
                    MelonLogger.Msg($"[KGT] Fallback after {WIN_FORCE_FALLBACK_SECONDS}s: destroyed {killedS} struct(s) + {killedU} unit(s) the finishing force couldn't kill.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] win fallback failed: {ex.Message}");
            }
        }

        // === Finishing force: spawn winner's signature unit around each losing team's HQ. ===

        const string PREFAB_GOLIATH      = "Goliath";
        const string PREFAB_SIEGE_TANK   = "Sol_UltraHeavy_SiegeTank";
        const string PREFAB_CRIMSON_TANK = "Cent_UltraHeavy_CrimsonTank";
        const float  FORCE_RING_RADIUS   = 90f;   // tanks at this distance from HQ
        const float  GOLIATH_DROP_HEIGHT = 80f;   // Goliaths spawn this far above HQ; gravity does the rest

        static void SpawnFinishingForce(Team winner)
        {
            string winnerShort = (winner.GetTeamShortName() ?? "");
            bool winnerIsAlien = winnerShort.IndexOf("Alien", StringComparison.OrdinalIgnoreCase) >= 0;
            bool winnerIsSol   = winnerShort.IndexOf("Sol",   StringComparison.OrdinalIgnoreCase) >= 0;
            bool winnerIsCent  = winnerShort.IndexOf("Cent",  StringComparison.OrdinalIgnoreCase) >= 0;

            string prefabName;
            if (winnerIsAlien)      prefabName = PREFAB_GOLIATH;
            else if (winnerIsCent)  prefabName = PREFAB_CRIMSON_TANK;
            else if (winnerIsSol)   prefabName = PREFAB_SIEGE_TANK;
            else { MelonLogger.Msg("[KGT] No finishing-force prefab for unknown winner — skipping."); return; }

            int idx = GameDatabase.GetSpawnablePrefabIndex(prefabName);
            if (idx < 0) { MelonLogger.Warning($"[KGT] Finishing prefab '{prefabName}' not found"); return; }
            var prefab = GameDatabase.GetSpawnablePrefab(idx);
            if (prefab == null) return;

            int totalSpawned = 0;
            foreach (var losing in Team.Teams)
            {
                if (losing == null || ReferenceEquals(losing, winner)) continue;
                if (IsGamemasterTeam(losing)) continue;

                bool losingIsAlien = (losing.GetTeamShortName() ?? "")
                    .IndexOf("Alien", StringComparison.OrdinalIgnoreCase) >= 0;

                // Per-team count rule (matches user spec — DOUBLED from initial values):
                //   Alien winner             → drop 8 onto each enemy critical
                //   Sol/Cent winner vs human → 8 around HQ
                //   Sol/Cent winner vs alien → 16 around critical (Queen)
                int countFor(bool dropping) => dropping ? 8 : (losingIsAlien ? 16 : 8);

                // 1. Critical Structures (Sol/Cent HQs land here).
                var structures = losing.Structures;
                if (structures != null)
                {
                    for (int i = 0; i < structures.Count; i++)
                    {
                        var s = structures[i];
                        if (s == null || s.IsDestroyed) continue;
                        if (s.ObjectInfo == null || !s.ObjectInfo.Critical) continue;
                        Vector3 pos = s.transform.position;
                        totalSpawned += winnerIsAlien
                            ? SpawnDropOnHQ(prefab, winner, pos, countFor(true))
                            : SpawnRingAround(prefab, winner, pos, countFor(false), FORCE_RING_RADIUS);
                    }
                }

                // 2. Critical Units (Alien Queen lands here).
                var units = losing.Units;
                if (units != null)
                {
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u == null || u.IsDestroyed) continue;
                        if (u.ObjectInfo == null || !u.ObjectInfo.Critical) continue;
                        Vector3 pos = u.transform.position;
                        MelonLogger.Msg($"[KGT] Finishing force target (Unit): {u.ObjectInfo.DisplayName} on team {SafeTeamName(losing)} at ({pos.x:F0},{pos.z:F0})");
                        totalSpawned += winnerIsAlien
                            ? SpawnDropOnHQ(prefab, winner, pos, countFor(true))
                            : SpawnRingAround(prefab, winner, pos, countFor(false), FORCE_RING_RADIUS);
                    }
                }
            }
            MelonLogger.Msg($"[KGT] Finishing force: spawned {totalSpawned} {prefabName}(s) for team {SafeTeamName(winner)}.");
        }

        static int SpawnRingAround(GameObject prefab, Team team, Vector3 center, int count, float radius)
        {
            var terrain = Terrain.activeTerrain;
            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                float angle = (i / (float)count) * 2f * Mathf.PI;
                float x = center.x + radius * Mathf.Cos(angle);
                float z = center.z + radius * Mathf.Sin(angle);
                float y = SampleSurfaceY(terrain, x, z) + 2f; // slightly above to avoid clipping
                try
                {
                    var go = Game.SpawnPrefab(prefab, null, team, new Vector3(x, y, z),
                                              Quaternion.Euler(0f, -angle * Mathf.Rad2Deg + 90f, 0f),
                                              true, true);
                    if (go != null) spawned++;
                }
                catch (Exception ex) { MelonLogger.Warning($"[KGT] ring spawn failed: {ex.Message}"); }
            }
            return spawned;
        }

        static int SpawnDropOnHQ(GameObject prefab, Team team, Vector3 hqPos, int count)
        {
            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                // Small XZ jitter so they don't perfectly overlap; small Y stagger so they land sequentially.
                float jx = ((i & 1) == 0 ? -3f : 3f);
                float jz = (i < 2 ? -3f : 3f);
                Vector3 pos = new Vector3(hqPos.x + jx, hqPos.y + GOLIATH_DROP_HEIGHT + i * 8f, hqPos.z + jz);
                try
                {
                    var go = Game.SpawnPrefab(prefab, null, team, pos, Quaternion.identity, true, true);
                    if (go != null) spawned++;
                }
                catch (Exception ex) { MelonLogger.Warning($"[KGT] drop spawn failed: {ex.Message}"); }
            }
            return spawned;
        }

        // Reuse the surface-sampling logic from Outposts.cs (same partial class).
        // (Definition already exists as SampleSurfaceY there.)

        /// <summary>Pick the appropriate win sound based on team short name.</summary>
        static string GetWinSoundForTeam(Team winner)
        {
            try
            {
                string s = winner.GetTeamShortName() ?? "";
                if (s.IndexOf("Alien", StringComparison.OrdinalIgnoreCase) >= 0) return Cfg.SoundWinAlien;
                if (s.IndexOf("Cent",  StringComparison.OrdinalIgnoreCase) >= 0) return Cfg.SoundWinCentauri;
                if (s.IndexOf("Sol",   StringComparison.OrdinalIgnoreCase) >= 0) return Cfg.SoundWinSol;
            }
            catch { }
            return Cfg.SoundWinSol; // safest fallback
        }

        /// <summary>Pick the per-team win chat message. Falls back to WinMessageDefault otherwise.</summary>
        static string GetWinMessageForTeam(Team winner, string displayName)
        {
            try
            {
                string s = winner.GetTeamShortName() ?? "";
                if (s.IndexOf("Sol", StringComparison.OrdinalIgnoreCase) >= 0
                    && !string.IsNullOrEmpty(Cfg.WinMessageSol)) return Cfg.WinMessageSol;
                if ((s.IndexOf("Cent", StringComparison.OrdinalIgnoreCase) >= 0
                     || s.IndexOf("Centauri", StringComparison.OrdinalIgnoreCase) >= 0)
                    && !string.IsNullOrEmpty(Cfg.WinMessageCentauri)) return Cfg.WinMessageCentauri;
                if (s.IndexOf("Alien", StringComparison.OrdinalIgnoreCase) >= 0
                    && !string.IsNullOrEmpty(Cfg.WinMessageAlien)) return Cfg.WinMessageAlien;
            }
            catch { }
            return (Cfg.WinMessageDefault ?? "{name} wins").Replace("{name}", displayName);
        }
    }
}
