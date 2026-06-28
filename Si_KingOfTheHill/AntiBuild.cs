/*
 AntiBuild.cs — block construction inside the KoH exclusion radius.

 Uses SilicaAdminMod's Event_Construction.OnRequestBuildStructure (which
 databomb's AdminMod populates via a transpiler on Structure.Construct).
 We subscribe and set args.Block = true when an enemy team tries to build
 inside Cfg.BuildExclusionRadius of the KoH centre.

 Current king is allowed to build inside their own captured zone.
 Neutral teams (Gamemaster / Wildlife) bypass the check.
*/

using MelonLoader;
using SilicaAdminMod;
using System;
using UnityEngine;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        static bool _antiBuildHooked;

        static void HookAntiBuild()
        {
            if (_antiBuildHooked) return;
            try
            {
                Event_Construction.OnRequestBuildStructure -= OnRequestBuild_KohExclusion;
                Event_Construction.OnRequestBuildStructure += OnRequestBuild_KohExclusion;
                _antiBuildHooked = true;
                MelonLogger.Msg("[KGT] Subscribed to Event_Construction.OnRequestBuildStructure for build-exclusion enforcement");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] HookAntiBuild failed: {ex.Message}");
            }
        }

        static void OnRequestBuild_KohExclusion(object sender, OnRequestBuildArgs args)
        {
            try
            {
                if (!ModeKohActive || !_hasKoh) return;
                if (args == null || args.Block) return;
                if (args.ParentStructure == null) return;

                var team = args.ParentStructure.Team;
                if (team == null) return;

                // Active exclusion radius:
                //   - If MapBalance's per-map KoH entry defines ExclusionRadius (> 0),
                //     use that. This is the canonical source, broadcast via
                //     kgt_koh_spawn and visualized in MapReplay.
                //   - Only when the active spawn config has no exclusion (==0) do we
                //     fall back to Cfg.BuildExclusionRadius.
                // This guarantees the default never silently shrinks the visible zone.
                float exclusion = (_exclusionRadius > 0f) ? _exclusionRadius : Cfg.BuildExclusionRadius;
                if (exclusion <= 0f) return;

                // Neutral teams (KoH building itself, Wildlife, Gamemaster) are exempt.
                // King exemption removed — the capture zone is off-limits to everyone
                // (matches user intent that aliens shouldn't get to fortify the zone
                //  just because they hold the king at the moment).
                bool isNeutral = IsGamemasterTeam(team);

                // 2D (XZ) distance to the KoH centre.
                float dx = args.Position.x - _kohCenter.x;
                float dz = args.Position.z - _kohCenter.z;
                float distSq = dx * dx + dz * dz;
                float exclSq = exclusion * exclusion;
                bool inside = (distSq <= exclSq);

                MelonLogger.Msg($"[KGT] AntiBuild fired: team={team.GetTeamShortName()} type={args.ConstructionData?.ObjectInfo?.DisplayName ?? "?"} dist={Mathf.Sqrt(distSq):F0}m (excl={exclusion:F0}m) isNeutral={isNeutral} inside={inside}");

                if (isNeutral) return;
                if (!inside) return;

                args.Block = true;
                float dist = Mathf.Sqrt(distSq);
                MelonLogger.Msg($"[KGT] Build denied: team={team.GetTeamShortName()} type={args.ConstructionData?.ObjectInfo?.DisplayName ?? "?"} dist={dist:F0}m (excl={exclusion:F0}m)");

                // Tell the player why their build was rejected. Hidden-by-default per
                // player so we don't spam them on every click — broadcast on the team
                // channel so the player who tried sees it.
                NotifyBuildBlocked(team, args.ConstructionData?.ObjectInfo?.DisplayName ?? "structure", dist, exclusion);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] OnRequestBuild_KohExclusion threw: {ex.Message}");
            }
        }

        // Throttle so a player spamming build attempts gets one notice every ~3 s.
        static float _lastBuildDenyNoticeTime = -10f;
        static void NotifyBuildBlocked(Team team, string what, float dist, float excl)
        {
            try
            {
                if (Time.unscaledTime - _lastBuildDenyNoticeTime < 3.0f) return;
                _lastBuildDenyNoticeTime = Time.unscaledTime;
                BroadcastAllChat($"[KGT] {team.GetTeamShortName()} cannot build {what} inside the Galactic Teleporter exclusion zone ({dist:F0}m / {excl:F0}m).");
            }
            catch { }
        }
    }
}
