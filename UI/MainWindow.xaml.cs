using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace EtsBot;

public partial class MainWindow : Window
{
    private sealed record Choice(string Value, string Display)
    {
        public override string ToString() => Display;
    }

    private readonly AppSettings _s;
    private readonly BotService _bot;
    private readonly OverlayServer _overlay;
    private readonly ObservableCollection<LogEntry> _entries = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly FrameworkElement[] _pages;
    private FrameworkElement _currentPage;
    private List<Choice> _choices = new();

    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _trayToggle;

    private PanicHotkey? _panic;
    private bool _importing;

    private bool _ready, _reallyClose, _stopped, _loadingRule;
    private int _tick;
    private bool _curShown;

    public MainWindow()
    {
        InitializeComponent();

        _s = AppSettings.Load();
        _bot = new BotService(_s);
        _overlay = new OverlayServer(_s);
        _pages = new FrameworkElement[] { PageHome, PageRewards, PageDonations, PageSettings, PageLog };
        _currentPage = PageHome;

        PageSettings.DataContext = _s;
        PageDonations.DataContext = _s.Donations;
        RewardList.ItemsSource = _s.Rewards;
        RuleList.ItemsSource = _s.Donations.Rules;
        LogList.ItemsSource = _entries;
        TwSecret.Password = _s.Twitch.ClientSecret;
        DaSecret.Password = _s.Donations.ClientSecret;
        VersionText.Text = "v1.0  ·  .NET " + Environment.Version.Major;

        var icon = AppIcon.CreateImageSource();
        Icon = icon;
        TitleIcon.Source = icon;

        Log.Entry += OnLogEntry;
        _bot.StateChanged += () => UI(() => { RefreshStates(); UpdateStartButton(); });
        _bot.Runner.QueueChanged += () => UI(RefreshQueue);
        _bot.Runner.CurrentChanged += () => UI(RefreshCurrent);
        _s.Donations.PropertyChanged += (_, _) => UI(RefreshStates);
        _s.General.PropertyChanged += (_, e) => UI(() =>
        {
            UpdateStatusRight();
            if (e.PropertyName == nameof(GeneralSettings.PanicHotkey)) ApplyPanicHotkey(true);
            if (e.PropertyName is nameof(GeneralSettings.OverlayEnabled) or nameof(GeneralSettings.OverlayPort))
            {
                _overlay.Restart();
                UpdateOverlayInfo();
            }
        });

        _timer.Tick += Timer_Tick;
        _timer.Start();

        SetupTray();
        UpdateAdminInfo();
        RefreshStates();
        RefreshQueue();
        RefreshCurrent();
        RefreshGame();
        UpdateStartButton();
        UpdateStatusRight();

        StateChanged += MainWindow_StateChanged;
        Loaded += MainWindow_Loaded;
        _ready = true;
    }

    private void UI(Action a) => Dispatcher.BeginInvoke(a);

    private Brush B(string key) => (Brush)FindResource(key);

    // ---------------------------------------------------------------- запуск окна

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RootBorder.Opacity = 0;
        RootBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        StartGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(8, 32, TimeSpan.FromSeconds(1.4))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });

        Animate(PageHome);

        _overlay.Restart();
        UpdateOverlayInfo();

        try
        {
            _panic = new PanicHotkey(this, () => UI(async () =>
            {
                await _bot.StopTestsAsync();
                ShowToast("ПАНИКА: всё остановлено, клавиши отпущены, очередь очищена");
                Log.Warn("Паника", "Нажата горячая клавиша аварийной остановки");
            }));
            ApplyPanicHotkey(false);
        }
        catch (Exception ex) { Log.Warn("Паника", "Горячая клавиша недоступна: " + ex.Message); }

        if (string.IsNullOrWhiteSpace(_s.Twitch.ClientId))
        {
            if (!_s.General.WizardDone && !App.NoWizard) UI(OpenWizard);
            else ShowToast("Начни с вкладки «Настройки»: впиши Client ID и Client Secret от Twitch");
        }
        else
        {
            if (_s.General.AutoStart) StartBot();
            if (_s.General.SyncRewards) _ = SyncRewardsOnLaunchAsync();
        }
    }

    private void OpenWizard()
    {
        var w = new WizardWindow(_s, _bot) { Owner = this };
        w.ShowDialog();
        TwSecret.Password = _s.Twitch.ClientSecret;
        DaSecret.Password = _s.Donations.ClientSecret;
        _s.Save();
        RefreshStates();
        UpdateStartButton();
        if (w.Finished && w.StartBotRequested && !_bot.IsRunning) StartBot();
    }

    private void Wizard_Click(object sender, RoutedEventArgs e) => OpenWizard();

    private void ApplyPanicHotkey(bool toast)
    {
        if (_panic == null) return;
        var ok = _panic.Register(_s.General.PanicHotkey, out var err);
        if (!ok)
        {
            Log.Warn("Паника", "Горячая клавиша не включена: " + err);
            if (toast) ShowToast("Горячая клавиша: " + err, true);
        }
        else if (_s.General.PanicHotkey.Length > 0)
        {
            Log.Info("Паника", "Аварийная остановка: " + _s.General.PanicHotkey);
            if (toast) ShowToast("Горячая клавиша «Паника»: " + _s.General.PanicHotkey);
        }
    }

    private async Task SyncRewardsOnLaunchAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var (done, failed) = await _bot.SyncRewardsAsync(true, cts.Token);
            if (done > 0 || failed > 0) ShowToast(failed > 0 ? $"Награды на Twitch: готово {done}, ошибок {failed}" : $"Награды на Twitch включены: {done}", failed > 0);
        }
        catch (OperationCanceledException) { ShowToast("Не удалось войти в Twitch вовремя — награды не обновлены", true); }
        catch (Exception ex) { Log.Error("Награды", ex.Message); ShowToast("Награды на Twitch: " + ex.Message, true); }
    }

    // ---------------------------------------------------------------- навигация и анимации

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var tag = (sender as RadioButton)?.Tag as string;
        FrameworkElement? page = tag switch
        {
            "Home" => PageHome,
            "Rewards" => PageRewards,
            "Donations" => PageDonations,
            "Settings" => PageSettings,
            "Log" => PageLog,
            _ => null,
        };
        if (page != null) ShowPage(page);
    }

    private void ShowPage(FrameworkElement page)
    {
        if (page == _currentPage) return;
        _s.Save();
        foreach (var p in _pages)
            if (p != page) p.Visibility = Visibility.Collapsed;
        _currentPage = page;
        Animate(page);

        if (page == PageDonations) RefreshRewardChoices();
        if (page == PageSettings) UpdateAdminInfo();
        if (page == PageLog && _entries.Count > 0) LogList.ScrollIntoView(_entries[^1]);
    }

    private static void Animate(FrameworkElement page)
    {
        page.Visibility = Visibility.Visible;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        if (page.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(320))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void ShowToast(string text, bool error = false)
    {
        ToastText.Text = text;
        Toast.BorderBrush = B(error ? "RedBrush" : "AccentBrush");

        var kf = new DoubleAnimationUsingKeyFrames();
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(4200))));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(4700))));
        Toast.BeginAnimation(OpacityProperty, kf);

        var tt = new TranslateTransform();
        Toast.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(300))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private static void Pulse(Ellipse halo, bool on)
    {
        if (halo.Tag is bool cur && cur == on) return;
        halo.Tag = on;
        halo.RenderTransformOrigin = new Point(0.5, 0.5);
        if (halo.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(1, 1);
            halo.RenderTransform = st;
        }
        if (on)
        {
            var grow = new DoubleAnimation(1, 2.6, TimeSpan.FromSeconds(1.5)) { RepeatBehavior = RepeatBehavior.Forever };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            halo.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(1.5)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            halo.BeginAnimation(OpacityProperty, null);
            halo.Opacity = 0;
        }
    }

    // ---------------------------------------------------------------- состояния

    private void SetCard(Ellipse dot, Ellipse halo, TextBlock state, TextBlock detail,
                         string text, string detailText, string brushKey, bool pulse)
    {
        var brush = B(brushKey);
        dot.Fill = brush;
        halo.Fill = brush;
        state.Text = text;
        detail.Text = detailText;
        Pulse(halo, pulse);
    }

    private static string StateText(ConnState s) => s switch
    {
        ConnState.Connecting => "Подключение…",
        ConnState.On => "Подключено",
        ConnState.Error => "Ошибка",
        _ => "Не подключено",
    };

    private static string StateBrush(ConnState s) => s switch
    {
        ConnState.Connecting => "YellowBrush",
        ConnState.On => "GreenBrush",
        ConnState.Error => "RedBrush",
        _ => "MutedBrush",
    };

    private void RefreshStates()
    {
        var tw = _bot.TwitchState;
        string twDetail = tw == ConnState.Error ? (_bot.TwitchError ?? "")
            : _bot.TwitchLogin != null ? "Канал: " + _bot.TwitchLogin
            : _bot.IsRunning ? "" : "Нажми «Запустить»";
        SetCard(TwDot, TwHalo, TwState, TwDetail, StateText(tw), twDetail, StateBrush(tw),
            tw == ConnState.On || tw == ConnState.Connecting);

        if (!_s.Donations.Enabled)
        {
            SetCard(DaDot, DaHalo, DaState, DaDetail, "Выключено", "Включи во вкладке «Донаты»", "MutedBrush", false);
        }
        else
        {
            var da = _bot.DonationState;
            string daDetail = da == ConnState.Error ? (_bot.DonationError ?? "")
                : _bot.IsRunning ? "" : "Нажми «Запустить»";
            SetCard(DaDot, DaHalo, DaState, DaDetail, StateText(da), daDetail, StateBrush(da),
                da == ConnState.On || da == ConnState.Connecting);
        }
    }

    private void RefreshGame()
    {
        var g = _s.General;
        if (!g.RequireGameFocus)
        {
            SetCard(GameDot, GameHalo, GameState, GameDetail, "Проверка выключена", "Клавиши жмутся в любое окно", "YellowBrush", false);
            return;
        }
        var title = KeySender.ForegroundTitle();
        if (!string.IsNullOrWhiteSpace(g.GameWindowTitle) && title.Contains(g.GameWindowTitle, StringComparison.OrdinalIgnoreCase))
            SetCard(GameDot, GameHalo, GameState, GameDetail, "В фокусе", "Окно игры активно", "GreenBrush", true);
        else
            SetCard(GameDot, GameHalo, GameState, GameDetail, "Не в фокусе",
                title.Length > 0 ? "Сейчас активно: " + title : "Окно игры не активно", "MutedBrush", false);
    }

    private void RefreshQueue()
    {
        var items = _bot.Runner.Snapshot();
        QueueList.ItemsSource = items;
        QueueEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        QueueTitle.Text = items.Length == 0 ? "Очередь" : $"Очередь ({items.Length})";
    }

    private void RefreshCurrent()
    {
        var cur = _bot.Runner.Current;
        if (cur == null)
        {
            _curShown = false;
            CurPanel.Visibility = Visibility.Collapsed;
            CurIdle.Visibility = Visibility.Visible;
            ProgressScale.ScaleX = 0;
            return;
        }
        CurIdle.Visibility = Visibility.Collapsed;
        CurPanel.Visibility = Visibility.Visible;
        CurTitle.Text = cur.Display;
        CurUser.Text = (cur.User.Length > 0 ? cur.User + "  ·  " : "") + cur.SourceText;
        if (!_curShown)
        {
            _curShown = true;
            CurPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _tick++;

        var cur = _bot.Runner.Current;
        if (cur != null)
        {
            double dur = _bot.Runner.CurrentDuration;
            double el = (DateTime.UtcNow - _bot.Runner.CurrentStartUtc).TotalSeconds;
            if (dur <= 0)
            {
                ProgressScale.ScaleX = 0;
                CurTime.Text = "Подготовка…";
            }
            else
            {
                ProgressScale.ScaleX = Math.Clamp(el / dur, 0, 1);
                CurTime.Text = $"Осталось {Math.Max(0, dur - el):0.0} сек";
            }
        }

        if (_tick % 20 == 0) RefreshGame();

        // раз в 30 секунд подстраховочное сохранение при работе в редакторах
        if (_tick % 600 == 0 && _currentPage != PageHome && _currentPage != PageLog) _s.Save();
    }

    private void UpdateStartButton()
    {
        bool r = _bot.IsRunning;
        StartText.Text = r ? "■  Остановить" : "▶  Запустить";
        StartBtn.Style = (Style)FindResource(r ? "DangerButton" : "PrimaryButton");
        StartGlow.Color = r ? Color.FromRgb(0xFF, 0x54, 0x70) : Color.FromRgb(0x7C, 0x5C, 0xFF);
        RunHint.Text = r
            ? (_s.General.DryRun
                ? "Работает в ТЕСТОВОМ режиме: клавиши не нажимаются, всё пишется в журнал."
                : "Бот работает. Переключись в игру — награды и донаты будут срабатывать.")
            : "Нажми «Запустить», затем переключись в игру.";
        if (_trayToggle != null) _trayToggle.Text = r ? "Остановить" : "Запустить";
    }

    private void UpdateStatusRight()
    {
        var parts = new List<string>();
        if (IsAdmin()) parts.Add("Администратор");
        if (_s.General.DryRun) parts.Add("ТЕСТОВЫЙ РЕЖИМ");
        StatusRight.Text = string.Join("   ·   ", parts);
    }

    private static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private void UpdateAdminInfo()
    {
        bool admin = IsAdmin();
        AdminInfo.Text = admin
            ? "Программа запущена от администратора — подойдёт и для игры, запущенной от администратора."
            : "Если ETS2 запущена от администратора, а программа нет — Windows не пропустит нажатия клавиш. В этом случае перезапусти программу от администратора кнопкой ниже.";
        AdminBtn.IsEnabled = !admin;
    }

    // ---------------------------------------------------------------- журнал

    private void OnLogEntry(LogEntry e)
    {
        UI(() =>
        {
            _entries.Add(e);
            while (_entries.Count > 600) _entries.RemoveAt(0);
            StatusText.Text = $"{e.TimeText}   {e.Source}: {e.Message}";
            if (_currentPage == PageLog) LogList.ScrollIntoView(e);
        });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _entries.Clear();

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(Paths.LogFile))
                Process.Start(new ProcessStartInfo(Paths.LogFile) { UseShellExecute = true });
            else ShowToast("Файл журнала ещё не создан");
        }
        catch (Exception ex) { ShowToast("Не удалось открыть: " + ex.Message, true); }
    }

    // ---------------------------------------------------------------- главная

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        StartBtn.IsEnabled = false;
        try
        {
            if (_bot.IsRunning) await _bot.StopAsync();
            else StartBot();
        }
        finally
        {
            StartBtn.IsEnabled = true;
            UpdateStartButton();
        }
    }

    private void StartBot()
    {
        _s.Save();
        _bot.Start();
        UpdateStartButton();
    }

    private async void EmergencyStop_Click(object sender, RoutedEventArgs e)
    {
        await _bot.StopTestsAsync();
        ShowToast("Всё остановлено, клавиши отпущены, очередь очищена");
    }

    private async void CreateRewards_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;
        try
        {
            _s.Save();
            ShowToast("Если нужно — откроется браузер для входа в Twitch…");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var (created, skipped, failed) = await _bot.CreateRewardsAsync(cts.Token);
            ShowToast($"Награды: создано {created}, уже было {skipped}, ошибок {failed}", failed > 0);
        }
        catch (OperationCanceledException) { ShowToast("Время ожидания входа истекло", true); }
        catch (Exception ex)
        {
            Log.Error("Награды", ex.Message);
            ShowToast(ex.Message, true);
        }
        finally { if (btn != null) btn.IsEnabled = true; }
    }

    private async void UpdateRewards_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null) btn.IsEnabled = false;
        try
        {
            _s.Save();
            ShowToast("Отправляю цены на Twitch… если нужно, откроется браузер для входа");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var (updated, missing, failed) = await _bot.UpdateRewardsAsync(cts.Token);
            ShowToast($"Цены обновлены: {updated}, нет на канале {missing}, ошибок {failed}", failed > 0);
        }
        catch (OperationCanceledException) { ShowToast("Время ожидания входа истекло", true); }
        catch (Exception ex)
        {
            Log.Error("Награды", ex.Message);
            ShowToast(ex.Message, true);
        }
        finally { if (btn != null) btn.IsEnabled = true; }
    }

    // ---------------------------------------------------------------- награды

    private void UpdateStepsInfo(string? text, TextBlock info)
    {
        var p = ActionScript.Parse(text, _s.KeyExists);
        if (!p.Ok)
        {
            info.Text = "⚠ " + string.Join("\n⚠ ", p.Errors.Take(4));
            info.Foreground = B("RedBrush");
        }
        else if (p.Steps.Count == 0)
        {
            info.Text = "Действий нет";
            info.Foreground = B("MutedBrush");
        }
        else
        {
            info.Text = $"✓ Всё верно · длительность ≈ {ActionScript.Estimate(p.Steps, _s.General.TapSeconds):0.0} сек";
            info.Foreground = B("GreenBrush");
        }
    }

    private void RewardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool has = RewardList.SelectedItem is RewardDef;
        RewardEditor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        RewardEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (RewardList.SelectedItem is RewardDef r)
        {
            UpdateStepsInfo(r.Steps, RewardStepsInfo);
            RewardEditor.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }
    }

    private void RewardSteps_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateStepsInfo(RewardSteps.Text, RewardStepsInfo);

    private void AddReward_Click(object sender, RoutedEventArgs e)
    {
        var r = new RewardDef { Title = "Новая награда " + (_s.Rewards.Count + 1) };
        _s.Rewards.Add(r);
        RewardList.SelectedItem = r;
        RewardList.ScrollIntoView(r);
    }

    private void DuplicateReward_Click(object sender, RoutedEventArgs e)
    {
        if (RewardList.SelectedItem is not RewardDef r) return;
        var c = r.Clone();
        _s.Rewards.Add(c);
        RewardList.SelectedItem = c;
        RewardList.ScrollIntoView(c);
    }

    private void DeleteReward_Click(object sender, RoutedEventArgs e)
    {
        if (RewardList.SelectedItem is not RewardDef r) return;
        if (MessageBox.Show($"Удалить награду «{r.Title}» из программы?\n(На Twitch она не удалится.)", "Удаление",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int i = _s.Rewards.IndexOf(r);
        _s.Rewards.Remove(r);
        if (_s.Rewards.Count > 0) RewardList.SelectedIndex = Math.Min(i, _s.Rewards.Count - 1);
    }

    private async void TestReward_Click(object sender, RoutedEventArgs e)
    {
        if (RewardList.SelectedItem is not RewardDef r) return;
        var err = await _bot.TestStepsAsync(r.Title, r.Steps);
        if (err != null) ShowToast(err, true);
        else ShowToast(_s.General.DryRun
            ? "Тестовый режим: действие только запишется в журнал"
            : "Через 3 секунды начнётся — переключись в игру!");
    }

    // ---------------------------------------------------------------- донаты

    private void RefreshRewardChoices()
    {
        _choices = new List<Choice> { new("", "— свои шаги —") };
        _choices.AddRange(_s.Rewards.Select(r => new Choice(r.Title, r.Title)));
        _loadingRule = true;
        RuleRewardBox.DisplayMemberPath = "Display";
        RuleRewardBox.ItemsSource = _choices;
        _loadingRule = false;
        if (RuleList.SelectedItem is DonationRule rule) SyncRuleCombo(rule);
    }

    private void SyncRuleCombo(DonationRule rule)
    {
        _loadingRule = true;
        RuleRewardBox.SelectedItem = _choices.FirstOrDefault(c =>
            string.Equals(c.Value, rule.Reward ?? "", StringComparison.OrdinalIgnoreCase));
        _loadingRule = false;
    }

    private void RuleRewardBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingRule || RuleList.SelectedItem is not DonationRule rule) return;
        if (RuleRewardBox.SelectedItem is Choice c)
        {
            rule.Reward = c.Value;
            UpdateRuleInfo(rule);
        }
    }

    private void UpdateRuleInfo(DonationRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Reward))
        {
            var rw = _s.FindReward(rule.Reward);
            if (rw == null)
            {
                RuleStepsInfo.Text = $"⚠ Награды «{rule.Reward}» нет в списке — правило не сработает";
                RuleStepsInfo.Foreground = B("RedBrush");
            }
            else
            {
                RuleStepsInfo.Text = $"Используется действие награды «{rw.Title}», свои шаги игнорируются";
                RuleStepsInfo.Foreground = B("MutedBrush");
            }
        }
        else UpdateStepsInfo(rule.Steps, RuleStepsInfo);
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool has = RuleList.SelectedItem is DonationRule;
        RuleEditor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        RuleEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (RuleList.SelectedItem is DonationRule rule)
        {
            if (_choices.Count == 0) RefreshRewardChoices();
            SyncRuleCombo(rule);
            UpdateRuleInfo(rule);
            RuleEditor.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }
    }

    private void RuleSteps_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (RuleList.SelectedItem is DonationRule rule && string.IsNullOrWhiteSpace(rule.Reward))
            UpdateStepsInfo(RuleSteps.Text, RuleStepsInfo);
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var r = new DonationRule { Name = "Новое правило " + (_s.Donations.Rules.Count + 1), MinAmount = 50, Steps = "press horn" };
        _s.Donations.Rules.Add(r);
        RuleList.SelectedItem = r;
        RuleList.ScrollIntoView(r);
    }

    private void DuplicateRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not DonationRule r) return;
        var c = r.Clone();
        _s.Donations.Rules.Add(c);
        RuleList.SelectedItem = c;
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not DonationRule r) return;
        if (MessageBox.Show($"Удалить правило «{r.Name}»?", "Удаление",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int i = _s.Donations.Rules.IndexOf(r);
        _s.Donations.Rules.Remove(r);
        if (_s.Donations.Rules.Count > 0) RuleList.SelectedIndex = Math.Min(i, _s.Donations.Rules.Count - 1);
    }

    private void MoveRuleUp_Click(object sender, RoutedEventArgs e) => MoveRule(-1);
    private void MoveRuleDown_Click(object sender, RoutedEventArgs e) => MoveRule(1);

    private void MoveRule(int delta)
    {
        if (RuleList.SelectedItem is not DonationRule r) return;
        int i = _s.Donations.Rules.IndexOf(r);
        int j = i + delta;
        if (j < 0 || j >= _s.Donations.Rules.Count) return;
        _s.Donations.Rules.Move(i, j);
        RuleList.SelectedItem = r;
    }

    private async void TestRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not DonationRule rule) return;
        string steps = rule.Steps;
        string title = rule.Name;
        if (!string.IsNullOrWhiteSpace(rule.Reward))
        {
            var rw = _s.FindReward(rule.Reward);
            if (rw == null) { ShowToast($"Награды «{rule.Reward}» нет в списке", true); return; }
            steps = rw.Steps;
            title = rw.Title;
        }
        var err = await _bot.TestStepsAsync(title, steps);
        if (err != null) ShowToast(err, true);
        else ShowToast(_s.General.DryRun ? "Тестовый режим: действие только запишется в журнал"
                                         : "Через 3 секунды начнётся — переключись в игру!");
    }

    private DonationEvent? ReadTestDonation()
    {
        if (!double.TryParse(TestAmount.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            TestResult.Text = "⚠ Сумма должна быть числом";
            TestResult.Foreground = B("RedBrush");
            return null;
        }
        return new DonationEvent("test-" + Guid.NewGuid().ToString("N")[..6],
            TestUser.Text.Trim().Length > 0 ? TestUser.Text.Trim() : "Зритель",
            TestMessage.Text, amount, TestCurrency.Text.Trim().ToUpperInvariant());
    }

    private void CheckDonation_Click(object sender, RoutedEventArgs e)
    {
        var d = ReadTestDonation();
        if (d == null) return;
        var m = DonationMatcher.Match(_s, d);
        if (m == null)
        {
            TestResult.Text = "Ни одно правило не подошло — донат ничего не сделает";
            TestResult.Foreground = B("YellowBrush");
        }
        else if (m.Error != null)
        {
            TestResult.Text = $"⚠ Правило «{m.RuleName}»: {m.Error}";
            TestResult.Foreground = B("RedBrush");
        }
        else
        {
            double sec = ActionScript.Estimate(m.Steps, _s.General.TapSeconds) * m.Times;
            TestResult.Text = $"✓ Сработает правило «{m.RuleName}» → {m.Title}" + (m.Times > 1 ? $" ×{m.Times}" : "")
                              + $" · ≈ {sec:0.0} сек · сумма {m.AmountInBase:0.##} {_s.Donations.BaseCurrency}";
            TestResult.Foreground = B("GreenBrush");
        }
    }

    private async void SimulateDonation_Click(object sender, RoutedEventArgs e)
    {
        var d = ReadTestDonation();
        if (d == null) return;
        await _bot.SimulateDonationAsync(d);
        ShowToast(_s.General.DryRun ? "Донат добавлен (тестовый режим — только журнал)"
                                    : "Донат в очереди. Переключись в игру!");
    }

    // ---------------------------------------------------------------- настройки

    private void TwSecret_PasswordChanged(object sender, RoutedEventArgs e) => _s.Twitch.ClientSecret = TwSecret.Password;
    private void DaSecret_PasswordChanged(object sender, RoutedEventArgs e) => _s.Donations.ClientSecret = DaSecret.Password;

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* ignore */ }
    }

    private void OpenTwitchConsole_Click(object sender, RoutedEventArgs e) => OpenUrl("https://dev.twitch.tv/console/apps");
    private void OpenDaConsole_Click(object sender, RoutedEventArgs e) => OpenUrl("https://www.donationalerts.com/application/clients");

    private void LogoutTwitch_Click(object sender, RoutedEventArgs e)
    {
        _bot.LogoutTwitch();
        ShowToast("Вышел из Twitch. При следующем запуске нужно войти заново.");
    }

    private void LogoutDa_Click(object sender, RoutedEventArgs e)
    {
        _bot.LogoutDonations();
        ShowToast("Вышел из DonationAlerts. При следующем запуске нужно войти заново.");
    }

    /// <summary>Режим --shots: сохраняет страницы окна в PNG (2x) в папку dir.</summary>
    public async System.Threading.Tasks.Task CaptureShotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        await System.Threading.Tasks.Task.Delay(1800);
        Toast.Visibility = Visibility.Collapsed;
        string P(string n) => Path.Combine(dir, n + ".png");

        await ShotHelper.Snap(RootBorder, P("home"));

        NavRewards.IsChecked = true;
        await System.Threading.Tasks.Task.Delay(500);
        RewardList.SelectedIndex = 0;
        await ShotHelper.Snap(RootBorder, P("rewards"));

        NavDonations.IsChecked = true;
        await System.Threading.Tasks.Task.Delay(500);
        RuleList.SelectedIndex = 0;
        await ShotHelper.Snap(RootBorder, P("donations"));

        NavSettings.IsChecked = true;
        await ShotHelper.Snap(RootBorder, P("settings_top"));

        SettingsScroll.UpdateLayout();
        double y = OverlayCard.TransformToAncestor(SettingsScroll).Transform(new Point(0, 0)).Y;
        SettingsScroll.ScrollToVerticalOffset(SettingsScroll.VerticalOffset + y - 6);
        await ShotHelper.Snap(RootBorder, P("settings_overlay"), 2.0, 1200);

        NavLog.IsChecked = true;
        await ShotHelper.Snap(RootBorder, P("log"));
    }

    private void UpdateOverlayInfo()
    {
        OverlayUrl.Text = _overlay.Url;
        AlertsUrl.Text = _overlay.AlertsUrl;
        TopUrl.Text = _overlay.TopUrl;
        VoteUrl.Text = _overlay.VoteUrl;
        GoalUrl.Text = _overlay.GoalUrl;
        if (!_s.General.OverlayEnabled)
        {
            OverlayStatus.Text = "Оверлей выключен";
            OverlayStatus.Foreground = B("MutedBrush");
        }
        else if (_overlay.Running)
        {
            OverlayStatus.Text = "● Работает. Ссылка доступна только на этом компьютере.";
            OverlayStatus.Foreground = B("GreenBrush");
        }
        else
        {
            OverlayStatus.Text = "⚠ Не запущен: " + (_overlay.Error ?? "неизвестная ошибка");
            OverlayStatus.Foreground = B("RedBrush");
        }
    }

    private void CopyOverlay_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_overlay.Url); ShowToast("Ссылка скопирована — вставь её в OBS: Источник «Браузер»"); }
        catch (Exception ex) { ShowToast("Не удалось скопировать: " + ex.Message, true); }
    }

    private void CopyAlerts_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_overlay.AlertsUrl); ShowToast("Ссылка на алерты скопирована — OBS: «Браузер», ширина 700, высота 220"); }
        catch (Exception ex) { ShowToast("Не удалось скопировать: " + ex.Message, true); }
    }

    private void CopyTop_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_overlay.TopUrl); ShowToast("Ссылка на топ скопирована — OBS: «Браузер», ширина 500, высота 450. Для всего времени добавь ?mode=all"); }
        catch (Exception ex) { ShowToast("Не удалось скопировать: " + ex.Message, true); }
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        var url = tag == "goal" ? _overlay.GoalUrl : _overlay.VoteUrl;
        try { Clipboard.SetText(url); ShowToast("Ссылка скопирована — вставь её в OBS: Источник «Браузер»"); }
        catch (Exception ex) { ShowToast("Не удалось скопировать: " + ex.Message, true); }
    }

    private async void StartVote_Click(object sender, RoutedEventArgs e)
    {
        var err = await _bot.StartVoteAsync();
        ShowToast(err ?? "Голосование запущено, смотри чат", err != null);
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        var path = _bot.SaveReport();
        if (path == null) { ShowToast("Не удалось сохранить отчёт, подробности в журнале", true); return; }
        OpenUrl(path);
    }

    private void OpenReports_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Paths.ReportsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Paths.ReportsDir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowToast(ex.Message, true); }
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "ets2bot_settings.json",
            Filter = "Настройки (*.json)|*.json",
            Title = "Сохранить настройки",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _s.Save();
            File.Copy(Paths.SettingsFile, dlg.FileName, true);
            ShowToast("Настройки сохранены. Внутри лежат ключи Twitch и DonationAlerts — никому не отправляй файл");
        }
        catch (Exception ex) { ShowToast("Не удалось сохранить: " + ex.Message, true); }
    }

    private async void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Настройки (*.json)|*.json",
            Title = "Загрузить настройки",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var text = File.ReadAllText(dlg.FileName);
            if (AppSettings.Parse(text, out var err) == null)
            {
                ShowToast("Этот файл не подходит: " + err, true);
                return;
            }
            _s.Save();
            File.Copy(Paths.SettingsFile, Paths.SettingsFile + ".backup", true); // на случай, если передумаешь
            File.WriteAllText(Paths.SettingsFile, text);
            _importing = true;

            var exe = Environment.ProcessPath;
            if (exe == null) throw new InvalidOperationException("Не удалось определить путь к программе");
            Process.Start(new ProcessStartInfo(exe, "--restarted") { UseShellExecute = true });
            if (_bot.IsRunning) await _bot.StopAsync();
            _reallyClose = true;
            await Dispatcher.Yield(DispatcherPriority.Background);
            Close();
        }
        catch (Exception ex)
        {
            _importing = false;
            ShowToast("Не удалось загрузить: " + ex.Message, true);
        }
    }

    private void OpenOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (!_overlay.Running) { ShowToast("Оверлей не запущен — включи его и проверь порт", true); return; }
        OpenUrl(_overlay.Url);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Paths.AppDir + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast(ex.Message, true); }
    }

    private async void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (IsAdmin()) { ShowToast("Уже запущено от администратора"); return; }
        try
        {
            _s.Save();
            var exe = Environment.ProcessPath;
            if (exe == null) throw new InvalidOperationException("Не удалось определить путь к программе");
            Process.Start(new ProcessStartInfo(exe, "--restarted") { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception)
        {
            ShowToast("Запуск от администратора отменён");
            return;
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, true);
            return;
        }
        if (_bot.IsRunning) await _bot.StopAsync();
        _reallyClose = true;
        await Dispatcher.Yield(DispatcherPriority.Background);
        Close();
    }

    // ---------------------------------------------------------------- заголовок окна

    private void TitleMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // у развёрнутого окна WindowChrome оставляет невидимую рамку - компенсируем
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (WindowState == WindowState.Minimized && _s.General.MinimizeToTray) HideToTray();
    }

    // ---------------------------------------------------------------- трей

    private void SetupTray()
    {
        try
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = AppIcon.CreateIcon(),
                Text = "ETS2 Twitch Bot",
                Visible = false,
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Открыть", null, (s, e) => UI(RestoreFromTray));
            _trayToggle = new System.Windows.Forms.ToolStripMenuItem("Запустить", null,
                (s, e) => UI(() => StartStop_Click(this, new RoutedEventArgs())));
            menu.Items.Add(_trayToggle);
            menu.Items.Add("Выход", null, (s, e) => UI(() => { _reallyClose = true; Close(); }));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => UI(RestoreFromTray);
        }
        catch (Exception ex) { Log.Warn("Трей", "Не удалось создать значок в трее: " + ex.Message); }
    }

    private void HideToTray()
    {
        if (_tray == null) { WindowState = WindowState.Minimized; return; }
        _tray.Visible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_tray != null) _tray.Visible = false;
    }

    // ---------------------------------------------------------------- закрытие

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_reallyClose && _s.General.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        if (!_stopped)
        {
            e.Cancel = true;
            _stopped = true;
            if (!_importing) _s.Save();
            try
            {
                if (_bot.IsRunning) await _bot.StopAsync();
                else await _bot.StopTestsAsync();
            }
            catch { /* ignore */ }
            if (_s.General.SyncRewards)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _bot.SyncRewardsAsync(false, cts.Token);
                }
                catch { /* нет сети или входа — закрываемся как есть */ }
            }
            _reallyClose = true;
            // Close() нельзя вызывать, пока окно ещё в процессе закрытия — откладываем
            await Dispatcher.Yield(DispatcherPriority.Background);
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer.Stop();
        try { _panic?.Dispose(); } catch { /* ignore */ }
        try { _overlay.Dispose(); } catch { /* ignore */ }
        Log.Entry -= OnLogEntry;
        try { _bot.Keys.ReleaseAll(); } catch { /* ignore */ }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Application.Current.Shutdown();
    }
}
