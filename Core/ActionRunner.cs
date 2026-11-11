using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EtsBot;

public enum ActionSource { Twitch, Donation, Test, Event }

public sealed class QueuedAction
{
    public Guid Id { get; } = Guid.NewGuid();
    public ActionSource Source { get; init; }
    public string Title { get; init; } = "";
    public string User { get; init; } = "";
    public List<Step> Steps { get; init; } = new();
    public int Times { get; init; } = 1;
    public string? RewardId { get; init; }
    public string? RedemptionId { get; init; }
    /// <summary>Сколько баллов зритель потратил на это действие (для отчёта).</summary>
    public int Points { get; init; }

    public string Display => Times > 1 ? $"{Title} ×{Times}" : Title;
    public string SourceText => Source switch
    {
        ActionSource.Twitch => "Twitch",
        ActionSource.Donation => "Донат",
        ActionSource.Event => "Событие",
        _ => "Тест",
    };
}

/// <summary>
/// Очередь действий: выполняет по одному (иначе «руль влево» и «руль вправо» смешались бы).
/// </summary>
public sealed class ActionRunner
{
    private readonly AppSettings _s;
    private readonly KeySender _keys;
    private readonly object _lock = new();
    private readonly List<QueuedAction> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private CancellationTokenSource? _currentCts;

    public QueuedAction? Current { get; private set; }
    public DateTime CurrentStartUtc { get; private set; }
    public double CurrentDuration { get; private set; }

    public event Action? QueueChanged;
    public event Action? CurrentChanged;
    /// <summary>Действие реально началось (окно игры в фокусе, клавиши пошли).</summary>
    public event Action<QueuedAction>? ActionStarted;

    /// <summary>Вызывается, чтобы отметить активацию награды: "FULFILLED" или "CANCELED" (возврат баллов).</summary>
    public Func<QueuedAction, string, Task>? RedemptionResult { get; set; }

    public ActionRunner(AppSettings s, KeySender keys) { _s = s; _keys = keys; }

    public QueuedAction[] Snapshot() { lock (_lock) return _pending.ToArray(); }

    private bool HasPending() { lock (_lock) return _pending.Count > 0; }

    public async Task<bool> EnqueueAsync(QueuedAction a)
    {
        bool full = false;
        lock (_lock)
        {
            if ((a.Source == ActionSource.Twitch || a.Source == ActionSource.Event) && _pending.Count >= _s.General.MaxQueue) full = true;
            else _pending.Add(a);
        }
        if (full)
        {
            Log.Warn("Очередь", $"Очередь переполнена ({_s.General.MaxQueue}), «{a.Title}» отменена, баллы возвращены");
            await ReportAsync(a, "CANCELED");
            return false;
        }
        _signal.Release();
        QueueChanged?.Invoke();
        return true;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct);
                QueuedAction? next = null;
                lock (_lock)
                {
                    if (_pending.Count > 0) { next = _pending[0]; _pending.RemoveAt(0); }
                }
                QueueChanged?.Invoke();
                if (next != null)
                {
                    await ExecuteAsync(next, ct);
                    // пауза между наградами: даём стримеру прийти в себя
                    double gap = _s.General.GapSeconds;
                    if (gap > 0 && HasPending()) await Task.Delay(TimeSpan.FromSeconds(gap), ct);
                }
            }
        }
        catch (OperationCanceledException) { /* остановка */ }
    }

    /// <summary>Аварийная остановка: прервать текущее, отпустить клавиши, вернуть баллы за очередь.</summary>
    public async Task StopAllAsync()
    {
        QueuedAction[] dropped;
        lock (_lock)
        {
            try { _currentCts?.Cancel(); } catch { /* ignore */ }
            dropped = _pending.ToArray();
            _pending.Clear();
        }
        _keys.ReleaseAll();
        QueueChanged?.Invoke();
        foreach (var a in dropped)
            if (a.Source == ActionSource.Twitch) await ReportAsync(a, "CANCELED");
        if (dropped.Length > 0) Log.Warn("Очередь", $"Очередь очищена ({dropped.Length} шт.)");
    }

    private async Task ExecuteAsync(QueuedAction a, CancellationToken appCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var ct = cts.Token;
        lock (_lock) { _currentCts = cts; Current = a; CurrentStartUtc = DateTime.UtcNow; CurrentDuration = 0; }
        CurrentChanged?.Invoke();

        bool ok = false;
        try
        {
            if (a.Source == ActionSource.Test && !_s.General.DryRun)
            {
                Log.Info("Тест", $"«{a.Title}» начнётся через 3 секунды — переключись в игру!");
                await Task.Delay(3000, ct);
            }

            if (!await WaitForFocusAsync(a, ct))
            {
                Log.Warn("Игра", $"Окно игры не в фокусе — «{a.Title}» пропущено");
            }
            else
            {
                double dur = ActionScript.Estimate(a.Steps, _s.General.TapSeconds) * a.Times;
                lock (_lock) { CurrentStartUtc = DateTime.UtcNow; CurrentDuration = dur; }
                CurrentChanged?.Invoke();
                WriteObs(a);
                Log.Action("Действие", $"{a.Display}" + (a.User.Length > 0 ? $"  ({a.User})" : ""));
                try { ActionStarted?.Invoke(a); } catch (Exception ex) { Log.Warn("Действие", "Оповещение о запуске: " + ex.Message); }
                for (int i = 0; i < a.Times; i++)
                    await RunStepsAsync(a.Steps, ct);
                ok = true;
            }
        }
        catch (OperationCanceledException) { Log.Warn("Действие", $"«{a.Title}» прервано"); }
        catch (Exception ex) { Log.Error("Действие", $"«{a.Title}»: {ex.Message}"); }
        finally
        {
            _keys.ReleaseAll();
            lock (_lock) { Current = null; _currentCts = null; }
            CurrentChanged?.Invoke();
        }

        if (a.Source == ActionSource.Twitch)
        {
            if (ok) { if (_s.General.MarkFulfilled) await ReportAsync(a, "FULFILLED"); }
            else await ReportAsync(a, "CANCELED");
        }
    }

    private async Task<bool> WaitForFocusAsync(QueuedAction a, CancellationToken ct)
    {
        var g = _s.General;
        if (!g.RequireGameFocus || _keys.GameFocused(g.GameWindowTitle)) return true;
        if (a.Source != ActionSource.Donation && a.Source != ActionSource.Event) return false;

        Log.Info("Игра", $"Жду окно игры до {_s.Donations.FocusWaitSeconds:0} сек...");
        var until = DateTime.UtcNow.AddSeconds(Math.Max(0, _s.Donations.FocusWaitSeconds));
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(500, ct);
            if (_keys.GameFocused(g.GameWindowTitle)) return true;
        }
        return false;
    }

    private async Task RunStepsAsync(IEnumerable<Step> steps, CancellationToken ct)
    {
        foreach (var st in steps)
        {
            ct.ThrowIfCancellationRequested();
            switch (st)
            {
                case HoldStep h:
                {
                    var keys = h.Keys.Select(_s.ResolveKey).ToArray();
                    try
                    {
                        foreach (var k in keys) _keys.KeyDown(k);
                        await Delay(h.Seconds, ct);
                    }
                    finally { foreach (var k in keys.Reverse()) _keys.KeyUp(k); }
                    break;
                }
                case PressStep p:
                {
                    var keys = p.Keys.Select(_s.ResolveKey).ToArray();
                    for (int i = 0; i < p.Times; i++)
                    {
                        try
                        {
                            foreach (var k in keys) _keys.KeyDown(k);
                            await Delay(_s.General.TapSeconds, ct);
                        }
                        finally { foreach (var k in keys.Reverse()) _keys.KeyUp(k); }
                        if (i < p.Times - 1) await Delay(p.Interval, ct);
                    }
                    break;
                }
                case WaitStep w:
                    await Delay(w.Seconds, ct);
                    break;
                case ScrollStep sc:
                {
                    int dir = sc.Clicks > 0 ? 1 : -1;
                    for (int i = 0; i < Math.Abs(sc.Clicks); i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        _keys.Wheel(dir);
                        if (i < Math.Abs(sc.Clicks) - 1) await Delay(sc.Interval, ct);
                    }
                    break;
                }
                case RepeatStep r:
                    for (int i = 0; i < r.Count; i++) await RunStepsAsync(r.Body, ct);
                    break;
            }
        }
    }

    private static Task Delay(double sec, CancellationToken ct)
        => sec <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromSeconds(sec), ct);

    private void WriteObs(QueuedAction a)
    {
        var f = _s.General.ObsTextFile;
        if (string.IsNullOrWhiteSpace(f)) return;
        try
        {
            var path = Path.IsPathRooted(f) ? f : Path.Combine(Paths.AppDir, f);
            File.WriteAllText(path, a.Display + (a.User.Length > 0 ? " — " + a.User : ""));
        }
        catch { /* не критично */ }
    }

    private async Task ReportAsync(QueuedAction a, string status)
    {
        if (a.RedemptionId == null || RedemptionResult == null) return;
        try { await RedemptionResult(a, status); }
        catch (Exception ex) { Log.Warn("Twitch", $"Не удалось отметить награду ({status}): {ex.Message}"); }
    }
}
