using System;
using System.IO;

namespace EtsBot;

/// <summary>Все файлы программы лежат рядом с exe (портативная версия).</summary>
public static class Paths
{
    public static readonly string AppDir = ResolveAppDir();
    public static string SettingsFile => Path.Combine(AppDir, "settings.json");
    public static string TwitchTokens => Path.Combine(AppDir, "twitch_tokens.json");
    public static string DonationTokens => Path.Combine(AppDir, "da_tokens.json");
    public static string StatsFile => Path.Combine(AppDir, "stats.json");
    public static string PiggyFile => Path.Combine(AppDir, "piggy.json");
    public static string ReportsDir => Path.Combine(AppDir, "reports");
    public static string LogFile => Path.Combine(AppDir, "ets2bot.log");
    public static string SelfTestLog => Path.Combine(AppDir, "selftest_log.txt");

    private static string ResolveAppDir()
    {
        try
        {
            var p = Environment.ProcessPath;
            var dir = p == null ? null : Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        catch { /* ignore */ }
        return AppContext.BaseDirectory;
    }
}
