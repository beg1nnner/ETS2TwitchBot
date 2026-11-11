using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EtsBot;

/// <summary>
/// Установка/удаление без сторонних программ. Установщик - это тот же exe, запущенный как «Setup».
/// Ставит в папку пользователя (права администратора не нужны).
/// </summary>
public static class Installer
{
    public const string AppName = "ETS2 Twitch Bot";
    public const string ExeName = "ETS2TwitchBot.exe";
    public const string Version = "1.5.0";
    private const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ETS2TwitchBot";

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    private static string DesktopLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    private static string StartMenuLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    /// <summary>Файл называется *Setup*.exe или запущен с ключом --setup.</summary>
    public static bool IsSetupMode(string[] args)
    {
        if (args.Contains("--setup")) return true;
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        return name.Contains("setup", StringComparison.OrdinalIgnoreCase)
            || name.Contains("install", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Только для режима --shots: показывать окно как при первой установке.</summary>
    public static bool ForceFresh { get; set; }

    public static bool IsInstalled(string dir) => !ForceFresh && File.Exists(Path.Combine(dir, ExeName));

    public static void Install(string dir, bool desktopShortcut, Action<double, string> progress)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь установщика");
        var target = Path.Combine(dir, ExeName);

        progress(0.08, "Подготовка…");
        Directory.CreateDirectory(dir);

        progress(0.2, "Закрываю запущенную копию программы…");
        CloseRunning(target);

        progress(0.35, "Копирую файлы…");
        try { File.Copy(self, target, true); }
        catch (IOException ex)
        {
            throw new IOException("Не удалось записать файл. Закрой программу ETS2 Twitch Bot и повтори. (" + ex.Message + ")");
        }

        progress(0.6, "Создаю ярлыки…");
        Shortcut(StartMenuLink, target, dir);
        if (desktopShortcut) Shortcut(DesktopLink, target, dir);
        else TryDelete(DesktopLink);

        progress(0.82, "Регистрирую в списке программ Windows…");
        using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
        {
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "ETS2 Twitch Bot");
            k.SetValue("InstallLocation", dir);
            k.SetValue("DisplayIcon", target + ",0");
            k.SetValue("UninstallString", "\"" + target + "\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(target).Length / 1024), RegistryValueKind.DWord);
        }

        progress(1.0, "Готово!");
    }

    public static string? InstalledExe()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstKey);
            var d = k?.GetValue("InstallLocation") as string;
            return d == null ? null : Path.Combine(d, ExeName);
        }
        catch { return null; }
    }

    /// <summary>Запускается из установленной копии с ключом --uninstall.</summary>
    public static void Uninstall()
    {
        var exe = Environment.ProcessPath ?? "";
        var dir = Path.GetDirectoryName(exe) ?? "";
        if (!File.Exists(Path.Combine(dir, ExeName)))
        {
            System.Windows.MessageBox.Show("Не удалось найти установленную программу.", AppName);
            return;
        }

        if (System.Windows.MessageBox.Show("Удалить ETS2 Twitch Bot с этого компьютера?", AppName + " — удаление",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;

        bool keep = System.Windows.MessageBox.Show(
            "Оставить твои настройки, ключи и журнал (папка программы)?\n\n" +
            "«Да» — оставить (пригодится при повторной установке).\n«Нет» — удалить всё.",
            AppName + " — удаление", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            == System.Windows.MessageBoxResult.Yes;

        CloseRunning(exe, exceptSelf: true);
        TryDelete(DesktopLink);
        TryDelete(StartMenuLink);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { /* ignore */ }

        // сам exe занят до выхода — удаляем его отдельным скрытым процессом чуть позже
        var cmd = keep
            ? $"/c ping 127.0.0.1 -n 3 >nul & del /f /q \"{exe}\""
            : $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{dir}\"";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { /* ignore */ }

        System.Windows.MessageBox.Show("Программа удалена.", AppName, System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private static void CloseRunning(string exePath, bool exceptSelf = false)
    {
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try
            {
                if (p.Id == me) continue;
                p.CloseMainWindow();
                if (!p.WaitForExit(3000)) p.Kill();
            }
            catch { /* ignore */ }
        }
        System.Threading.Thread.Sleep(300);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static void Shortcut(string lnk, string target, string workDir)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен");
        dynamic sh = Activator.CreateInstance(t)!;
        try
        {
            dynamic sc = sh.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = workDir;
            sc.Description = AppName;
            sc.IconLocation = target + ",0";
            sc.Save();
            Marshal.FinalReleaseComObject(sc);
        }
        finally { Marshal.FinalReleaseComObject(sh); }
    }
}
