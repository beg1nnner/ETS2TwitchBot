using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace EtsBot;

public sealed record VoteResult(int Winner, int Votes, string[] Options, int[] Counts);

/// <summary>Голосование чата: !1 !2 !3. Один зритель — один голос (последний выбор считается).</summary>
public static class VoteState
{
    private static readonly object L = new();
    private static string[] _opts = Array.Empty<string>();
    private static int[] _counts = Array.Empty<int>();
    private static readonly Dictionary<string, int> Voters = new(StringComparer.OrdinalIgnoreCase);
    private static bool _active;
    private static long _id;
    private static DateTime _start, _end, _showUntil = DateTime.MinValue;
    private static string _winnerTitle = "";

    public static bool Active { get { lock (L) return _active; } }

    public static bool Start(string[] options, double seconds)
    {
        lock (L)
        {
            if (_active || options.Length < 2) return false;
            _opts = options.ToArray();
            _counts = new int[options.Length];
            Voters.Clear();
            _start = DateTime.UtcNow;
            _end = _start.AddSeconds(Math.Max(5, seconds));
            _winnerTitle = "";
            _showUntil = DateTime.MinValue;
            _active = true;
            _id++;
            return true;
        }
    }

    /// <summary>option — от 1. true, если голос принят.</summary>
    public static bool Cast(string voterId, int option)
    {
        lock (L)
        {
            if (!_active || DateTime.UtcNow > _end || option < 1 || option > _opts.Length) return false;
            voterId = (voterId ?? "").Trim();
            if (voterId.Length == 0) return false;
            if (Voters.TryGetValue(voterId, out var prev)) _counts[prev - 1]--;
            Voters[voterId] = option;
            _counts[option - 1]++;
            return true;
        }
    }

    public static VoteResult? Finish()
    {
        lock (L)
        {
            if (!_active) return null;
            _active = false;
            int max = _counts.Length == 0 ? 0 : _counts.Max();
            int winner = -1;
            if (max > 0)
            {
                var best = Enumerable.Range(0, _counts.Length).Where(i => _counts[i] == max).ToList();
                winner = best[Random.Shared.Next(best.Count)]; // при ничьей — случайный из лидеров
                _winnerTitle = _opts[winner];
            }
            _showUntil = DateTime.UtcNow.AddSeconds(8);
            return new VoteResult(winner, max, _opts.ToArray(), _counts.ToArray());
        }
    }

    public static void Cancel()
    {
        lock (L) { _active = false; _showUntil = DateTime.MinValue; }
    }

    public static object Snapshot()
    {
        lock (L)
        {
            bool visible = _active || DateTime.UtcNow < _showUntil;
            double total = Math.Max(1, (_end - _start).TotalSeconds);
            double remaining = _active ? Math.Max(0, (_end - DateTime.UtcNow).TotalSeconds) : 0;
            var options = _opts.Select((t, i) => new { n = i + 1, title = t, count = i < _counts.Length ? _counts[i] : 0 }).ToList();
            return new { visible, active = _active, id = _id, duration = total, remaining, options, winner = _winnerTitle };
        }
    }
}

/// <summary>Общая копилка зрителей: баллы копятся, при достижении цели срабатывает «мега-хаос».</summary>
public sealed class PiggyStore
{
    public static PiggyStore Instance { get; } = new(Paths.PiggyFile);

    private readonly object _lock = new();
    private readonly string? _file;
    private int _amount;

    public PiggyStore(string? file)
    {
        _file = file;
        if (file == null) return;
        try { if (File.Exists(file)) _amount = Math.Max(0, JsonSerializer.Deserialize<int>(File.ReadAllText(file))); }
        catch { /* битый файл — начинаем с нуля */ }
    }

    public int Amount { get { lock (_lock) return _amount; } }

    /// <summary>Кладёт баллы. true — цель достигнута (излишек остаётся в копилке).</summary>
    public bool Add(int points, int goal)
    {
        lock (_lock)
        {
            _amount += Math.Max(0, points);
            bool reached = goal > 0 && _amount >= goal;
            if (reached) _amount -= goal;
            Save();
            return reached;
        }
    }

    public void Reset() { lock (_lock) { _amount = 0; Save(); } }

    private void Save()
    {
        if (_file == null) return;
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_amount)); } catch { /* не критично */ }
    }
}

/// <summary>Итоги стрима: сколько и чего запущено, сколько потрачено баллов и пришло донатов.</summary>
public sealed class SessionReport
{
    private readonly object _l = new();
    private readonly Dictionary<string, int> _byReward = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _bySource = new(StringComparer.OrdinalIgnoreCase);

    public DateTime StartedUtc { get; private set; } = DateTime.UtcNow;
    public int Actions { get; private set; }
    public long PointsSpent { get; private set; }
    public int DonationsCount { get; private set; }
    public double DonationsSum { get; private set; }
    public int Refunded { get; private set; }
    public int Blocked { get; private set; }

    public void Reset()
    {
        lock (_l)
        {
            StartedUtc = DateTime.UtcNow; Actions = 0; PointsSpent = 0; DonationsCount = 0;
            DonationsSum = 0; Refunded = 0; Blocked = 0; _byReward.Clear(); _bySource.Clear();
        }
    }

    public bool HasData { get { lock (_l) return Actions > 0 || DonationsCount > 0; } }

    public void RecordAction(string title, string sourceText, int points)
    {
        lock (_l)
        {
            Actions++;
            PointsSpent += Math.Max(0, points);
            title = title.Trim();
            _byReward[title] = (_byReward.TryGetValue(title, out var a) ? a : 0) + 1;
            _bySource[sourceText] = (_bySource.TryGetValue(sourceText, out var b) ? b : 0) + 1;
        }
    }

    public void RecordDonation(double amountInBase)
    {
        lock (_l) { DonationsCount++; DonationsSum += amountInBase; }
    }

    public void RecordRefund() { lock (_l) Refunded++; }
    public void RecordBlocked() { lock (_l) Blocked++; }

    public List<(string Name, int Count)> TopRewards(int n)
    {
        lock (_l)
            return _byReward.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(n).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public string BuildHtml(DateTime endUtc, string currency, List<(string Name, int Count)> topUsers)
    {
        string E(string x) => WebUtility.HtmlEncode(x ?? "");
        var local = StartedUtc.ToLocalTime();
        var dur = endUtc - StartedUtc;
        var sb = new StringBuilder();
        lock (_l)
        {
            sb.Append("<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>Отчёт за стрим</title><style>");
            sb.Append("body{font-family:'Segoe UI',sans-serif;background:#12141f;color:#f4f6fc;margin:0;padding:32px}");
            sb.Append("h1{margin:0 0 4px}h2{margin:28px 0 10px;color:#c4b3ff}.muted{color:#a9b2cc}");
            sb.Append(".cards{display:flex;flex-wrap:wrap;gap:14px;margin-top:18px}.card{background:#1d2033;border-radius:14px;padding:16px 22px;min-width:170px}");
            sb.Append(".big{font-size:34px;font-weight:800}table{border-collapse:collapse;min-width:420px}td{padding:6px 18px 6px 0;border-bottom:1px solid #2a2e45}");
            sb.Append("</style></head><body>");
            sb.Append("<h1>😈 Отчёт за стрим</h1><div class=\"muted\">")
              .Append(E(local.ToString("dd.MM.yyyy HH:mm"))).Append(" · длительность ")
              .Append((int)dur.TotalHours).Append(" ч ").Append(dur.Minutes).Append(" мин</div>");
            sb.Append("<div class=\"cards\">");
            void Card(string label, string value) => sb.Append("<div class=\"card\"><div class=\"big\">").Append(E(value))
                .Append("</div><div class=\"muted\">").Append(E(label)).Append("</div></div>");
            Card("действий запущено", Actions.ToString());
            Card("баллов потрачено", PointsSpent.ToString("N0"));
            Card("донатов", DonationsCount + " на " + DonationsSum.ToString("N0") + " " + currency);
            Card("отменено (баллы вернули)", Refunded.ToString());
            Card("заблокировано", Blocked.ToString());
            sb.Append("</div>");

            sb.Append("<h2>Самые популярные награды</h2><table>");
            var rewards = _byReward.OrderByDescending(kv => kv.Value).Take(10).ToList();
            if (rewards.Count == 0) sb.Append("<tr><td class=\"muted\">Пока ничего</td></tr>");
            foreach (var kv in rewards) sb.Append("<tr><td>").Append(E(kv.Key)).Append("</td><td>").Append(kv.Value).Append("</td></tr>");
            sb.Append("</table>");

            sb.Append("<h2>Топ мучителей</h2><table>");
            if (topUsers.Count == 0) sb.Append("<tr><td class=\"muted\">Пока никого</td></tr>");
            int pos = 1;
            foreach (var u in topUsers) sb.Append("<tr><td>").Append(pos++).Append(". ").Append(E(u.Name)).Append("</td><td>").Append(u.Count).Append("</td></tr>");
            sb.Append("</table>");

            sb.Append("<h2>Откуда действия</h2><table>");
            foreach (var kv in _bySource.OrderByDescending(x => x.Value)) sb.Append("<tr><td>").Append(E(kv.Key)).Append("</td><td>").Append(kv.Value).Append("</td></tr>");
            sb.Append("</table></body></html>");
        }
        return sb.ToString();
    }

    /// <summary>Сохраняет отчёт в папку reports рядом с программой, возвращает путь к файлу.</summary>
    public string Save(string currency)
    {
        Directory.CreateDirectory(Paths.ReportsDir);
        var path = Path.Combine(Paths.ReportsDir, "stream_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".html");
        File.WriteAllText(path, BuildHtml(DateTime.UtcNow, currency, StatsStore.Instance.Top(true, 10)), new UTF8Encoding(true));
        return path;
    }
}
