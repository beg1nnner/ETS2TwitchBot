using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtsBot;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class TwitchSettings : Observable
{
    private string _clientId = "";
    private string _clientSecret = "";
    private string _redirectUri = "http://localhost:3000";

    public string ClientId { get => _clientId; set => Set(ref _clientId, value ?? ""); }
    public string ClientSecret { get => _clientSecret; set => Set(ref _clientSecret, value ?? ""); }
    public string RedirectUri { get => _redirectUri; set => Set(ref _redirectUri, value ?? ""); }
}

public sealed class DonationSettings : Observable
{
    private bool _enabled;
    private string _clientId = "";
    private string _clientSecret = "";
    private string _redirectUri = "http://localhost:3001";
    private string _baseCurrency = "RUB";
    private double _focusWaitSeconds = 30;
    private string _currencyRatesText = "USD=90\nEUR=100\nUAH=2.2\nKZT=0.19\nBYN=30\nBRL=17\nTRY=2.7";

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string ClientId { get => _clientId; set => Set(ref _clientId, value ?? ""); }
    public string ClientSecret { get => _clientSecret; set => Set(ref _clientSecret, value ?? ""); }
    public string RedirectUri { get => _redirectUri; set => Set(ref _redirectUri, value ?? ""); }
    public string BaseCurrency { get => _baseCurrency; set => Set(ref _baseCurrency, (value ?? "RUB").Trim().ToUpperInvariant()); }
    public double FocusWaitSeconds { get => _focusWaitSeconds; set => Set(ref _focusWaitSeconds, value); }
    /// <summary>По строке на валюту: USD=90 (сколько единиц базовой валюты в 1 единице).</summary>
    public string CurrencyRatesText { get => _currencyRatesText; set => Set(ref _currencyRatesText, value ?? ""); }

    public ObservableCollection<DonationRule> Rules { get; set; } = new();
}

public sealed class GeneralSettings : Observable
{
    private string _gameWindowTitle = "Euro Truck Simulator 2";
    private bool _requireGameFocus = true;
    private bool _markFulfilled = true;
    private int _maxQueue = 10;
    private double _tapSeconds = 0.08;
    private bool _dryRun;
    private string _obsTextFile = "last_action.txt";
    private bool _minimizeToTray;
    private bool _autoStart;
    private bool _syncRewards = true;

    public string GameWindowTitle { get => _gameWindowTitle; set => Set(ref _gameWindowTitle, value ?? ""); }
    public bool RequireGameFocus { get => _requireGameFocus; set => Set(ref _requireGameFocus, value); }
    public bool MarkFulfilled { get => _markFulfilled; set => Set(ref _markFulfilled, value); }
    public int MaxQueue { get => _maxQueue; set => Set(ref _maxQueue, value < 1 ? 1 : value); }
    public double TapSeconds { get => _tapSeconds; set => Set(ref _tapSeconds, value < 0.01 ? 0.01 : value); }
    public bool DryRun { get => _dryRun; set => Set(ref _dryRun, value); }
    public string ObsTextFile { get => _obsTextFile; set => Set(ref _obsTextFile, value ?? ""); }
    public bool MinimizeToTray { get => _minimizeToTray; set => Set(ref _minimizeToTray, value); }
    public bool AutoStart { get => _autoStart; set => Set(ref _autoStart, value); }
    /// <summary>При открытии программы создавать/включать награды на Twitch, при закрытии — скрывать.</summary>
    public bool SyncRewards { get => _syncRewards; set => Set(ref _syncRewards, value); }

    // --- пауза, «Паника», чат и события Twitch ---
    private double _gapSeconds = 2;
    private string _panicHotkey = "Ctrl+Alt+X";
    private bool _chatAnnounce;
    private bool _chatCommands;
    private string _subReward = "";
    private string _bitsReward = "";
    private int _bitsMin = 100;
    private string _raidReward = "";
    private int _raidMin = 5;

    /// <summary>Пауза между двумя наградами подряд (секунды), чтобы стример успел «выдохнуть».</summary>
    public double GapSeconds { get => _gapSeconds; set => Set(ref _gapSeconds, value < 0 ? 0 : value > 60 ? 60 : value); }
    /// <summary>Глобальная горячая клавиша аварийной остановки, например Ctrl+Alt+X. Пусто — выключено.</summary>
    public string PanicHotkey { get => _panicHotkey; set => Set(ref _panicHotkey, (value ?? "").Trim()); }
    /// <summary>Писать в чат, когда награда запускается.</summary>
    public bool ChatAnnounce { get => _chatAnnounce; set => Set(ref _chatAnnounce, value); }
    /// <summary>Отвечать на !награды списком наград.</summary>
    public bool ChatCommands { get => _chatCommands; set => Set(ref _chatCommands, value); }
    /// <summary>Название награды, которая запускается при новой подписке (пусто — выкл.).</summary>
    public string SubReward { get => _subReward; set => Set(ref _subReward, (value ?? "").Trim()); }
    public string BitsReward { get => _bitsReward; set => Set(ref _bitsReward, (value ?? "").Trim()); }
    public int BitsMin { get => _bitsMin; set => Set(ref _bitsMin, value < 1 ? 1 : value); }
    public string RaidReward { get => _raidReward; set => Set(ref _raidReward, (value ?? "").Trim()); }
    public int RaidMin { get => _raidMin; set => Set(ref _raidMin, value < 1 ? 1 : value); }

    private bool _wizardDone;
    /// <summary>Мастер первой настройки уже пройден.</summary>
    public bool WizardDone { get => _wizardDone; set => Set(ref _wizardDone, value); }

    // --- рулетка, голосование, дорожание, копилка, чёрный список ---
    private string _rouletteReward = "Рулетка";
    private bool _voteEnabled;
    private int _voteSeconds = 30;
    private int _voteEveryMinutes;
    private int _priceDecayMinutes = 5;
    private string _piggyReward = "Копилка";
    private int _piggyGoal = 3000;
    private string _piggySteps = "repeat 6\n  hold steer_left 0.5\n  hold steer_right 0.5\nend\npress horn 5 0.15";
    private string _blacklist = "";

    /// <summary>Название награды-рулетки: вместо своего действия запускает случайную награду. Пусто — выкл.</summary>
    public string RouletteReward { get => _rouletteReward; set => Set(ref _rouletteReward, (value ?? "").Trim()); }
    /// <summary>Голосование чата (!1 !2 !3). Нужны права на чат.</summary>
    public bool VoteEnabled { get => _voteEnabled; set => Set(ref _voteEnabled, value); }
    public int VoteSeconds { get => _voteSeconds; set => Set(ref _voteSeconds, value < 10 ? 10 : value > 300 ? 300 : value); }
    /// <summary>Как часто бот сам запускает голосование (минуты). 0 — только по кнопке.</summary>
    public int VoteEveryMinutes { get => _voteEveryMinutes; set => Set(ref _voteEveryMinutes, value < 0 ? 0 : value > 600 ? 600 : value); }
    /// <summary>Через сколько минут дорожавшая награда дешевеет на один шаг.</summary>
    public int PriceDecayMinutes { get => _priceDecayMinutes; set => Set(ref _priceDecayMinutes, value < 1 ? 1 : value > 600 ? 600 : value); }
    /// <summary>Награда-вклад в общую копилку (цена награды = сколько баллов кладём). Пусто — выкл.</summary>
    public string PiggyReward { get => _piggyReward; set => Set(ref _piggyReward, (value ?? "").Trim()); }
    public int PiggyGoal { get => _piggyGoal; set => Set(ref _piggyGoal, value < 1 ? 1 : value); }
    /// <summary>Что происходит, когда копилка собрана.</summary>
    public string PiggySteps { get => _piggySteps; set => Set(ref _piggySteps, value ?? ""); }
    /// <summary>Ники через запятую или с новой строки: их награды отменяются, баллы возвращаются.</summary>
    public string Blacklist { get => _blacklist; set => Set(ref _blacklist, value ?? ""); }

    // --- оверлей для OBS (источник «Браузер») ---
    private bool _overlayEnabled = true;
    private int _overlayPort = 3002;
    private string _overlayTitle = "Что можно сделать за донат";
    private int _overlayRows = 6;
    private double _overlaySeconds = 8;

    public bool OverlayEnabled { get => _overlayEnabled; set => Set(ref _overlayEnabled, value); }
    public int OverlayPort { get => _overlayPort; set => Set(ref _overlayPort, value < 1024 ? 1024 : value > 65535 ? 65535 : value); }
    public string OverlayTitle { get => _overlayTitle; set => Set(ref _overlayTitle, value ?? ""); }
    /// <summary>Сколько правил показывать на одной «странице» оверлея.</summary>
    public int OverlayRows { get => _overlayRows; set => Set(ref _overlayRows, value < 1 ? 1 : value > 20 ? 20 : value); }
    /// <summary>Через сколько секунд листать на следующую страницу.</summary>
    public double OverlaySeconds { get => _overlaySeconds; set => Set(ref _overlaySeconds, value < 2 ? 2 : value > 120 ? 120 : value); }
}

public sealed class KeyBinding : Observable
{
    private string _name = "";
    private string _label = "";
    private string _key = "";

    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public string Label { get => _label; set => Set(ref _label, value ?? ""); }
    public string Key { get => _key; set => Set(ref _key, (value ?? "").Trim().ToLowerInvariant()); }
}

public sealed class RewardDef : Observable
{
    private string _title = "Новая награда";
    private int _cost = 100;
    private string _prompt = "";
    private string _color = "";
    private int _cooldown = 10;
    private bool _enabled = true;
    private string _steps = "press horn";
    private bool _inRoulette = true;
    private int _priceStep;

    /// <summary>Может ли награда выпасть в рулетке и попасть в голосование чата.</summary>
    public bool InRoulette { get => _inRoulette; set => Set(ref _inRoulette, value); }
    /// <summary>На сколько баллов награда дорожает после каждой покупки (0 — цена не меняется).</summary>
    public int PriceStep { get => _priceStep; set => Set(ref _priceStep, value < 0 ? 0 : value); }

    public string Title { get => _title; set => Set(ref _title, value ?? ""); }
    public int Cost { get => _cost; set => Set(ref _cost, value < 1 ? 1 : value); }
    public string Prompt { get => _prompt; set => Set(ref _prompt, value ?? ""); }
    public string Color { get => _color; set => Set(ref _color, value ?? ""); }
    public int Cooldown { get => _cooldown; set => Set(ref _cooldown, value < 0 ? 0 : value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>Действие в текстовом виде (см. ActionScript).</summary>
    public string Steps { get => _steps; set => Set(ref _steps, value ?? ""); }

    public RewardDef Clone() => new()
    {
        Title = Title + " (копия)", Cost = Cost, Prompt = Prompt, Color = Color,
        Cooldown = Cooldown, Enabled = Enabled, Steps = Steps, InRoulette = InRoulette, PriceStep = PriceStep
    };
}

public sealed class DonationRule : Observable
{
    private string _name = "Правило";
    private bool _enabled = true;
    private string _keywords = "";
    private double? _minAmount;
    private double? _maxAmount;
    private double? _perAmount;
    private int _maxTimes = 5;
    private string _reward = "";
    private string _steps = "";

    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>Слова через запятую; пусто - любое сообщение.</summary>
    public string Keywords { get => _keywords; set => Set(ref _keywords, value ?? ""); }
    public double? MinAmount { get => _minAmount; set => Set(ref _minAmount, value); }
    public double? MaxAmount { get => _maxAmount; set => Set(ref _maxAmount, value); }
    /// <summary>Повторять действие floor(сумма / PerAmount) раз.</summary>
    public double? PerAmount { get => _perAmount; set => Set(ref _perAmount, value); }
    public int MaxTimes { get => _maxTimes; set => Set(ref _maxTimes, value < 1 ? 1 : value); }
    /// <summary>Название награды из списка (берётся её действие). Пусто - используются свои шаги.</summary>
    public string Reward { get => _reward; set => Set(ref _reward, value ?? ""); }
    public string Steps { get => _steps; set => Set(ref _steps, value ?? ""); }

    private bool _showOnOverlay = true;
    private string _hint = "";
    /// <summary>Показывать правило в оверлее для OBS.</summary>
    public bool ShowOnOverlay { get => _showOnOverlay; set => Set(ref _showOnOverlay, value); }
    /// <summary>Подсказка зрителям. Пусто — берётся описание награды.</summary>
    public string Hint { get => _hint; set => Set(ref _hint, value ?? ""); }

    public DonationRule Clone() => new()
    {
        Name = Name + " (копия)", Enabled = Enabled, Keywords = Keywords, MinAmount = MinAmount,
        MaxAmount = MaxAmount, PerAmount = PerAmount, MaxTimes = MaxTimes, Reward = Reward, Steps = Steps,
        ShowOnOverlay = ShowOnOverlay, Hint = Hint
    };
}

public sealed class AppSettings : Observable
{
    /// <summary>Версия набора настроек по умолчанию: по ней старые settings.json дополняются новыми наградами.</summary>
    public int SettingsVersion { get; set; }

    public TwitchSettings Twitch { get; set; } = new();
    public DonationSettings Donations { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public ObservableCollection<KeyBinding> Keys { get; set; } = new();
    public ObservableCollection<RewardDef> Rewards { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        AppSettings? s = null;
        try
        {
            if (File.Exists(Paths.SettingsFile))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Json);
        }
        catch (Exception ex)
        {
            Log.Error("Настройки", "Не удалось прочитать settings.json: " + ex.Message);
            try { File.Copy(Paths.SettingsFile, Paths.SettingsFile + ".broken", true); } catch { /* ignore */ }
        }

        if (s == null)
        {
            s = Defaults.Create();
            s.Save();
            return s;
        }

        // Добавляем клавиши, которых нет в файле (если в новых версиях появятся новые)
        foreach (var k in Defaults.Keys())
            if (!s.Keys.Any(x => string.Equals(x.Name, k.Name, StringComparison.OrdinalIgnoreCase)))
                s.Keys.Add(k);
        if (Defaults.Migrate(s)) s.Save();
        return s;
    }

    /// <summary>Разбирает текст settings.json (для импорта). null и текст ошибки, если файл не подходит.</summary>
    public static AppSettings? Parse(string text, out string error)
    {
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(text, Json);
            if (s == null || s.Rewards.Count == 0) { error = "в файле нет наград — это не файл настроек программы"; return null; }
            error = "";
            return s;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public void Save()
    {
        try
        {
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Log.Error("Настройки", "Не удалось сохранить settings.json: " + ex.Message);
        }
    }

    public string ResolveKey(string nameOrKey)
    {
        var b = Keys.FirstOrDefault(k => string.Equals(k.Name, nameOrKey, StringComparison.OrdinalIgnoreCase));
        return b != null && b.Key.Length > 0 ? b.Key : nameOrKey.ToLowerInvariant();
    }

    public bool KeyExists(string nameOrKey) => KeySender.IsKnownKey(ResolveKey(nameOrKey));

    public RewardDef? FindReward(string title)
        => Rewards.FirstOrDefault(r => string.Equals(r.Title.Trim(), (title ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Настройки по умолчанию: те же награды, клавиши и правила, что были в Python-версии.</summary>
public static class Defaults
{
    public const int CurrentVersion = 4;
    private const string StallPrompt = "Двигатель глохнет и включается ручной тормоз. Стример должен снять ручник и завести двигатель!";
    private const string StallSteps = "press engine\nwait 0.4\npress handbrake";

    private static IEnumerable<RewardDef> NewRewardsV2() => new[]
    {
        R("Камера туда-обратно", 100, "Камера дважды переключается туда-обратно", "#8E44AD", 30,
            "repeat 2\n  press camera_2\n  wait 1.2\n  press camera_1\n  wait 1.2\nend"),
        R("Обманка с рулём", 150, "Руль чуть уходит влево, а потом резко вправо", "#16A085", 30,
            "hold steer_left 0.15\nhold steer_right 1.2"),
        R("Дёрганый режим", 250, "5 секунд газ-тормоз-газ-тормоз: машина дёргается", "#C0392B", 60,
            "repeat 10\n  hold accelerate 0.25\n  hold brake 0.25\nend"),
        R("Лупа", 200, "Картинка резко приближается на несколько секунд", "#2980B9", 60,
            "scroll up 15 0.02\nwait 5\nscroll down 15 0.02"),
    };

    private static IEnumerable<DonationRule> NewRulesV2() => new[]
    {
        new DonationRule { Name = "Камера туда-обратно", Keywords = "камера, camera", MinAmount = 100, Reward = "Камера туда-обратно" },
        new DonationRule { Name = "Обманка с рулём", Keywords = "обманка, fake", MinAmount = 150, Reward = "Обманка с рулём" },
        new DonationRule { Name = "Дёрганый режим", Keywords = "дёрг, дерг, jerk", MinAmount = 250, Reward = "Дёрганый режим" },
        new DonationRule { Name = "Лупа", Keywords = "лупа, zoom", MinAmount = 200, Reward = "Лупа" },
    };

    // Гудок у машины один, высоту тона не поменять, поэтому «мелодии» узнаются по ритму: короткие/длинные гудки и паузы.
    private static IEnumerable<RewardDef> NewRewardsV3() => new[]
    {
        R("Гудок: Кукарача", 150, "Гудок играет ритм «La Cucaracha»", "#E67E22", 30,
            "repeat 2\n  hold horn 0.12\n  wait 0.08\n  hold horn 0.12\n  wait 0.08\n  hold horn 0.12\n  wait 0.2\n  hold horn 0.4\n  wait 0.15\n  hold horn 0.7\n  wait 0.5\nend"),
        R("Гудок: Имперский марш", 150, "Гудок играет ритм «Имперского марша» из Звёздных войн", "#34495E", 30,
            "hold horn 0.5\nwait 0.12\nhold horn 0.5\nwait 0.12\nhold horn 0.5\nwait 0.12\nhold horn 0.35\nwait 0.08\nhold horn 0.15\nwait 0.12\nhold horn 0.5\nwait 0.12\nhold horn 0.35\nwait 0.08\nhold horn 0.15\nwait 0.12\nhold horn 1.0"),
        R("Гудок: Jingle Bells", 150, "Гудок играет ритм «Jingle Bells»", "#C0392B", 30,
            "repeat 2\n  hold horn 0.2\n  wait 0.1\n  hold horn 0.2\n  wait 0.1\n  hold horn 0.5\n  wait 0.3\nend\nrepeat 4\n  hold horn 0.2\n  wait 0.1\nend\nhold horn 0.9"),
        R("Гудок: С днём рождения", 150, "Гудок играет ритм песни «С днём рождения»", "#E91E63", 30,
            "repeat 2\n  hold horn 0.2\n  wait 0.06\n  hold horn 0.1\n  wait 0.06\n  hold horn 0.4\n  wait 0.08\n  hold horn 0.4\n  wait 0.08\n  hold horn 0.4\n  wait 0.08\n  hold horn 0.8\n  wait 0.5\nend"),
        R("Гудок: тук-тук", 100, "Тра-та-та, та-та… и ответ «та-та»: ритм «Shave and a haircut»", "#27AE60", 20,
            "hold horn 0.18\nwait 0.08\nhold horn 0.1\nwait 0.08\nhold horn 0.1\nwait 0.08\nhold horn 0.18\nwait 0.08\nhold horn 0.18\nwait 0.8\nhold horn 0.18\nwait 0.15\nhold horn 0.18"),
    };

    private static IEnumerable<DonationRule> NewRulesV3() => new[]
    {
        new DonationRule { Name = "Гудок: Кукарача", Keywords = "кукарача, cucaracha", MinAmount = 150, Reward = "Гудок: Кукарача" },
        new DonationRule { Name = "Гудок: Имперский марш", Keywords = "марш, imperial", MinAmount = 150, Reward = "Гудок: Имперский марш" },
        new DonationRule { Name = "Гудок: Jingle Bells", Keywords = "джингл, jingle, новый год", MinAmount = 150, Reward = "Гудок: Jingle Bells" },
        new DonationRule { Name = "Гудок: С днём рождения", Keywords = "день рождения, днём рождения, birthday", MinAmount = 150, Reward = "Гудок: С днём рождения" },
        new DonationRule { Name = "Гудок: тук-тук", Keywords = "тук-тук, тук тук, knock", MinAmount = 100, Reward = "Гудок: тук-тук" },
    };

    private static IEnumerable<RewardDef> NewRewardsV4() => new[]
    {
        R("Рулетка", 200, "Случайная подлянка из списка! Что выпадет — узнаем в игре", "#9146FF", 20, "press horn"),
        R("Копилка", 100, "Вклад в общую копилку: когда зрители накопят цель, стримера ждёт мега-хаос", "#F5A623", 5, "press horn"),
    };

    private static IEnumerable<DonationRule> NewRulesV4() => new[]
    {
        new DonationRule { Name = "Рулетка", Keywords = "рулетка, roulette", MinAmount = 200, Reward = "Рулетка" },
    };

    /// <summary>Дополняет старый settings.json новыми наградами и правилами. true — что-то изменилось.</summary>
    public static bool Migrate(AppSettings s)
    {
        if (s.SettingsVersion >= CurrentVersion) return false;

        if (s.SettingsVersion < 2) MigrateV2(s);
        if (s.SettingsVersion < 3)
        {
            foreach (var r in NewRewardsV3())
                if (s.FindReward(r.Title) == null) s.Rewards.Add(r);
            foreach (var rule in NewRulesV3())
                if (!s.Donations.Rules.Any(x => string.Equals(x.Name, rule.Name, StringComparison.OrdinalIgnoreCase)))
                    s.Donations.Rules.Insert(Math.Max(0, s.Donations.Rules.Count - 3), rule);
        }
        if (s.SettingsVersion < 4)
        {
            foreach (var r in NewRewardsV4())
                if (s.FindReward(r.Title) == null) s.Rewards.Add(r);
            foreach (var rule in NewRulesV4())
                if (!s.Donations.Rules.Any(x => string.Equals(x.Name, rule.Name, StringComparison.OrdinalIgnoreCase)))
                    s.Donations.Rules.Insert(Math.Max(0, s.Donations.Rules.Count - 3), rule);
        }
        s.SettingsVersion = CurrentVersion;
        return true;
    }

    private static void MigrateV2(AppSettings s)
    {
        // «Газ в пол»: 4 сек → 10 сек (только если пользователь не менял действие)
        var gas = s.Rewards.FirstOrDefault(r => r.Title.Trim() == "Газ в пол");
        if (gas != null && gas.Steps.Trim() == "hold accelerate 4")
        {
            gas.Steps = "hold accelerate 10";
            if (gas.Prompt.Contains("4 секунды")) gas.Prompt = "Газ в пол на 10 секунд. Держись!";
        }
        // «Заглушить двигатель»: добавляем ручник (если действие не менялось)
        var stall = s.Rewards.FirstOrDefault(r => r.Title.Trim() == "Заглушить двигатель");
        if (stall != null && stall.Steps.Trim() == "press engine")
        {
            stall.Steps = StallSteps;
            stall.Prompt = StallPrompt;
        }
        foreach (var r in NewRewardsV2())
            if (s.FindReward(r.Title) == null) s.Rewards.Add(r);
        foreach (var rule in NewRulesV2())
            if (!s.Donations.Rules.Any(x => string.Equals(x.Name, rule.Name, StringComparison.OrdinalIgnoreCase)))
                s.Donations.Rules.Insert(Math.Max(0, s.Donations.Rules.Count - 3), rule);
    }

    public static IEnumerable<KeyBinding> Keys() => new[]
    {
        new KeyBinding { Name = "accelerate", Label = "Газ", Key = "w" },
        new KeyBinding { Name = "brake", Label = "Тормоз", Key = "s" },
        new KeyBinding { Name = "steer_left", Label = "Руль влево", Key = "a" },
        new KeyBinding { Name = "steer_right", Label = "Руль вправо", Key = "d" },
        new KeyBinding { Name = "handbrake", Label = "Ручной тормоз", Key = "space" },
        new KeyBinding { Name = "horn", Label = "Гудок", Key = "h" },
        new KeyBinding { Name = "engine", Label = "Двигатель", Key = "e" },
        new KeyBinding { Name = "lights", Label = "Фары", Key = "l" },
        new KeyBinding { Name = "high_beam", Label = "Дальний свет", Key = "k" },
        new KeyBinding { Name = "wipers", Label = "Дворники", Key = "p" },
        new KeyBinding { Name = "blinker_left", Label = "Левый поворотник", Key = "[" },
        new KeyBinding { Name = "blinker_right", Label = "Правый поворотник", Key = "]" },
        new KeyBinding { Name = "hazard", Label = "Аварийка", Key = "f" },
        new KeyBinding { Name = "cruise", Label = "Круиз-контроль", Key = "c" },
        new KeyBinding { Name = "camera_1", Label = "Камера 1", Key = "1" },
        new KeyBinding { Name = "camera_2", Label = "Камера 2", Key = "2" },
        new KeyBinding { Name = "camera_3", Label = "Камера 3", Key = "3" },
    };

    private static RewardDef R(string title, int cost, string prompt, string color, int cooldown, string steps)
        => new() { Title = title, Cost = cost, Prompt = prompt, Color = color, Cooldown = cooldown, Steps = steps };

    public static AppSettings Create()
    {
        var s = new AppSettings { SettingsVersion = CurrentVersion };
        foreach (var k in Keys()) s.Keys.Add(k);

        s.Rewards.Add(R("Руль влево", 100, "Руль плавно уходит влево на 1.5 секунды", "#3498DB", 10, "hold steer_left 1.5"));
        s.Rewards.Add(R("Руль вправо", 100, "Руль плавно уходит вправо на 1.5 секунды", "#E67E22", 10, "hold steer_right 1.5"));
        s.Rewards.Add(R("Тормоз", 150, "Стример давит на тормоз 3 секунды", "#E74C3C", 20, "hold brake 3"));
        s.Rewards.Add(R("Газ в пол", 200, "Газ в пол на 10 секунд. Держись!", "#2ECC71", 30, "hold accelerate 10"));
        s.Rewards.Add(R("Ручник", 250, "Ручной тормоз на 2 секунды", "#9B59B6", 30, "hold handbrake 2"));
        s.Rewards.Add(R("Змейка", 300, "Руль мотает влево-вправо", "#F1C40F", 60,
            "repeat 4\n  hold steer_left 0.6\n  hold steer_right 0.6\nend"));
        s.Rewards.Add(R("Посигналь!", 50, "Три громких гудка", "#1ABC9C", 5, "press horn 3 0.25"));
        s.Rewards.Add(R("Аварийка", 50, "Включить/выключить аварийную сигнализацию", "", 10, "press hazard"));
        s.Rewards.Add(R("Левый поворотник", 50, "Включить/выключить левый поворотник", "", 10, "press blinker_left"));
        s.Rewards.Add(R("Правый поворотник", 50, "Включить/выключить правый поворотник", "", 10, "press blinker_right"));
        s.Rewards.Add(R("Дальний свет", 50, "Переключить дальний свет", "", 10, "press high_beam"));
        s.Rewards.Add(R("Дворники", 50, "Переключить дворники", "", 10, "press wipers"));
        s.Rewards.Add(R("Сменить камеру", 100, "Случайный вид: салон / снаружи", "", 30, "press camera_2\nwait 4\npress camera_1"));
        s.Rewards.Add(R("Заглушить двигатель", 500, StallPrompt, "#000000", 120, StallSteps));
        foreach (var r in NewRewardsV2()) s.Rewards.Add(r);
        foreach (var r in NewRewardsV3()) s.Rewards.Add(r);
        foreach (var r in NewRewardsV4()) s.Rewards.Add(r);

        var d = s.Donations;
        d.Rules.Add(new DonationRule { Name = "Руль влево", Keywords = "влево, left", MinAmount = 50, Reward = "Руль влево" });
        d.Rules.Add(new DonationRule { Name = "Руль вправо", Keywords = "вправо, right", MinAmount = 50, Reward = "Руль вправо" });
        d.Rules.Add(new DonationRule { Name = "Тормоз", Keywords = "тормоз, brake", MinAmount = 100, Reward = "Тормоз" });
        d.Rules.Add(new DonationRule { Name = "Газ", Keywords = "газ, gas", MinAmount = 100, Reward = "Газ в пол" });
        d.Rules.Add(new DonationRule { Name = "Ручник", Keywords = "ручник", MinAmount = 150, Reward = "Ручник" });
        d.Rules.Add(new DonationRule { Name = "Змейка", Keywords = "змейка, snake", MinAmount = 200, Reward = "Змейка" });
        d.Rules.Add(new DonationRule { Name = "Заглушить двигатель", Keywords = "заглуши", MinAmount = 500, Reward = "Заглушить двигатель" });
        d.Rules.Add(new DonationRule
        {
            Name = "Гудки (каждые 30 ₽ = 1 гудок)", Keywords = "сигнал, гудок, beep",
            MinAmount = 30, PerAmount = 30, MaxTimes = 5, Reward = "Посигналь!"
        });
        d.Rules.Add(new DonationRule
        {
            Name = "Мега-хаос от 1000", MinAmount = 1000,
            Steps = "repeat 6\n  hold steer_left 0.5\n  hold steer_right 0.5\nend\npress horn 5 0.15"
        });
        d.Rules.Add(new DonationRule { Name = "Мелкий донат - гудок", MinAmount = 20, MaxAmount = 99, Reward = "Посигналь!" });
        foreach (var r in NewRulesV2()) d.Rules.Insert(Math.Max(0, d.Rules.Count - 3), r);
        foreach (var r in NewRulesV3()) d.Rules.Insert(Math.Max(0, d.Rules.Count - 3), r);
        foreach (var r in NewRulesV4()) d.Rules.Insert(Math.Max(0, d.Rules.Count - 3), r);
        return s;
    }
}
