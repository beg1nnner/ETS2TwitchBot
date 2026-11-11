using System;
using System.IO;

namespace EtsBot;

public enum LogLevel { Info, Ok, Warn, Error, Action }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>Простой журнал: событие для окна + запись в ets2bot.log.</summary>
public static class Log
{
    public static event Action<LogEntry>? Entry;
    private static readonly object Gate = new();

    static Log()
    {
        try
        {
            var fi = new FileInfo(Paths.LogFile);
            if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
        }
        catch { /* ignore */ }
    }

    public static void Write(LogLevel level, string source, string message)
    {
        var e = new LogEntry(DateTime.Now, level, source, message);
        try
        {
            lock (Gate)
                File.AppendAllText(Paths.LogFile,
                    $"{e.Time:yyyy-MM-dd HH:mm:ss} [{level}] {source}: {message}{Environment.NewLine}");
        }
        catch { /* ignore */ }
        try { Entry?.Invoke(e); } catch { /* ignore */ }
    }

    public static void Info(string source, string msg) => Write(LogLevel.Info, source, msg);
    public static void Ok(string source, string msg) => Write(LogLevel.Ok, source, msg);
    public static void Warn(string source, string msg) => Write(LogLevel.Warn, source, msg);
    public static void Error(string source, string msg) => Write(LogLevel.Error, source, msg);
    public static void Action(string source, string msg) => Write(LogLevel.Action, source, msg);
}
