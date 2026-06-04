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

        static bool IsGamemasterTeam(object? team)
        {
            if (team == null) return false;
            if (_teamClassification.TryGetValue(team, out bool cached)) return cached;

            // First-time encounter for this team object: do the slow classification once.
            string shortName = (_teamShortNameField?.GetValue(team) as string) ?? "";
            string goName = (team as Component)?.name ?? "";
            bool isNeutral = shortName.IndexOf("Master", StringComparison.OrdinalIgnoreCase) >= 0
                          || shortName.IndexOf("Wildlife", StringComparison.OrdinalIgnoreCase) >= 0
                          || shortName.IndexOf("Worm", StringComparison.OrdinalIgnoreCase) >= 0
                          || goName.IndexOf("Master", StringComparison.OrdinalIgnoreCase) >= 0
                          || goName.IndexOf("Wildlife", StringComparison.OrdinalIgnoreCase) >= 0
                          || goName.IndexOf("Worm", StringComparison.OrdinalIgnoreCase) >= 0;
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
        static bool Prefix_ApplyDamage(object __instance, ref float __result)
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
                            MelonLogger.Msg($"[KoH/DBG] ApplyDamage HIT #{_damageHitLog} — KoH immunity engaged (damage zeroed)");
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
            FieldInfo? healthField = _damageManagerType?.GetField("Health",
                BindingFlags.Public | BindingFlags.Instance);
            MethodInfo? onDmgRecv = _damageManagerType?.GetMethod("OnDamageReceived",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (healthField == null || onDmgRecv == null)
            {
                MelonLogger.Warning("[KGT] Transpile_OnReceive: Health field or OnDamageReceived not found — passing through original IL");
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

            // 3) Walk backwards from onDmgIdx to find the `stfld Health` (the Health -= value line).
            //    Then continue backwards to find the preceding `ldarg.0 ldarg.0 ldfld Health` triple
            //    which is where the Health-decrement expression begins.
            int stfldHealthIdx = -1;
            for (int i = onDmgIdx - 1; i >= 0; i--)
            {
                if (codes[i].opcode == System.Reflection.Emit.OpCodes.Stfld
                    && codes[i].operand is FieldInfo fi && fi == healthField)
                {
                    stfldHealthIdx = i;
                    break;
                }
            }
            if (stfldHealthIdx < 0)
            {
                MelonLogger.Warning("[KGT] Transpile_OnReceive: stfld Health not found before OnDamageReceived — passing through");
                return codes;
            }

            // The "Health -= value" expression in IL looks like:
            //   ldarg.0          (idx blockStart)
            //   ldarg.0
            //   ldfld Health
            //   ldloc value
            //   sub
            //   stfld Health     (idx stfldHealthIdx)
            // We want to insert our check BEFORE blockStart.
            int blockStart = -1;
            for (int i = stfldHealthIdx - 1; i >= System.Math.Max(0, stfldHealthIdx - 8); i--)
            {
                // Look for the pattern: [i] = ldarg.0, [i+1] = ldarg.0, [i+2] = ldfld Health
                if (i + 2 < codes.Count
                    && codes[i].opcode == System.Reflection.Emit.OpCodes.Ldarg_0
                    && codes[i + 1].opcode == System.Reflection.Emit.OpCodes.Ldarg_0
                    && codes[i + 2].opcode == System.Reflection.Emit.OpCodes.Ldfld
                    && codes[i + 2].operand is FieldInfo f2 && f2 == healthField)
                {
                    blockStart = i;
                    break;
                }
            }
            if (blockStart < 0)
            {
                MelonLogger.Warning("[KGT] Transpile_OnReceive: ldarg.0 ldarg.0 ldfld Health preamble not found — passing through");
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
