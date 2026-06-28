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
                case "mode":
                    if (int.TryParse(val, out int m) && m >= 1 && m <= 3)
                    {
                        Cfg.Mode = m;
                        SaveConfig();
                        Reply(caller, $"[KGT] Mode = {m} ({ModeLabel(m)}). Takes full effect at next round start.");
                    }
                    else
                    {
                        Reply(caller, $"[KGT] Mode = {Cfg.Mode} ({ModeLabel(Cfg.Mode)}). Usage: /koh mode <1|2|3>  (1=Full, 2=BuyOnly, 3=Disabled)");
                    }
                    break;
                case "starter":
                    if (int.TryParse(val, out int s) && s >= 0)
                    {
                        Cfg.StarterCreditsPerPlayer = s;
                        SaveConfig();
                        Reply(caller, $"[KGT] Starter credits = {s}cr per player per round.");
                    }
                    else
                    {
                        Reply(caller, $"[KGT] Starter credits = {Cfg.StarterCreditsPerPlayer}cr per player per round. Usage: /koh starter <amount>");
                    }
                    break;
                case "bounty":
                    HandleBountySubcommand(caller, val, parts);
                    break;
                default:
                    Reply(caller, "/koh [status|on|off|radius <m>|threshold <pts>|rate <pts/s>|reset|mode <1-3>|starter <amount>|bounty (on|off|list|add|remove|reload)]");
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
            Reply(caller, $"[KGT] {(Cfg.Enabled ? "ON" : "OFF")} mode={Cfg.Mode}({ModeLabel(Cfg.Mode)}) | r={Cfg.CaptureRadius}m excl={Cfg.BuildExclusionRadius}m | {kingLine}");

            // Live tick info: how much we gained last tick, what the cap is, and the raw
            // king-score (uncapped). Lets admins see "running at max" vs "below cap due to
            // few units" vs "contested → 0".
            float gain = _lastTickGain;
            float capV = _lastTickMaxAllowed;
            float kingScore = _lastTickKingScore;
            float total = _lastTickTotalScore;
            string capState = capV > 0 && gain >= capV ? "CAPPED" : (gain > 0 ? "below cap" : "no gain");
            Reply(caller, $"[KGT] Last tick: gain={gain:F1}/{capV:F1} ({capState}) | king-score={kingScore:F1} of total {total:F1} in zone");

            // Commander-reward handicap snapshot: per-team multiplier from the latest
            // RecomputeHandicap pass. Shows >1.0 boost for trailing teams, <1.0 handicap
            // for dominant ones. Skipped entirely when the feature is off.
            if (Cfg.HandicapEnabled)
            {
                if (_handicapMult.Count == 0)
                {
                    Reply(caller, "[KGT] Handicap: <i>computing…</i>");
                }
                else
                {
                    var sb = new System.Text.StringBuilder("[KGT] Handicap (×CommReward):");
                    bool first = true;
                    foreach (var kv in _handicapMult)
                    {
                        sb.Append(first ? " " : " · ");
                        first = false;
                        // Colour the multiplier: green for boost (>1.05), red for handicap (<0.95).
                        string colour = kv.Value > 1.05f ? "#39ff14"
                                      : kv.Value < 0.95f ? "#ff5050"
                                      : "#cccccc";
                        sb.Append($"{ColoredTeamName(kv.Key)} <color={colour}>×{kv.Value:F2}</color>");
                    }
                    Reply(caller, sb.ToString());
                }
            }
        }

        // ----- helpers -----

        static void Reply(Player? caller, string msg)
        {
            if (caller != null) HelperMethods.SendChatMessageToPlayer(caller, msg);
            MelonLoader.MelonLogger.Msg(msg);
        }

        // Server-wide chat broadcast — loops Player.Players and sends per-player. Mirrors
        // Si_CrabCannon.SendTeamChat pattern. Skips the server's own ghost player.
        /// <summary>
        /// Admin sub-commands under /koh bounty:
        ///   /koh bounty                        → list current bounties
        ///   /koh bounty list                   → same
        ///   /koh bounty reload                 → re-read Bounties.json from disk
        ///   /koh bounty add <steamId> <amt> [Name...]   → add or overwrite an entry
        ///   /koh bounty remove <steamId>       → remove an entry
        /// </summary>
        static void HandleBountySubcommand(Player? caller, string action, string[] parts)
        {
            action = (action ?? "").ToLowerInvariant();
            switch (action)
            {
                case "on":
                case "enable":
                    Cfg.BountiesEnabled = true; SaveConfig();
                    _announcedBounties.Clear();  // let announcements fire immediately
                    Reply(caller, "[KGT/Bounty] Bounties ENABLED.");
                    return;

                case "off":
                case "disable":
                    Cfg.BountiesEnabled = false; SaveConfig();
                    Reply(caller, "[KGT/Bounty] Bounties DISABLED. (Entries kept; re-enable with /koh bounty on.)");
                    return;

                case "":
                case "list":
                    string state = Cfg.BountiesEnabled ? "<color=#39ff14>ON</color>" : "<color=#ff5050>OFF</color>";
                    if (_bountyCfg.Bounties.Count == 0)
                    {
                        Reply(caller, $"[KGT/Bounty] {state}. No bounties active.");
                        return;
                    }
                    Reply(caller, $"[KGT/Bounty] {state} — {_bountyCfg.Bounties.Count} active (default {_bountyCfg.DefaultAmount}cr):");
                    foreach (var kv in _bountyCfg.Bounties)
                    {
                        int amt = kv.Value.Amount > 0 ? kv.Value.Amount : _bountyCfg.DefaultAmount;
                        string nm = string.IsNullOrEmpty(kv.Value.Name) ? "(no name)" : kv.Value.Name;
                        Reply(caller, $"  {kv.Key} — {nm} — {amt}cr");
                    }
                    return;

                case "reload":
                    LoadBountyConfig();
                    _announcedBounties.Clear();
                    Reply(caller, $"[KGT/Bounty] Reloaded. {_bountyCfg.Bounties.Count} active.");
                    return;

                case "add":
                    if (parts.Length < 4 || !long.TryParse(parts[3], out long addSid))
                    {
                        Reply(caller, "[KGT/Bounty] Usage: /koh bounty add <steamId> <amount> [name...]");
                        return;
                    }
                    int addAmount = _bountyCfg.DefaultAmount;
                    if (parts.Length >= 5) int.TryParse(parts[4], out addAmount);
                    string addName = parts.Length >= 6 ? string.Join(" ", parts, 5, parts.Length - 5) : "";
                    _bountyCfg.Bounties[addSid] = new BountyEntry { Name = addName, Amount = addAmount };
                    SaveBountyConfig();
                    _announcedBounties.Remove(addSid); // let the announcement fire if they're already connected
                    Reply(caller, $"[KGT/Bounty] Added: {addSid} — {(string.IsNullOrEmpty(addName) ? "(no name)" : addName)} — {addAmount}cr");
                    return;

                case "remove":
                case "rm":
                case "del":
                    if (parts.Length < 4 || !long.TryParse(parts[3], out long rmSid))
                    {
                        Reply(caller, "[KGT/Bounty] Usage: /koh bounty remove <steamId>");
                        return;
                    }
                    if (!_bountyCfg.Bounties.Remove(rmSid))
                    {
                        Reply(caller, $"[KGT/Bounty] No entry for {rmSid}.");
                        return;
                    }
                    SaveBountyConfig();
                    _announcedBounties.Remove(rmSid);
                    Reply(caller, $"[KGT/Bounty] Removed: {rmSid}");
                    return;

                default:
                    Reply(caller, "[KGT/Bounty] Usage: /koh bounty [on|off|list|reload|add <steamId> <amount> [name]|remove <steamId>]");
                    return;
            }
        }

        static string ModeLabel(int m)
        {
            switch (m)
            {
                case 1: return "Full";
                case 2: return "BuyOnly";
                case 3: return "Disabled";
                default: return "?";
            }
        }

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
