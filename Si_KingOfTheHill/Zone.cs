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
        // 0 = MapBalance didn't define an exclusion for this map → AntiBuild
        // falls back to Cfg.BuildExclusionRadius. >0 = canonical value from MapBalance.
        static float _exclusionRadius = 0f;
        static bool _hasKoh = false;
        // Cached for auto-respawn when a player kills the KoH despite our immunity
        // patches. _kohCenter (Vector3) is the canonical reference for capture and
        // outpost logic — the GameObject is now cosmetic.
        static GameObject? _kohPrefabRef;
        static Team? _kohTeam;
        static Quaternion _kohRotation = Quaternion.identity;
        static float _kohAliveCheckTimer = 0f;
        static int _kohRespawnCount = 0;
        const float KOH_ALIVE_CHECK_INTERVAL = 3.0f;

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
        static readonly Dictionary<string, int> _unitCost = new Dictionary<string, int>();           // DisplayName -> effective cost (vanilla * UnitBalance cost_mult)
        static readonly Dictionary<string, int> _unitCostVanilla = new Dictionary<string, int>();    // DisplayName -> vanilla cost (from dump; never mutated)
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
                int skippedStructures = 0;
                foreach (var u in unitsToken)
                {
                    string? name = u["name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    // Skip structures — they appear in the dump alongside units (e.g.
                    // "Air Factory" with built_at="Headquarters") and would otherwise
                    // pollute the /buy menu's category list with non-unit options.
                    bool isStructure = u["is_structure"]?.Value<bool>() ?? false;
                    if (isStructure) { skippedStructures++; continue; }

                    _unitToBuilding[name!] = u["built_at"]?.ToString() ?? "";
                    int vanillaCost = u["cost"]?.Value<int>() ?? 0;
                    _unitCostVanilla[name!] = vanillaCost;
                    _unitCost[name!] = vanillaCost;  // overlay applied in ApplyUnitBalanceCostOverrides
                    _unitFaction[name!] = u["faction"]?.ToString() ?? "";

                    // Derive prefab name from "internal" (ObjectInfo asset name).
                    //   Sol_Soldier_Heavy  → game DB key "Sol_Soldier_Heavy"      (humans keep prefix)
                    //   Alien_Scorpion     → game DB key "Scorpion"               (aliens DROP prefix)
                    // Alien creature prefabs are registered in GameDatabase without
                    // the "Alien_" faction prefix (confirmed by Timer.cs finishing-force
                    // constants like PREFAB_GOLIATH = "Goliath"). Without this strip,
                    // /buy fails on every alien purchase with "Prefab 'Alien_X' not in
                    // spawnable database".
                    string internalName = u["internal"]?.ToString() ?? "";
                    if (internalName.StartsWith("ObjectInfo_"))
                        internalName = internalName.Substring("ObjectInfo_".Length);
                    if (internalName.StartsWith("Alien_"))
                        internalName = internalName.Substring("Alien_".Length);
                    _unitPrefab[name!] = internalName;

                    count++;
                }
                MelonLogger.Msg($"[KGT] Loaded {count} units from dump ({skippedStructures} structures skipped)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] Failed to parse unit dump: {ex.Message}");
            }
        }

        /// <summary>
        /// Rebuild _unitCost from the vanilla dump snapshot + Si_UnitBalance's cost_mult
        /// overrides. Reading ConstructionData.ResourceCost at runtime returns vanilla
        /// because OverrideManager applies an overlay rather than mutating prefab fields,
        /// so we parse Si_UnitBalance_Config.json directly.
        ///
        /// Idempotent: always reset _unitCost from _unitCostVanilla before multiplying,
        /// so calling per round-start can't compound.
        /// </summary>
        static void ApplyUnitBalanceCostOverrides()
        {
            // 1. Ensure we have a vanilla baseline. If the dump didn't populate
            //    _unitCostVanilla (Si_UnitBalance not installed), seed it from a live
            //    ConstructionData scan. That read returns vanilla values whether or
            //    not Si_UnitBalance is loaded (its OverrideManager is a runtime overlay,
            //    not a field mutation), so it's a safe baseline either way.
            if (_unitCostVanilla.Count == 0)
            {
                int scanned = SeedVanillaCostsFromConstructionData();
                MelonLogger.Msg($"[KGT] No dump — seeded {scanned} vanilla unit costs from live ConstructionData (fallback)");
            }

            // 2. Reset _unitCost to vanilla snapshot.
            foreach (var kv in _unitCostVanilla) _unitCost[kv.Key] = kv.Value;

            string path = Path.Combine("UserData", "UnitBalance_cfg", "Si_UnitBalance_Config.json");
            if (!File.Exists(path))
            {
                MelonLogger.Msg($"[KGT] {path} not found — rewards use vanilla unit costs");
                return;
            }

            // 2. Apply cost_mult overrides from UnitBalance config.
            try
            {
                var root = JObject.Parse(File.ReadAllText(path));
                var unitsToken = root["units"];
                if (unitsToken == null || unitsToken.Type != JTokenType.Object)
                {
                    MelonLogger.Warning("[KGT] Si_UnitBalance_Config.json: 'units' object missing");
                    return;
                }

                int touched = 0, skipped = 0;
                foreach (var prop in unitsToken.Children<JProperty>())
                {
                    string unitName = prop.Name;
                    // Skip pseudo-keys: _teleport, _comment_*, etc.
                    if (string.IsNullOrEmpty(unitName) || unitName[0] == '_') continue;
                    var u = prop.Value;
                    if (u == null || u.Type != JTokenType.Object) continue;

                    var mt = u["cost_mult"];
                    if (mt == null) continue;
                    float mult = mt.Value<float>();
                    if (mult <= 0f) continue;

                    if (!_unitCostVanilla.TryGetValue(unitName, out int vanilla) || vanilla <= 0)
                    {
                        skipped++;  // UnitBalance has this unit but our dump doesn't
                        continue;
                    }

                    int modded = (int)Math.Round(vanilla * mult);
                    if (modded != vanilla)
                    {
                        _unitCost[unitName] = modded;
                        touched++;
                    }
                }
                MelonLogger.Msg($"[KGT] Applied cost_mult overrides from Si_UnitBalance_Config.json: {touched} units changed, {skipped} not in dump");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] ApplyUnitBalanceCostOverrides failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Last-resort fallback when Si_UnitBalance_Dump.json is absent (e.g. the server
        /// runs KGT without Si_UnitBalance installed). Walks every loaded ConstructionData
        /// and pulls DisplayName + ResourceCost into _unitCostVanilla. The values are
        /// vanilla because OverrideManager doesn't mutate prefab fields, which is exactly
        /// what we want as a baseline. Returns the number of entries written.
        /// </summary>
        static int SeedVanillaCostsFromConstructionData()
        {
            int n = 0;
            try
            {
                var allCD = Resources.FindObjectsOfTypeAll<ConstructionData>();
                foreach (var cd in allCD)
                {
                    if (cd == null || cd.ObjectInfo == null) continue;
                    string name = cd.ObjectInfo.DisplayName;
                    if (string.IsNullOrEmpty(name)) continue;
                    int cost = cd.ResourceCost;
                    if (cost <= 0) continue;
                    if (_unitCostVanilla.ContainsKey(name)) continue;
                    _unitCostVanilla[name] = cost;
                    n++;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] SeedVanillaCostsFromConstructionData failed: {ex.Message}");
            }
            return n;
        }

        /// <summary>
        /// Called every OnUpdate tick. Periodically (KOH_ALIVE_CHECK_INTERVAL) checks
        /// whether the KoH GameObject is still alive; if a player managed to destroy
        /// it despite our damage-immunity patches, spawn a fresh copy at _kohCenter.
        ///
        /// Capture state (King.cs accumulator, zone scoring, outpost ring) is keyed
        /// off the Vector3 _kohCenter, NOT _kohTower — so it survives destruction
        /// regardless. Auto-respawn just restores the visible building.
        /// </summary>
        static void TickKohAliveCheck(float dt)
        {
            if (!_hasKoh) return;
            _kohAliveCheckTimer -= dt;
            if (_kohAliveCheckTimer > 0f) return;
            _kohAliveCheckTimer = KOH_ALIVE_CHECK_INTERVAL;

            // Unity's overloaded == operator treats destroyed Objects as null.
            bool isDestroyed = (_kohTower == null) || (_kohTower.gameObject == null);
            if (!isDestroyed && _kohTower != null)
            {
                try
                {
                    var dm = _kohTower.gameObject.GetComponent<DamageManager>();
                    if (dm != null && dm.IsDestroyed) isDestroyed = true;
                }
                catch { }
            }
            if (!isDestroyed) return;

            // Can't respawn without the cached prefab/team — give up gracefully.
            if (_kohPrefabRef == null || _kohTeam == null)
            {
                if (_kohRespawnCount == 0)  // log once
                {
                    MelonLogger.Warning("[KGT] KoH destroyed but cache is empty — cannot auto-respawn. Capture continues by position.");
                    _kohRespawnCount = -1;  // flag so we don't spam this warning
                }
                return;
            }

            try
            {
                var newObj = Game.SpawnPrefab(_kohPrefabRef, null, _kohTeam, _kohCenter, _kohRotation, true, true);
                if (newObj != null)
                {
                    _kohTower = newObj.transform;
                    _kohRespawnCount = Mathf.Max(0, _kohRespawnCount) + 1;
                    MelonLogger.Msg($"[KGT] KoH auto-respawned at ({_kohCenter.x:F0},{_kohCenter.z:F0}) — respawn #{_kohRespawnCount}");
                    BroadcastAllChat("[KGT] <color=#39ff14>The Galactic Teleporter has rebuilt itself.</color>");
                }
                else
                {
                    MelonLogger.Warning("[KGT] KoH respawn returned null GameObject");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] KoH respawn threw: {ex.Message}");
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
                // Mode 2 (BuyOnly) / Mode 3 (Disabled): ignore the MapBalance KoH
                // payload entirely — don't claim the building, don't spawn the outpost
                // ring, don't arm the watchdog. If MapBalance still picked a koh layout
                // the building just sits there as a neutral structure with no KGT logic
                // attached.
                if (!ModeKohActive)
                {
                    MelonLogger.Msg($"[KGT] OnMapBalanceSpecialsReady ignored — Mode={Cfg.Mode} (KoH gameplay disabled)");
                    return;
                }
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
                // 0 = unset by MapBalance (per-map config has no exclusion). AntiBuild
                // then falls back to Cfg.BuildExclusionRadius. Don't apply a hard-coded
                // 75 m default here — it silently shadowed the real per-map value.
                _exclusionRadius = (float)(kohType.GetField("ExclusionRadius")?.GetValue(koh) ?? 0f);

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
                _kohRotation = _kohTower.rotation;
                _hasKoh = true;
                _kohRespawnCount = 0;
                _kohAliveCheckTimer = KOH_ALIVE_CHECK_INTERVAL;
                // Cache prefab + team so we can auto-respawn the building if a player
                // manages to slip damage past the immunity patches. Strip "(Clone)" to
                // recover the prefab key used in GameDatabase.
                try
                {
                    string prefabKey = obj.name ?? "";
                    if (prefabKey.EndsWith("(Clone)"))
                        prefabKey = prefabKey.Substring(0, prefabKey.Length - "(Clone)".Length);
                    int pIdx = GameDatabase.GetSpawnablePrefabIndex(prefabKey);
                    _kohPrefabRef = (pIdx >= 0) ? GameDatabase.GetSpawnablePrefab(pIdx) : null;
                    var bgo = obj.GetComponent<BaseGameObject>();
                    _kohTeam = bgo?.Team;
                    if (_kohPrefabRef == null)
                        MelonLogger.Warning($"[KGT] KoH prefab '{prefabKey}' not in GameDatabase — auto-respawn disabled this round");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[KGT] Caching KoH prefab/team failed: {ex.Message}");
                }
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
            _kohRotation = Quaternion.identity;
            _hasKoh = false;
            _neutralTeam = null; // re-resolve on next spawn (KoH may be different team next round)
            // Auto-respawn cache — must clear so next round picks up the new map's prefab/team.
            _kohPrefabRef = null;
            _kohTeam = null;
            _kohRespawnCount = 0;
            _kohAliveCheckTimer = 0f;
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
