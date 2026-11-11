using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace EtsBot;

/// <summary>
/// Эмуляция клавиатуры через WinAPI SendInput со СКАН-КОДАМИ:
/// игры читают DirectInput/RawInput и игнорируют обычные виртуальные клавиши.
/// </summary>
public sealed class KeySender
{
    private static readonly Dictionary<string, (ushort Scan, bool Ext)> Scan = BuildTable();

    private static Dictionary<string, (ushort, bool)> BuildTable()
    {
        var d = new Dictionary<string, (ushort, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["esc"] = (0x01, false), ["1"] = (0x02, false), ["2"] = (0x03, false), ["3"] = (0x04, false),
            ["4"] = (0x05, false), ["5"] = (0x06, false), ["6"] = (0x07, false), ["7"] = (0x08, false),
            ["8"] = (0x09, false), ["9"] = (0x0A, false), ["0"] = (0x0B, false), ["-"] = (0x0C, false),
            ["="] = (0x0D, false), ["backspace"] = (0x0E, false), ["tab"] = (0x0F, false),
            ["q"] = (0x10, false), ["w"] = (0x11, false), ["e"] = (0x12, false), ["r"] = (0x13, false),
            ["t"] = (0x14, false), ["y"] = (0x15, false), ["u"] = (0x16, false), ["i"] = (0x17, false),
            ["o"] = (0x18, false), ["p"] = (0x19, false), ["["] = (0x1A, false), ["]"] = (0x1B, false),
            ["enter"] = (0x1C, false), ["ctrl"] = (0x1D, false),
            ["a"] = (0x1E, false), ["s"] = (0x1F, false), ["d"] = (0x20, false), ["f"] = (0x21, false),
            ["g"] = (0x22, false), ["h"] = (0x23, false), ["j"] = (0x24, false), ["k"] = (0x25, false),
            ["l"] = (0x26, false), [";"] = (0x27, false), ["'"] = (0x28, false), ["`"] = (0x29, false),
            ["shift"] = (0x2A, false), ["\\"] = (0x2B, false),
            ["z"] = (0x2C, false), ["x"] = (0x2D, false), ["c"] = (0x2E, false), ["v"] = (0x2F, false),
            ["b"] = (0x30, false), ["n"] = (0x31, false), ["m"] = (0x32, false),
            [","] = (0x33, false), ["."] = (0x34, false), ["/"] = (0x35, false),
            ["rshift"] = (0x36, false), ["alt"] = (0x38, false), ["space"] = (0x39, false),
            ["f1"] = (0x3B, false), ["f2"] = (0x3C, false), ["f3"] = (0x3D, false), ["f4"] = (0x3E, false),
            ["f5"] = (0x3F, false), ["f6"] = (0x40, false), ["f7"] = (0x41, false), ["f8"] = (0x42, false),
            ["f9"] = (0x43, false), ["f10"] = (0x44, false), ["f11"] = (0x57, false), ["f12"] = (0x58, false),
            ["up"] = (0x48, true), ["left"] = (0x4B, true), ["right"] = (0x4D, true), ["down"] = (0x50, true),
            ["home"] = (0x47, true), ["end"] = (0x4F, true), ["pageup"] = (0x49, true),
            ["pagedown"] = (0x51, true), ["insert"] = (0x52, true), ["delete"] = (0x53, true),
            ["rctrl"] = (0x1D, true), ["ralt"] = (0x38, true),
        };
        return d;
    }

    public static bool IsKnownKey(string key) => Scan.ContainsKey(key);
    public static IEnumerable<string> AllKeys => Scan.Keys;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint INPUT_KEYBOARD = 1;
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg; public ushort wParamL; public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public bool DryRun { get; set; }

    public void KeyDown(string key) => Send(key, up: false);
    public void KeyUp(string key) => Send(key, up: true);

    private void Send(string key, bool up)
    {
        if (!Scan.TryGetValue(key, out var info))
            throw new ArgumentException($"Неизвестная клавиша '{key}'");

        lock (_gate)
        {
            if (up) _held.Remove(key); else _held.Add(key);
        }

        if (DryRun)
        {
            Log.Info("Клавиши", $"[тест] {(up ? "отпустить" : "нажать")} {key}");
            return;
        }

        uint flags = KEYEVENTF_SCANCODE;
        if (up) flags |= KEYEVENTF_KEYUP;
        if (info.Ext) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = info.Scan, dwFlags = flags, time = 0, dwExtraInfo = IntPtr.Zero } }
        };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException("SendInput не отправил событие (если игра запущена от администратора - запусти программу от администратора).");
    }

    /// <summary>Один щелчок колёсика мыши: dir &gt; 0 — вверх, &lt; 0 — вниз.</summary>
    public void Wheel(int dir)
    {
        if (DryRun)
        {
            Log.Info("Клавиши", $"[тест] колёсико мыши {(dir > 0 ? "вверх" : "вниз")}");
            return;
        }
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = unchecked((uint)(dir > 0 ? 120 : -120)), dwFlags = MOUSEEVENTF_WHEEL, time = 0, dwExtraInfo = IntPtr.Zero } }
        };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException("SendInput не отправил событие колёсика мыши.");
    }

    /// <summary>Отпустить всё, что сейчас зажато (аварийно).</summary>
    public void ReleaseAll()
    {
        string[] keys;
        lock (_gate) keys = new List<string>(_held).ToArray();
        foreach (var k in keys)
        {
            try { Send(k, up: true); } catch { /* ignore */ }
        }
    }

    public static string ForegroundTitle()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            int len = GetWindowTextLength(h);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    public bool GameFocused(string titlePart)
    {
        if (DryRun) return true;
        if (string.IsNullOrWhiteSpace(titlePart)) return true;
        return ForegroundTitle().Contains(titlePart, StringComparison.OrdinalIgnoreCase);
    }
}
