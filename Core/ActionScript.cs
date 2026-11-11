using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EtsBot;

public abstract record Step;
public sealed record HoldStep(string[] Keys, double Seconds) : Step;
public sealed record PressStep(string[] Keys, int Times, double Interval) : Step;
public sealed record WaitStep(double Seconds) : Step;
/// <summary>Прокрутка колёсика мыши: Clicks &gt; 0 — вверх (от себя), &lt; 0 — вниз.</summary>
public sealed record ScrollStep(int Clicks, double Interval) : Step;
public sealed record RepeatStep(int Count, List<Step> Body) : Step;

public sealed class ParseResult
{
    public List<Step> Steps { get; } = new();
    public List<string> Errors { get; } = new();
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Мини-язык действий (вместо YAML):
///   hold steer_left 1.5       - зажать клавишу на 1.5 сек
///   hold ctrl+shift 2         - несколько клавиш сразу
///   press horn 3 0.25         - нажать 3 раза с паузой 0.25 сек
///   press ctrl+t              - сочетание клавиш
///   wait 0.5                  - пауза
///   scroll up 10              - колёсико мыши вверх на 10 щелчков (scroll down - вниз)
///   repeat 4 ... end          - повторить блок 4 раза
/// Всё после # или // - комментарий.
/// </summary>
public static class ActionScript
{
    public const double MaxHold = 60;
    public const int MaxTimes = 100;

    public static ParseResult Parse(string? text, Func<string, bool>? keyExists = null)
    {
        var res = new ParseResult();
        var stack = new Stack<(RepeatStep Node, int Line)>();
        List<Step> cur = res.Steps;
        int ln = 0;

        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            ln++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();

            switch (cmd)
            {
                case "hold":
                {
                    if (parts.Length < 2) { Err(res, ln, "hold: укажи клавишу, например: hold steer_left 1.5"); break; }
                    var keys = SplitKeys(parts[1]);
                    double sec = 1;
                    if (parts.Length >= 3 && !TryNum(parts[2], out sec)) { Err(res, ln, $"hold: '{parts[2]}' - не число секунд"); break; }
                    if (sec < 0 || sec > MaxHold) { Err(res, ln, $"hold: секунд должно быть от 0 до {MaxHold}"); break; }
                    if (!CheckKeys(res, ln, keys, keyExists)) break;
                    cur.Add(new HoldStep(keys, sec));
                    break;
                }
                case "press":
                {
                    if (parts.Length < 2) { Err(res, ln, "press: укажи клавишу, например: press horn 3 0.25"); break; }
                    var keys = SplitKeys(parts[1]);
                    double times = 1, interval = 0.15;
                    if (parts.Length >= 3 && !TryNum(parts[2], out times)) { Err(res, ln, $"press: '{parts[2]}' - не число повторов"); break; }
                    if (parts.Length >= 4 && !TryNum(parts[3], out interval)) { Err(res, ln, $"press: '{parts[3]}' - не число секунд"); break; }
                    if (times < 1 || times > MaxTimes) { Err(res, ln, $"press: повторов должно быть от 1 до {MaxTimes}"); break; }
                    if (interval < 0 || interval > MaxHold) { Err(res, ln, "press: пауза должна быть от 0 до 60 сек"); break; }
                    if (!CheckKeys(res, ln, keys, keyExists)) break;
                    cur.Add(new PressStep(keys, (int)times, interval));
                    break;
                }
                case "wait":
                {
                    double sec = 1;
                    if (parts.Length < 2 || !TryNum(parts[1], out sec)) { Err(res, ln, "wait: укажи секунды, например: wait 0.5"); break; }
                    if (sec < 0 || sec > MaxHold) { Err(res, ln, "wait: от 0 до 60 секунд"); break; }
                    cur.Add(new WaitStep(sec));
                    break;
                }
                case "scroll":
                {
                    if (parts.Length < 2 || (parts[1].ToLowerInvariant() != "up" && parts[1].ToLowerInvariant() != "down"))
                    { Err(res, ln, "scroll: укажи направление, например: scroll up 10 или scroll down 10"); break; }
                    double clicks = 1, interval = 0.03;
                    if (parts.Length >= 3 && !TryNum(parts[2], out clicks)) { Err(res, ln, $"scroll: '{parts[2]}' - не число щелчков"); break; }
                    if (parts.Length >= 4 && !TryNum(parts[3], out interval)) { Err(res, ln, $"scroll: '{parts[3]}' - не число секунд"); break; }
                    if (clicks < 1 || clicks > 100) { Err(res, ln, "scroll: щелчков должно быть от 1 до 100"); break; }
                    if (interval < 0 || interval > 5) { Err(res, ln, "scroll: пауза между щелчками от 0 до 5 сек"); break; }
                    cur.Add(new ScrollStep(parts[1].ToLowerInvariant() == "up" ? (int)clicks : -(int)clicks, interval));
                    break;
                }
                case "repeat":
                {
                    double n = 1;
                    if (parts.Length < 2 || !TryNum(parts[1], out n) || n < 1 || n > MaxTimes)
                    {
                        Err(res, ln, $"repeat: укажи число повторов от 1 до {MaxTimes}, например: repeat 4");
                        n = 1;
                    }
                    if (stack.Count >= 3) { Err(res, ln, "repeat: слишком глубокая вложенность (максимум 3)"); }
                    var node = new RepeatStep((int)n, new List<Step>());
                    cur.Add(node);
                    stack.Push((node, ln));
                    cur = node.Body;
                    break;
                }
                case "end":
                {
                    if (stack.Count == 0) { Err(res, ln, "end без repeat"); break; }
                    stack.Pop();
                    cur = stack.Count == 0 ? res.Steps : stack.Peek().Node.Body;
                    break;
                }
                default:
                    Err(res, ln, $"неизвестная команда '{parts[0]}'. Доступно: hold, press, wait, scroll, repeat, end");
                    break;
            }
        }

        foreach (var open in stack)
            Err(res, open.Line, "repeat не закрыт: добавь строку end");

        return res;
    }

    public static double Estimate(IEnumerable<Step> steps, double tap = 0.08)
    {
        double t = 0;
        foreach (var s in steps)
        {
            switch (s)
            {
                case HoldStep h: t += h.Seconds; break;
                case PressStep p: t += p.Times * tap + (p.Times - 1) * p.Interval; break;
                case WaitStep w: t += w.Seconds; break;
                case ScrollStep sc: t += Math.Abs(sc.Clicks) * sc.Interval; break;
                case RepeatStep r: t += r.Count * Estimate(r.Body, tap); break;
            }
        }
        return t;
    }

    private static string StripComment(string s)
    {
        int i = s.IndexOf('#');
        int j = s.IndexOf("//", StringComparison.Ordinal);
        int cut = (i >= 0 && j >= 0) ? Math.Min(i, j) : Math.Max(i, j);
        return cut >= 0 ? s.Substring(0, cut) : s;
    }

    private static string[] SplitKeys(string s)
        => s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryNum(string s, out double v)
        => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool CheckKeys(ParseResult res, int ln, string[] keys, Func<string, bool>? exists)
    {
        if (keys.Length == 0) { Err(res, ln, "не указана клавиша"); return false; }
        if (exists == null) return true;
        foreach (var k in keys)
            if (!exists(k)) { Err(res, ln, $"неизвестная клавиша '{k}' (имя из раздела клавиш или обычная клавиша: a, space, f5...)"); return false; }
        return true;
    }

    private static void Err(ParseResult res, int ln, string msg) => res.Errors.Add($"Строка {ln}: {msg}");
}
