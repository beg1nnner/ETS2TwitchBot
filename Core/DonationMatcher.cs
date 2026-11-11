using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EtsBot;

public sealed class MatchResult
{
    public string RuleName { get; init; } = "";
    public string Title { get; init; } = "";
    public List<Step> Steps { get; init; } = new();
    public int Times { get; init; } = 1;
    public double AmountInBase { get; init; }
    public string? Error { get; init; }
}

/// <summary>Подбирает правило для доната. Правила проверяются сверху вниз, срабатывает первое подходящее.</summary>
public static class DonationMatcher
{
    public static Dictionary<string, double> ParseRates(string text)
    {
        var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var p = line.Split('=');
            if (p.Length != 2) continue;
            if (double.TryParse(p[1].Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                d[p[0].Trim().ToUpperInvariant()] = v;
        }
        return d;
    }

    public static double ToBase(AppSettings s, double amount, string currency)
    {
        var cur = (currency ?? "").Trim().ToUpperInvariant();
        if (cur.Length == 0 || cur == s.Donations.BaseCurrency) return amount;
        var rates = ParseRates(s.Donations.CurrencyRatesText);
        if (rates.TryGetValue(cur, out var r)) return amount * r;
        Log.Warn("Донаты", $"Неизвестная валюта {cur}: курс не задан, считаю сумму как есть (добавь курс в разделе «Донаты»)");
        return amount;
    }

    public static MatchResult? Match(AppSettings s, DonationEvent d)
    {
        double amount = ToBase(s, d.Amount, d.Currency);
        var msg = (d.Message ?? "").ToLowerInvariant();

        foreach (var rule in s.Donations.Rules)
        {
            if (!rule.Enabled) continue;
            if (rule.MinAmount.HasValue && amount < rule.MinAmount.Value) continue;
            if (rule.MaxAmount.HasValue && amount > rule.MaxAmount.Value) continue;

            var words = rule.Keywords.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length > 0 && !words.Any(w => msg.Contains(w.ToLowerInvariant()))) continue;

            string stepsText, title;
            if (!string.IsNullOrWhiteSpace(rule.Reward))
            {
                var rw = s.FindReward(rule.Reward);
                if (rw == null)
                    return new MatchResult { RuleName = rule.Name, Error = $"в правиле «{rule.Name}» указана награда «{rule.Reward}», которой нет в списке" };
                stepsText = rw.Steps; title = rw.Title;
            }
            else { stepsText = rule.Steps; title = rule.Name; }

            var parsed = ActionScript.Parse(stepsText, s.KeyExists);
            if (!parsed.Ok)
                return new MatchResult { RuleName = rule.Name, Title = title, Error = "ошибка в шагах: " + parsed.Errors[0] };
            if (parsed.Steps.Count == 0)
                return new MatchResult { RuleName = rule.Name, Title = title, Error = "у правила нет действия" };

            int times = 1;
            if (rule.PerAmount.HasValue && rule.PerAmount.Value > 0)
                times = Math.Clamp((int)Math.Floor(amount / rule.PerAmount.Value), 1, Math.Max(1, rule.MaxTimes));

            return new MatchResult { RuleName = rule.Name, Title = title, Steps = parsed.Steps, Times = times, AmountInBase = amount };
        }
        return null;
    }
}
