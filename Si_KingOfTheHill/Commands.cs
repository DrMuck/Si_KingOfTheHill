using SilicaAdminMod;
using System;
using System.Globalization;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        // === /koh — status visible to all; mutating sub-commands require admin Generic ===
        static void OnKohCommand(Player? caller, string args)
        {
            string sub = "";
            string val = "";
            var parts = (args ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1) sub = parts[1].ToLowerInvariant();
            if (parts.Length > 2) val = parts[2];

            // No sub → status (any player can read).
            if (string.IsNullOrEmpty(sub) || sub == "status")
            {
                ShowStatus(caller);
                return;
            }

            // Everything below is admin-only.
            bool isAdmin = caller == null || caller.CanAdminExecute(Power.Generic);
            if (!isAdmin)
            {
                Reply(caller, "[KGT] Admin only.");
                return;
            }

            switch (sub)
            {
                case "on":
                    Cfg.Enabled = true; SaveConfig();
                    Reply(caller, "[KGT] ON");
                    break;
                case "off":
                    Cfg.Enabled = false; SaveConfig();
                    Reply(caller, "[KGT] OFF");
                    break;
                case "radius":
                    if (TryParseFloat(val, out float r) && r > 0f)
                    { Cfg.CaptureRadius = r; SaveConfig(); Reply(caller, $"[KGT] capture radius = {r}m"); }
                    else Reply(caller, $"[KGT] capture radius = {Cfg.CaptureRadius}m");
                    break;
                case "threshold":
                    if (TryParseFloat(val, out float t) && t > 0f)
                    { Cfg.WinThreshold = t; SaveConfig(); Reply(caller, $"[KGT] win threshold = {t} pts"); }
                    else Reply(caller, $"[KGT] win threshold = {Cfg.WinThreshold} pts");
                    break;
                case "rate":
                    if (TryParseFloat(val, out float rate) && rate > 0f)
                    { Cfg.MaxCapturePointsPerSecond = rate; SaveConfig(); Reply(caller, $"[KGT] max capture rate = {rate} pts/s"); }
                    else Reply(caller, $"[KGT] max capture rate = {Cfg.MaxCapturePointsPerSecond} pts/s");
                    break;
                case "reset":
                    ResetKingState();
                    Reply(caller, "[KGT] State cleared (king + accumulation + milestones).");
                    break;
                default:
                    Reply(caller, "/koh [status|on|off|radius <m>|threshold <pts>|rate <pts/s>|reset]");
                    break;
            }
        }

        static void ShowStatus(Player? caller)
        {
            string kingLine;
            if (_currentKing == null)
            {
                kingLine = "No king (zone empty or contested)";
            }
            else
            {
                float acc = GetCurrentProgress(_currentKing);
                float pct = _winThreshold > 0f ? (acc / _winThreshold * 100f) : 0f;
                kingLine = $"King: {ColoredTeamName(_currentKing)}  {ColoredPercent(pct)}  ({acc:F0}/{_winThreshold:F0})";
            }
            Reply(caller, $"[KGT] {(Cfg.Enabled ? "ON" : "OFF")} | r={Cfg.CaptureRadius}m excl={Cfg.BuildExclusionRadius}m | {kingLine}");

            // Live tick info: how much we gained last tick, what the cap is, and the raw
            // king-score (uncapped). Lets admins see "running at max" vs "below cap due to
            // few units" vs "contested → 0".
            float gain = _lastTickGain;
            float capV = _lastTickMaxAllowed;
            float kingScore = _lastTickKingScore;
            float total = _lastTickTotalScore;
            string capState = capV > 0 && gain >= capV ? "CAPPED" : (gain > 0 ? "below cap" : "no gain");
            Reply(caller, $"[KGT] Last tick: gain={gain:F1}/{capV:F1} ({capState}) | king-score={kingScore:F1} of total {total:F1} in zone");
        }

        // ----- helpers -----

        static void Reply(Player? caller, string msg)
        {
            if (caller != null) HelperMethods.SendChatMessageToPlayer(caller, msg);
            MelonLoader.MelonLogger.Msg(msg);
        }

        // Server-wide chat broadcast — loops Player.Players and sends per-player. Mirrors
        // Si_CrabCannon.SendTeamChat pattern. Skips the server's own ghost player.
        static void BroadcastAllChat(string msg)
        {
            try
            {
                var serverPlayer = NetworkGameServer.GetServerPlayer();
                for (int i = 0; i < Player.Players.Count; i++)
                {
                    var p = Player.Players[i];
                    if (p != null && p != serverPlayer)
                        HelperMethods.SendChatMessageToPlayer(p, msg);
                }
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"[KGT] BroadcastAllChat failed: {ex.Message}");
            }
            MelonLoader.MelonLogger.Msg(msg);
        }

        static bool TryParseFloat(string s, out float val)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        }
    }
}
