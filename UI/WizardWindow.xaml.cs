using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace EtsBot;

/// <summary>Мастер первой настройки: Twitch → награды → донаты → клавиши → проверка.</summary>
public partial class WizardWindow : Window
{
    private readonly AppSettings _s;
    private readonly BotService _bot;
    private readonly FrameworkElement[] _steps;
    private int _step;
    private bool _busy;

    public bool Finished { get; private set; }
    public bool StartBotRequested { get; private set; }

    public WizardWindow(AppSettings s, BotService bot)
    {
        InitializeComponent();
        _s = s;
        _bot = bot;
        DataContext = s;
        Icon = AppIcon.CreateImageSource();
        _steps = new FrameworkElement[] { Step0, Step1, Step2, Step3, Step4, Step5 };

        TwSecret.Password = s.Twitch.ClientSecret;
        DaSecret.Password = s.Donations.ClientSecret;

        var names = new[] { "accelerate", "brake", "steer_left", "steer_right", "handbrake", "horn", "engine", "camera_1", "camera_2" };
        KeyList.ItemsSource = s.Keys.Where(k => names.Contains(k.Name)).ToList();

        GoTo(0);
    }

    private void GoTo(int i)
    {
        _step = Math.Clamp(i, 0, _steps.Length - 1);
        for (int k = 0; k < _steps.Length; k++)
            _steps[k].Visibility = k == _step ? Visibility.Visible : Visibility.Collapsed;
        StepLabel.Text = $"Шаг {_step + 1} из {_steps.Length}";
        BackBtn.IsEnabled = _step > 0;
        NextBtn.Content = _step == _steps.Length - 1 ? "Готово" : "Далее";
        Hint.Text = "";
    }

    private bool TwitchFilled()
        => !string.IsNullOrWhiteSpace(_s.Twitch.ClientId) && !string.IsNullOrWhiteSpace(_s.Twitch.ClientSecret);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_step == 1 && !TwitchFilled())
        {
            Hint.Text = "Впиши Client ID и Client Secret";
            return;
        }
        if (_step == _steps.Length - 1)
        {
            _s.General.WizardDone = true;
            _s.Save();
            Finished = true;
            StartBotRequested = StartCheck.IsChecked == true;
            Close();
            return;
        }
        GoTo(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy) GoTo(_step - 1);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void TwSecret_PasswordChanged(object sender, RoutedEventArgs e) => _s.Twitch.ClientSecret = TwSecret.Password;
    private void DaSecret_PasswordChanged(object sender, RoutedEventArgs e) => _s.Donations.ClientSecret = DaSecret.Password;

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* ignore */ }
    }

    private void OpenTwitch_Click(object sender, RoutedEventArgs e) => OpenUrl("https://dev.twitch.tv/console/apps");
    private void OpenDa_Click(object sender, RoutedEventArgs e) => OpenUrl("https://www.donationalerts.com/application/clients");

    private void CopyRedirect_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_s.Twitch.RedirectUri); Hint.Text = ""; }
        catch (Exception ex) { Hint.Text = "Не удалось скопировать: " + ex.Message; }
    }

    private async void CreateRewards_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!TwitchFilled())
        {
            RewardsStatus.Text = "Сначала впиши Client ID и Client Secret на прошлом шаге.";
            return;
        }
        _busy = true;
        CreateBtn.IsEnabled = false;
        RewardsStatus.Text = "Открываю браузер для входа в Twitch… нажми там «Авторизовать».";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var (created, skipped, failed) = await _bot.CreateRewardsAsync(cts.Token);
            RewardsStatus.Text = $"Готово: создано {created}, уже было {skipped}"
                + (failed > 0 ? $", ошибок {failed} (подробности во вкладке «Журнал»)" : ".");
        }
        catch (Exception ex) { RewardsStatus.Text = "Не получилось: " + ex.Message; }
        finally
        {
            _busy = false;
            CreateBtn.IsEnabled = true;
        }
    }

    private async void TestHorn_Click(object sender, RoutedEventArgs e)
    {
        var err = await _bot.TestStepsAsync("Проверка гудка", "press horn 2 0.3");
        TestStatus.Text = err ?? "Через 3 секунды машина дважды посигналит — переключись в игру.";
    }
}
