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
        // Parallel arrays (index-aligned with _outposts) holding the team-shortname
        // and world position of each spawned outpost. Used to emit MapReplay
        // structure_kill events on despawn (we can't read the GameObject's
        // transform after Destroy).
        static readonly List<string> _outpostTeamNames = new List<string>();
        static readonly List<Vector3> _outpostPositions = new List<Vector3>();
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
                    var spawnPos = new Vector3(x, y, z);
                    var go = Game.SpawnPrefab(prefab, null, teamForThis,
                                              spawnPos,
                                              Quaternion.identity, true, true);
                    if (go != null)
                    {
                        _outposts.Add(go);
                        string teamShort = ReplayTeamTag(teamForThis);
                        _outpostTeamNames.Add(teamShort);
                        _outpostPositions.Add(spawnPos);
                        if (i < kingN) spawnedKing++; else spawnedNeutral++;
                        // MapReplay: emit a construction_complete so the buildings dict
                        // gains this outpost under (team, "Outpost", x, z) and the renderer
                        // picks up the team colour at the ring slot.
                        LogReplayConstructionComplete(teamShort, "Outpost", spawnPos);
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
            for (int i = 0; i < _outposts.Count; i++)
            {
                var go = _outposts[i];
                // Emit MapReplay structure_kill for THIS slot (whether or not the GO
                // is still alive) so the buildings dict marks destroy_t. The cached
                // _outpostPositions/_outpostTeamNames are index-aligned with _outposts.
                if (i < _outpostPositions.Count && i < _outpostTeamNames.Count)
                {
                    LogReplayStructureKill("Outpost", _outpostTeamNames[i], _outpostPositions[i]);
                }
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
            _outpostTeamNames.Clear();
            _outpostPositions.Clear();
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
        /// Surface Y at (x, z) suitable for spawning a vehicle/unit near an existing
        /// anchor (the buyer's controlled unit). The generic SampleSurfaceY raycasts
        /// from terrain+200m down 400m and accepts ANY collider, so a Bomber / mid-air
        /// projectile crossing the spawn ray returns a 150-200m "ground" — the spawn
        /// drops from the sky and explodes. We bound the ray to ±50m around the anchor
        /// so only nearby static surfaces (terrain, structure floors next to the player)
        /// can be picked. Falls back to terrain Y, then anchor Y, then 0.
        /// </summary>
        static float SampleSurfaceY_NearAnchor(Terrain terrain, float x, float z, float anchorY)
        {
            float rayStart = anchorY + 10f;
            try
            {
                if (Physics.Raycast(new Vector3(x, rayStart, z), Vector3.down,
                                    out RaycastHit hit, 60f, ~0, QueryTriggerInteraction.Ignore))
                {
                    return hit.point.y;
                }
            }
            catch { }
            float terrainY = SampleTerrainYLocal(terrain, x, z);
            // If terrain sample is wildly off vs anchor (e.g. tank on a building roof),
            // prefer the anchor — never let the caller spawn far above the ground.
            if (Mathf.Abs(terrainY - anchorY) > 50f) return anchorY;
            return terrainY;
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
