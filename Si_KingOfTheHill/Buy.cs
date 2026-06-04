/*
 Buy.cs — multi-step /buy menu for spending credits on units.

 Flow:
   /buy           → open menu at Categories level (shows credits + per-faction categories)
   /buy <N>       → select option N at current level
                    - At Categories: drill into category N
                    - At UnitList: PURCHASE unit N (costs credits, spawns unit)
   /buy back      → go up one level
   /buy exit      → close menu

 We avoid /1-/20 player commands (Si_UnitBalanceUI owns those for !b / /stats).
 All navigation goes through /buy <subcmd> instead.

 Spawn: purchased unit appears Cfg.BuySpawnDistance metres in front of the
 player's controlled unit, on the player's team. Refuses if player isn't
 controlling a unit (commander/observer mode).
*/

using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        enum BuyLevel { Closed, Categories, UnitList }

        class BuyState
        {
            public BuyLevel Level = BuyLevel.Closed;
            public string CategoryBuiltAt = "";                                // e.g. "Barracks" / "Light Factory"
            public List<string> UnitsShown = new List<string>();               // unit DisplayNames in shown order (1-indexed via UI)
            public List<string> CategoriesShown = new List<string>();          // built_at names in shown order
        }

        static readonly Dictionary<long, BuyState> _buyStates = new Dictionary<long, BuyState>();

        // === Color tags (re-use scheme from King.cs) ===
        const string COLOR_AFFORD     = "#39ff14"; // uranium green — affordable
        const string COLOR_TOOEXPENSIVE = "#888888"; // dim gray — can't afford

        // === Public entry point: registered as /buy player command. ===
        static void OnBuyCommand(Player? caller, string args)
        {
            if (caller == null) return;
            long key = GetPlayerSteamId(caller);
            if (!_buyStates.TryGetValue(key, out var state))
            {
                state = new BuyState();
                _buyStates[key] = state;
            }

            // Parse subcommand. SilicaAdminMod passes the FULL chat line as args
            // (e.g. "/buy 1"). We read parts[1] as the sub. If a single-word command
            // came in we treat parts[0] as the sub instead — defensive against any
            // tooling that strips the command prefix.
            string a = (args ?? "").Trim();
            var parts = a.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub;
            if (parts.Length >= 2) sub = parts[1].ToLowerInvariant();
            else if (parts.Length == 1 && parts[0].Length > 0 && parts[0][0] != '/' && !parts[0].Equals("buy", StringComparison.OrdinalIgnoreCase))
                sub = parts[0].ToLowerInvariant();
            else sub = "";

            // Diagnostic — temporary, will quiet once we confirm flow works.
            MelonLogger.Msg($"[KGT/Buy/DBG] args='{a}' parts={parts.Length} sub='{sub}' level={state.Level} cats={state.CategoriesShown.Count} units={state.UnitsShown.Count}");

            // No subcommand → toggle menu open (or refresh current view).
            if (string.IsNullOrEmpty(sub))
            {
                if (state.Level == BuyLevel.Closed)
                {
                    state.Level = BuyLevel.Categories;
                    state.CategoryBuiltAt = "";
                    ShowCategories(caller, state);
                }
                else
                {
                    // Already in menu — re-render current level.
                    if (state.Level == BuyLevel.UnitList) ShowUnits(caller, state);
                    else ShowCategories(caller, state);
                }
                return;
            }

            HandleBuyNav(caller, state, sub);
        }

        /// <summary>
        /// Shared navigation dispatch — used by both /buy &lt;sub&gt; and the chat-hook
        /// /1-/N / /back / /0 path. Assumes state is non-null and already in _buyStates.
        /// </summary>
        static void HandleBuyNav(Player caller, BuyState state, string sub)
        {
            switch (sub)
            {
                case "exit":
                case "close":
                case "0":
                    state.Level = BuyLevel.Closed;
                    Reply(caller, "[KGT/Buy] Menu closed.");
                    return;
                case "back":
                    if (state.Level == BuyLevel.UnitList)
                    {
                        state.Level = BuyLevel.Categories;
                        ShowCategories(caller, state);
                    }
                    else
                    {
                        state.Level = BuyLevel.Closed;
                        Reply(caller, "[KGT/Buy] Menu closed.");
                    }
                    return;
            }

            // Numeric selection.
            if (int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1)
            {
                if (state.Level == BuyLevel.Categories)
                {
                    if (n > state.CategoriesShown.Count) { Reply(caller, "[KGT/Buy] Invalid choice."); return; }
                    state.CategoryBuiltAt = state.CategoriesShown[n - 1];
                    state.Level = BuyLevel.UnitList;
                    ShowUnits(caller, state);
                }
                else if (state.Level == BuyLevel.UnitList)
                {
                    if (n > state.UnitsShown.Count) { Reply(caller, "[KGT/Buy] Invalid choice."); return; }
                    TryPurchase(caller, state.UnitsShown[n - 1]);
                }
                else
                {
                    Reply(caller, "[KGT/Buy] Open the menu first: /buy");
                }
                return;
            }

            Reply(caller, "[KGT/Buy] Usage: /1-N to drill in / buy  ·  /back  ·  /0 to close");
        }

        /// <summary>
        /// Subscribed to SilicaAdminMod.Event_Chat.OnRequestPlayerChat (fires BEFORE the
        /// dispatcher blocks the message — GameEvents.OnChatMessage doesn't fire for blocked
        /// chats, which kills /1-/N because UB registers them with hideFromChat=true).
        /// When the player is in an open /buy menu AND the message is /1-/N / /back / /0,
        /// route to the buy nav. Runs alongside SilicaAdminMod's own dispatch.
        /// </summary>
        static void OnRequestChat_BuyNav(object? sender, OnRequestPlayerChatArgs args)
        {
            try
            {
                if (args.Player == null) return;
                long key = GetPlayerSteamId(args.Player);
                if (!_buyStates.TryGetValue(key, out var state)) return;
                if (state.Level == BuyLevel.Closed) return;

                string text = (args.Text ?? "").Trim();
                if (text.Length < 2 || text[0] != '/') return;
                string sub = text.Substring(1).ToLowerInvariant();

                bool isNum = int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                if (!isNum && sub != "back" && sub != "0" && sub != "exit" && sub != "close") return;

                HandleBuyNav(args.Player, state, sub);
                // Suppress the chat line so /1 doesn't broadcast publicly.
                args.Block = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Buy] OnRequestChat threw: {ex.Message}");
            }
        }

        static bool _buyChatHooked;
        static void HookBuyChat()
        {
            if (_buyChatHooked) return;
            try
            {
                Event_Chat.OnRequestPlayerChat -= OnRequestChat_BuyNav;
                Event_Chat.OnRequestPlayerChat += OnRequestChat_BuyNav;
                _buyChatHooked = true;
                MelonLogger.Msg("[KGT/Buy] Subscribed to Event_Chat.OnRequestPlayerChat for /1-/N nav");
            }
            catch (Exception ex) { MelonLogger.Warning($"[KGT/Buy] HookBuyChat failed: {ex.Message}"); }
        }
        static void ReHookBuyChat() { _buyChatHooked = false; HookBuyChat(); }

        // === Menu rendering ===

        static void ShowCategories(Player caller, BuyState state)
        {
            int credits = GetCredits(caller);
            string faction = DumpFactionForTeam(caller.Team);
            if (string.IsNullOrEmpty(faction))
            {
                Reply(caller, "[KGT/Buy] No faction detected for your team — cannot show menu.");
                return;
            }

            // Collect distinct categories (built_at values) for units of this faction.
            var seen = new HashSet<string>();
            var ordered = new List<string>();
            foreach (var kv in _unitFaction)
            {
                if (!string.Equals(kv.Value, faction, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_unitToBuilding.TryGetValue(kv.Key, out string builtAt)) continue;
                if (string.IsNullOrEmpty(builtAt)) continue;      // skip auxiliaries
                if (!_unitCost.TryGetValue(kv.Key, out int cost) || cost <= 0) continue; // only buyables
                if (seen.Add(builtAt)) ordered.Add(builtAt);
            }
            // Sort by friendly order (Barracks first, then factories by ascending "tier")
            ordered.Sort((a, b) => CategoryOrder(a).CompareTo(CategoryOrder(b)));

            state.CategoriesShown = ordered;

            Reply(caller, $"[KGT/Buy] === Buy Menu === Credits: <color={COLOR_AFFORD}>{credits}</color>");
            for (int i = 0; i < ordered.Count; i++)
            {
                int unitCount = CountUnitsInCategoryForFaction(ordered[i], faction);
                Reply(caller, $"  {i + 1}. {ordered[i]} ({unitCount})");
            }
            Reply(caller, "/1-/N drill in  ·  /0 to close  (or /buy <N>)");
        }

        static void ShowUnits(Player caller, BuyState state)
        {
            int credits = GetCredits(caller);
            string faction = DumpFactionForTeam(caller.Team);

            // Collect units in this faction + category, sorted by cost asc.
            var units = new List<string>();
            foreach (var kv in _unitFaction)
            {
                if (!string.Equals(kv.Value, faction, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_unitToBuilding.TryGetValue(kv.Key, out string builtAt)) continue;
                if (!string.Equals(builtAt, state.CategoryBuiltAt, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_unitCost.TryGetValue(kv.Key, out int cost) || cost <= 0) continue;
                units.Add(kv.Key);
            }
            units.Sort((a, b) =>
            {
                _unitCost.TryGetValue(a, out int ca);
                _unitCost.TryGetValue(b, out int cb);
                return ca.CompareTo(cb);
            });

            state.UnitsShown = units;

            Reply(caller, $"[KGT/Buy] === {state.CategoryBuiltAt} === Credits: <color={COLOR_AFFORD}>{credits}</color>");
            for (int i = 0; i < units.Count; i++)
            {
                _unitCost.TryGetValue(units[i], out int cost);
                string color = cost <= credits ? COLOR_AFFORD : COLOR_TOOEXPENSIVE;
                Reply(caller, $"  {i + 1}. <color={color}>{units[i]}</color>  cost <color={color}>{cost}</color>cr");
            }
            Reply(caller, "/1-/N to buy  ·  /back  ·  /0 to close  (or /buy <N>)");
        }

        // === Purchase + spawn ===

        static void TryPurchase(Player caller, string unitName)
        {
            long key = GetPlayerSteamId(caller);
            int credits = GetCredits(key);

            if (!_unitCost.TryGetValue(unitName, out int cost) || cost <= 0)
            {
                Reply(caller, $"[KGT/Buy] {unitName}: cost unknown — cannot purchase.");
                return;
            }
            if (credits < cost)
            {
                Reply(caller, $"[KGT/Buy] Insufficient credits — {unitName} costs {cost}, you have {credits}.");
                return;
            }

            var controlled = caller.ControlledUnit;
            if (controlled == null)
            {
                Reply(caller, "[KGT/Buy] You must be controlling a unit (the purchase spawns next to you).");
                return;
            }

            if (!_unitPrefab.TryGetValue(unitName, out string prefabName) || string.IsNullOrEmpty(prefabName))
            {
                Reply(caller, $"[KGT/Buy] {unitName}: prefab name unknown — cannot spawn.");
                return;
            }

            int idx = GameDatabase.GetSpawnablePrefabIndex(prefabName);
            if (idx < 0)
            {
                Reply(caller, $"[KGT/Buy] Prefab '{prefabName}' not in spawnable database — purchase aborted.");
                return;
            }
            var prefab = GameDatabase.GetSpawnablePrefab(idx);
            if (prefab == null)
            {
                Reply(caller, $"[KGT/Buy] Prefab '{prefabName}' lookup returned null — purchase aborted.");
                return;
            }

            // Spawn position: BuySpawnDistance metres in front of player's facing.
            // Use SampleSurfaceY_NearAnchor (not the generic SampleSurfaceY which casts
            // from terrain+200m and accepts any collider hit) to avoid the spawn-in-sky
            // bug where a flying unit / projectile between sky and ground was being
            // returned as the surface.
            Vector3 origin = controlled.transform.position;
            Vector3 fwd = controlled.transform.forward;
            float dist = Mathf.Max(2f, Cfg.BuySpawnDistance);
            Vector3 xz = origin + fwd * dist;
            float groundY = SampleSurfaceY_NearAnchor(Terrain.activeTerrain, xz.x, xz.z, origin.y) + 2f;
            Vector3 pos = new Vector3(xz.x, groundY, xz.z);

            // Anti-rush: refuse purchase if the spawn would land too close to ANY enemy critical
            // (HQ / Nest / Queen). Bypass when configured radius is 0.
            if (IsTooCloseToEnemyCritical(caller, pos, out string blocker, out float bDist))
            {
                Reply(caller, $"[KGT/Buy] Too close to enemy {blocker}: {bDist:F0}m (min {Cfg.BuyMinDistanceFromEnemyCritical:F0}m). Move further away and try again. <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>");
                return;
            }

            GameObject? spawned = null;
            try
            {
                spawned = Game.SpawnPrefab(prefab, null, caller.Team, pos, controlled.transform.rotation, true, true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Buy] Game.SpawnPrefab threw for {prefabName}: {ex.Message}");
            }

            if (spawned == null)
            {
                Reply(caller, $"[KGT/Buy] Spawn failed for '{unitName}' — credits NOT charged.");
                return;
            }

            AddCredits(key, -cost);
            int newCredits = GetCredits(key);
            Reply(caller, $"[KGT/Buy] Purchased <color={COLOR_AFFORD}>{unitName}</color> for {cost}cr. Remaining: {newCredits}cr");
            MelonLogger.Msg($"[KGT/Buy] {caller.PlayerName} bought {unitName} ({prefabName}) for {cost}cr. Spawned at ({pos.x:F0},{pos.z:F0}).");
        }

        // === Helpers ===

        static int CountUnitsInCategoryForFaction(string builtAt, string faction)
        {
            int n = 0;
            foreach (var kv in _unitFaction)
            {
                if (!string.Equals(kv.Value, faction, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_unitToBuilding.TryGetValue(kv.Key, out string b) || !string.Equals(b, builtAt, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_unitCost.TryGetValue(kv.Key, out int c) || c <= 0) continue;
                n++;
            }
            return n;
        }

        /// <summary>
        /// Returns true if <paramref name="spawnPos"/> is within Cfg.BuyMinDistanceFromEnemyCritical
        /// of any critical structure or unit belonging to a team that isn't the player's.
        /// Skips neutral teams (Gamemaster/Wildlife). 3D distance.
        /// </summary>
        static bool IsTooCloseToEnemyCritical(Player player, Vector3 spawnPos, out string blocker, out float distance)
        {
            blocker = "";
            distance = float.PositiveInfinity;

            float minDist = Cfg.BuyMinDistanceFromEnemyCritical;
            if (minDist <= 0f) return false;

            var ownTeam = player?.Team;
            try
            {
                foreach (var t in Team.Teams)
                {
                    if (t == null) continue;
                    if (ReferenceEquals(t, ownTeam)) continue;
                    if (IsGamemasterTeam(t)) continue; // skip neutrals (Wildlife/Gamemaster KoH building etc.)

                    var structs = t.Structures;
                    if (structs != null)
                    {
                        for (int i = 0; i < structs.Count; i++)
                        {
                            var s = structs[i];
                            if (s == null || s.IsDestroyed) continue;
                            if (s.ObjectInfo == null || !s.ObjectInfo.Critical) continue;
                            float d = Vector3.Distance(spawnPos, s.transform.position);
                            if (d < minDist && d < distance)
                            {
                                distance = d;
                                blocker = (s.ObjectInfo.DisplayName ?? s.name) + " (" + SafeTeamName(t) + ")";
                            }
                        }
                    }
                    var units = t.Units;
                    if (units != null)
                    {
                        for (int i = 0; i < units.Count; i++)
                        {
                            var u = units[i];
                            if (u == null || u.IsDestroyed) continue;
                            if (u.ObjectInfo == null || !u.ObjectInfo.Critical) continue;
                            float d = Vector3.Distance(spawnPos, u.transform.position);
                            if (d < minDist && d < distance)
                            {
                                distance = d;
                                blocker = (u.ObjectInfo.DisplayName ?? u.name) + " (" + SafeTeamName(t) + ")";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Buy] IsTooCloseToEnemyCritical threw: {ex.Message}");
            }

            return !string.IsNullOrEmpty(blocker);
        }

        // Map Silica team to dump's faction field.
        static string DumpFactionForTeam(Team team)
        {
            string s = team?.GetTeamShortName() ?? "";
            if (s.Equals("Cent", StringComparison.OrdinalIgnoreCase)) return "Centauri";
            if (s.Equals("Sol",   StringComparison.OrdinalIgnoreCase)) return "Sol";
            if (s.Equals("Alien", StringComparison.OrdinalIgnoreCase)) return "Alien";
            return s;
        }

        // Friendly ordering for category list: Barracks first, then Light → Heavy → Ultra → Air,
        // then alien Lesser → Greater → Grand → Colossal.
        static int CategoryOrder(string builtAt)
        {
            switch (builtAt)
            {
                case "Barracks":               return 1;
                case "Light Factory":          return 2;
                case "Heavy Factory":          return 3;
                case "Ultra Heavy Factory":    return 4;
                case "Air Factory":            return 5;
                case "Lesser Spawning Cyst":   return 11;
                case "Greater Spawning Cyst":  return 12;
                case "Grand Spawning Cyst":    return 13;
                case "Colossal Spawning Cyst": return 14;
                default:                       return 99;
            }
        }

        static void ResetBuyState()
        {
            _buyStates.Clear();
        }
    }
}
