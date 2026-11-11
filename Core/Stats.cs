using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EtsBot;

/// <summary>Топ «мучителей»: сколько раз каждый зритель запускал действия. Хранится в stats.json.</summary>
public sealed class StatsStore
{
    public static StatsStore Instance { get; } = new(Paths.StatsFile);

    private readonly object _lock = new();
    private readonly string? _file;
    private readonly Dictionary<string, int> _all = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _session = new(StringComparer.OrdinalIgnoreCase);

    public StatsStore(string? file)
    {
        _file = file;
        if (file == null) return;
        try
        {
            if (File.Exists(file))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(file));
                if (d != null) foreach (var kv in d) _all[kv.Key] = kv.Value;
            }
        }
        catch { /* битый файл — начинаем заново */ }
    }

    public void Record(string user)
    {
        user = (user ?? "").Trim();
        if (user.Length == 0) return;
        lock (_lock)
        {
            _all[user] = (_all.TryGetValue(user, out var a) ? a : 0) + 1;
            _session[user] = (_session.TryGetValue(user, out var b) ? b : 0) + 1;
            Save();
        }
    }

    public List<(string Name, int Count)> Top(bool session, int n)
    {
        lock (_lock)
        {
            var src = session ? _session : _all;
            return src.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, n)).Select(kv => (kv.Key, kv.Value)).ToList();
        }
    }

    public void ResetSession() { lock (_lock) _session.Clear(); }

    private void Save()
    {
        if (_file == null) return;
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_all)); }
        catch { /* не критично */ }
    }
}

/// <summary>Что сейчас показывать во всплывающем алерте оверлея (/alerts).</summary>
public static class OverlayState
{
    private static readonly object L = new();
    private static long _id;
    private static string _title = "", _user = "", _source = "";
    private static double _duration;
    private static DateTime _start = DateTime.MinValue;

    public static void Show(QueuedAction a, double duration)
    {
        lock (L)
        {
            _id++;
            _title = a.Display; _user = a.User; _source = a.SourceText;
            _duration = Math.Max(3, duration);
            _start = DateTime.UtcNow;
        }
    }

    public static void Clear()
    {
        lock (L) { _start = DateTime.MinValue; }
    }

    public static object Snapshot()
    {
        lock (L)
        {
            double elapsed = (DateTime.UtcNow - _start).TotalSeconds;
            bool active = _start != DateTime.MinValue && elapsed < _duration;
            return new
            {
                active,
                id = _id,
                title = _title,
                user = _user,
                source = _source,
                duration = _duration,
                remaining = active ? Math.Max(0, _duration - elapsed) : 0,
            };
        }
    }
}
