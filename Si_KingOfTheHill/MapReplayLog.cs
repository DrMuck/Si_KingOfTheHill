/*
 MapReplayLog.cs — emit Half-Life-style game-log lines that Si_MapReplay
 (the replay viewer) can pick up from UserData/logs/L*.log.

 Format: World triggered "<event>" (key "val") (key "val") ...
 Same shape Si_MapBalance uses for its "map_layout" event.

 Events emitted by KGT:
   kgt_koh_spawn      — position + capture radius + exclusion radius
   kgt_outpost_ring   — centre + radius + outpost count (ring positions are
                        derivable; we don't spam individual outposts)
   kgt_king_change    — new king team + accumulated progress %
   kgt_progress       — milestone crossings (25/50/75/95%)
   kgt_win            — round winner

 Dependency: Si_Logging (databomb's "Half-Life Logger") provides
 Si_Logging.HL_Logging.PrintLogLine(string, bool). Resolved via reflection
 so KGT doesn't take a compile-time dependency on it.
*/

using MelonLoader;
using System;
using System.Reflection;
using System.Text;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        static MethodInfo? _printLogLineMethod;
        static bool _printLogLineResolved;

        static void ResolvePrintLogLine()
        {
            if (_printLogLineResolved) return;
            _printLogLineResolved = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var hlType = asm.GetType("Si_Logging.HL_Logging");
                    if (hlType == null) continue;
                    _printLogLineMethod = hlType.GetMethod("PrintLogLine",
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(string), typeof(bool) }, null);
                    break;
                }
                if (_printLogLineMethod != null)
                    MelonLogger.Msg("[KGT/Replay] HL_Logging.PrintLogLine resolved — MapReplay events enabled");
                else
                    MelonLogger.Msg("[KGT/Replay] Si_Logging.HL_Logging not loaded — MapReplay events skipped");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Replay] ResolvePrintLogLine failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Emit a Half-Life-style log line that MapReplay reads.
        /// Format: World triggered "&lt;event&gt;" (key1 "val1") (key2 "val2") ...
        /// No-op if Si_Logging isn't installed.
        /// </summary>
        static void LogToReplay(string eventName, params (string Key, object Value)[] kv)
        {
            ResolvePrintLogLine();
            if (_printLogLineMethod == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append("World triggered \"").Append(eventName).Append("\"");
                if (kv != null)
                {
                    foreach (var pair in kv)
                    {
                        if (string.IsNullOrEmpty(pair.Key)) continue;
                        sb.Append(" (").Append(pair.Key).Append(" \"")
                          .Append(pair.Value?.ToString() ?? "")
                          .Append("\")");
                    }
                }
                _printLogLineMethod.Invoke(null, new object[] { sb.ToString(), true });
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Replay] LogToReplay('{eventName}') failed: {ex.Message}");
            }
        }

        // Convenience: short team identifier for log events (e.g. "Sol", "Cent", "Alien").
        // Falls back to GameObject name if TeamShortName is empty.
        static string ReplayTeamTag(Team t)
        {
            if (t == null) return "none";
            try
            {
                var s = t.GetTeamShortName();
                if (!string.IsNullOrEmpty(s)) return s;
                return t.name ?? "?";
            }
            catch { return "?"; }
        }

        // Emit a Team-triggered HL log line in MapReplay-compatible format.
        // Used to make our KGT-spawned outposts appear in MapReplay's buildings
        // tracking — they bypass Structure.Construct so the normal construction
        // log lines don't fire for them.
        static void LogReplayTeamEvent(string teamName, string eventName, params (string Key, object Value)[] kv)
        {
            ResolvePrintLogLine();
            if (_printLogLineMethod == null) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append("Team \"").Append(teamName).Append("\" triggered \"").Append(eventName).Append("\"");
                if (kv != null)
                {
                    foreach (var pair in kv)
                    {
                        if (string.IsNullOrEmpty(pair.Key)) continue;
                        sb.Append(" (").Append(pair.Key).Append(" \"")
                          .Append(pair.Value?.ToString() ?? "")
                          .Append("\")");
                    }
                }
                _printLogLineMethod.Invoke(null, new object[] { sb.ToString(), true });
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Replay] LogReplayTeamEvent('{eventName}') failed: {ex.Message}");
            }
        }

        // Synthetic construction_complete in MapReplay's parser format. Position
        // is "x z y" (planar coords first, height last) to match how MapReplay
        // splits it via parse_position: parts[0]=x, parts[1]=y(=Z planar), parts[2]=height.
        static void LogReplayConstructionComplete(string teamName, string buildingName, UnityEngine.Vector3 pos)
        {
            LogReplayTeamEvent(teamName, "construction_complete",
                ("building_name", buildingName),
                ("building_position", $"{pos.x:F0} {pos.z:F0} {pos.y:F0}"));
        }

        // Synthetic structure_kill in MapReplay's parser format. Attacker fields
        // are placeholders — we just want the buildings dict to mark destroy_t so
        // the outpost stops rendering after this point.
        static void LogReplayStructureKill(string buildingName, string buildingTeam, UnityEngine.Vector3 pos)
        {
            ResolvePrintLogLine();
            if (_printLogLineMethod == null) return;
            try
            {
                string posStr = $"{pos.x:F0} {pos.z:F0} {pos.y:F0}";
                string line =
                    $"\"KGT<0><BOT:KGT><KGT>\" triggered \"structure_kill\" " +
                    $"(structure \"{buildingName}\") (weapon \"kgt_clock\") (struct_team \"{buildingTeam}\") " +
                    $"(attacker_position \"{posStr}\") (building_position \"{posStr}\")";
                _printLogLineMethod.Invoke(null, new object[] { line, true });
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT/Replay] LogReplayStructureKill failed: {ex.Message}");
            }
        }
    }
}
