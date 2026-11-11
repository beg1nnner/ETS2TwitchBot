using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EtsBot;

public sealed record DonationEvent(string Id, string Username, string Message, double Amount, string Currency);

public sealed class DaTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public string ClientId { get; set; } = "";
}

/// <summary>Вход в DonationAlerts (OAuth) + автообновление токена (da_tokens.json).</summary>
public sealed class DonationAuth
{
    public const string Scopes = "oauth-user-show oauth-donation-subscribe";
    private const string AuthUrl = "https://www.donationalerts.com/oauth/authorize";
    private const string TokenUrl = "https://www.donationalerts.com/oauth/token";

    private readonly HttpClient _http;
    private readonly DonationSettings _cfg;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DaTokens? _tok;

    public DonationAuth(HttpClient http, DonationSettings cfg)
    {
        _http = http;
        _cfg = cfg;
        try
        {
            if (File.Exists(Paths.DonationTokens))
                _tok = JsonSerializer.Deserialize<DaTokens>(File.ReadAllText(Paths.DonationTokens));
            if (_tok != null && !string.Equals(_tok.ClientId, _cfg.ClientId.Trim(), StringComparison.Ordinal)) _tok = null;
        }
        catch { _tok = null; }
    }

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_tok == null) await LoginAsync(ct);
            else if (DateTime.UtcNow > _tok.ExpiresAtUtc.AddMinutes(-5)) await RefreshAsync(ct);
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
        try { if (File.Exists(Paths.DonationTokens)) File.Delete(Paths.DonationTokens); } catch { /* ignore */ }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _tok!.RefreshToken,
                ["client_id"] = _cfg.ClientId.Trim(),
                ["client_secret"] = _cfg.ClientSecret.Trim(),
                ["scope"] = Scopes,
            }), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(body);
            Store(JsonDocument.Parse(body).RootElement);
            Log.Info("Донаты", "Токен обновлён");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("Донаты", "Не удалось обновить токен (" + ex.Message + "). Нужен повторный вход.");
            await LoginAsync(ct);
        }
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cfg.ClientId) || string.IsNullOrWhiteSpace(_cfg.ClientSecret))
            throw new InvalidOperationException("Не заполнены Client ID / Secret DonationAlerts (вкладка «Настройки»).");

        var code = await OAuthLoopback.GetCodeAsync(AuthUrl, new Dictionary<string, string>
        {
            ["client_id"] = _cfg.ClientId.Trim(),
            ["response_type"] = "code",
            ["scope"] = Scopes,
        }, _cfg.RedirectUri.Trim(), "Донаты", ct);

        using var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = _cfg.ClientId.Trim(),
            ["client_secret"] = _cfg.ClientSecret.Trim(),
            ["redirect_uri"] = _cfg.RedirectUri.Trim(),
            ["code"] = code,
        }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("DonationAlerts не выдал токен: " + body);
        Store(JsonDocument.Parse(body).RootElement);
        Log.Ok("Донаты", "Вход в DonationAlerts выполнен");
    }

    private void Store(JsonElement tok)
    {
        int expires = tok.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 3600;
        _tok = new DaTokens
        {
            AccessToken = tok.GetProperty("access_token").GetString() ?? "",
            RefreshToken = tok.GetProperty("refresh_token").GetString() ?? "",
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expires),
            ClientId = _cfg.ClientId.Trim(),
        };
        File.WriteAllText(Paths.DonationTokens, JsonSerializer.Serialize(_tok));
    }
}

/// <summary>Получение донатов в реальном времени (Centrifugo WebSocket DonationAlerts).</summary>
public sealed class DonationClient
{
    private const string Api = "https://www.donationalerts.com/api/v1";
    private const string WsUrl = "wss://centrifugo.donationalerts.com/connection/websocket";

    private readonly HttpClient _http;
    private readonly DonationAuth _auth;
    private readonly Func<DonationEvent, Task> _onDonation;
    private readonly Action<ConnState, string?> _state;
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public DonationClient(HttpClient http, DonationAuth auth, Func<DonationEvent, Task> onDonation, Action<ConnState, string?> state)
    {
        _http = http; _auth = auth; _onDonation = onDonation; _state = state;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _state(ConnState.Connecting, null);
                await ConnectOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _state(ConnState.Error, ex.Message);
                Log.Error("Донаты", "Ошибка соединения: " + ex.Message + ". Повтор через 5 сек...");
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
        }
        _state(ConnState.Off, null);
    }

    private async Task<JsonElement> ApiAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var token = await _auth.GetTokenAsync(ct);
            using var req = new HttpRequestMessage(method, Api + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode == 401 && attempt == 1) { await _auth.ForceRefreshAsync(ct); continue; }
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"DonationAlerts API {(int)resp.StatusCode}: {text}");
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        throw new InvalidOperationException("unreachable");
    }

    private async Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        var user = (await ApiAsync(HttpMethod.Get, "/user/oauth", null, ct)).GetProperty("data");
        var userId = user.GetProperty("id").ToString();
        var socketToken = user.GetProperty("socket_connection_token").GetString() ?? "";
        var channel = "$alerts:donation_" + userId;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(WsUrl), ct);
        await SendAsync(ws, JsonSerializer.Serialize(new { @params = new { token = socketToken }, id = 1 }), ct);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pinger = Task.Run(async () =>
        {
            int n = 100;
            try
            {
                while (!pingCts.IsCancellationRequested)
                {
                    await Task.Delay(25000, pingCts.Token);
                    await SendAsync(ws, "{\"method\":7,\"id\":" + (n++) + "}", pingCts.Token);
                }
            }
            catch { /* остановка */ }
        });

        try
        {
            while (true)
            {
                string? text;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    try { text = await ReceiveTextAsync(ws, timeout.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Log.Warn("Донаты", "Нет сообщений от DonationAlerts, переподключаюсь");
                        return;
                    }
                }
                if (text == null) { Log.Warn("Донаты", "Соединение закрыто, переподключаюсь"); return; }

                foreach (var part in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    JsonElement msg;
                    try { msg = JsonDocument.Parse(part).RootElement.Clone(); }
                    catch { continue; }
                    if (msg.ValueKind != JsonValueKind.Object) continue;

                    // пинг от сервера: {} -> отвечаем {}
                    if (!msg.EnumerateObject().GetEnumerator().MoveNext()) { await SendAsync(ws, "{}", ct); continue; }

                    if (msg.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                    {
                        Log.Error("Донаты", "Ошибка сервера: " + err);
                        continue;
                    }

                    int id = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : 0;
                    if (!msg.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) continue;

                    if (id == 1 && result.TryGetProperty("client", out var cl))
                    {
                        var clientId = cl.GetString() ?? "";
                        var sub = await ApiAsync(HttpMethod.Post, "/centrifuge/subscribe",
                            new { channels = new[] { channel }, client = clientId }, ct);
                        var chToken = sub.GetProperty("channels")[0].GetProperty("token").GetString() ?? "";
                        await SendAsync(ws, JsonSerializer.Serialize(new { @params = new { channel, token = chToken }, method = 1, id = 2 }), ct);
                    }
                    else if (id == 2)
                    {
                        _state(ConnState.On, null);
                        Log.Ok("Донаты", "Подключено к DonationAlerts. Жду донаты...");
                    }
                    else if (result.TryGetProperty("channel", out _) && result.TryGetProperty("data", out var data))
                    {
                        var d = data;
                        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object)
                            d = inner;
                        await HandleDonationAsync(d);
                    }
                }
            }
        }
        finally
        {
            pingCts.Cancel();
            try { await pinger; } catch { /* ignore */ }
        }
    }

    private async Task HandleDonationAsync(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) return;
        var id = Str(d, "id");
        if (id.Length > 0)
        {
            if (!_seen.Add(id)) return;
            _seenOrder.Enqueue(id);
            while (_seenOrder.Count > 200) _seen.Remove(_seenOrder.Dequeue());
        }
        double amount = 0;
        if (d.TryGetProperty("amount", out var a))
        {
            if (a.ValueKind == JsonValueKind.Number) amount = a.GetDouble();
            else if (a.ValueKind == JsonValueKind.String)
                double.TryParse(a.GetString()!.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out amount);
        }
        var ev = new DonationEvent(id, Str(d, "username"), Str(d, "message"), amount, Str(d, "currency"));
        try { await _onDonation(ev); }
        catch (Exception ex) { Log.Error("Донаты", "Ошибка обработки доната: " + ex.Message); }
    }

    private static string Str(JsonElement d, string name)
        => d.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
