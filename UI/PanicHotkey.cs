using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace EtsBot;

/// <summary>Глобальная горячая клавиша (работает, даже когда активна игра или окно свёрнуто в трей).</summary>
public sealed class PanicHotkey : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4E57;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly Action _onPressed;
    private bool _registered;

    public PanicHotkey(System.Windows.Window window, Action onPressed)
    {
        _onPressed = onPressed;
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>Разбор строки вида «Ctrl+Alt+X» или «F9». Без модификатора разрешены только F1–F24.</summary>
    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }

        var keyText = parts[parts.Length - 1];
        if (keyText.Length == 1 && char.IsDigit(keyText[0])) keyText = "D" + keyText;
        if (!Enum.TryParse<System.Windows.Input.Key>(keyText, true, out var key) || key == System.Windows.Input.Key.None)
            return false;
        int v = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        if (v <= 0) return false;
        vk = (uint)v;

        bool isFKey = key >= System.Windows.Input.Key.F1 && key <= System.Windows.Input.Key.F24;
        if (modifiers == 0 && !isFKey) return false;
        return true;
    }

    /// <summary>Регистрирует клавишу (старую снимает). Пустая строка — просто выключает.</summary>
    public bool Register(string text, out string error)
    {
        Unregister();
        error = "";
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!TryParse(text, out var mods, out var vk))
        {
            error = "не понимаю сочетание «" + text + "». Пример: Ctrl+Alt+X или F9";
            return false;
        }
        if (!RegisterHotKey(_hwnd, HotkeyId, mods | MOD_NOREPEAT, vk))
        {
            error = "Windows не дала занять сочетание (возможно, оно уже используется другой программой)";
            return false;
        }
        _registered = true;
        return true;
    }

    private void Unregister()
    {
        if (!_registered) return;
        try { UnregisterHotKey(_hwnd, HotkeyId); } catch { /* ignore */ }
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try { _onPressed(); } catch { /* ignore */ }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        try { _source?.RemoveHook(WndProc); } catch { /* ignore */ }
    }
}
