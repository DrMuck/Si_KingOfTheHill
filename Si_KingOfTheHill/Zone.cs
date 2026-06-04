/*
 Zone.cs — KoH tower lookup + cylinder zone checks + per-tick scoring.

 - Subscribes to Si_MapBalance.MapBalanceSpecials.OnSpecialsReady via reflection.
 - Provides InCaptureZone / InExclusionZone (XZ cylinder).
 - Loads Si_UnitBalance_Dump.json once to build the unit-name -> built_at map
   used to classify units into category multipliers.
 - EvaluateScores walks Unit.Units, filters by cylinder, computes per-team
   weighted scores using:  weight = cap * categoryMult * playerControlledMult
*/

using MelonLoader;
using Newtonsoft.Json.Linq;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        // ----- MapBalance subscription state -----

        static Transform? _kohTower;
        static Vector3 _kohCenter = Vector3.zero;
        static float _captureRadius = 50f;
        static float _exclusionRadius = 75f;
        static bool _hasKoh = false;

        // Per-round win threshold. Set from MapBalance.Koh.WinThreshold if non-zero,
        // otherwise falls back to Cfg.WinThreshold. Use this everywhere instead of Cfg.WinThreshold.
        static float _winThreshold = 50000f;

        static Type? _specialsType;
        static FieldInfo? _specialsKohField;
        static EventInfo? _specialsReadyEvent;
        static bool _subscribedReady;

        // ----- Unit classification (from Si_UnitBalance dump) -----

        // DisplayName -> "built_at" (production building name). Empty string for auxiliaries.
        static readonly Dictionary<string, string> _unitToBuilding = new Dictionary<string, string>();
        // Extended dump data (used by reward + /buy system).
        static readonly Dictionary<string, int> _unitCost = new Dictionary<string, int>();           // DisplayName -> initial cost
        static readonly Dictionary<string, string> _unitFaction = new Dictionary<string, string>(); // DisplayName -> "Sol"/"Centauri"/"Alien"
        static readonly Dictionary<string, string> _unitPrefab = new Dictionary<string, string>();  // DisplayName -> prefab name (internal minus "ObjectInfo_" prefix)
        static bool _dumpLoaded;

        static void LoadUnitDump()
        {
            if (_dumpLoaded) return;
            _dumpLoaded = true; // single-shot regardless of outcome

            string path = Path.Combine("UserData", "UnitBalance_cfg", "Si_UnitBalance_Dump.json");
            if (!File.Exists(path))
            {
                MelonLogger.Warning($"[KGT] Unit dump not found at {path} — all units will fall back to '{Cfg.DefaultCategoryForUnknown}' category");
                return;
            }
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var unitsToken = root["units"];
                if (unitsToken == null || unitsToken.Type != JTokenType.Array)
                {
                    MelonLogger.Warning("[KGT] Unit dump 'units' array missing");
                    return;
                }
                // Iterate JToken directly — casting to JArray trips the netstandard2.1 /
                // net472 Newtonsoft.Json type resolution (ICloneable / IBindingList interfaces).
                int count = 0;
                foreach (var u in unitsToken)
                {
                    string? name = u["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    _unitToBuilding[name!] = u["built_at"]?.ToString() ?? "";
                    _unitCost[name!] = u["cost"]?.Value<int>() ?? 0;
                    _unitFaction[name!] = u["faction"]?.ToString() ?? "";

                    // Derive prefab name from "internal" (ObjectInfo asset name).
                    // e.g. "ObjectInfo_Sol_Soldier_Heavy" -> "Sol_Soldier_Heavy"
                    string internalName = u["internal"]?.ToString() ?? "";
                    if (internalName.StartsWith("ObjectInfo_"))
                        internalName = internalName.Substring("ObjectInfo_".Length);
                    _unitPrefab[name!] = internalName;

                    count++;
                }
                MelonLogger.Msg($"[KGT] Loaded {count} units from dump (name → built_at/cost/faction/prefab)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] Failed to parse unit dump: {ex.Message}");
            }
        }

        /// <summary>
        /// Refresh _unitCost from live ConstructionData.ResourceCost values.
        /// Si_UnitBalance applies cost overrides via OverrideManager at game start; those
        /// land on ConstructionData.ResourceCost, NOT the JSON dump (which is the static
        /// vanilla snapshot). Call this AFTER overrides are applied so rewards reflect
        /// actual game-economy costs.
        /// </summary>
        static void RefreshLiveUnitCosts()
        {
            try
            {
                var allCD = Resources.FindObjectsOfTypeAll<ConstructionData>();
                int updated = 0, additions = 0;
                foreach (var cd in allCD)
                {
                    if (cd == null || cd.ObjectInfo == null) continue;
                    string name = cd.ObjectInfo.DisplayName;
                    if (string.IsNullOrEmpty(name)) continue;
                    int live = cd.ResourceCost;
                    if (live <= 0) continue;
                    if (_unitCost.TryGetValue(name, out int prev))
                    {
                        if (prev != live) { _unitCost[name] = live; updated++; }
                    }
                    else
                    {
                        _unitCost[name] = live; additions++;
                    }
                }
                if (updated + additions > 0)
                    MelonLogger.Msg($"[KGT] Refreshed live unit costs from ConstructionData: {updated} updated, {additions} added");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] RefreshLiveUnitCosts failed: {ex.Message}");
            }
        }

        // ----- MapBalance subscription -----

        static bool TrySubscribeMapBalance()
        {
            if (_subscribedReady) return true;
            try
            {
                if (_specialsType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("Si_MapBalance.MapBalanceSpecials");
                        if (t != null) { _specialsType = t; break; }
                    }
                }
                if (_specialsType == null) return false;

                _specialsKohField ??= _specialsType.GetField("Koh", BindingFlags.Public | BindingFlags.Static);
                _specialsReadyEvent ??= _specialsType.GetEvent("OnSpecialsReady", BindingFlags.Public | BindingFlags.Static);
                if (_specialsReadyEvent == null)
                {
                    MelonLogger.Warning("[KGT] MapBalance.OnSpecialsReady event not found");
                    return false;
                }

                Action handler = OnMapBalanceSpecialsReady;
                _specialsReadyEvent.AddEventHandler(null, handler);
                _subscribedReady = true;
                MelonLogger.Msg("[KGT] Subscribed to Si_MapBalance.OnSpecialsReady");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] TrySubscribeMapBalance failed: {ex.Message}");
                return false;
            }
        }

        static void OnMapBalanceSpecialsReady()
        {
            try
            {
                ResetKohState();
                if (_specialsKohField == null) return;
                var koh = _specialsKohField.GetValue(null);
                if (koh == null)
                {
                    MelonLogger.Msg("[KGT] No KoH configured for this map — running inert this round.");
                    return;
                }
                var kohType = koh.GetType();
                var obj = kohType.GetField("Obj")?.GetValue(koh) as GameObject;
                if (obj == null) { MelonLogger.Warning("[KGT] MapBalance KoH.Obj is null"); return; }

                _captureRadius = (float)(kohType.GetField("CaptureRadius")?.GetValue(koh) ?? 50f);
                _exclusionRadius = (float)(kohType.GetField("ExclusionRadius")?.GetValue(koh) ?? 75f);

                // Resolve win threshold from our own config (per-map override > global default).
                // Map name = the active Unity scene name (matches what Si_MapBalance also uses).
                string mapName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
                bool perMap = !string.IsNullOrEmpty(mapName)
                              && Cfg.WinThresholdPerMap != null
                              && Cfg.WinThresholdPerMap.TryGetValue(mapName, out float mapWin)
                              && mapWin > 0f;
                _winThreshold = perMap ? Cfg.WinThresholdPerMap[mapName] : Cfg.WinThreshold;

                _kohTower = obj.transform;
                _kohCenter = _kohTower.position;
                _hasKoh = true;
                // Reset damage diagnostic so this round's HIT/MISS lines aren't suppressed.
                ResetDamageDiag();

                MelonLogger.Msg($"[KGT] Tower acquired from MapBalance: {obj.name} at ({_kohCenter.x:F0},{_kohCenter.z:F0}) " +
                                $"R_capture={_captureRadius} R_excl={_exclusionRadius} " +
                                $"win_threshold={_winThreshold:F0} ({(perMap ? "per-map override for " + mapName : "global default")})");

                // MapReplay: emit KoH spawn + ring metadata so the replay viewer can render.
                LogToReplay("kgt_koh_spawn",
                    ("x", _kohCenter.x.ToString("F0")),
                    ("z", _kohCenter.z.ToString("F0")),
                    ("capture_radius", _captureRadius.ToString("F0")),
                    ("exclusion_radius", _exclusionRadius.ToString("F0")),
                    ("win_threshold", _winThreshold.ToString("F0")));
                LogToReplay("kgt_outpost_ring",
                    ("center_x", _kohCenter.x.ToString("F0")),
                    ("center_z", _kohCenter.z.ToString("F0")),
                    ("radius", _captureRadius.ToString("F0")),
                    ("count", Cfg.OutpostCount.ToString()),
                    ("bury_depth", Cfg.OutpostBuryDepth.ToString("F0")));

                // Spawn the initial buried-outpost ring — 0 king, all neutral.
                // Subsequent ticks call UpdateOutpostClock to fill as progress grows.
                SpawnOutpostsSplit(null, 0);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] OnMapBalanceSpecialsReady failed: {ex.Message}");
            }
        }

        static void ResetKohState()
        {
            DespawnOutposts(); // before clearing _kohTower so we can still find the ring
            _kohTower = null;
            _kohCenter = Vector3.zero;
            _hasKoh = false;
            _neutralTeam = null; // re-resolve on next spawn (KoH may be different team next round)
            // King + timer reset is handled in King.ResetKingState()
            ResetKingState();
        }

        // ----- Cylinder zone (XZ only — Y unbounded). -----

        static bool InCaptureZone(Vector3 pos)
        {
            if (!_hasKoh) return false;
            float dx = pos.x - _kohCenter.x;
            float dz = pos.z - _kohCenter.z;
            return (dx * dx + dz * dz) <= (_captureRadius * _captureRadius);
        }

        static bool InExclusionZone(Vector3 pos)
        {
            if (!_hasKoh) return false;
            float dx = pos.x - _kohCenter.x;
            float dz = pos.z - _kohCenter.z;
            return (dx * dx + dz * dz) <= (_exclusionRadius * _exclusionRadius);
        }

        // ----- Per-tick scoring. -----

        // Players currently inside the capture zone, refreshed each tick.
        // Used for enter/exit chat notifications (see ProcessPlayerNotifications).
        static readonly HashSet<Player> _playersInZone = new HashSet<Player>();
        static readonly HashSet<Player> _playersInZonePrev = new HashSet<Player>();

        /// <summary>
        /// Returns weighted score per team for units currently inside the capture cylinder.
        /// Empty dict if KoH isn't active or no qualifying units are inside.
        /// Also populates _playersInZone with players whose ControlledUnit is in the zone.
        /// </summary>
        static Dictionary<Team, float> EvaluateScores()
        {
            // Swap prev/current buffers (reuse allocations).
            _playersInZonePrev.Clear();
            foreach (var p in _playersInZone) _playersInZonePrev.Add(p);
            _playersInZone.Clear();

            var scores = new Dictionary<Team, float>();
            if (!_hasKoh || _kohTower == null) return scores;

            var perUnitOverride = Cfg.PerUnitOverride;
            var categoryMult = Cfg.CategoryMultiplier;
            var buildingToCategory = Cfg.BuildingToCategory;
            float playerMult = Cfg.PlayerControlledMultiplier;
            string fallbackCat = string.IsNullOrEmpty(Cfg.DefaultCategoryForUnknown) ? "Infantry" : Cfg.DefaultCategoryForUnknown;

            var units = Unit.Units;
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null || unit.IsDestroyed) continue;
                if (unit.ObjectInfo == null || unit.Team == null) continue;
                if (!InCaptureZone(unit.transform.position)) continue;

                int cap = unit.ObjectInfo.UnitCapValue;
                if (cap <= 0) continue; // auxiliary units (harvesters, etc.) don't count

                string unitName = unit.ObjectInfo.DisplayName ?? "";

                // Multiplier lookup: PerUnitOverride > CategoryMultiplier > 1.0
                float mult;
                if (!perUnitOverride.TryGetValue(unitName, out mult))
                {
                    string category;
                    if (_unitToBuilding.TryGetValue(unitName, out string? builtAt) && !string.IsNullOrEmpty(builtAt))
                    {
                        if (!buildingToCategory.TryGetValue(builtAt!, out category!))
                            category = fallbackCat;
                    }
                    else
                    {
                        // No built_at in dump → auxiliary (e.g. harvesters, queens, hover bike)
                        category = "Auxiliary";
                    }
                    if (!categoryMult.TryGetValue(category, out mult)) mult = 1.0f;
                }

                if (mult <= 0f) continue;

                float weight = cap * mult;
                if (unit.ControlledBy != null)
                {
                    weight *= playerMult;
                    _playersInZone.Add(unit.ControlledBy);
                    // Reward this player for being in the zone this tick.
                    GrantZonePresenceReward(unit.ControlledBy, cap, unitName);
                }

                if (scores.TryGetValue(unit.Team, out float current))
                    scores[unit.Team] = current + weight;
                else
                    scores[unit.Team] = weight;
            }

            return scores;
        }

        /// <summary>
        /// Diff _playersInZone vs _playersInZonePrev → enter/exit chat notifications.
        /// </summary>
        static void ProcessPlayerNotifications()
        {
            if (!Cfg.EnterExitNotifications) return;

            // Entered: in current, not in prev.
            foreach (var p in _playersInZone)
            {
                if (p == null) continue;
                if (!_playersInZonePrev.Contains(p))
                {
                    try { HelperMethods.SendChatMessageToPlayer(p, "[KGT] You have entered the king's domain."); }
                    catch (Exception) { }
                }
            }
            // Exited: in prev, not in current.
            foreach (var p in _playersInZonePrev)
            {
                if (p == null) continue;
                if (!_playersInZone.Contains(p))
                {
                    try { HelperMethods.SendChatMessageToPlayer(p, "[KGT] You have departed the king's domain."); }
                    catch (Exception) { }
                }
            }
        }
    }
}
