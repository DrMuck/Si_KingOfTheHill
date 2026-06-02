/*
 Outposts.cs — outpost ring on the capture-radius edge, used as a CAPTURE CLOCK.

 The first N outposts (clockwise from north) are on the current king's team,
 colouring those segments of the radar ring in the king's faction colour.
 The remaining (OutpostCount - N) outposts stay on the KoH's neutral team
 (Wildlife/Gamemaster — same team as the KoH building itself).

 N = round(OutpostCount * progress / WinThreshold). So:
   - 0%   progress → 0 king, 12 neutral  (no fill)
   - 50%  progress → 6 king,  6 neutral  (half fill)
   - 100% progress → 12 king, 0 neutral  (full fill — round ends here)

 Lifecycle:
   - Initial spawn: on KoH tower acquired (Zone.cs) → 0 king, all neutral.
   - Update: King.cs UpdateOwnership → UpdateOutpostClock per accumulation tick.
     Only respawns when (kingTeam, kingCount) actually changed.
   - Despawn: on round end via ResetKohState.
*/

using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        const string OUTPOST_PREFAB_NAME = "Outpost";

        static readonly List<GameObject> _outposts = new List<GameObject>();
        static Team? _outpostKingTeam;           // current king team painted on ring (null = all neutral)
        static int _outpostKingCount;            // 0..Cfg.OutpostCount
        static Team? _neutralTeam;               // cached KoH building's team (Wildlife by default)

        /// <summary>
        /// Spawn outposts split between <paramref name="kingTeam"/> (first <paramref name="kingCount"/>
        /// clockwise from north) and the cached neutral team (the rest).
        /// Despawns any existing ring first. Idempotent at the same (kingTeam, kingCount).
        /// </summary>
        static void SpawnOutpostsSplit(Team? kingTeam, int kingCount)
        {
            DespawnOutposts();
            if (!_hasKoh || _kohTower == null) return;

            // Resolve / cache the neutral team (the KoH building's team — Wildlife / Gamemaster).
            if (_neutralTeam == null)
            {
                try
                {
                    var bgo = _kohTower.GetComponent<BaseGameObject>();
                    _neutralTeam = bgo?.Team;
                }
                catch { }
            }
            if (_neutralTeam == null)
            {
                MelonLogger.Warning("[KGT] SpawnOutpostsSplit: neutral team unresolved — skipping ring");
                return;
            }

            int total = Mathf.Max(1, Cfg.OutpostCount);
            int kingN = Mathf.Clamp(kingCount, 0, total);

            int idx;
            try { idx = GameDatabase.GetSpawnablePrefabIndex(OUTPOST_PREFAB_NAME); }
            catch (Exception ex) { MelonLogger.Warning($"[KGT] GetSpawnablePrefabIndex threw: {ex.Message}"); return; }
            if (idx < 0)
            {
                MelonLogger.Warning($"[KGT] Outpost prefab '{OUTPOST_PREFAB_NAME}' not found");
                return;
            }
            var prefab = GameDatabase.GetSpawnablePrefab(idx);
            if (prefab == null) return;

            var terrain = Terrain.activeTerrain;
            float buryDepth = Cfg.OutpostBuryDepth;
            int spawnedKing = 0, spawnedNeutral = 0;

            for (int i = 0; i < total; i++)
            {
                Team teamForThis = (i < kingN && kingTeam != null) ? kingTeam : _neutralTeam;

                float angle = (i / (float)total) * 2f * Mathf.PI;
                float x = _kohCenter.x + _captureRadius * Mathf.Cos(angle);
                float z = _kohCenter.z + _captureRadius * Mathf.Sin(angle);
                float y = SampleSurfaceY(terrain, x, z) - buryDepth;

                try
                {
                    var go = Game.SpawnPrefab(prefab, null, teamForThis,
                                              new Vector3(x, y, z),
                                              Quaternion.identity, true, true);
                    if (go != null)
                    {
                        _outposts.Add(go);
                        if (i < kingN) spawnedKing++; else spawnedNeutral++;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[KGT] Outpost spawn at ({x:F0},{z:F0}) failed: {ex.Message}");
                }
            }
            _outpostKingTeam = kingTeam;
            _outpostKingCount = kingN;
            MelonLogger.Msg($"[KGT] Outpost clock: {spawnedKing} on {SafeTeamName(kingTeam ?? _neutralTeam)} / {spawnedNeutral} on {SafeTeamName(_neutralTeam)}");
        }

        /// <summary>
        /// Convenience entry point — recompute kingCount from progress fraction and respawn
        /// only if the (kingTeam, kingCount) pair actually changed. Cheap to call per-tick.
        /// </summary>
        static void UpdateOutpostClock(Team? king, float progressFraction)
        {
            if (!_hasKoh) return;
            int total = Mathf.Max(1, Cfg.OutpostCount);
            int kingN = Mathf.Clamp(Mathf.RoundToInt(total * progressFraction), 0, total);

            // No-op if neither the count nor the team changed.
            if (kingN == _outpostKingCount && ReferenceEquals(king, _outpostKingTeam)) return;

            SpawnOutpostsSplit(king, kingN);
        }

        static void DespawnOutposts()
        {
            int count = _outposts.Count;
            foreach (var go in _outposts)
            {
                if (go == null) continue;
                try
                {
                    var dm = go.GetComponent<DamageManager>();
                    if (dm != null) dm.SetHealth01(0f);
                    else UnityEngine.Object.Destroy(go);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[KGT] Outpost despawn failed: {ex.Message}");
                }
            }
            if (count > 0) MelonLogger.Msg($"[KGT] Despawned {count} outpost(s).");
            _outposts.Clear();
            _outpostKingTeam = null;
            _outpostKingCount = 0;
        }

        static float SampleTerrainYLocal(Terrain terrain, float x, float z)
        {
            if (terrain == null) return 0f;
            try
            {
                return terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;
            }
            catch { return 0f; }
        }

        /// <summary>
        /// True playable surface at (x, z): raycasts downward from terrainY+200m to catch
        /// rocks / structure floors that sit ABOVE the heightmap. Falls back to terrain Y
        /// on raycast miss. Mirrors SampleSurfaceHeight from Si_MapBalance.
        /// </summary>
        static float SampleSurfaceY(Terrain terrain, float x, float z)
        {
            float terrainY = SampleTerrainYLocal(terrain, x, z);
            float rayStart = terrainY + 200f;
            try
            {
                if (Physics.Raycast(new Vector3(x, rayStart, z), Vector3.down,
                                    out RaycastHit hit, 400f, ~0, QueryTriggerInteraction.Ignore))
                {
                    return hit.point.y;
                }
            }
            catch { }
            return terrainY;
        }
    }
}
