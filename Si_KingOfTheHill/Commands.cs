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
                    {
                        Cfg.CaptureRadius = r;
                        // Runtime uses _captureRadius which is normally latched from the
                        // KoH tower at round start. Update it live too so /koh radius
                        // takes effect this tick.
                        _captureRadius = r;
                        SaveConfig();
                        Reply(caller, $"[KGT] capture radius = {r}m (applied live)");
                    }
                    else Reply(caller, $"[KGT] capture radius = {_captureRadius}m (Cfg default = {Cfg.CaptureRadius})");
                    break;
                case "threshold":
                    if (TryParseFloat(val, out float t) && t > 0f)
                    {
                        Cfg.WinThreshold = t;
                        // Also overwrite the runtime _winThreshold (which was latched at
                        // round start from Cfg or WinThresholdPerMap) — otherwise the
                        // change wouldn't take effect until the next round.
                        _winThreshold = t;
                        SaveConfig();
                        Reply(caller, $"[KGT] win threshold = {t} pts (applied live)");
                    }
                    else Reply(caller, $"[KGT] win threshold = {_winThreshold} pts (Cfg default = {Cfg.WinThreshold})");
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
                case "buildingreward":
                case "structurereward":
                {
                    bool? want = ParseOnOff(val);
                    if (!want.HasValue)
                    {
                        Reply(caller, $"[KGT] Building-kill reward = {(Cfg.StructureKillRewardsEnabled ? "ON" : "OFF")}. Usage: /koh buildingreward <on|off>");
                        break;
                    }
                    Cfg.StructureKillRewardsEnabled = want.Value;
                    SaveConfig();
                    Reply(caller, $"[KGT] Building-kill reward = {(want.Value ? "ON" : "OFF")}. " +
                        (want.Value ? "Destroying enemy structures now pays the killer credits (cost × RewardKillFraction)." : "Structure destruction no longer pays the killer. Unit kills still pay."));
                    break;
                }
                case "buystructures":
                case "buybuildings":
                {
                    bool? want = ParseOnOff(val);
                    if (!want.HasValue)
                    {
                        Reply(caller, $"[KGT] /buy structures = {(Cfg.BuyStructuresEnabled ? "ON" : "OFF")}. Usage: /koh buystructures <on|off>");
                        break;
                    }
                    Cfg.BuyStructuresEnabled = want.Value;
                    SaveConfig();
                    Reply(caller, $"[KGT] /buy structures = {(want.Value ? "ON" : "OFF")}. " +
                        (want.Value ? "A 'Buildings' category now appears at the end of the /buy menu." : "Structures are hidden from the /buy menu again."));
                    break;
                }
                default:
                    Reply(caller, "/koh [status|on|off|radius <m>|threshold <pts>|rate <pts/s>|reset|mode <1-3>|starter <amount>|bounty (on|off|list|add|remove|reload)|buildingreward <on|off>|buystructures <on|off>]");
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

        /// <summary>
        /// Parses "on"/"off"/"1"/"0"/"true"/"false"/"yes"/"no" to bool. Returns null if not recognised.
        /// Used by admin toggle commands so an empty arg shows the current state instead of flipping.
        /// </summary>
        static bool? ParseOnOff(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg)) return null;
            string a = arg.Trim().ToLowerInvariant();
            if (a == "on" || a == "1" || a == "true"  || a == "yes" || a == "y") return true;
            if (a == "off" || a == "0" || a == "false" || a == "no"  || a == "n") return false;
            return null;
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
                {
                    if (parts.Length < 4 || !long.TryParse(parts[3], out long addSid))
                    {
                        Reply(caller, "[KGT/Bounty] Usage: /koh bounty add <steamId> <amount> [name...]");
                        return;
                    }
                    // Re-read file first so any manual JSON edits (e.g. names typed in by hand)
                    // aren't clobbered when we serialize our in-memory dict back to disk.
                    LoadBountyConfig();

                    // Parse the optional amount WITHOUT letting int.TryParse zero out the
                    // default on a non-numeric arg (e.g. "/koh bounty add <sid> Dram" — Dram
                    // is a name, not an amount, and falls through to the name field).
                    int addAmount = _bountyCfg.DefaultAmount;
                    int nameStartIdx = 4;
                    if (parts.Length >= 5 && int.TryParse(parts[4], out int parsedAmount))
                    {
                        addAmount = parsedAmount;
                        nameStartIdx = 5;
                    }
                    string addName = parts.Length > nameStartIdx
                        ? string.Join(" ", parts, nameStartIdx, parts.Length - nameStartIdx)
                        : "";

                    // Preserve existing name/note if the caller didn't pass new ones.
                    string finalName = addName;
                    string finalNote = "";
                    if (_bountyCfg.Bounties.TryGetValue(addSid, out var existing))
                    {
                        if (string.IsNullOrEmpty(finalName)) finalName = existing.Name ?? "";
                        finalNote = existing.Note ?? "";
                    }
                    _bountyCfg.Bounties[addSid] = new BountyEntry { Name = finalName, Amount = addAmount, Note = finalNote };
                    SaveBountyConfig();
                    _announcedBounties.Remove(addSid); // let the announcement fire if they're already connected
                    Reply(caller, $"[KGT/Bounty] Added: {addSid} — {(string.IsNullOrEmpty(finalName) ? "(no name)" : finalName)} — {addAmount}cr");
                    return;
                }

                case "remove":
                case "rm":
                case "del":
                {
                    if (parts.Length < 4 || !long.TryParse(parts[3], out long rmSid))
                    {
                        Reply(caller, "[KGT/Bounty] Usage: /koh bounty remove <steamId>");
                        return;
                    }
                    // Re-read file first so we operate on the current on-disk state and
                    // don't overwrite manual edits made between commands.
                    LoadBountyConfig();
                    if (!_bountyCfg.Bounties.Remove(rmSid))
                    {
                        Reply(caller, $"[KGT/Bounty] No entry for {rmSid}.");
                        return;
                    }
                    SaveBountyConfig();
                    _announcedBounties.Remove(rmSid);
                    Reply(caller, $"[KGT/Bounty] Removed: {rmSid}");
                    return;
                }

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
