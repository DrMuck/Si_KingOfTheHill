/*
 BuyBridge.cs — optional link to Si_Buy (the credit economy that was carved out
 of this mod in v0.5.0). Resolved by name via reflection, no compile-time
 dependency: the KoH game runs without Si_Buy, it just pays no personal credits.

 Also keeps the pre-split entry points on Si_KingOfTheHill.KingOfTheHill alive
 (GrantExternalCredits / GetCreditsBySteamId / GetCredits / ModeBuyActive) so a
 consumer that still looks here (Si_Logistics <= 0.3.9) is forwarded to Si_Buy.
*/

using MelonLoader;
using System;
using System.Reflection;

namespace Si_KingOfTheHill
{
    internal static class BuyBridge
    {
        const string TYPE_NAME = "Si_Buy.Buy";

        static bool _probed;
        static MethodInfo? _grant, _balance;
        static PropertyInfo? _active;

        static void Resolve()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? t = null;
                    try { t = asm.GetType(TYPE_NAME); } catch { }
                    if (t == null) continue;
                    _grant   = t.GetMethod("GrantExternalCredits", BindingFlags.Public | BindingFlags.Static, null,
                                           new[] { typeof(long), typeof(int), typeof(string) }, null);
                    _balance = t.GetMethod("GetCreditsBySteamId", BindingFlags.Public | BindingFlags.Static, null,
                                           new[] { typeof(long) }, null);
                    _active  = t.GetProperty("BuyActive", BindingFlags.Public | BindingFlags.Static);
                    break;
                }
                MelonLogger.Msg(_grant != null
                    ? "[KGT] Si_Buy linked — zone presence pays credits through Si_Buy.Buy"
                    : "[KGT] Si_Buy not found — zone presence pays no personal credits (install Si_Buy for /buy)");
            }
            catch (Exception ex) { MelonLogger.Warning("[KGT] Si_Buy probe failed: " + ex.Message); }
        }

        /// <summary>Si_Buy is loaded and its economy is switched on.</summary>
        public static bool Active
        {
            get
            {
                Resolve();
                if (_active == null) return _grant != null;
                try { return _active.GetValue(null) is bool b && b; } catch { return false; }
            }
        }

        public static bool Grant(long steamId, int amount, string reason)
        {
            Resolve();
            if (_grant == null || steamId == 0 || amount <= 0) return false;
            try { return _grant.Invoke(null, new object[] { steamId, amount, reason }) is bool b && b; }
            catch (Exception ex) { MelonLogger.Warning("[KGT] Si_Buy grant failed: " + ex.Message); return false; }
        }

        public static int Balance(long steamId)
        {
            Resolve();
            if (_balance == null || steamId == 0) return 0;
            try { return _balance.Invoke(null, new object[] { steamId }) is int n ? n : 0; }
            catch { return 0; }
        }
    }

    public partial class KingOfTheHill
    {
        // ----- Pre-0.5.0 cross-mod API, forwarded to Si_Buy -----
        // Kept so older consumers that resolve "Si_KingOfTheHill.KingOfTheHill" keep
        // working. New code should resolve "Si_Buy.Buy" directly.
        public static bool ModeBuyActive => BuyBridge.Active;
        public static bool GrantExternalCredits(long steamId, int amount, string reason) => BuyBridge.Grant(steamId, amount, reason);
        public static int GetCreditsBySteamId(long steamId) => BuyBridge.Balance(steamId);
        public static int GetCredits(Player player) => player == null ? 0 : BuyBridge.Balance(GetPlayerSteamId(player));
    }
}
