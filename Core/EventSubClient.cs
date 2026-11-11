using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EtsBot;

/// <summary>Дополнительная подписка EventSub (чат, подписки, биты, рейды).</summary>
public sealed record EventSubscription(string Type, string Version, Dictionary<string, string> Condition);

/// <summary>
/// Twitch EventSub по WebSocket: слушает активации наград за баллы канала.
/// Сам переподключается, поддерживает session_reconnect.
/// </summary>
public sealed class EventSubClient
{
    private const string WsUrl = "wss://eventsub.wss.twitch.tv/ws";
    private readonly TwitchApi _api;
    private readonly Func<JsonElement, Task> _onRedemption;
    private readonly Action<ConnState, string?> _state;

    private readonly Func<string, JsonElement, Task>? _onOther;
    private readonly Func<List<EventSubscription>>? _extras;

    public EventSubClient(TwitchApi api, Func<JsonElement, Task> onRedemption, Action<ConnState, string?> state,
        Func<string, JsonElement, Task>? onOther = null, Func<List<EventSubscription>>? extras = null)
    {
        _api = api;
        _onRedemption = onRedemption;
        _state = state;
        _onOther = onOther;
        _extras = extras;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var url = WsUrl;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _state(ConnState.Connecting, null);
                url = await ConnectOnceAsync(url, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _state(ConnState.Error, ex.Message);
                Log.Error("Twitch", "Ошибка соединения: " + ex.Message + ". Повтор через 5 сек...");
                url = WsUrl;
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
        }
        _state(ConnState.Off, null);
    }

    private async Task<string> ConnectOnceAsync(string url, CancellationToken ct)
    {
        bool fresh = url == WsUrl;
        int keepalive = 40;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);

        while (true)
        {
            string? text;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(keepalive + 10));
                try { text = await ReceiveTextAsync(ws, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Log.Warn("Twitch", "Нет сообщений от Twitch, переподключаюсь");
                    return WsUrl;
                }
            }
            if (text == null)
            {
                Log.Warn("Twitch", "Соединение закрыто, переподключаюсь");
                return WsUrl;
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var meta = root.GetProperty("metadata");
            var type = meta.GetProperty("message_type").GetString();

            switch (type)
            {
                case "session_welcome":
                {
                    var session = root.GetProperty("payload").GetProperty("session");
                    if (session.TryGetProperty("keepalive_timeout_seconds", out var k) && k.ValueKind == JsonValueKind.Number)
                        keepalive = k.GetInt32();
                    if (fresh)
                    {
                        var sessionId = session.GetProperty("id").GetString()!;
                        await _api.SubscribeRedemptionsAsync(sessionId, ct);
                        Log.Ok("Twitch", "Подписка на награды создана. Слушаю канал...");
                        if (_extras != null)
                        {
                            foreach (var sub in _extras())
                            {
                                try
                                {
                                    await _api.SubscribeAsync(sub.Type, sub.Version, sub.Condition, sessionId, ct);
                                    Log.Ok("Twitch", "Подписка на события создана: " + sub.Type);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    Log.Warn("Twitch", $"Не удалось подписаться на {sub.Type}: {ex.Message}");
                                }
                            }
                        }
                    }
                    else Log.Info("Twitch", "Переподключение выполнено");
                    _state(ConnState.On, null);
                    break;
                }
                case "notification":
                {
                    var st = meta.TryGetProperty("subscription_type", out var s) ? s.GetString() : null;
                    if (st == "channel.channel_points_custom_reward_redemption.add")
                    {
                        var ev = root.GetProperty("payload").GetProperty("event").Clone();
                        _ = Task.Run(async () =>
                        {
                            try { await _onRedemption(ev); }
                            catch (Exception ex) { Log.Error("Twitch", "Ошибка обработки награды: " + ex.Message); }
                        }, CancellationToken.None);
                    }
                    else if (_onOther != null && st != null)
                    {
                        var ev = root.GetProperty("payload").GetProperty("event").Clone();
                        var evType = st;
                        _ = Task.Run(async () =>
                        {
                            try { await _onOther(evType, ev); }
                            catch (Exception ex) { Log.Error("Twitch", "Ошибка обработки события: " + ex.Message); }
                        }, CancellationToken.None);
                    }
                    break;
                }
                case "session_reconnect":
                    Log.Info("Twitch", "Twitch просит переподключиться");
                    return root.GetProperty("payload").GetProperty("session").GetProperty("reconnect_url").GetString()!;
                case "revocation":
                    Log.Error("Twitch", "Подписка отозвана Twitch: " + text);
                    return WsUrl;
                // session_keepalive - просто сбрасывает таймер
            }
        }
    }

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
