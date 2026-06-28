/*
 Patches.cs — Harmony patches for KoH.

 Currently:
   - DamageManager.ApplyDamage prefix: zero out damage when the target is the
     KoH building. Required now that we spawn Sol_UltraHeavyFactory (which has
     a DamageManager + HP); without immunity players could destroy the KoH.

 Planned:
   - Anti-build inside BuildExclusionRadius (Structure.Construct prefix).
   - Round-end forcing on KoH timer expiry.
*/

using HarmonyLib;
using MelonLoader;
using System;
using System.Reflection;
using UnityEngine;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        static Type? _damageManagerType;
        static MethodInfo? _applyDamageMethod;
        static bool _damagePatchInstalled;

        // Cached team type + Gamemaster team for the AI-targeting bypass.
        static Type? _teamTypeForTargeting;
        static FieldInfo? _teamShortNameField;
        static object? _cachedGamemasterTeam;
        static bool _targetingPatchInstalled;

        /// <summary>
        /// Install the Harmony damage-immunity patch. Idempotent. Called from
        /// OnInitializeMelon; safe to call again later if the type wasn't loaded yet.
        /// </summary>
        static void InstallDamagePatch(HarmonyLib.Harmony harmony)
        {
            if (_damagePatchInstalled) return;
            try
            {
                if (_damageManagerType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("DamageManager");
                        if (t != null) { _damageManagerType = t; break; }
                    }
                }
                if (_damageManagerType == null)
                {
                    MelonLogger.Warning("[KGT] DamageManager type not found — damage immunity patch deferred");
                    return;
                }

                // public float ApplyDamage(Collider, float, EDamageType, GameObject, Vector3)
                _applyDamageMethod = _damageManagerType.GetMethod("ApplyDamage",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_applyDamageMethod != null)
                {
                    var prefix = typeof(KingOfTheHill).GetMethod(
                        nameof(Prefix_ApplyDamage),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(_applyDamageMethod, prefix: new HarmonyMethod(prefix));
                    MelonLogger.Msg("[KGT] Patched DamageManager.ApplyDamage");
                }
                else
                {
                    MelonLogger.Warning("[KGT] DamageManager.ApplyDamage method not found");
                }

                // Also patch SetHealth(float, GameObject, bool) — direct health writes that
                // bypass ApplyDamage (network sync, debug, externally-triggered damage).
                var setHealthMethod = _damageManagerType.GetMethod("SetHealth",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(float), typeof(GameObject), typeof(bool) },
                    null);
                if (setHealthMethod != null)
                {
                    var prefix = typeof(KingOfTheHill).GetMethod(
                        nameof(Prefix_SetHealth),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(setHealthMethod, prefix: new HarmonyMethod(prefix));
                    MelonLogger.Msg("[KGT] Patched DamageManager.SetHealth");
                }
                else
                {
                    MelonLogger.Warning("[KGT] DamageManager.SetHealth(float,GameObject,bool) not found");
                }

                // Also patch SetHealth01(float) — the fractional setter. This is what
                // StrategyMode.PerformDestroyStructure (sell action) calls with 0f.
                // Without this patch, ANY player can sell the KoH and bypass our other guards.
                var setHealth01Method = _damageManagerType.GetMethod("SetHealth01",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(float) },
                    null);
                if (setHealth01Method != null)
                {
                    var prefix = typeof(KingOfTheHill).GetMethod(
                        nameof(Prefix_SetHealth01),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(setHealth01Method, prefix: new HarmonyMethod(prefix));
                    MelonLogger.Msg("[KGT] Patched DamageManager.SetHealth01");
                }
                else
                {
                    MelonLogger.Warning("[KGT] DamageManager.SetHealth01(float) not found");
                }

                // Also patch OnReceiveClientDamageHitPacket — the CRITICAL path for
                // player-controlled-unit damage. Client computes damage locally and sends a
                // packet; server applies via Health -= value (bypasses ApplyDamage).
                //
                // Per databomb: a plain Prefix returning false would leave the packet stream
                // un-consumed, corrupting subsequent reads. So we transpile: skip just the
                // "Health -= value; OnDamageReceived(...)" instructions when target is KoH,
                // letting all packetReader reads above them run normally.
                var clientHitMethod = _damageManagerType.GetMethod("OnReceiveClientDamageHitPacket",
                    BindingFlags.Public | BindingFlags.Instance);
                if (clientHitMethod != null)
                {
                    var transpiler = typeof(KingOfTheHill).GetMethod(
                        nameof(Transpile_OnReceiveClientDamageHitPacket),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(clientHitMethod, transpiler: new HarmonyMethod(transpiler));
                    MelonLogger.Msg("[KGT] Transpiled DamageManager.OnReceiveClientDamageHitPacket");
                }
                else
                {
                    MelonLogger.Warning("[KGT] DamageManager.OnReceiveClientDamageHitPacket not found");
                }

                _damagePatchInstalled = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] InstallDamagePatch failed: {ex.Message}");
            }

            InstallTargetingPatch(harmony);
            return;
        }

        /// <summary>
        /// Patch GameMode.GetTeamsAreEnemy to make Gamemaster team allied with every team.
        /// This blocks AI sensors from selecting our KoH as a target (and also prevents
        /// FOW exposure / friendly fire flags — all of which gate on GetTeamsAreEnemy).
        /// Pattern matches Si_4way's Alliance system.
        /// </summary>
        static void InstallTargetingPatch(HarmonyLib.Harmony harmony)
        {
            if (_targetingPatchInstalled) return;
            try
            {
                Type? gameModeType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("GameMode");
                    if (t != null) { gameModeType = t; break;}
                }
                if (gameModeType == null) { MelonLogger.Warning("[KGT] GameMode type not found — targeting bypass skipped"); return; }

                _teamTypeForTargeting = gameModeType.Assembly.GetType("Team");
                _teamShortNameField = _teamTypeForTargeting?.GetField("TeamShortName", BindingFlags.Public | BindingFlags.Instance);

                var method = gameModeType.GetMethod("GetTeamsAreEnemy", BindingFlags.Public | BindingFlags.Instance);
                if (method == null) { MelonLogger.Warning("[KGT] GameMode.GetTeamsAreEnemy not found"); return; }

                var postfix = typeof(KingOfTheHill).GetMethod(nameof(Postfix_GetTeamsAreEnemy),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                _targetingPatchInstalled = true;
                MelonLogger.Msg("[KGT] Patched GameMode.GetTeamsAreEnemy (Gamemaster team is always allied)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] InstallTargetingPatch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on GameMode.GetTeamsAreEnemy. If either team is Gamemaster, mark allied.
        /// Hot path — called hundreds of thousands of times per second. Inlined, no try/catch
        /// (TryGetValue doesn't throw; first-time classification swallows its own errors).
        ///
        /// Short-circuits when no KoH is active: in non-KoH rounds we have nothing to protect,
        /// so let the game decide enemy relations as normal. Costs one bool read per call.
        /// </summary>
        static void Postfix_GetTeamsAreEnemy(object team1, object team2, ref bool __result)
        {
            if (!_hasKoh) return;       // no KoH this round → don't interfere
            if (!__result) return;      // already friendly — nothing to flip
            if (IsGamemasterTeam(team1) || IsGamemasterTeam(team2))
                __result = false;
        }

        // Cache EVERY team's classification (true = neutral, false = real faction). This is the
        // hot path — GetTeamsAreEnemy is called hundreds of thousands of times per second by
        // AI sensors, FOW checks, etc. Without caching false results, every Sol/Cent/Alien
        // check re-runs reflection + 6 string IndexOf calls. With caching, it's an O(1)
        // dict lookup after the first encounter per team.
        //
        // Matches any "neutral" team name (Master OR Wildlife OR Worm) — see
        // project_si_kingofthehill_4way memory for why this is safe given KoH and 4way
        // don't share a session.
        static readonly System.Collections.Generic.Dictionary<object, bool> _teamClassification
            = new System.Collections.Generic.Dictionary<object, bool>();

        static bool ShortNameIsNeutral(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            // Exact short-name match for the Gamemaster team (Silica uses "GM"; some mods
            // use "Master") plus the wildlife / worm tribes that KoH should never anger.
            return s.Equals("GM",       StringComparison.OrdinalIgnoreCase)
                || s.Equals("Master",   StringComparison.OrdinalIgnoreCase)
                || s.Equals("Wildlife", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Worm",     StringComparison.OrdinalIgnoreCase);
        }

        static bool GoNameIsNeutral(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            // GameObject name substrings — keep as substring match in case the engine
            // prefixes/suffixes the type (e.g. "Wildlife_AI_root").
            return s.IndexOf("Master",     StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Gamemaster", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Wildlife",   StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Worm",       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsGamemasterTeam(object? team)
        {
            if (team == null) return false;
            if (_teamClassification.TryGetValue(team, out bool cached)) return cached;

            // First-time encounter for this team object: do the slow classification once.
            string shortName = (_teamShortNameField?.GetValue(team) as string) ?? "";
            string goName = (team as Component)?.name ?? "";
            // Match neutral / unaligned teams. Silica uses "GM" (not "Master") as the
            // Gamemaster team's TeamShortName, so we need both. Match by exact-short
            // names + GameObject-name substrings to be defensive against future variations.
            bool isNeutral = ShortNameIsNeutral(shortName) || GoNameIsNeutral(goName);
            _teamClassification[team] = isNeutral; // cache BOTH true and false
            MelonLogger.Msg($"[KGT] Team classified: short='{shortName}', go='{goName}' -> neutral={isNeutral}");
            return isNeutral;
        }

        // Diagnostic: log the first few misses AND the first few HITs.
        // Counters reset on each KoH spawn (see ResetDamageDiag below) so we see fresh
        // data every round, not just the first one of a session.
        static int _damageMissLog = 0;
        static int _damageHitLog = 0;
        const int DAMAGE_DIAG_LIMIT = 5;

        /// <summary>Reset damage-diagnostic counters. Called on KoH (re-)acquisition.</summary>
        static void ResetDamageDiag()
        {
            _damageMissLog = 0;
            _damageHitLog = 0;
            _clientHitBlockLog = 0;
        }

        /// <summary>
        /// Harmony prefix on DamageManager.ApplyDamage. If the DamageManager belongs to the
        /// KoH building, force damage to 0 and skip the original.
        /// </summary>
        static bool Prefix_ApplyDamage(object __instance, ref float __result, GameObject? __3)
        {
            try
            {
                if (!_hasKoh || _kohTower == null) return true; // run original
                var comp = __instance as Component;
                if (comp == null) return true;

                var kohGo = _kohTower.gameObject;

                // Walk the parent hierarchy looking for our KoH GameObject.
                Transform? t = comp.transform;
                while (t != null)
                {
                    if (ReferenceEquals(t.gameObject, kohGo))
                    {
                        if (_damageHitLog < DAMAGE_DIAG_LIMIT)
                        {
                            _damageHitLog++;
                            string atk = DescribeInstigator(__3);
                            MelonLogger.Msg($"[KoH/DBG] ApplyDamage HIT #{_damageHitLog} — KoH immunity engaged (damage zeroed) | attacker={atk}");
                        }
                        __result = 0f;
                        return false; // skip original — KoH is immune
                    }
                    t = t.parent;
                }

                if (_damageMissLog < DAMAGE_DIAG_LIMIT)
                {
                    _damageMissLog++;
                    string chain = BuildHierarchyChain(comp.transform);
                    MelonLogger.Msg($"[KoH/DBG] ApplyDamage MISS #{_damageMissLog}: target={chain} | koh={kohGo.name}#{kohGo.GetInstanceID()}");
                }
            }
            catch (Exception)
            {
                // Never let our patch break the damage pipeline.
            }
            return true; // run original
        }

        /// <summary>
        /// Harmony prefix on DamageManager.SetHealth(float, GameObject, bool). Direct health
        /// writes bypass ApplyDamage; this is the belt-and-suspenders catch.
        /// </summary>
        static bool Prefix_SetHealth(object __instance, float health, GameObject instigator, bool force)
        {
            try
            {
                if (!_hasKoh || _kohTower == null) return true;
                var comp = __instance as Component;
                if (comp == null) return true;
                var kohGo = _kohTower.gameObject;

                Transform? t = comp.transform;
                while (t != null)
                {
                    if (ReferenceEquals(t.gameObject, kohGo))
                    {
                        MelonLogger.Msg($"[KoH/DBG] SetHealth on KoH BLOCKED (caller wanted health={health}, force={force})");
                        return false; // skip original
                    }
                    t = t.parent;
                }
            }
            catch (Exception) { }
            return true;
        }

        /// <summary>
        /// Harmony prefix on DamageManager.SetHealth01(float). StrategyMode.PerformDestroyStructure
        /// calls this with 0f when a player sells / demolishes the structure.
        /// </summary>
        static bool Prefix_SetHealth01(object __instance, float health01)
        {
            try
            {
                if (!_hasKoh || _kohTower == null) return true;
                var comp = __instance as Component;
                if (comp == null) return true;
                var kohGo = _kohTower.gameObject;

                Transform? t = comp.transform;
                while (t != null)
                {
                    if (ReferenceEquals(t.gameObject, kohGo))
                    {
                        MelonLogger.Msg($"[KoH/DBG] SetHealth01 on KoH BLOCKED (caller wanted health01={health01})");
                        return false; // skip original — KoH is sell-proof
                    }
                    t = t.parent;
                }
            }
            catch (Exception) { }
            return true;
        }

        /// <summary>
        /// Called from the OnReceiveClientDamageHitPacket transpiler. Returns true if the
        /// given DamageManager's hierarchy contains the cached KoH GameObject — i.e.
        /// the damage should be skipped. Cheap: parent-chain walk.
        /// MUST be public so the patched IL can call it (Harmony emits `call` to this).
        /// </summary>
        public static bool IsKohDamageManager(object dm)
        {
            try
            {
                if (!_hasKoh || _kohTower == null) return false;
                var comp = dm as Component;
                if (comp == null) return false;
                var kohGo = _kohTower.gameObject;
                Transform? t = comp.transform;
                while (t != null)
                {
                    if (ReferenceEquals(t.gameObject, kohGo))
                    {
                        if (_clientHitBlockLog < DAMAGE_DIAG_LIMIT)
                        {
                            _clientHitBlockLog++;
                            MelonLogger.Msg($"[KoH/DBG] OnReceiveClientDamageHitPacket: KoH damage skipped (block #{_clientHitBlockLog})");
                        }
                        return true;
                    }
                    t = t.parent;
                }
                return false;
            }
            catch (Exception) { return false; }
        }
        static int _clientHitBlockLog = 0;

        /// <summary>
        /// Harmony transpiler for DamageManager.OnReceiveClientDamageHitPacket.
        /// Inserts an early-out before the "Health -= value; OnDamageReceived(...)" block
        /// when the target's DamageManager belongs to the KoH building.
        ///
        /// Strategy (decompiled body, key tail lines):
        ///   value = Mathf.Clamp(value, 0f, Health);
        ///   if (!(value &lt;= 0f) &amp;&amp; !DamageDisabled &amp;&amp; ... &amp;&amp; !(Health &lt;= 0f))
        ///   {
        ///       Health -= value;                                  // ← skip from here
        ///       OnDamageReceived(value, instigator, hitAng);      // ← ...to here
        ///   }                                                     // ← branch target
        ///
        /// We find the call to OnDamageReceived, label the instruction AFTER it,
        /// then find the Health-decrement preamble (`ldarg.0 ldarg.0 ldfld Health`)
        /// and inject:
        ///       ldarg.0
        ///       call IsKohDamageManager
        ///       brtrue postOnDamageReceived
        /// </summary>
        static System.Collections.Generic.IEnumerable<CodeInstruction> Transpile_OnReceiveClientDamageHitPacket(
            System.Collections.Generic.IEnumerable<CodeInstruction> instructions,
            System.Reflection.Emit.ILGenerator generator)
        {
            var codes = new System.Collections.Generic.List<CodeInstruction>(instructions);
            // Silica < 0.9.27: Health was a public field.
            // Silica ≥ 0.9.27: Health is a property; the underlying storage is HealthInternal.
            // Try the property route first; fall back to field-mode for older builds.
            FieldInfo?   healthField  = _damageManagerType?.GetField("Health",
                BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo? healthProp  = _damageManagerType?.GetProperty("Health",
                BindingFlags.Public | BindingFlags.Instance);
            MethodInfo?  healthGetter = healthProp?.GetGetMethod(nonPublic: true);
            MethodInfo?  healthSetter = healthProp?.GetSetMethod(nonPublic: true);
            MethodInfo?  onDmgRecv    = _damageManagerType?.GetMethod("OnDamageReceived",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            bool propMode = healthField == null && healthGetter != null && healthSetter != null;
            bool fieldMode = healthField != null;

            if (onDmgRecv == null || (!propMode && !fieldMode))
            {
                MelonLogger.Warning(
                    "[KGT] Transpile_OnReceive: Health not resolvable (field=" + (healthField != null) +
                    ", prop=" + (healthProp != null) + ", get=" + (healthGetter != null) +
                    ", set=" + (healthSetter != null) + ") or OnDamageReceived missing — passing through original IL");
                return codes;
            }

            // 1) Find the call to OnDamageReceived.
            int onDmgIdx = -1;
            for (int i = 0; i < codes.Count; i++)
            {
                var c = codes[i];
                if ((c.opcode == System.Reflection.Emit.OpCodes.Call || c.opcode == System.Reflection.Emit.OpCodes.Callvirt)
                    && c.operand is MethodInfo m && m == onDmgRecv)
                {
                    onDmgIdx = i;
                    break;
                }
            }
            if (onDmgIdx < 0 || onDmgIdx + 1 >= codes.Count)
            {
                MelonLogger.Warning("[KGT] Transpile_OnReceive: OnDamageReceived call site not found — passing through");
                return codes;
            }

            // 2) The instruction immediately AFTER OnDamageReceived is the branch target.
            int postIdx = onDmgIdx + 1;
            var postLabel = generator.DefineLabel();
            codes[postIdx].labels.Add(postLabel);

            // 3) Walk backwards from onDmgIdx to find the Health write (the Health -= value line).
            //    Field mode: stfld Health
            //    Property mode: call/callvirt set_Health
            int writeIdx = -1;
            for (int i = onDmgIdx - 1; i >= 0; i--)
            {
                var op = codes[i].opcode;
                if (fieldMode
                    && op == System.Reflection.Emit.OpCodes.Stfld
                    && codes[i].operand is FieldInfo fi && fi == healthField)
                {
                    writeIdx = i;
                    break;
                }
                if (propMode
                    && (op == System.Reflection.Emit.OpCodes.Call || op == System.Reflection.Emit.OpCodes.Callvirt)
                    && codes[i].operand is MethodInfo sm && sm == healthSetter)
                {
                    writeIdx = i;
                    break;
                }
            }
            if (writeIdx < 0)
            {
                MelonLogger.Warning("[KGT] Transpile_OnReceive: Health write site (stfld or set_Health) not found before OnDamageReceived — passing through");
                return codes;
            }

            // The "Health -= value" expression in IL:
            //   Field mode:                    Property mode:
            //     ldarg.0  (blockStart)          ldarg.0  (blockStart, the 'this' for set_Health)
            //     ldarg.0                        ldarg.0  (the 'this' for get_Health)
            //     ldfld Health                   call get_Health
            //     ldloc value                    ldloc value
            //     sub                            sub
            //     stfld Health  (writeIdx)       call set_Health  (writeIdx)
            int blockStart = -1;
            for (int i = writeIdx - 1; i >= System.Math.Max(0, writeIdx - 10); i--)
            {
                if (i + 2 >= codes.Count) continue;
                if (codes[i].opcode != System.Reflection.Emit.OpCodes.Ldarg_0) continue;
                if (codes[i + 1].opcode != System.Reflection.Emit.OpCodes.Ldarg_0) continue;
                var third = codes[i + 2];
                bool matchesField = fieldMode
                    && third.opcode == System.Reflection.Emit.OpCodes.Ldfld
                    && third.operand is FieldInfo f2 && f2 == healthField;
                bool matchesProp = propMode
                    && (third.opcode == System.Reflection.Emit.OpCodes.Call || third.opcode == System.Reflection.Emit.OpCodes.Callvirt)
                    && third.operand is MethodInfo gm && gm == healthGetter;
                if (matchesField || matchesProp)
                {
                    blockStart = i;
                    break;
                }
            }
            if (blockStart < 0)
            {
                MelonLogger.Warning("[KGT] Transpile_OnReceive: Health-decrement preamble (ldarg.0 ldarg.0 ld[fld|call get]_Health) not found — passing through");
                return codes;
            }

            // 4) Build our injected check:
            //      ldarg.0
            //      call IsKohDamageManager
            //      brtrue postLabel
            var isKoh = AccessTools.Method(typeof(KingOfTheHill), nameof(IsKohDamageManager));
            var injected = new System.Collections.Generic.List<CodeInstruction>
            {
                new CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0),
                new CodeInstruction(System.Reflection.Emit.OpCodes.Call, isKoh),
                new CodeInstruction(System.Reflection.Emit.OpCodes.Brtrue, postLabel),
            };

            // 5) Splice in.  If the original blockStart had any labels (from earlier branches
            //    targeting it), move them to the FIRST injected instruction so those branches
            //    still hit the right entry point.
            if (codes[blockStart].labels.Count > 0)
            {
                injected[0].labels.AddRange(codes[blockStart].labels);
                codes[blockStart].labels.Clear();
            }
            codes.InsertRange(blockStart, injected);

            MelonLogger.Msg("[KGT] Transpiled OnReceiveClientDamageHitPacket — KoH skip-branch injected before Health decrement");
            return codes;
        }

        /// <summary>
        /// Compact human-readable description of a damage instigator. Tries to identify
        /// the controlling player (if any), the owning team, and the unit/structure name
        /// so the diagnostic distinguishes AI attacks from player-controlled hits.
        /// </summary>
        static string DescribeInstigator(GameObject? instigator)
        {
            if (instigator == null) return "<null>";
            try
            {
                string goName = instigator.name ?? "?";
                // Walk to a BaseGameObject (Unit / Structure) if present.
                var bgo = instigator.GetComponentInParent<BaseGameObject>();
                string teamName = "?";
                string playerName = "";
                if (bgo != null)
                {
                    try { teamName = bgo.Team?.GetTeamShortName() ?? "?"; } catch { }
                    try
                    {
                        var owner = bgo.NetworkComponent?.OwnerPlayer;
                        if (owner != null) playerName = owner.PlayerName ?? "";
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(playerName))
                    return $"player='{playerName}' team={teamName} unit={goName}";
                return $"ai team={teamName} unit={goName}";
            }
            catch (Exception)
            {
                return instigator.name ?? "<err>";
            }
        }

        static string BuildHierarchyChain(Transform leaf)
        {
            var parts = new System.Collections.Generic.List<string>();
            Transform? t = leaf;
            for (int i = 0; t != null && i < 10; i++, t = t.parent)
                parts.Add($"{t.gameObject.name}#{t.gameObject.GetInstanceID()}");
            parts.Reverse();
            return string.Join(" > ", parts);
        }
    }
}
