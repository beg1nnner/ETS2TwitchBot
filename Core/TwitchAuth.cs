using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EtsBot;

public sealed class TwitchTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public string UserId { get; set; } = "";
    public string Login { get; set; } = "";
    public string ClientId { get; set; } = "";
    /// <summary>Права, реально выданные токену (через пробел).</summary>
    public string Scope { get; set; } = "";
}

/// <summary>Вход в Twitch (OAuth) и автообновление токена. Токены - в twitch_tokens.json.</summary>
public sealed class TwitchAuth
{
    public const string Scopes = "channel:read:redemptions channel:manage:redemptions user:read:chat user:write:chat channel:read:subscriptions bits:read";
    private const string AuthUrl = "https://id.twitch.tv/oauth2/authorize";
    private const string TokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string ValidateUrl = "https://id.twitch.tv/oauth2/validate";

    private readonly HttpClient _http;
    private readonly TwitchSettings _cfg;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TwitchTokens? _tok;

    /// <summary>false — никогда не открывать браузер для входа (нужно при закрытии программы).</summary>
    public bool AllowLogin { get; set; } = true;

    public TwitchAuth(HttpClient http, TwitchSettings cfg)
    {
        _http = http;
        _cfg = cfg;
        LoadFromFile();
    }

    private static readonly SemaphoreSlim LoginLock = new(1, 1);
    private bool _reloginTried;

    private void LoadFromFile()
    {
        try
        {
            _tok = null;
            if (File.Exists(Paths.TwitchTokens))
                _tok = JsonSerializer.Deserialize<TwitchTokens>(File.ReadAllText(Paths.TwitchTokens));
            // токены от другого приложения Twitch не подходят
            if (_tok != null && !string.Equals(_tok.ClientId, ClientId, StringComparison.Ordinal)) _tok = null;
        }
        catch { _tok = null; }
    }

    /// <summary>У сохранённого токена нет каких-то из нужных прав (программа обновилась) — нужен повторный вход.</summary>
    public bool NeedsRelogin
    {
        get
        {
            if (_tok == null) return false;
            var have = new HashSet<string>((_tok.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            foreach (var need in Scopes.Split(' ')) if (!have.Contains(need)) return true;
            return false;
        }
    }

    public string ClientId => _cfg.ClientId.Trim();
    public string? Login => _tok?.Login;
    public string UserId => _tok?.UserId ?? "";
    public bool IsLoggedIn => _tok != null;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_tok == null || (NeedsRelogin && AllowLogin && !_reloginTried))
            {
                if (_tok != null) _reloginTried = true; // если Twitch всё равно не выдал права — не зацикливаемся
                await LoginAsync(ct, true);
            }
            else if (DateTime.UtcNow > _tok.ExpiresAtUtc.AddMinutes(-2)) await RefreshAsync(ct);
            return _tok!.AccessToken;
        }
        finally { _lock.Release(); }
    }

    public async Task ForceRefreshAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { await RefreshAsync(ct); }
        finally { _lock.Release(); }
    }

    public void Logout()
    {
        _tok = null;
        try { if (File.Exists(Paths.TwitchTokens)) File.Delete(Paths.TwitchTokens); } catch { /* ignore */ }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _tok!.RefreshToken,
                ["client_id"] = ClientId,
                ["client_secret"] = _cfg.ClientSecret.Trim(),
            }), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(body);
            await StoreAsync(JsonDocument.Parse(body).RootElement, ct);
            Log.Info("Twitch", "Токен обновлён");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("Twitch", "Не удалось обновить токен (" + ex.Message + "). Нужен повторный вход.");
            await LoginAsync(ct);
        }
    }

    private async Task LoginAsync(CancellationToken ct, bool skipIfFresh = false)
    {
        if (!AllowLogin) throw new InvalidOperationException("Нужен вход в Twitch");
        if (string.IsNullOrWhiteSpace(_cfg.ClientId) || string.IsNullOrWhiteSpace(_cfg.ClientSecret))
            throw new InvalidOperationException("Не заполнены Client ID / Client Secret Twitch (вкладка «Настройки»).");

        // вход один на всю программу: иначе запуск бота и синхронизация наград откроют два окна входа сразу
        await LoginLock.WaitAsync(ct);
        try
        {
            if (skipIfFresh)
            {
                LoadFromFile();
                if (_tok != null && !NeedsRelogin) return;
            }
            await DoLoginAsync(ct);
        }
        finally { LoginLock.Release(); }
    }

    private async Task DoLoginAsync(CancellationToken ct)
    {
        var code = await OAuthLoopback.GetCodeAsync(AuthUrl, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["scope"] = Scopes,
        }, _cfg.RedirectUri.Trim(), "Twitch", ct);

        using var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = _cfg.ClientSecret.Trim(),
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = _cfg.RedirectUri.Trim(),
        }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Twitch не выдал токен: " + body);
        await StoreAsync(JsonDocument.Parse(body).RootElement, ct);
        Log.Ok("Twitch", "Вход выполнен как " + _tok!.Login);
    }

    private async Task StoreAsync(JsonElement tok, CancellationToken ct)
    {
        var access = tok.GetProperty("access_token").GetString() ?? "";
        var refresh = tok.GetProperty("refresh_token").GetString() ?? "";
        int expires = tok.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        using var req = new HttpRequestMessage(HttpMethod.Get, ValidateUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", access);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("validate: " + body);
        var info = JsonDocument.Parse(body).RootElement;

        _tok = new TwitchTokens
        {
            AccessToken = access,
            RefreshToken = refresh,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expires),
            UserId = info.GetProperty("user_id").GetString() ?? "",
            Login = info.GetProperty("login").GetString() ?? "",
            ClientId = ClientId,
            Scope = info.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array
                ? string.Join(" ", System.Linq.Enumerable.Select(sc.EnumerateArray(), x => x.GetString() ?? ""))
                : "",
        };
        File.WriteAllText(Paths.TwitchTokens, JsonSerializer.Serialize(_tok));
    }
}
