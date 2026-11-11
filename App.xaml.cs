using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace EtsBot;

public partial class App : Application
{
    private Mutex? _mutex;

    /// <summary>true — не показывать мастер настройки (режим скриншотов).</summary>
    public static bool NoWizard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--selftest"))
        {
            // в отдельном потоке: без контекста окна, иначе async-код в тесте зависнет
            int code = Task.Run(SelfTest.Run).GetAwaiter().GetResult();
            Shutdown(code);
            return;
        }

        int shotsAt = Array.IndexOf(e.Args, "--shots");
        if (shotsAt >= 0)
        {
            string dir = e.Args.Length > shotsAt + 1 ? e.Args[shotsAt + 1] : System.IO.Path.Combine(Paths.AppDir, "shots");
            RunShots(dir);
            return;
        }

        if (e.Args.Contains("--uninstall"))
        {
            Installer.Uninstall();
            Shutdown();
            return;
        }

        if (Installer.IsSetupMode(e.Args))
        {
            new SetupWindow().Show();
            return;
        }

        bool restarted = e.Args.Contains("--restarted");
        _mutex = new Mutex(true, "EtsBot_SingleInstance_5C1A", out bool created);
        if (!created && restarted)
        {
            try { created = _mutex.WaitOne(8000); }
            catch (AbandonedMutexException) { created = true; }
        }
        if (!created)
        {
            MessageBox.Show("Программа уже запущена (проверь значок в трее или панель задач).",
                "ETS2 Twitch Bot", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (s, ex) =>
        {
            Log.Error("Программа", "Необработанная ошибка: " + ex.Exception.Message);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            Log.Error("Программа", "Критическая ошибка: " + (ex.ExceptionObject as Exception)?.Message);
        TaskScheduler.UnobservedTaskException += (s, ex) =>
        {
            Log.Warn("Программа", "Ошибка в фоновой задаче: " + ex.Exception.GetBaseException().Message);
            ex.SetObserved();
        };

        try { new MainWindow().Show(); }
        catch (Exception ex)
        {
            Log.Error("Программа", "Не удалось открыть окно: " + ex);
            MessageBox.Show("Не удалось открыть окно программы:\n\n" + ex.Message, "ETS2 Twitch Bot",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async void RunShots(string dir)
    {
        // скриншоты для документации: окна рисуют сами себя в PNG, затем программа выходит
        _ = Task.Run(async () => { await Task.Delay(90000); Environment.Exit(2); });
        try
        {
            NoWizard = true;
            Installer.ForceFresh = true;
            var setup = new SetupWindow();
            setup.Show();
            await setup.CaptureAsync(dir);

            Installer.ForceFresh = false;
            var main = new MainWindow();
            main.Show();
            await main.CaptureShotsAsync(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "done.txt"), "ok");
        }
        catch (Exception ex)
        {
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "error.txt"), ex.ToString()); } catch { }
        }
        Environment.Exit(0);
    }
}
