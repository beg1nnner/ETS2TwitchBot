using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EtsBot;

/// <summary>Проверка ядра без окна: запуск ETS2TwitchBot.exe --selftest, результат в selftest_log.txt.</summary>
public static class SelfTest
{
    public static int Run()
    {
        var sb = new StringBuilder();
        int fails = 0;
        void Check(string name, bool ok, string extra = "")
        {
            sb.AppendLine((ok ? "OK    " : "FAIL  ") + name + (extra.Length > 0 ? "  -> " + extra : ""));
            if (!ok) fails++;
        }

        try
        {
            var s = Defaults.Create();

            // 1. шаги всех стандартных наград разбираются без ошибок
            foreach (var r in s.Rewards)
            {
                var p = ActionScript.Parse(r.Steps, s.KeyExists);
                Check("награда «" + r.Title + "»", p.Ok && p.Steps.Count > 0, p.Ok ? "" : string.Join("; ", p.Errors));
            }
            foreach (var rule in s.Donations.Rules.Where(x => x.Steps.Length > 0))
            {
                var p = ActionScript.Parse(rule.Steps, s.KeyExists);
                Check("правило «" + rule.Name + "»", p.Ok, p.Ok ? "" : string.Join("; ", p.Errors));
            }

            // 2. язык шагов
            var t = ActionScript.Parse("repeat 2\n hold a 0.5\n wait 1\nend\npress horn 3 0.25 # коммент", s.KeyExists);
            Check("парсер: вложенный repeat", t.Ok && t.Steps.Count == 2);
            Check("парсер: длительность", Math.Abs(ActionScript.Estimate(t.Steps, 0.1) - (2 * 1.5 + 0.3 + 0.5)) < 0.001,
                ActionScript.Estimate(t.Steps, 0.1).ToString());
            Check("парсер: ошибка неизвестной клавиши", !ActionScript.Parse("hold qqq 1", s.KeyExists).Ok);
            Check("парсер: ошибка незакрытого repeat", !ActionScript.Parse("repeat 2\nhold a 1", s.KeyExists).Ok);
            Check("парсер: ошибка лишнего end", !ActionScript.Parse("end", s.KeyExists).Ok);
            Check("парсер: запятая в числе", ActionScript.Parse("hold a 1,5", s.KeyExists).Ok);
            var sc = ActionScript.Parse("scroll up 15 0.02\nscroll down 3", s.KeyExists);
            Check("парсер: scroll up/down", sc.Ok && sc.Steps.Count == 2 && sc.Steps[0] is ScrollStep { Clicks: 15 } && sc.Steps[1] is ScrollStep { Clicks: -3 });
            Check("парсер: ошибка scroll без направления", !ActionScript.Parse("scroll 5", s.KeyExists).Ok);

            // 2б. новые награды и перенос старых настроек
            Check("газ в пол: 10 секунд", s.FindReward("Газ в пол")?.Steps.Trim() == "hold accelerate 10");
            var jerk = ActionScript.Parse(s.FindReward("Дёрганый режим")?.Steps, s.KeyExists);
            Check("дёрганый режим: 5 секунд", jerk.Ok && Math.Abs(ActionScript.Estimate(jerk.Steps, 0.08) - 5.0) < 0.001, ActionScript.Estimate(jerk.Steps, 0.08).ToString());
            var cam = ActionScript.Parse(s.FindReward("Камера туда-обратно")?.Steps, s.KeyExists);
            Check("камера: два раза туда-обратно", cam.Ok && cam.Steps.Count == 1 && cam.Steps[0] is RepeatStep { Count: 2 });
            var old = Defaults.Create();
            old.SettingsVersion = 0;
            foreach (var t2 in new[] { "Камера туда-обратно", "Обманка с рулём", "Дёрганый режим", "Лупа" }) old.Rewards.Remove(old.FindReward(t2)!);
            foreach (var rr in old.Donations.Rules.Where(x => x.Name == "Лупа" || x.Name == "Дёрганый режим").ToList()) old.Donations.Rules.Remove(rr);
            old.FindReward("Газ в пол")!.Steps = "hold accelerate 4";
            old.FindReward("Заглушить двигатель")!.Steps = "press engine";
            Check("перенос: изменения применились", Defaults.Migrate(old) && !Defaults.Migrate(old));
            Check("перенос: газ 10 сек, новые награды, глушение с ручником",
                old.FindReward("Газ в пол")!.Steps == "hold accelerate 10" && old.FindReward("Лупа") != null
                && old.FindReward("Дёрганый режим") != null && old.FindReward("Заглушить двигатель")!.Steps.Contains("handbrake")
                && old.Donations.Rules.Any(x => x.Name == "Лупа"));
            var old2 = Defaults.Create(); old2.SettingsVersion = 0; old2.FindReward("Газ в пол")!.Steps = "hold accelerate 6";
            Defaults.Migrate(old2);
            Check("перенос: свои шаги пользователя не трогаются", old2.FindReward("Газ в пол")!.Steps == "hold accelerate 6");

            // 2б. мелодии гудком
            foreach (var mel in s.Rewards.Where(r => r.Title.StartsWith("Гудок: ")))
            {
                var mp = ActionScript.Parse(mel.Steps, s.KeyExists);
                double md = mp.Ok ? ActionScript.Estimate(mp.Steps, s.General.TapSeconds) : 0;
                Check("мелодия «" + mel.Title + "»: 2–15 сек", mp.Ok && md >= 2 && md <= 15, md.ToString("0.0") + " сек");
            }
            var old3 = Defaults.Create(); old3.SettingsVersion = 2;
            foreach (var rr in old3.Rewards.Where(x => x.Title.StartsWith("Гудок: ")).ToList()) old3.Rewards.Remove(rr);
            foreach (var rr in old3.Donations.Rules.Where(x => x.Name.StartsWith("Гудок: ")).ToList()) old3.Donations.Rules.Remove(rr);
            Check("перенос v3: мелодии добавляются старым настройкам",
                Defaults.Migrate(old3) && old3.Rewards.Count(x => x.Title.StartsWith("Гудок: ")) == 5
                && old3.Donations.Rules.Count(x => x.Name.StartsWith("Гудок: ")) == 5);

            // 3. клавиши
            Check("клавиши: имя из списка", s.ResolveKey("steer_left") == "a");
            Check("клавиши: просто клавиша", s.KeyExists("f5") && s.KeyExists("space"));

            // 4. донаты
            DonationMatcher.Match(s, new DonationEvent("1", "t", "", 10, "RUB"));
            var m1 = DonationMatcher.Match(s, new DonationEvent("1", "t", "руль влево пожалуйста", 100, "RUB"));
            Check("донат: ключевое слово «влево»", m1 != null && m1.Error == null && m1.Title == "Руль влево");
            var m2 = DonationMatcher.Match(s, new DonationEvent("2", "t", "гудок!", 100, "RUB"));
            Check("донат: каждые 30 ₽ = гудок (×3)", m2 != null && m2.Times == 3, m2?.Times.ToString() ?? "null");
            var m3 = DonationMatcher.Match(s, new DonationEvent("3", "t", "", 2000, "RUB"));
            Check("донат: мега-хаос от 1000", m3 != null && m3.Error == null && m3.Title.Contains("хаос", StringComparison.OrdinalIgnoreCase));
            var m4 = DonationMatcher.Match(s, new DonationEvent("4", "t", "влево", 1, "USD"));
            Check("донат: курс валют (1 USD = 90 ₽)", m4 != null && Math.Abs(m4.AmountInBase - 90) < 0.001);
            var m5 = DonationMatcher.Match(s, new DonationEvent("5", "t", "", 5, "RUB"));
            Check("донат: слишком мелкий — без реакции", m5 == null);

            // 5. настройки: сериализация туда-обратно
            var tmp = Path.Combine(Path.GetTempPath(), "ets2bot_selftest.json");
            var opt = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(s, opt));
            var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(tmp), opt);
            Check("настройки: сохранение/загрузка", back != null && back.Rewards.Count == s.Rewards.Count
                && back.Donations.Rules.Count == s.Donations.Rules.Count && back.Keys.Count == s.Keys.Count);
            Check("импорт: мусор и пустой файл отклоняются", AppSettings.Parse("{}", out _) == null && AppSettings.Parse("не json", out _) == null);
            Check("импорт: нормальные настройки принимаются", AppSettings.Parse(File.ReadAllText(tmp), out _) != null);
            try { File.Delete(tmp); } catch { }

            // 6. выполнение действия (без реальных нажатий)
            var keys = new KeySender { DryRun = true };
            var runner = new ActionRunner(s, keys);
            s.General.TapSeconds = 0.01;
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            var run = runner.RunAsync(cts.Token);
            var steps = ActionScript.Parse("press horn 2 0.05\nhold steer_left 0.1", s.KeyExists).Steps;
            runner.EnqueueAsync(new QueuedAction { Source = ActionSource.Donation, Title = "t", Steps = steps }).GetAwaiter().GetResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((runner.Current != null || runner.Snapshot().Length > 0 || sw.ElapsedMilliseconds < 200) && sw.ElapsedMilliseconds < 5000)
                System.Threading.Thread.Sleep(50);
            Check("очередь: действие выполнено", runner.Current == null && runner.Snapshot().Length == 0, sw.ElapsedMilliseconds + " мс");
            cts.Cancel();

            // 6б. пауза между наградами
            s.General.RequireGameFocus = false;
            s.General.GapSeconds = 0.4;
            var runner2 = new ActionRunner(s, new KeySender { DryRun = true });
            var starts = new List<DateTime>();
            runner2.ActionStarted += _ => { lock (starts) starts.Add(DateTime.UtcNow); };
            using var cts2 = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            var run2 = runner2.RunAsync(cts2.Token);
            var quick = ActionScript.Parse("wait 0.05", s.KeyExists).Steps;
            runner2.EnqueueAsync(new QueuedAction { Source = ActionSource.Donation, Title = "a", Steps = quick }).GetAwaiter().GetResult();
            runner2.EnqueueAsync(new QueuedAction { Source = ActionSource.Event, Title = "b", Steps = quick }).GetAwaiter().GetResult();
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            while (sw2.ElapsedMilliseconds < 5000) { lock (starts) { if (starts.Count >= 2) break; } System.Threading.Thread.Sleep(50); }
            double gapSec = 0;
            lock (starts) { if (starts.Count >= 2) gapSec = (starts[1] - starts[0]).TotalSeconds; }
            Check("пауза: между наградами не меньше 0,4 сек", gapSec >= 0.4, gapSec.ToString("0.00") + " сек");
            cts2.Cancel();
            s.General.GapSeconds = 2;

            // 6в. горячая клавиша, чат, статистика, алерты
            Check("паника: Ctrl+Alt+X разбирается", PanicHotkey.TryParse("Ctrl+Alt+X", out var pm, out var pv) && pm != 0 && pv != 0);
            Check("паника: F9 без модификатора можно", PanicHotkey.TryParse("F9", out _, out _));
            Check("паника: одиночная буква нельзя", !PanicHotkey.TryParse("X", out _, out _));
            Check("паника: мусор отклоняется", !PanicHotkey.TryParse("Ctrl+", out _, out _) && !PanicHotkey.TryParse("Foo+X", out _, out _));

            var st = new StatsStore(null);
            st.Record("Вася"); st.Record("вася"); st.Record("Петя"); st.Record("");
            var top = st.Top(true, 5);
            Check("топ: счёт по зрителям без учёта регистра", top.Count == 2 && top[0].Count == 2 && top[1].Name == "Петя");

            var few = new AppSettings();
            few.Rewards.Add(new RewardDef { Title = "Лупа", Cost = 200 });
            Check("чат: список наград с ценами", BotService.RewardsChatText(few).Contains("Лупа — 200"));
            var many = new AppSettings();
            for (int i = 0; i < 60; i++) many.Rewards.Add(new RewardDef { Title = "Очень длинная награда номер " + i, Cost = 100 });
            var longText = BotService.RewardsChatText(many);
            Check("чат: длинный список обрезается до 480", longText.Length <= 480 && longText.EndsWith("…"), longText.Length + " симв.");

            OverlayState.Show(new QueuedAction { Title = "Лупа", User = "Вася", Source = ActionSource.Event }, 1);
            Check("алерт: активен после запуска", System.Text.Json.JsonSerializer.Serialize(OverlayState.Snapshot()).Contains("\"active\":true"));
            OverlayState.Clear();
            Check("алерт: сбрасывается", System.Text.Json.JsonSerializer.Serialize(OverlayState.Snapshot()).Contains("\"active\":false"));

            Check("мастер: по умолчанию не пройден", !s.General.WizardDone);

            // 6г. рулетка, голосование, копилка, чёрный список, отчёт
            Check("рулетка и копилка есть по умолчанию", s.FindReward("Рулетка") != null && s.FindReward("Копилка") != null
                && s.Donations.Rules.Any(x => x.Name == "Рулетка"));
            var old4 = Defaults.Create(); old4.SettingsVersion = 3;
            old4.Rewards.Remove(old4.FindReward("Рулетка")!); old4.Rewards.Remove(old4.FindReward("Копилка")!);
            Check("перенос v4: рулетка и копилка добавляются", Defaults.Migrate(old4) && old4.FindReward("Рулетка") != null && old4.FindReward("Копилка") != null);
            Check("цена: шаг дорожания не бывает отрицательным", new RewardDef { PriceStep = -5 }.PriceStep == 0);

            Check("чёрный список: запятые, новая строка, @, регистр",
                BotService.IsInList("Вася, @петя\nМаша", "петя") && BotService.IsInList("Вася, @петя\nМаша", "МАША")
                && !BotService.IsInList("Вася", "Коля") && !BotService.IsInList("", "Вася"));

            Check("голосование: старт и голоса", VoteState.Start(new[] { "A", "B", "C" }, 30)
                && VoteState.Cast("u1", 1) && VoteState.Cast("u2", 2) && VoteState.Cast("u3", 2));
            Check("голосование: можно передумать, чужой номер не принимается", VoteState.Cast("u3", 1) && !VoteState.Cast("u9", 7));
            var vr = VoteState.Finish();
            Check("голосование: побеждает большинство", vr != null && vr.Winner == 0 && vr.Votes == 2 && vr.Counts[1] == 1);
            Check("голосование: после конца голоса не принимаются", !VoteState.Cast("u5", 1));
            VoteState.Start(new[] { "A", "B" }, 30);
            var empty = VoteState.Finish();
            Check("голосование: без голосов победителя нет", empty != null && empty.Winner == -1);

            var pg = new PiggyStore(null);
            Check("копилка: копится до цели", !pg.Add(100, 300) && !pg.Add(100, 300) && pg.Amount == 200);
            Check("копилка: цель достигнута, излишек остаётся", pg.Add(150, 300) && pg.Amount == 50);

            var srep = new SessionReport();
            srep.RecordAction("Лупа", "Twitch", 200); srep.RecordAction("Лупа", "Донат", 0);
            srep.RecordDonation(150); srep.RecordRefund(); srep.RecordBlocked();
            var repHtml = srep.BuildHtml(DateTime.UtcNow, "RUB", new List<(string Name, int Count)> { ("Вася", 3) });
            Check("отчёт: цифры и HTML", srep.Actions == 2 && srep.PointsSpent == 200 && srep.DonationsCount == 1
                && repHtml.Contains("Лупа") && repHtml.Contains("Вася"));

            // 7. оверлей для OBS
            s.General.OverlayPort = 39172;
            using var ov = new OverlayServer(s);
            ov.Restart();
            Check("оверлей: сервер запущен", ov.Running, ov.Error ?? "");
            if (ov.Running)
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var json = http.GetStringAsync("http://localhost:39172/data").GetAwaiter().GetResult();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var items = doc.RootElement.GetProperty("items");
                Check("оверлей: /data отдаёт правила", items.GetArrayLength() == s.Donations.Rules.Count, items.GetArrayLength() + " шт.");
                Check("оверлей: текст в UTF-8", json.Contains("Руль влево") && json.Contains("от 50"), "");
                var html = http.GetStringAsync("http://localhost:39172/donations").GetAwaiter().GetResult();
                Check("оверлей: страница отдаётся", html.Contains("<!doctype html>") && html.Contains("/data"));
                var alertsHtml = http.GetStringAsync("http://localhost:39172/alerts").GetAwaiter().GetResult();
                Check("оверлей: страница алертов", alertsHtml.Contains("/alert-data"));
                var alertJson = http.GetStringAsync("http://localhost:39172/alert-data").GetAwaiter().GetResult();
                Check("оверлей: /alert-data", alertJson.Contains("\"active\""));
                var topPage = http.GetStringAsync("http://localhost:39172/top").GetAwaiter().GetResult();
                var topJson = http.GetStringAsync("http://localhost:39172/top-data?mode=session&n=3").GetAwaiter().GetResult();
                Check("оверлей: топ мучителей", topPage.Contains("/top-data") && topJson.Contains("\"items\""));
                var voteHtml = http.GetStringAsync("http://localhost:39172/vote").GetAwaiter().GetResult();
                var voteJson = http.GetStringAsync("http://localhost:39172/vote-data").GetAwaiter().GetResult();
                var goalHtml = http.GetStringAsync("http://localhost:39172/goal").GetAwaiter().GetResult();
                var goalJson = http.GetStringAsync("http://localhost:39172/goal-data").GetAwaiter().GetResult();
                Check("оверлей: голосование и копилка", voteHtml.Contains("/vote-data") && voteJson.Contains("\"visible\"")
                    && goalHtml.Contains("/goal-data") && goalJson.Contains("\"goal\""));
                s.Donations.Rules[0].ShowOnOverlay = false;
                json = http.GetStringAsync("http://localhost:39172/data").GetAwaiter().GetResult();
                using var doc2 = System.Text.Json.JsonDocument.Parse(json);
                Check("оверлей: скрытое правило не показывается", doc2.RootElement.GetProperty("items").GetArrayLength() == s.Donations.Rules.Count - 1);
            }
            ov.Stop();
        }
        catch (Exception ex)
        {
            sb.AppendLine("FAIL  исключение: " + ex);
            fails++;
        }

        sb.AppendLine();
        sb.AppendLine(fails == 0 ? "ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ" : "ОШИБОК: " + fails);
        try { File.WriteAllText(Paths.SelfTestLog, sb.ToString(), new UTF8Encoding(true)); } catch { }
        return fails == 0 ? 0 : 1;
    }
}
