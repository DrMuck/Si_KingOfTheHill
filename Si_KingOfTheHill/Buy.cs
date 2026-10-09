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

        // One selectable row in the /buy unit/structure list. Carries its own resolved
        // prefab + cost so purchase never re-derives them from the display-name-keyed
        // dicts (which collide for faction-shared structure names — see _structRows).
        class BuyChoice
        {
            public string Display    = "";
            public string Prefab     = "";   // name hint only — resolution goes through ObjectInfoName
            public string ObjectInfoName = "";  // dump "internal", e.g. "ObjectInfo_Alien_Nest"
            public int    Cost;
            public bool   IsStructure;
        }

        class BuyState
        {
            public BuyLevel Level = BuyLevel.Closed;
            public string CategoryBuiltAt = "";                                // e.g. "Barracks" / "Light Factory"
            public List<BuyChoice> UnitsShown = new List<BuyChoice>();          // items in shown order (1-indexed via UI)
            public List<string> CategoriesShown = new List<string>();          // built_at names in shown order
        }

        static readonly Dictionary<long, BuyState> _buyStates = new Dictionary<long, BuyState>();

        // Anti-spam (#3): Time.time of each player's last successful purchase.
        // Cleared between rounds by ResetBuyState().
        static readonly Dictionary<long, float> _lastPurchaseTime = new Dictionary<long, float>();

        // Seconds since the current round started (Time.time based). Returns
        // +Infinity if no round-start stamp yet, so purchases fail-open before a round.
        static float RoundElapsedSeconds()
            => _roundStartTime > 0f ? Mathf.Max(0f, Time.time - _roundStartTime) : float.PositiveInfinity;

        // A "shrimp" is any unit whose display name contains "shrimp" (covers future variants).
        static bool IsShrimpUnit(string displayName)
            => !string.IsNullOrEmpty(displayName)
               && displayName.IndexOf("shrimp", StringComparison.OrdinalIgnoreCase) >= 0;

        // Compact mm ss formatting for lock / cooldown messages.
        static string FormatMMSS(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int total = Mathf.CeilToInt(seconds);
            int m = total / 60;
            int s = total % 60;
            return m > 0 ? $"{m}m{s:00}s" : $"{s}s";
        }

        // === Color tags (re-use scheme from King.cs) ===
        const string COLOR_AFFORD     = "#39ff14"; // uranium green — affordable
        const string COLOR_TOOEXPENSIVE = "#888888"; // dim gray — can't afford

        // Pseudo-category appended at the end of the /buy menu when
        // Cfg.BuyStructuresEnabled is true. Lists all faction structures
        // (mirrors the "STRUCTURES (SHARED)" group in the UnitBalance editor).
        const string STRUCTURES_CATEGORY = "Buildings";

        // === Public entry point: registered as /buy player command. ===
        static void OnBuyCommand(Player? caller, string args)
        {
            if (caller == null) return;
            if (!ModeBuyActive)
            {
                Reply(caller, "[KGT/Buy] /buy is currently disabled.");
                return;
            }
            if (Cfg.BuyAdminOnly && !caller.CanAdminExecute(Power.Generic))
            {
                Reply(caller, "[KGT/Buy] /buy is admin-only on this server.");
                return;
            }
            long key = GetPlayerSteamId(caller);
            // If the caller is an admin with an open !b editor session, /1-/N would
            // route to BOTH menus and dump balance/HTP output into the buy session.
            // Refuse and ask them to close !b first. Non-admins can't open !b so they
            // skip this guard automatically.
            if (caller.CanAdminExecute(Power.Generic) && IsUnitBalanceEditorOpen(key))
            {
                Reply(caller, "[KGT/Buy] You have a balance editor (/b) session open. Close it first (type /b again), then run /buy.");
                return;
            }
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

        // === Prefab resolution ===
        //
        // The dump's "internal" field is the ObjectInfo ASSET name; the spawn database is
        // keyed by PREFAB name, and no single string rule maps one onto the other:
        //   ObjectInfo_Alien_Scorpion    → "Scorpion"            (creatures drop the faction prefix)
        //   ObjectInfo_Alien_Nest        → "Alien_Nest"          (structures keep it)
        //   ObjectInfo_Alien_CrabHorned  → "Crab_Horned"         (asset name differs outright)
        //   ObjectInfo_FusionReactor     → "FusionReactor_01"
        // Deriving the key by string surgery broke every alien building ("Prefab 'Nest' not
        // in spawnable database"). ObjectInfo holds a direct reference to its own prefab, so
        // we index THAT and keep the name lookup only as a fallback.
        static readonly Dictionary<string, GameObject> _prefabByObjectInfo =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        static bool _prefabIndexBuilt;

        static void BuildPrefabIndex(bool force)
        {
            if (_prefabIndexBuilt && !force) return;
            _prefabIndexBuilt = true;
            try
            {
                // Resources.FindObjectsOfTypeAll is what Si_UnitBalance uses to produce the
                // dump in the first place, so every "internal" value has a match here.
                var all = Resources.FindObjectsOfTypeAll<ObjectInfo>();
                if (all == null) return;
                _prefabByObjectInfo.Clear();
                foreach (var oi in all)
                {
                    if (oi == null || oi.Prefab == null) continue;
                    string key = oi.name ?? "";
                    if (key.Length == 0) continue;
                    _prefabByObjectInfo[key] = oi.Prefab;
                }
                MelonLogger.Msg($"[KGT/Buy] Prefab index built: {_prefabByObjectInfo.Count} ObjectInfo→prefab entries");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Buy] Prefab index build failed ({ex.Message}) — falling back to name lookup");
            }
        }

        static GameObject? PrefabForObjectInfo(string objectInfoName)
        {
            if (string.IsNullOrEmpty(objectInfoName)) return null;
            BuildPrefabIndex(false);
            bool known = _prefabByObjectInfo.TryGetValue(objectInfoName, out var go);
            if (known && go != null) return go;
            if (!known) return null;   // scanned and genuinely absent — don't rescan per row
            // Known key but the reference went stale (map reload) — rebuild once and retry.
            BuildPrefabIndex(true);
            return _prefabByObjectInfo.TryGetValue(objectInfoName, out go) && go != null ? go : null;
        }

        static GameObject? PrefabByName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            try
            {
                int idx = GameDatabase.GetSpawnablePrefabIndex(prefabName);
                return idx >= 0 ? GameDatabase.GetSpawnablePrefab(idx) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Resolve a menu row to the prefab Game.SpawnPrefab should be handed.
        /// ObjectInfo first, spawn-DB name lookup second. Null = not purchasable.
        /// </summary>
        static GameObject? ResolveBuyPrefab(BuyChoice choice, out string resolvedName)
        {
            var go = PrefabForObjectInfo(choice.ObjectInfoName);
            if (go != null)
            {
                resolvedName = go.name;
                // ObjectInfo.Prefab should also be the networked prefab the DB knows. If it
                // isn't, spawning still works server-side but clients may not see it — worth
                // a log line rather than a silent oddity.
                try
                {
                    if (GameDatabase.GetSpawnablePrefabIndex(go) < 0)
                        MelonLogger.Warning($"[KGT/Buy] '{resolvedName}' (from {choice.ObjectInfoName}) is not in the spawnable DB — spawning the ObjectInfo prefab directly.");
                }
                catch { }
                return go;
            }
            go = PrefabByName(choice.Prefab);
            resolvedName = go != null ? go.name : choice.Prefab;
            return go;
        }

        // Menu-side purchasability test — same two lookups, no spawn, no logging. Rows that
        // fail are hidden so players never see a building the server can't actually spawn.
        static bool CanResolveBuyChoice(BuyChoice choice)
            => PrefabForObjectInfo(choice.ObjectInfoName) != null || PrefabByName(choice.Prefab) != null;

        // Same test for a unit row that hasn't been built into a BuyChoice yet (category counts).
        static bool CanResolveUnit(string display)
        {
            _internalByDisplay.TryGetValue(display, out var objectInfoName);
            _unitPrefab.TryGetValue(display, out var prefabHint);
            return PrefabForObjectInfo(objectInfoName ?? "") != null || PrefabByName(prefabHint ?? "") != null;
        }

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
                if (_isStructure.Contains(kv.Key)) continue;      // structures aren't buyable
                if (!_unitToBuilding.TryGetValue(kv.Key, out string builtAt)) continue;
                if (string.IsNullOrEmpty(builtAt)) continue;      // skip auxiliaries
                if (!_unitCost.TryGetValue(kv.Key, out int cost) || cost <= 0) continue; // only buyables
                if (!CanResolveUnit(kv.Key)) continue;            // no spawnable prefab → not a real category member
                if (seen.Add(builtAt)) ordered.Add(builtAt);
            }
            // Sort by friendly order (Barracks first, then factories by ascending "tier")
            ordered.Sort((a, b) => CategoryOrder(a).CompareTo(CategoryOrder(b)));

            // Optional structures pseudo-category, always appended last so the
            // existing unit-tier numbering stays stable.
            if (Cfg.BuyStructuresEnabled && CountStructuresForFaction(faction) > 0)
                ordered.Add(STRUCTURES_CATEGORY);

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

            // Collect items in this faction + category, sorted by cost asc.
            bool wantStructures = string.Equals(state.CategoryBuiltAt, STRUCTURES_CATEGORY, StringComparison.OrdinalIgnoreCase);
            var items = new List<BuyChoice>();
            if (wantStructures)
            {
                // Structures come from the faction-aware catalog (NOT the display-keyed
                // dicts), so Sol/Centauri same-named buildings each resolve their own
                // prefab. Cost is faction-independent → look it up by display name.
                foreach (var r in _structRows)
                {
                    if (!string.Equals(r.Faction, faction, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(r.Prefab) && string.IsNullOrEmpty(r.ObjectInfo)) continue;
                    if (!_unitCost.TryGetValue(r.Display, out int cost) || cost <= 0) continue;
                    var choice = new BuyChoice
                    {
                        Display = r.Display, Prefab = r.Prefab, ObjectInfoName = r.ObjectInfo,
                        Cost = cost, IsStructure = true,
                    };
                    if (!CanResolveBuyChoice(choice)) continue;   // no spawnable prefab → don't offer it
                    items.Add(choice);
                }
            }
            else
            {
                // Units: display names don't collide across factions, so the dicts are safe.
                foreach (var kv in _unitFaction)
                {
                    if (!string.Equals(kv.Value, faction, StringComparison.OrdinalIgnoreCase)) continue;
                    if (_isStructure.Contains(kv.Key)) continue;   // structures handled above
                    if (!_unitToBuilding.TryGetValue(kv.Key, out string builtAt)) continue;
                    if (!string.Equals(builtAt, state.CategoryBuiltAt, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!_unitCost.TryGetValue(kv.Key, out int cost) || cost <= 0) continue;
                    _unitPrefab.TryGetValue(kv.Key, out string prefab);
                    _internalByDisplay.TryGetValue(kv.Key, out string objectInfoName);
                    var choice = new BuyChoice
                    {
                        Display = kv.Key, Prefab = prefab ?? "", ObjectInfoName = objectInfoName ?? "",
                        Cost = cost, IsStructure = false,
                    };
                    if (!CanResolveBuyChoice(choice)) continue;   // no spawnable prefab → don't offer it
                    items.Add(choice);
                }
            }
            items.Sort((a, b) => a.Cost.CompareTo(b.Cost));

            state.UnitsShown = items;

            float shrimpRemain = Cfg.ShrimpBuyUnlockSeconds > 0f ? Cfg.ShrimpBuyUnlockSeconds - RoundElapsedSeconds() : 0f;
            Reply(caller, $"[KGT/Buy] === {state.CategoryBuiltAt} === Credits: <color={COLOR_AFFORD}>{credits}</color>");

            // Heads-up when the player is standing outside the base leash — otherwise a
            // refused purchase is the first feedback they get.
            var ctrl = caller.ControlledUnit;
            if (ctrl != null && IsTooFarFromOwnCritical(caller, ctrl.transform.position, out float awayDist))
                Reply(caller, $"  <color={COLOR_TOOEXPENSIVE}>You are {awayDist:F0}m from your nearest HQ/Nest — purchases are refused beyond {Cfg.BuyMaxDistanceFromOwnCritical:F0}m.</color>");
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                bool locked = shrimpRemain > 0f && IsShrimpUnit(it.Display);
                if (locked)
                {
                    Reply(caller, $"  {i + 1}. <color={COLOR_TOOEXPENSIVE}>{it.Display}</color>  <color={COLOR_TOOEXPENSIVE}>LOCKED — unlocks in {FormatMMSS(shrimpRemain)}</color>");
                    continue;
                }
                string color = it.Cost <= credits ? COLOR_AFFORD : COLOR_TOOEXPENSIVE;
                Reply(caller, $"  {i + 1}. <color={color}>{it.Display}</color>  cost <color={color}>{it.Cost}</color>cr");
            }
            Reply(caller, "/1-/N to buy  ·  /back  ·  /0 to close  (or /buy <N>)");
        }

        // === Purchase + spawn ===

        static void TryPurchase(Player caller, BuyChoice choice)
        {
            long key = GetPlayerSteamId(caller);
            int credits = GetCredits(key);
            string unitName = choice.Display;
            int cost = choice.Cost;

            if (cost <= 0)
            {
                Reply(caller, $"[KGT/Buy] {unitName}: cost unknown — cannot purchase.");
                return;
            }

            // #1 — Shrimp time lock: no shrimps until ShrimpBuyUnlockSeconds into the round.
            if (Cfg.ShrimpBuyUnlockSeconds > 0f && IsShrimpUnit(unitName))
            {
                float elapsed = RoundElapsedSeconds();
                if (elapsed < Cfg.ShrimpBuyUnlockSeconds)
                {
                    float remain = Cfg.ShrimpBuyUnlockSeconds - elapsed;
                    Reply(caller, $"[KGT/Buy] {unitName} is locked for the first {FormatMMSS(Cfg.ShrimpBuyUnlockSeconds)} of the round — unlocks in {FormatMMSS(remain)}. <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>");
                    return;
                }
            }

            // #3 — Anti-spam cooldown: one purchase per BuyCooldownSeconds per player.
            if (Cfg.BuyCooldownSeconds > 0f && _lastPurchaseTime.TryGetValue(key, out float lastBuy))
            {
                float since = Time.time - lastBuy;
                if (since < Cfg.BuyCooldownSeconds)
                {
                    float remain = Cfg.BuyCooldownSeconds - since;
                    Reply(caller, $"[KGT/Buy] Purchase on cooldown — wait {FormatMMSS(remain)} before buying again. <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>");
                    return;
                }
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

            if (string.IsNullOrEmpty(choice.Prefab) && string.IsNullOrEmpty(choice.ObjectInfoName))
            {
                Reply(caller, $"[KGT/Buy] {unitName}: prefab name unknown — cannot spawn.");
                return;
            }

            var prefab = ResolveBuyPrefab(choice, out string prefabName);
            if (prefab == null)
            {
                Reply(caller, $"[KGT/Buy] Prefab '{prefabName}' not in spawnable database — purchase aborted.");
                MelonLogger.Warning($"[KGT/Buy] Could not resolve {unitName}: objectInfo='{choice.ObjectInfoName}' nameHint='{choice.Prefab}'");
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

            // Base leash: /buy only works near your own HQ / Nest.
            if (IsTooFarFromOwnCritical(caller, pos, out float ownDist))
            {
                Reply(caller, $"[KGT/Buy] Too far from your nearest HQ/Nest: {ownDist:F0}m (max {Cfg.BuyMaxDistanceFromOwnCritical:F0}m). Move closer to base and try again. <color={COLOR_TOOEXPENSIVE}>Credits not charged.</color>");
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
            _lastPurchaseTime[key] = Time.time;   // #3 — start anti-spam cooldown
            int newCredits = GetCredits(key);
            string cooldownNote = Cfg.BuyCooldownSeconds > 0f ? $" Next buy in {FormatMMSS(Cfg.BuyCooldownSeconds)}." : "";
            Reply(caller, $"[KGT/Buy] Purchased <color={COLOR_AFFORD}>{unitName}</color> for {cost}cr. Remaining: {newCredits}cr.{cooldownNote}");
            MelonLogger.Msg($"[KGT/Buy] {caller.PlayerName} bought {unitName} ({prefabName}) for {cost}cr. Spawned at ({pos.x:F0},{pos.z:F0}).");
        }

        // === Helpers ===

        static int CountUnitsInCategoryForFaction(string builtAt, string faction)
        {
            if (string.Equals(builtAt, STRUCTURES_CATEGORY, StringComparison.OrdinalIgnoreCase))
                return CountStructuresForFaction(faction);

            int n = 0;
            foreach (var kv in _unitFaction)
            {
                if (!string.Equals(kv.Value, faction, StringComparison.OrdinalIgnoreCase)) continue;
                if (_isStructure.Contains(kv.Key)) continue;
                if (!_unitToBuilding.TryGetValue(kv.Key, out string b) || !string.Equals(b, builtAt, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_unitCost.TryGetValue(kv.Key, out int c) || c <= 0) continue;
                if (!CanResolveUnit(kv.Key)) continue;   // hidden from ShowUnits → don't count it
                n++;
            }
            return n;
        }

        static int CountStructuresForFaction(string faction)
        {
            // Count from the faction-aware catalog so Sol/Centauri same-named buildings
            // are each counted for their own faction (a display-keyed scan would drop the
            // duplicates and undercount Centauri to just its HQ).
            int n = 0;
            foreach (var r in _structRows)
            {
                if (!string.Equals(r.Faction, faction, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(r.Prefab) && string.IsNullOrEmpty(r.ObjectInfo)) continue;
                if (!_unitCost.TryGetValue(r.Display, out int c) || c <= 0) continue;
                if (!CanResolveBuyChoice(new BuyChoice { Prefab = r.Prefab, ObjectInfoName = r.ObjectInfo })) continue;
                n++;
            }
            return n;
        }

        /// <summary>
        /// Returns true if <paramref name="spawnPos"/> is within Cfg.BuyMinDistanceFromEnemyCritical
        /// of any critical structure or unit belonging to a team that isn't the player's.
        /// Skips neutral teams (Gamemaster/Wildlife). Uses CYLINDRICAL (horizontal/radial,
        /// XZ-plane) distance so altitude is ignored — buying in high orbit directly above
        /// an enemy HQ/Nest is blocked exactly the same as buying at ground level next to it.
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
                            float d = HorizontalDistance(spawnPos, s.transform.position);
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
                            float d = HorizontalDistance(spawnPos, u.transform.position);
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

        /// <summary>
        /// Returns true if <paramref name="spawnPos"/> is FURTHER than
        /// Cfg.BuyMaxDistanceFromOwnCritical from every critical structure the player's team
        /// owns (HQ / Nest). Keeps /buy a base-reinforcement tool rather than a
        /// spawn-an-army-anywhere button. Uses the same CYLINDRICAL (XZ-plane) distance as the
        /// enemy-proximity gate, so altitude can't be used to dodge it.
        ///
        /// Only STRUCTURES anchor the bubble — the alien Queen is critical but mobile, and
        /// anchoring on her would let an alien team carry its buy zone across the map.
        ///
        /// Fails OPEN (returns false) when the team owns no critical structure at all: a team
        /// that just lost its last nest shouldn't also lose access to /buy.
        /// </summary>
        static bool IsTooFarFromOwnCritical(Player player, Vector3 spawnPos, out float distance)
        {
            distance = float.PositiveInfinity;

            float maxDist = Cfg.BuyMaxDistanceFromOwnCritical;
            if (maxDist <= 0f) return false;

            var team = player?.Team;
            if (team == null) return false;

            int anchors = 0;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                {
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.IsDestroyed) continue;
                        if (s.ObjectInfo == null || !s.ObjectInfo.Critical) continue;
                        anchors++;
                        float d = HorizontalDistance(spawnPos, s.transform.position);
                        if (d < distance) distance = d;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Buy] IsTooFarFromOwnCritical threw: {ex.Message}");
                return false;
            }

            if (anchors == 0)
            {
                MelonLogger.Msg($"[KGT/Buy] {player?.PlayerName} has no own HQ/Nest to anchor the buy radius — allowing purchase.");
                return false;
            }

            return distance > maxDist;
        }

        // Horizontal (XZ-plane) distance — ignores altitude so the no-buy zone is
        // a vertical cylinder around the enemy critical, not a sphere.
        static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
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
            _lastPurchaseTime.Clear();   // reset anti-spam cooldowns between rounds
            _prefabIndexBuilt = false;   // map change can invalidate cached prefab refs — rebuild on next use
        }

        // ----- Cross-mod menu collision guard -----
        //
        // Si_UnitBalanceUI keeps per-player admin-editor sessions in
        //   private static Dictionary<long, BalanceMenuState> _menuStates
        // on type Si_UnitBalance.Si_UnitBalance. We reflect-probe it (no compile-time
        // dependency) so we can detect when /b is open and tell the admin to close it
        // before opening /buy — otherwise /1-/N navigation would feed both menus.

        static System.Reflection.FieldInfo? _ubMenuStatesField;
        static bool _ubProbed;

        static bool IsUnitBalanceEditorOpen(long steamId)
        {
            if (!_ubProbed)
            {
                _ubProbed = true;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("Si_UnitBalance.Si_UnitBalance");
                        if (t == null) continue;
                        _ubMenuStatesField = t.GetField("_menuStates",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                        MelonLogger.Msg($"[KGT/Buy] Si_UnitBalanceUI integration: _menuStates={_ubMenuStatesField != null}");
                        break;
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"[KGT/Buy] UnitBalanceUI probe failed: {ex.Message}"); }
            }
            if (_ubMenuStatesField == null) return false;
            try
            {
                var dict = _ubMenuStatesField.GetValue(null);
                if (dict == null) return false;
                var contains = dict.GetType().GetMethod("ContainsKey", new[] { typeof(long) });
                var result = contains?.Invoke(dict, new object[] { steamId });
                return result is bool b && b;
            }
            catch { return false; }
        }
    }
}
