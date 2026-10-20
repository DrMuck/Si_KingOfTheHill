using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;

namespace Si_KingOfTheHill
{
    /// <summary>
    /// Every file the mod touches, named once. Configs live in UserData/cfg/KingOfTheHill/
    /// (the server's own cfg/ folder, next to gametype/ and maps/). An install
    /// that still has the pre-1.1 UserData/KingOfTheHill_cfg/ folder is moved there once.
    /// </summary>
    internal static class Paths
    {
        public static string UserData => MelonEnvironment.UserDataDirectory;

        /// <summary>UserData/cfg/Buy — created (or migrated) on first access.</summary>
        public static string ConfigDir => _configDir ??= Resolve("KingOfTheHill", "KingOfTheHill_cfg");
        static string? _configDir;

        /// <summary>A file in Si_UnitBalance's config folder (new location first,
        /// then the pre-1.1 UserData/UnitBalance_cfg/); the new path when neither exists.</summary>
        public static string UnitBalanceFile(string fileName)
        {
            string now = Path.Combine(UserData, "cfg", "UnitBalance", fileName);
            string old = Path.Combine(UserData, "UnitBalance_cfg", fileName);
            return File.Exists(now) || !File.Exists(old) ? now : old;
        }

        /// <summary>The first path that exists, or null.</summary>
        public static string? FirstExisting(params string[] candidates)
        {
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        static string Resolve(string name, string legacyFolder)
        {
            string dir = Path.Combine(UserData, "cfg", name);
            string legacy = Path.Combine(UserData, legacyFolder);
            try
            {
                if (!Directory.Exists(dir) && Directory.Exists(legacy))
                {
                    Directory.CreateDirectory(Path.Combine(UserData, "cfg"));
                    Directory.Move(legacy, dir);
                    MelonLogger.Msg($"[KGT] Moved config folder {legacy} -> {dir}");
                }
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[KGT] Could not prepare {dir}: {ex.Message}");
            }
            return dir;
        }
    }
}
