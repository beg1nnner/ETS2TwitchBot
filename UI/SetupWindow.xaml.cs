using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace EtsBot;

public partial class SetupWindow : Window
{
    private bool _installing, _done;
    private string? _installedExe;

    public SetupWindow()
    {
        InitializeComponent();
        PathBox.Text = Installer.DefaultDir;
        Icon = AppIcon.CreateImageSource();
        RefreshMode();

        Loaded += (s, e) =>
        {
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
            if (Body.RenderTransform is System.Windows.Media.TranslateTransform tt)
                tt.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                    new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(420))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
    }

    private void RefreshMode()
    {
        bool upd = Installer.IsInstalled(PathBox.Text);
        MainBtn.Content = upd ? "Обновить" : "Установить";
        HeadSub.Text = upd ? "Уже установлена — будет обновлена, настройки сохранятся"
                           : "Награды Twitch и донаты управляют Euro Truck Simulator 2";
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var d = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выбери папку, куда установить программу",
            SelectedPath = PathBox.Text,
            ShowNewFolderButton = true,
        };
        if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var p = d.SelectedPath;
            // чтобы не раскидывать файлы по корню диска/папке с чужими файлами
            if (Directory.Exists(p) && Directory.GetFileSystemEntries(p).Length > 0 && !Installer.IsInstalled(p))
                p = Path.Combine(p, Installer.AppName);
            PathBox.Text = p;
            RefreshMode();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        Close();
    }

    private async void Main_Click(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            if (LaunchCheck.IsChecked == true && _installedExe != null)
            {
                try { Process.Start(new ProcessStartInfo(_installedExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(_installedExe) }); }
                catch (Exception ex) { MessageBox.Show("Не удалось запустить: " + ex.Message, Installer.AppName); }
            }
            Close();
            return;
        }

        _installing = true;
        MainBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        OptionsPanel.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;

        string dir = PathBox.Text;
        bool desktop = DesktopCheck.IsChecked == true;
        try
        {
            await Task.Run(() => Installer.Install(dir, desktop, (p, msg) =>
                Dispatcher.BeginInvoke(new Action(() => SetProgress(p, msg)))));
            await Task.Delay(500);
            _installedExe = Path.Combine(dir, Installer.ExeName);
            _done = true;
            HeadTitle.Text = "Установлено!";
            StatusText.Text = "Программа готова к работе. Ярлык: " + (desktop ? "рабочий стол и «Пуск»" : "«Пуск»");
            MainBtn.Content = "Готово";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка: " + ex.Message;
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("RedBrush");
            MainBtn.Content = "Повторить";
            OptionsPanel.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }
        finally
        {
            _installing = false;
            MainBtn.IsEnabled = true;
        }
    }

    private void SetProgress(double p, string msg)
    {
        StatusText.Text = msg;
        BarScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(p, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Режим --shots: три состояния окна установки в PNG (2x).</summary>
    public async Task CaptureAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)Content;
        await ShotHelper.Snap(root, Path.Combine(dir, "setup_1.png"), 2.0, 1200, Background);

        OptionsPanel.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        MainBtn.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        SetProgress(0.6, "Копирую файлы…");
        await ShotHelper.Snap(root, Path.Combine(dir, "setup_2.png"), 2.0, 1000, Background);

        SetProgress(1, "Готово");
        HeadTitle.Text = "Установлено!";
        StatusText.Text = "Программа готова к работе. Ярлык: рабочий стол и «Пуск»";
        MainBtn.Content = "Готово";
        MainBtn.IsEnabled = true;
        await ShotHelper.Snap(root, Path.Combine(dir, "setup_3.png"), 2.0, 1000, Background);
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
