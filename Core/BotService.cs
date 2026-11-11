using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EtsBot;

/// <summary>Связывает всё вместе: Twitch + донаты + очередь действий. UI управляет только этим классом.</summary>
public sealed class BotService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public AppSettings Settings { get; }
    public KeySender Keys { get; } = new();
    public ActionRunner Runner { get; }
    public SessionReport Report { get; } = new();

    public ConnState TwitchState { get; private set; } = ConnState.Off;
    public ConnState DonationState { get; private set; } = ConnState.Off;
    public string? TwitchError { get; private set; }
    public string? DonationError { get; private set; }
    public string? TwitchLogin { get; private set; }
    public bool IsRunning { get; private set; }

    public event Action? StateChanged;

    private CancellationTokenSource? _cts;
    private TwitchApi? _api;
    private Task[] _tasks = Array.Empty<Task>();

    public BotService(AppSettings settings)
    {
        Settings = settings;
        Runner = new ActionRunner(settings, Keys);
        Runner.RedemptionResult = OnRedemptionResultAsync;
        Runner.ActionStarted += OnActionStarted;
        Runner.CurrentChanged += () => { if (Runner.Current == null) OverlayState.Clear(); };
    }

    public void Start()
    {
        if (IsRunning) return;
        Keys.DryRun = Settings.General.DryRun;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        Report.Reset();
        StatsStore.Instance.ResetSession();
        var list = new List<Task> { Task.Run(() => Runner.RunAsync(ct)) };

        var tw = Settings.Twitch;
        if (string.IsNullOrWhiteSpace(tw.ClientId) || string.IsNullOrWhiteSpace(tw.ClientSecret))
        {
            SetTwitch(ConnState.Error, "Не заполнены Client ID / Secret Twitch");
            Log.Error("Twitch", "Заполни Client ID и Client Secret на вкладке «Настройки»");
        }
        else
        {
            list.Add(Task.Run(() => RunTwitchAsync(ct)));
            list.Add(Task.Run(() => BackgroundLoopAsync(ct)));
        }

        if (Settings.Donations.Enabled)
        {
            if (string.IsNullOrWhiteSpace(Settings.Donations.ClientId) || string.IsNullOrWhiteSpace(Settings.Donations.ClientSecret))
            {
                SetDonation(ConnState.Error, "Не заполнены Client ID / Secret DonationAlerts");
                Log.Error("Донаты", "Заполни Client ID и Secret DonationAlerts на вкладке «Настройки»");
            }
            else list.Add(Task.Run(() => RunDonationsAsync(ct)));
        }

        _tasks = list.ToArray();
        Log.Ok("Бот", "Запущен" + (Settings.General.DryRun ? " (тестовый режим: клавиши НЕ нажимаются)" : ""));
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        VoteState.Cancel();
        try { await Task.WhenAny(ResetPricesAsync(), Task.Delay(5000)); } catch { /* ignore */ }
        try { _cts?.Cancel(); } catch { /* ignore */ }
        await Runner.StopAllAsync();
        if (Report.HasData) SaveReport();
        try { await Task.WhenAny(Task.WhenAll(_tasks), Task.Delay(3000)); } catch { /* ignore */ }
        Keys.ReleaseAll();
        TwitchState = ConnState.Off; DonationState = ConnState.Off;
        TwitchError = null; DonationError = null;
        _api = null;
        Log.Info("Бот", "Остановлен");
        StateChanged?.Invoke();
    }

    private void SetTwitch(ConnState s, string? err) { TwitchState = s; TwitchError = err; StateChanged?.Invoke(); }
    private void SetDonation(ConnState s, string? err) { DonationState = s; DonationError = err; StateChanged?.Invoke(); }

    // ---------------- Twitch ----------------

    private async Task RunTwitchAsync(CancellationToken ct)
    {
        try
        {
            SetTwitch(ConnState.Connecting, null);
            var auth = new TwitchAuth(Http, Settings.Twitch);
            // вход ДО подключения к EventSub: Twitch закрывает сокет, если подписки нет в первые 10 секунд
            await auth.GetTokenAsync(ct);
            TwitchLogin = auth.Login;
            _api = new TwitchApi(Http, auth);
            var client = new EventSubClient(_api, HandleRedemptionAsync, SetTwitch, HandleEventAsync, BuildExtraSubscriptions);
            await client.RunAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Twitch", ex.Message);
            SetTwitch(ConnState.Error, ex.Message);
        }
    }

    private async Task HandleRedemptionAsync(JsonElement ev)
    {
        var reward = ev.GetProperty("reward");
        var title = reward.GetProperty("title").GetString() ?? "";
        var rewardId = reward.GetProperty("id").GetString();
        var redemptionId = ev.GetProperty("id").GetString();
        var user = ev.TryGetProperty("user_name", out var u) ? u.GetString() ?? "" : "";

        var def = Settings.FindReward(title);
        if (def == null)
        {
            Log.Info("Twitch", $"Награда «{title}» ({user}) не настроена в программе — пропускаю");
            return;
        }
        if (!def.Enabled)
        {
            Log.Info("Twitch", $"Награда «{title}» выключена в программе — пропускаю");
            return;
        }

        var g = Settings.General;
        int points = CurrentCost(def);
        var stub = new QueuedAction
        {
            Source = ActionSource.Twitch, Title = def.Title, User = user,
            RewardId = rewardId, RedemptionId = redemptionId, Points = points,
        };

        if (IsBlocked(user))
        {
            Log.Warn("Чёрный список", $"{user} в чёрном списке: «{title}» отменена, баллы возвращены");
            Report.RecordBlocked();
            await OnRedemptionResultAsync(stub, "CANCELED");
            return;
        }

        _ = BumpPriceAsync(def, rewardId);

        if (Same(def.Title, g.PiggyReward))
        {
            await HandlePiggyAsync(stub, def);
            return;
        }

        QueuedAction? a;
        if (Same(def.Title, g.RouletteReward))
        {
            var pick = PickRandomReward();
            if (pick == null)
            {
                Log.Error("Рулетка", "Нет подходящих наград для рулетки — баллы возвращены");
                await OnRedemptionResultAsync(stub, "CANCELED");
                return;
            }
            Log.Info("Рулетка", $"{user}: выпало «{pick.Title}»");
            a = BuildAction(pick, ActionSource.Twitch, user, "Рулетка → " + pick.Title, rewardId, redemptionId, points);
        }
        else a = BuildAction(def, ActionSource.Twitch, user, null, rewardId, redemptionId, points);

        if (a == null)
        {
            await OnRedemptionResultAsync(stub, "CANCELED");
            return;
        }
        Log.Info("Twitch", $"{user} купил(а) «{title}»");
        await Runner.EnqueueAsync(a);
    }

    private QueuedAction? BuildAction(RewardDef def, ActionSource src, string user, string? title,
        string? rewardId, string? redemptionId, int points)
    {
        var parsed = ActionScript.Parse(def.Steps, Settings.KeyExists);
        if (!parsed.Ok || parsed.Steps.Count == 0)
        {
            Log.Error("Награды", $"У награды «{def.Title}» ошибка в шагах: " + (parsed.Errors.Count > 0 ? parsed.Errors[0] : "нет действий"));
            return null;
        }
        return new QueuedAction
        {
            Source = src, Title = title ?? def.Title, User = user, Steps = parsed.Steps,
            RewardId = rewardId, RedemptionId = redemptionId, Points = points,
        };
    }

    private async Task OnRedemptionResultAsync(QueuedAction a, string status)
    {
        if (status == "CANCELED") Report.RecordRefund();
        var api = _api;
        if (api == null || a.RewardId == null || a.RedemptionId == null) return;
        try
        {
            await api.UpdateRedemptionAsync(a.RewardId, a.RedemptionId, status, CancellationToken.None);
            Log.Info("Twitch", status == "FULFILLED" ? $"«{a.Title}» отмечена выполненной" : $"«{a.Title}»: баллы возвращены зрителю");
        }
        catch (TwitchApiException ex) when (ex.Status == 403)
        {
            Log.Warn("Twitch", "Награду нельзя изменить: её создали не через эту программу. Создай награды кнопкой «Создать награды на Twitch».");
        }
        catch (Exception ex)
        {
            Log.Warn("Twitch", "Не удалось обновить статус награды: " + ex.Message);
        }
    }

    // ---------------- Чат, события, алерты ----------------

    private void OnActionStarted(QueuedAction a)
    {
        OverlayState.Show(a, Runner.CurrentDuration);
        if (a.Source == ActionSource.Test || Settings.General.DryRun) return;
        StatsStore.Instance.Record(a.User);
        Report.RecordAction(a.Title, a.SourceText, a.Points);
        if (Settings.General.ChatAnnounce && _api != null)
        {
            var text = a.User.Length > 0 ? $"🚛 {a.User} → «{a.Display}»" : $"🚛 «{a.Display}»";
            _ = SendChatAsync(text);
        }
    }

    private async Task SendChatAsync(string text)
    {
        var api = _api;
        if (api == null) return;
        try { await api.SendChatAsync(Truncate(text, 480), CancellationToken.None); }
        catch (Exception ex) { Log.Warn("Чат", "Не удалось отправить сообщение: " + ex.Message); }
    }

    private List<EventSubscription> BuildExtraSubscriptions()
    {
        var g = Settings.General;
        var id = _api?.UserId ?? "";
        var list = new List<EventSubscription>();
        if (id.Length == 0) return list;
        if (g.ChatCommands || g.VoteEnabled)
            list.Add(new EventSubscription("channel.chat.message", "1",
                new Dictionary<string, string> { ["broadcaster_user_id"] = id, ["user_id"] = id }));
        if (g.SubReward.Length > 0)
            list.Add(new EventSubscription("channel.subscribe", "1",
                new Dictionary<string, string> { ["broadcaster_user_id"] = id }));
        if (g.BitsReward.Length > 0)
            list.Add(new EventSubscription("channel.cheer", "1",
                new Dictionary<string, string> { ["broadcaster_user_id"] = id }));
        if (g.RaidReward.Length > 0)
            list.Add(new EventSubscription("channel.raid", "1",
                new Dictionary<string, string> { ["to_broadcaster_user_id"] = id }));
        return list;
    }

    private static string Str(JsonElement ev, string name)
        => ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement ev, string name)
        => ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool Flag(JsonElement ev, string name)
        => ev.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private async Task HandleEventAsync(string type, JsonElement ev)
    {
        var g = Settings.General;
        switch (type)
        {
            case "channel.chat.message":
                await HandleChatAsync(ev);
                break;
            case "channel.subscribe":
                if (!Flag(ev, "is_gift") && g.SubReward.Length > 0)
                    await EnqueueEventAsync(g.SubReward, Str(ev, "user_name"), "новая подписка");
                break;
            case "channel.cheer":
            {
                int bits = Int(ev, "bits");
                var who = Flag(ev, "is_anonymous") ? "Аноним" : Str(ev, "user_name");
                if (g.BitsReward.Length > 0 && bits >= g.BitsMin)
                    await EnqueueEventAsync(g.BitsReward, who, bits + " битсов");
                break;
            }
            case "channel.raid":
            {
                int viewers = Int(ev, "viewers");
                if (g.RaidReward.Length > 0 && viewers >= g.RaidMin)
                    await EnqueueEventAsync(g.RaidReward, Str(ev, "from_broadcaster_user_name"), "рейд на " + viewers);
                break;
            }
        }
    }

    private async Task EnqueueEventAsync(string rewardName, string user, string why)
    {
        if (IsBlocked(user)) { Log.Warn("Чёрный список", $"{user} в чёрном списке: событие «{why}» пропущено"); Report.RecordBlocked(); return; }
        var def = Settings.FindReward(rewardName);
        if (def == null) { Log.Warn("События", $"Награда «{rewardName}» не найдена в программе"); return; }
        var parsed = ActionScript.Parse(def.Steps, Settings.KeyExists);
        if (!parsed.Ok || parsed.Steps.Count == 0)
        {
            Log.Error("События", $"У награды «{def.Title}» ошибка в шагах: " + (parsed.Errors.Count > 0 ? parsed.Errors[0] : "нет действий"));
            return;
        }
        Log.Info("События", $"{user}: {why} → «{def.Title}»");
        await Runner.EnqueueAsync(new QueuedAction
        {
            Source = ActionSource.Event, Title = def.Title, User = user, Steps = parsed.Steps,
        });
    }

    private DateTime _lastRewardsCmd = DateTime.MinValue;

    private async Task HandleChatAsync(JsonElement ev)
    {
        if (!ev.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return;
        var text = Str(m, "text").Trim().ToLowerInvariant();
        if (text.Length >= 2 && text[0] == '!' && text[1] >= '1' && text[1] <= '9' && (text.Length == 2 || text[2] == ' '))
        {
            if (!IsBlocked(Str(ev, "chatter_user_name"))) VoteState.Cast(Str(ev, "chatter_user_id"), text[1] - '0');
            return;
        }
        bool cmd = false;
        foreach (var c in new[] { "!награды", "!rewards" })
            if (text == c || text.StartsWith(c + " ")) cmd = true;
        if (!cmd) return;
        if ((DateTime.UtcNow - _lastRewardsCmd).TotalSeconds < 30) return; // защита от спама
        _lastRewardsCmd = DateTime.UtcNow;
        await SendChatAsync(RewardsChatText(Settings));
    }

    /// <summary>Список включённых наград с ценами одной строкой для чата (не длиннее 480 символов).</summary>
    public static string RewardsChatText(AppSettings s)
    {
        var items = s.Rewards.Where(r => r.Enabled).Select(r => $"{r.Title} — {r.Cost}");
        return Truncate("🎮 Награды за баллы: " + string.Join(" | ", items), 480);
    }

    public static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text.Substring(0, max - 1);
        int i = cut.LastIndexOf(" | ", StringComparison.Ordinal);
        if (i > max / 2) cut = cut.Substring(0, i);
        return cut.TrimEnd() + "…";
    }

    // ---------------- Рулетка, голосование, цены, копилка, чёрный список, отчёт ----------------

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(b) && string.Equals((a ?? "").Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool IsBlocked(string user) => IsInList(Settings.General.Blacklist, user);

    public static bool IsInList(string list, string user)
    {
        user = (user ?? "").Trim().TrimStart('@');
        if (user.Length == 0 || string.IsNullOrWhiteSpace(list)) return false;
        foreach (var raw in list.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(raw.Trim().TrimStart('@'), user, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Награды, которые могут выпасть в рулетке и попасть в голосование.</summary>
    private List<RewardDef> PoolRewards()
    {
        var g = Settings.General;
        var list = new List<RewardDef>();
        foreach (var r in Settings.Rewards)
        {
            if (!r.Enabled || !r.InRoulette || Same(r.Title, g.RouletteReward) || Same(r.Title, g.PiggyReward)) continue;
            var p = ActionScript.Parse(r.Steps, Settings.KeyExists);
            if (p.Ok && p.Steps.Count > 0) list.Add(r);
        }
        return list;
    }

    private RewardDef? PickRandomReward()
    {
        var pool = PoolRewards();
        return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
    }

    // --- копилка ---

    private async Task HandlePiggyAsync(QueuedAction stub, RewardDef def)
    {
        var g = Settings.General;
        bool reached = PiggyStore.Instance.Add(stub.Points, g.PiggyGoal);
        Report.RecordAction(def.Title, stub.SourceText, stub.Points);
        Log.Info("Копилка", $"{stub.User} вложил(а) {stub.Points}: {PiggyStore.Instance.Amount} из {g.PiggyGoal}");
        if (g.MarkFulfilled) await OnRedemptionResultAsync(stub, "FULFILLED");
        if (!reached)
        {
            if (g.ChatAnnounce) await SendChatAsync($"💰 Копилка: {PiggyStore.Instance.Amount} из {g.PiggyGoal}");
            return;
        }
        var parsed = ActionScript.Parse(g.PiggySteps, Settings.KeyExists);
        if (!parsed.Ok || parsed.Steps.Count == 0)
        {
            Log.Error("Копилка", "Цель достигнута, но в действии копилки ошибка: " + (parsed.Errors.Count > 0 ? parsed.Errors[0] : "нет действий"));
            return;
        }
        await SendChatAsync("💰 Копилка собрана! Стримера ждёт МЕГА-ХАОС!");
        await Runner.EnqueueAsync(new QueuedAction
        {
            Source = ActionSource.Event, Title = "Мега-хаос (копилка)", User = "", Steps = parsed.Steps,
        });
    }

    // --- дорожающие награды ---

    private readonly Dictionary<string, int> _bumps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rewardIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _priceLock = new();

    private int CurrentCost(RewardDef def)
    {
        lock (_priceLock)
            return def.Cost + (_bumps.TryGetValue(def.Title.Trim(), out var n) ? n : 0) * def.PriceStep;
    }

    private async Task BumpPriceAsync(RewardDef def, string? rewardId)
    {
        if (def.PriceStep <= 0 || rewardId == null) return;
        var key = def.Title.Trim();
        int cost;
        lock (_priceLock)
        {
            _bumps[key] = (_bumps.TryGetValue(key, out var n) ? n : 0) + 1;
            _rewardIds[key] = rewardId;
            cost = (int)Math.Min(1_000_000L, def.Cost + (long)_bumps[key] * def.PriceStep);
        }
        await PushCostAsync(rewardId, cost, def.Title);
    }

    private async Task PushCostAsync(string rewardId, int cost, string title)
    {
        var api = _api;
        if (api == null) return;
        try
        {
            await api.SetRewardCostAsync(rewardId, cost, CancellationToken.None);
            Log.Info("Цены", $"«{title}»: теперь {cost} баллов");
        }
        catch (Exception ex) { Log.Warn("Цены", $"«{title}»: не удалось поменять цену: {ex.Message}"); }
    }

    private async Task DecayPricesAsync()
    {
        var todo = new List<(string Title, string Id, int Cost)>();
        lock (_priceLock)
        {
            foreach (var key in _bumps.Keys.ToList())
            {
                if (_bumps[key] <= 0) continue;
                _bumps[key]--;
                var def = Settings.FindReward(key);
                if (def != null && _rewardIds.TryGetValue(key, out var id))
                    todo.Add((def.Title, id, def.Cost + _bumps[key] * def.PriceStep));
            }
        }
        foreach (var t in todo) await PushCostAsync(t.Id, t.Cost, t.Title);
    }

    private async Task ResetPricesAsync()
    {
        var todo = new List<(string Title, string Id, int Cost)>();
        lock (_priceLock)
        {
            foreach (var kv in _bumps.Where(k => k.Value > 0).ToList())
            {
                var def = Settings.FindReward(kv.Key);
                if (def != null && _rewardIds.TryGetValue(kv.Key, out var id)) todo.Add((def.Title, id, def.Cost));
            }
            _bumps.Clear();
        }
        foreach (var t in todo) await PushCostAsync(t.Id, t.Cost, t.Title);
    }

    // --- голосование чата ---

    /// <summary>Запускает голосование. null — запущено, иначе текст причины отказа.</summary>
    public async Task<string?> StartVoteAsync()
    {
        var g = Settings.General;
        if (!IsRunning || _api == null || TwitchState != ConnState.On) return "Сначала запусти бота и дождись подключения к Twitch";
        if (!g.VoteEnabled) return "Включи голосование чата в настройках и перезапусти бота";
        if (VoteState.Active) return "Голосование уже идёт";
        var picks = PoolRewards().OrderBy(_ => Random.Shared.Next()).Take(3).ToList();
        if (picks.Count < 2) return "Для голосования нужно хотя бы две награды с галочкой «в рулетке и голосовании»";
        var titles = picks.Select(p => p.Title).ToArray();
        if (!VoteState.Start(titles, g.VoteSeconds)) return "Не удалось начать голосование";

        var line = string.Join(" | ", titles.Select((t, i) => $"!{i + 1} {t}"));
        await SendChatAsync($"🗳 Голосуем, что сделать со стримером! {line} — {g.VoteSeconds} сек");
        var ct = _cts?.Token ?? CancellationToken.None;
        var seconds = g.VoteSeconds;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); }
            catch (OperationCanceledException) { VoteState.Cancel(); return; }
            await FinishVoteAsync(picks);
        });
        return null;
    }

    private async Task FinishVoteAsync(List<RewardDef> picks)
    {
        var res = VoteState.Finish();
        if (res == null) return;
        if (res.Winner < 0)
        {
            await SendChatAsync("🗳 Никто не проголосовал — ничего не происходит");
            return;
        }
        var def = picks[res.Winner];
        await SendChatAsync($"🗳 Победило «{def.Title}» ({res.Votes} гол.)! Стример, держись!");
        var a = BuildAction(def, ActionSource.Event, "Чат", "Чат выбрал: " + def.Title, null, null, 0);
        if (a != null) await Runner.EnqueueAsync(a);
    }

    // --- фоновые задачи: авто-голосование и «остывание» цен ---

    private async Task BackgroundLoopAsync(CancellationToken ct)
    {
        var nextVote = DateTime.MinValue;
        var nextDecay = DateTime.UtcNow.AddMinutes(Settings.General.PriceDecayMinutes);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                var g = Settings.General;
                var now = DateTime.UtcNow;
                if (now >= nextDecay)
                {
                    nextDecay = now.AddMinutes(g.PriceDecayMinutes);
                    await DecayPricesAsync();
                }
                if (g.VoteEnabled && g.VoteEveryMinutes > 0 && TwitchState == ConnState.On && !VoteState.Active)
                {
                    if (nextVote == DateTime.MinValue) nextVote = now.AddMinutes(g.VoteEveryMinutes);
                    else if (now >= nextVote && Runner.Current == null && Runner.Snapshot().Length == 0)
                    {
                        nextVote = now.AddMinutes(g.VoteEveryMinutes);
                        await StartVoteAsync();
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* остановка */ }
    }

    // --- отчёт ---

    /// <summary>Сохраняет отчёт за текущий стрим в папку reports. Возвращает путь или null.</summary>
    public string? SaveReport()
    {
        try
        {
            var path = Report.Save(Settings.Donations.BaseCurrency);
            Log.Ok("Отчёт", "Сохранён: " + path);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn("Отчёт", "Не удалось сохранить: " + ex.Message);
            return null;
        }
    }

    // ---------------- Донаты ----------------

    private async Task RunDonationsAsync(CancellationToken ct)
    {
        try
        {
            SetDonation(ConnState.Connecting, null);
            var auth = new DonationAuth(Http, Settings.Donations);
            await auth.GetTokenAsync(ct);
            var client = new DonationClient(Http, auth, HandleDonationAsync, SetDonation);
            await client.RunAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Донаты", ex.Message);
            SetDonation(ConnState.Error, ex.Message);
        }
    }

    public async Task HandleDonationAsync(DonationEvent d)
    {
        Log.Info("Донаты", $"{d.Username}: {d.Amount:0.##} {d.Currency}" + (d.Message.Length > 0 ? $" — «{d.Message}»" : ""));
        Report.RecordDonation(DonationMatcher.ToBase(Settings, d.Amount, d.Currency));
        if (IsBlocked(d.Username))
        {
            Log.Warn("Чёрный список", $"Донат от {d.Username}: он в чёрном списке, действие не выполняется");
            Report.RecordBlocked();
            return;
        }
        var m = DonationMatcher.Match(Settings, d);
        if (m == null) { Log.Info("Донаты", "Ни одно правило не подошло"); return; }
        if (m.Error != null) { Log.Error("Донаты", $"Правило «{m.RuleName}»: {m.Error}"); return; }
        Log.Info("Донаты", $"Сработало правило «{m.RuleName}»" + (m.Times > 1 ? $" (×{m.Times})" : ""));
        if (Same(m.Title, Settings.General.RouletteReward))
        {
            var pick = PickRandomReward();
            var picked = pick == null ? null : BuildAction(pick, ActionSource.Donation, d.Username, "Рулетка → " + pick.Title, null, null, 0);
            if (picked == null) { Log.Error("Рулетка", "Нет подходящих наград для рулетки"); return; }
            Log.Info("Рулетка", $"{d.Username}: выпало «{pick!.Title}»");
            await Runner.EnqueueAsync(picked);
            return;
        }
        await Runner.EnqueueAsync(new QueuedAction
        {
            Source = ActionSource.Donation, Title = m.Title, User = d.Username, Steps = m.Steps, Times = m.Times,
        });
    }

    // ---------------- Сервис ----------------

    /// <summary>Запуск действия вручную из интерфейса (кнопка «Проверить»).</summary>
    public async Task<string?> TestStepsAsync(string title, string stepsText, int times = 1)
    {
        Keys.DryRun = Settings.General.DryRun;
        var parsed = ActionScript.Parse(stepsText, Settings.KeyExists);
        if (!parsed.Ok) return parsed.Errors[0];
        if (parsed.Steps.Count == 0) return "Нет действий";
        await Runner.EnqueueAsync(new QueuedAction { Source = ActionSource.Test, Title = title, Steps = parsed.Steps, Times = times });
        EnsureRunnerForTests();
        return null;
    }

    private Task? _testRunner;
    private CancellationTokenSource? _testCts;

    private void EnsureRunnerForTests()
    {
        if (IsRunning) return;
        if (_testRunner != null && !_testRunner.IsCompleted) return;
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;
        _testRunner = Task.Run(async () =>
        {
            // работаем, пока очередь не опустеет
            var run = Runner.RunAsync(ct);
            int idle = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500);
                if (Runner.Current == null && Runner.Snapshot().Length == 0) { if (++idle >= 2) break; }
                else idle = 0;
            }
            _testCts.Cancel();
            await run;
        });
    }

    /// <summary>Имитация доната из интерфейса (работает и без запущенного бота).</summary>
    public async Task SimulateDonationAsync(DonationEvent d)
    {
        Keys.DryRun = Settings.General.DryRun;
        await HandleDonationAsync(d);
        EnsureRunnerForTests();
    }

    public async Task StopTestsAsync()
    {
        try { _testCts?.Cancel(); } catch { /* ignore */ }
        await Runner.StopAllAsync();
    }

    public void LogoutTwitch()
    {
        new TwitchAuth(Http, Settings.Twitch).Logout();
        TwitchLogin = null;
        Log.Info("Twitch", "Выход выполнен. При следующем запуске нужно войти заново.");
        StateChanged?.Invoke();
    }

    public void LogoutDonations()
    {
        new DonationAuth(Http, Settings.Donations).Logout();
        Log.Info("Донаты", "Выход выполнен. При следующем запуске нужно войти заново.");
    }

    /// <summary>Создаёт на канале награды из списка (уже существующие по названию пропускает).</summary>
    public async Task<(int Created, int Skipped, int Failed)> CreateRewardsAsync(CancellationToken ct)
    {
        var tw = Settings.Twitch;
        if (string.IsNullOrWhiteSpace(tw.ClientId) || string.IsNullOrWhiteSpace(tw.ClientSecret))
            throw new InvalidOperationException("Сначала заполни Client ID и Client Secret Twitch на вкладке «Настройки».");

        var auth = new TwitchAuth(Http, tw);
        await auth.GetTokenAsync(ct);
        var api = new TwitchApi(Http, auth);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await api.ListRewardsAsync(ct)) existing.Add(r.Title.Trim());

        int created = 0, skipped = 0, failed = 0;
        foreach (var r in Settings.Rewards)
        {
            if (!r.Enabled) { skipped++; continue; }
            if (existing.Contains(r.Title.Trim()))
            {
                skipped++;
                Log.Info("Награды", $"«{r.Title}» уже есть на канале — пропускаю");
                continue;
            }
            try
            {
                await api.CreateRewardAsync(r, ct);
                created++;
                Log.Ok("Награды", $"Создана «{r.Title}» ({r.Cost} баллов)");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                Log.Error("Награды", $"«{r.Title}»: {ex.Message}");
            }
        }
        Log.Ok("Награды", $"Готово: создано {created}, пропущено {skipped}, ошибок {failed}");
        return (created, skipped, failed);
    }

    /// <summary>Отправляет на Twitch текущие цены и настройки наград из программы (награды ищутся по названию).</summary>
    public async Task<(int Updated, int Missing, int Failed)> UpdateRewardsAsync(CancellationToken ct)
    {
        var tw = Settings.Twitch;
        if (string.IsNullOrWhiteSpace(tw.ClientId) || string.IsNullOrWhiteSpace(tw.ClientSecret))
            throw new InvalidOperationException("Сначала заполни Client ID и Client Secret Twitch на вкладке «Настройки».");

        var auth = new TwitchAuth(Http, tw);
        await auth.GetTokenAsync(ct);
        var api = new TwitchApi(Http, auth);

        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await api.ListRewardsAsync(ct)) existing[r.Title.Trim()] = r.Id;

        int updated = 0, missing = 0, failed = 0;
        foreach (var r in Settings.Rewards)
        {
            if (!existing.TryGetValue(r.Title.Trim(), out var id))
            {
                missing++;
                if (r.Enabled) Log.Warn("Награды", $"«{r.Title}» не найдена на канале — нажми «Создать награды на Twitch»");
                continue;
            }
            try
            {
                await api.UpdateRewardAsync(id, r, ct);
                updated++;
                Log.Ok("Награды", $"Обновлена «{r.Title}»: {r.Cost} баллов");
            }
            catch (OperationCanceledException) { throw; }
            catch (TwitchApiException ex) when (ex.Status == 403)
            {
                failed++;
                Log.Error("Награды", $"«{r.Title}»: Twitch не даёт менять награду, созданную не этой программой. Удали её на Twitch и создай заново кнопкой «Создать награды на Twitch».");
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error("Награды", $"«{r.Title}»: {ex.Message}");
            }
        }
        Log.Ok("Награды", $"Готово: обновлено {updated}, не найдено на канале {missing}, ошибок {failed}");
        return (updated, missing, failed);
    }

    /// <summary>
    /// show=true (при открытии): создаёт недостающие награды и включает/обновляет существующие.
    /// show=false (при закрытии): скрывает награды программы на Twitch. Без входа в Twitch ничего не делает при закрытии.
    /// </summary>
    public async Task<(int Done, int Failed)> SyncRewardsAsync(bool show, CancellationToken ct)
    {
        var tw = Settings.Twitch;
        if (string.IsNullOrWhiteSpace(tw.ClientId) || string.IsNullOrWhiteSpace(tw.ClientSecret)) return (0, 0);

        var auth = new TwitchAuth(Http, tw) { AllowLogin = show };
        if (!show && !auth.IsLoggedIn) return (0, 0);
        await auth.GetTokenAsync(ct);
        var api = new TwitchApi(Http, auth);

        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await api.ListRewardsAsync(ct)) existing[r.Title.Trim()] = r.Id;

        int done = 0, failed = 0;
        foreach (var r in Settings.Rewards)
        {
            var title = r.Title.Trim();
            try
            {
                if (show)
                {
                    if (existing.TryGetValue(title, out var id)) await api.UpdateRewardAsync(id, r, ct);
                    else if (r.Enabled) await api.CreateRewardAsync(r, ct);
                    else continue;
                }
                else
                {
                    if (!existing.TryGetValue(title, out var id)) continue;
                    await api.SetRewardEnabledAsync(id, false, ct);
                }
                done++;
            }
            catch (OperationCanceledException) { throw; }
            catch (TwitchApiException ex) when (ex.Status == 403)
            {
                failed++;
                Log.Warn("Награды", $"«{r.Title}»: Twitch не даёт менять награду, созданную не этой программой");
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error("Награды", $"«{r.Title}»: {ex.Message}");
            }
        }
        Log.Ok("Награды", show ? $"Награды на Twitch готовы: {done}" + (failed > 0 ? $", ошибок {failed}" : "")
                               : $"Награды скрыты на Twitch: {done}" + (failed > 0 ? $", ошибок {failed}" : ""));
        return (done, failed);
    }
}
