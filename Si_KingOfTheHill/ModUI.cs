using MelonLoader;
using System;
using System.Globalization;

namespace Si_KingOfTheHill
{
    public partial class KingOfTheHill
    {
        // Si_ModUI export: mirrors the /koh chat sub-commands, built from in-memory state only.
        public static string? ModUIData()
        {
            try
            {
                string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
                string king = "No king (zone empty or contested)";
                if (_currentKing != null)
                {
                    float acc = GetCurrentProgress(_currentKing);
                    float pct = _winThreshold > 0f ? acc / _winThreshold * 100f : 0f;
                    king = "King: " + SafeTeamName(_currentKing) + " " + pct.ToString("F0", CultureInfo.InvariantCulture) + "% (" +
                           acc.ToString("F0", CultureInfo.InvariantCulture) + "/" + _winThreshold.ToString("F0", CultureInfo.InvariantCulture) + ")";
                }
                return ModUI.ModUIExport.New()
                    .Info("KGT " + (Cfg.Enabled ? "ON" : "OFF") + " | buy=" + (BuyBridge.Active ? "linked" : "absent") +
                          " | r=" + F(_captureRadius) + "m excl=" + F(Cfg.BuildExclusionRadius) + "m")
                    .Info(king)
                    .Action("/koh", "Status", "Live capture status: king, progress, last tick gain and handicap multipliers.")
                    .OnOff("/koh enabled", "King of the Hill", "Master switch of the whole KoH game.", admin: true, on: Cfg.Enabled)
                    .Value("/koh radius", "Capture radius (m)", "Capture zone radius, applied live.", admin: true, current: F(_captureRadius))
                    .Value("/koh threshold", "Win threshold (pts)", "Points needed to win, applied live.", admin: true, current: F(_winThreshold))
                    .Value("/koh rate", "Max capture rate (pts/s)", "Cap on points gained per second.", admin: true, current: F(Cfg.MaxCapturePointsPerSecond))
                    .Action("/koh reset", "Reset state", "Clear king, accumulation and milestones for this round.", admin: true)
                    .Build();
            }
            catch (Exception exception) { MelonLogger.Warning("KGT ModUIData: " + exception.Message); return null; }
        }
    }
}
