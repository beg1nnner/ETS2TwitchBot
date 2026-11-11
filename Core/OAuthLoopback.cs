using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EtsBot;

/// <summary>
/// OAuth Authorization Code: открывает браузер, ловит ?code=... на локальном адресе
/// (redirect_uri), как это делает любая десктопная программа.
/// </summary>
public static class OAuthLoopback
{
    public static async Task<string> GetCodeAsync(
        string authorizeUrl, IDictionary<string, string> query, string redirectUri, string provider, CancellationToken ct)
    {
        var uri = new Uri(redirectUri);
        var prefix = $"{uri.Scheme}://{uri.Host}:{uri.Port}/";
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

        var q = new Dictionary<string, string>(query) { ["state"] = state, ["redirect_uri"] = redirectUri };
        var url = authorizeUrl + "?" + string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Не удалось открыть локальный порт {uri.Port} для входа ({ex.Message}). Возможно, он занят другой программой.");
        }

        Log.Info(provider, "Открываю браузер для входа...");
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn(provider, "Не удалось открыть браузер сам (" + ex.Message + "). Ссылка: " + url); }

        var deadline = Task.Delay(TimeSpan.FromMinutes(5), ct);
        while (true)
        {
            var ctxTask = listener.GetContextAsync();
            var done = await Task.WhenAny(ctxTask, deadline);
            if (done != ctxTask)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("Время ожидания входа истекло (5 минут).");
            }

            var ctx = await ctxTask;
            var qs = ctx.Request.QueryString;
            var got = qs["state"];
            if (got != null && got != state)
            {
                await Respond(ctx, "Неверный параметр state. Повтори вход из программы.", false);
                continue;
            }
            var err = qs["error"];
            if (err != null)
            {
                await Respond(ctx, "Доступ отклонён: " + (qs["error_description"] ?? err), false);
                throw new InvalidOperationException("Вход отклонён: " + (qs["error_description"] ?? err));
            }
            var code = qs["code"];
            if (code != null)
            {
                await Respond(ctx, "Готово! Можно закрыть эту вкладку и вернуться в программу.", true);
                return code;
            }
            await Respond(ctx, "Ожидаю вход...", true);
        }
    }

    private static async Task Respond(HttpListenerContext ctx, string text, bool ok)
    {
        var color = ok ? "#3DDC97" : "#FF5470";
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>ETS2 Twitch Bot</title></head>"
            + "<body style=\"margin:0;background:#0D0E13;color:#E8EAF2;font-family:Segoe UI,sans-serif;display:flex;"
            + "align-items:center;justify-content:center;height:100vh\"><div style=\"text-align:center\">"
            + $"<div style=\"font-size:56px;color:{color}\">{(ok ? "&#10003;" : "&#10007;")}</div>"
            + $"<div style=\"font-size:20px;margin-top:12px\">{WebUtility.HtmlEncode(text)}</div></div></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
}
