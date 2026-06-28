/*
 Bounty.cs — Headhunter bounty system.

 Admins maintain a list of "wanted" players in UserData/KingOfTheHill_cfg/Bounties.json.
 When a wanted player connects, the bounty is announced in chat. When somebody
 (on any other team) kills a wanted player's unit, the killer receives the bounty
 amount as a bonus on top of the normal kill reward.

 Bounty grants are routed through Rewards.AddCredits like every other credit
 source — they spend via /buy the same way kill rewards do.
*/

using MelonLoader;
using Newtonsoft.Json;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        // ----- Data types -----

        public class BountyEntry
        {
            public string Name = "";    // display name used in chat
            public int Amount = 0;      // credits awarded for the kill (0 → use DefaultAmount)
            public string Note = "";    // optional context — shown to admins, not players
        }

        public class BountyConfig
        {
            public int DefaultAmount = 1000;
            public bool AnnounceOnConnect = true;
            public bool BroadcastOnKill = true;
            public Dictionary<long, BountyEntry> Bounties = new Dictionary<long, BountyEntry>();
        }

        // ----- State -----

        static BountyConfig _bountyCfg = new BountyConfig();
        static string _bountyConfigPath = "";
        // Steam IDs we've already announced this session (cleared on game end).
        // Prevents re-spam if a bounty player disconnects + reconnects mid-round.
        static readonly HashSet<long> _announcedBounties = new HashSet<long>();

        // ----- Lifecycle -----

        static void LoadBountyConfig()
        {
            _bountyConfigPath = Path.Combine("UserData", "KingOfTheHill_cfg", "Bounties.json");
            try
            {
                if (!File.Exists(_bountyConfigPath))
                {
                    // Write an empty default — admins populate it via /koh bounty add or
                    // by editing the file by hand. We don't ship any real Steam IDs in
                    // the binary so this DLL is safe to publish/share publicly.
                    _bountyCfg = new BountyConfig();
                    SaveBountyConfig();
                    MelonLogger.Msg($"[KGT/Bounty] No bounty config — wrote empty default to {_bountyConfigPath}");
                    return;
                }
                var json = File.ReadAllText(_bountyConfigPath);
                _bountyCfg = JsonConvert.DeserializeObject<BountyConfig>(json) ?? new BountyConfig();
                if (_bountyCfg.Bounties == null) _bountyCfg.Bounties = new Dictionary<long, BountyEntry>();
                MelonLogger.Msg($"[KGT/Bounty] Loaded {_bountyCfg.Bounties.Count} bounty target(s) from {_bountyConfigPath} (default {_bountyCfg.DefaultAmount}cr)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Bounty] Failed to load {_bountyConfigPath}: {ex.Message}");
                _bountyCfg = new BountyConfig();
            }
        }

        static void SaveBountyConfig()
        {
            try
            {
                var dir = Path.GetDirectoryName(_bountyConfigPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json = JsonConvert.SerializeObject(_bountyCfg, Formatting.Indented);
                File.WriteAllText(_bountyConfigPath, json);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Bounty] Failed to save {_bountyConfigPath}: {ex.Message}");
            }
        }

        // ----- Lookups -----

        /// <summary>Returns the bounty amount on steamId, or 0 if not wanted.</summary>
        static int GetBountyAmount(long steamId)
        {
            if (!_bountyCfg.Bounties.TryGetValue(steamId, out var entry)) return 0;
            return entry.Amount > 0 ? entry.Amount : _bountyCfg.DefaultAmount;
        }

        /// <summary>Returns the display name for a bounty target, or the Steam ID as a string.</summary>
        static string GetBountyDisplayName(long steamId)
        {
            if (_bountyCfg.Bounties.TryGetValue(steamId, out var entry) && !string.IsNullOrEmpty(entry.Name))
                return entry.Name;
            return steamId.ToString();
        }

        // ----- Per-frame announcement (called from OnUpdate / starter-credits poll) -----

        /// <summary>
        /// Walk Player.Players; for every Steam ID that's both in the bounty list AND
        /// not yet announced this session, broadcast the bounty in chat.
        /// </summary>
        static void AnnounceBountiesIfNeeded()
        {
            if (!Cfg.BountiesEnabled) return;
            if (!_bountyCfg.AnnounceOnConnect) return;
            if (_bountyCfg.Bounties.Count == 0) return;

            try
            {
                var players = Player.Players;
                if (players == null) return;
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p == null) continue;
                    if (p == NetworkGameServer.GetServerPlayer()) continue;
                    long sid = GetPlayerSteamId(p);
                    if (sid == 0) continue;
                    if (!_bountyCfg.Bounties.ContainsKey(sid)) continue;
                    if (!_announcedBounties.Add(sid)) continue; // already announced
                    int amount = GetBountyAmount(sid);
                    string display = GetBountyDisplayName(sid);
                    BroadcastAllChat(
                        $"[KGT/Bounty] <color=#ff5050>HEADHUNTER:</color> <color=#39ff14>{amount}</color>cr bonus to anyone who kills <color=#ffcc00>{display}</color>!");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Bounty] AnnounceBountiesIfNeeded threw: {ex.Message}");
            }
        }

        // ----- Bounty payout (called from Rewards.OnUnitDestroyed_Reward) -----

        /// <summary>
        /// If the victim unit was controlled by a player on the bounty list, pay the
        /// killer's bonus and broadcast a message. Returns the bounty paid (0 if none).
        /// </summary>
        static int PayBountyIfApplicable(Unit victim, Player killer)
        {
            if (!Cfg.BountiesEnabled) return 0;
            if (_bountyCfg.Bounties.Count == 0) return 0;
            if (victim == null || killer == null) return 0;

            Player? victimPlayer = null;
            try { victimPlayer = victim.ControlledBy; } catch { }
            if (victimPlayer == null) return 0;
            if (victimPlayer == killer) return 0; // self-kill — no bounty

            long victimSid = GetPlayerSteamId(victimPlayer);
            if (victimSid == 0) return 0;
            int amount = GetBountyAmount(victimSid);
            if (amount <= 0) return 0;

            long killerKey = GetPlayerSteamId(killer);
            if (killerKey == 0) return 0;
            AddCredits(killerKey, amount);

            string victimName = GetBountyDisplayName(victimSid);
            string killerName = "";
            try { killerName = killer.PlayerName ?? ""; } catch { }

            try
            {
                HelperMethods.SendChatMessageToPlayer(killer,
                    $"[KGT/Bounty] <color=#39ff14>+{amount}</color>cr BOUNTY for killing <color=#ffcc00>{victimName}</color>!");
            }
            catch { }

            if (_bountyCfg.BroadcastOnKill)
            {
                BroadcastAllChat(
                    $"[KGT/Bounty] <color=#ffcc00>{victimName}</color> has been hunted down by <color=#39ff14>{killerName}</color> — <color=#39ff14>{amount}</color>cr bounty paid.");
            }

            return amount;
        }
    }
}
