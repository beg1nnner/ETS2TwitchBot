using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EtsBot;

public sealed class TwitchApiException : Exception
{
    public int Status { get; }
    public string Body { get; }
    public TwitchApiException(int status, string body) : base($"HTTP {status}: {body}") { Status = status; Body = body; }
}

/// <summary>Нужные запросы Twitch Helix API: подписка, награды, статус активации.</summary>
public sealed class TwitchApi
{
    private const string Helix = "https://api.twitch.tv/helix";
    private readonly HttpClient _http;
    private readonly TwitchAuth _auth;

    public TwitchApi(HttpClient http, TwitchAuth auth) { _http = http; _auth = auth; }

    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, string? query, object? body, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var token = await _auth.GetTokenAsync(ct);
            using var req = new HttpRequestMessage(method, Helix + path + (query != null ? "?" + query : ""));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Client-Id", _auth.ClientId);
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode == 401 && attempt == 1)
            {
                await _auth.ForceRefreshAsync(ct);
                continue;
            }
            if ((int)resp.StatusCode >= 400) throw new TwitchApiException((int)resp.StatusCode, text);
            if (string.IsNullOrWhiteSpace(text)) return default;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        throw new InvalidOperationException("unreachable");
    }

    public string UserId => _auth.UserId;

    /// <summary>Подписка EventSub произвольного типа (чат, подписки, биты, рейды).</summary>
    public Task SubscribeAsync(string type, string version, Dictionary<string, string> condition, string sessionId, CancellationToken ct)
        => RequestAsync(HttpMethod.Post, "/eventsub/subscriptions", null, new Dictionary<string, object>
        {
            ["type"] = type,
            ["version"] = version,
            ["condition"] = condition,
            ["transport"] = new Dictionary<string, string> { ["method"] = "websocket", ["session_id"] = sessionId },
        }, ct);

    /// <summary>Сообщение в чат канала от имени стримера (нужно право user:write:chat).</summary>
    public Task SendChatAsync(string message, CancellationToken ct)
        => RequestAsync(HttpMethod.Post, "/chat/messages", null, new Dictionary<string, string>
        {
            ["broadcaster_id"] = _auth.UserId,
            ["sender_id"] = _auth.UserId,
            ["message"] = message,
        }, ct);

    public Task SubscribeRedemptionsAsync(string sessionId, CancellationToken ct)
        => RequestAsync(HttpMethod.Post, "/eventsub/subscriptions", null, new Dictionary<string, object>
        {
            ["type"] = "channel.channel_points_custom_reward_redemption.add",
            ["version"] = "1",
            ["condition"] = new Dictionary<string, string> { ["broadcaster_user_id"] = _auth.UserId },
            ["transport"] = new Dictionary<string, string> { ["method"] = "websocket", ["session_id"] = sessionId },
        }, ct);

    public async Task<List<(string Id, string Title)>> ListRewardsAsync(CancellationToken ct)
    {
        var res = await RequestAsync(HttpMethod.Get, "/channel_points/custom_rewards",
            "broadcaster_id=" + _auth.UserId, null, ct);
        var list = new List<(string, string)>();
        if (res.ValueKind == JsonValueKind.Object && res.TryGetProperty("data", out var data))
            foreach (var r in data.EnumerateArray())
                list.Add((r.GetProperty("id").GetString() ?? "", r.GetProperty("title").GetString() ?? ""));
        return list;
    }

    private static Dictionary<string, object> RewardBody(RewardDef r)
    {
        var body = new Dictionary<string, object>
        {
            ["title"] = r.Title.Trim(),
            ["cost"] = r.Cost,
            ["prompt"] = r.Prompt ?? "",
            ["is_enabled"] = r.Enabled,
            // false - чтобы активации попадали в очередь, и программа могла отметить их выполненными / вернуть баллы
            ["should_redemptions_skip_request_queue"] = false,
        };
        if (Regex.IsMatch(r.Color ?? "", "^#[0-9A-Fa-f]{6}$")) body["background_color"] = r.Color;
        if (r.Cooldown > 0)
        {
            body["is_global_cooldown_enabled"] = true;
            body["global_cooldown_seconds"] = r.Cooldown;
        }
        else body["is_global_cooldown_enabled"] = false;
        return body;
    }

    public Task CreateRewardAsync(RewardDef r, CancellationToken ct)
        => RequestAsync(HttpMethod.Post, "/channel_points/custom_rewards",
            "broadcaster_id=" + _auth.UserId, RewardBody(r), ct);

    /// <summary>Обновляет уже созданную награду (цена, перезарядка, цвет, описание, включена ли).</summary>
    public Task UpdateRewardAsync(string rewardId, RewardDef r, CancellationToken ct)
        => RequestAsync(HttpMethod.Patch, "/channel_points/custom_rewards",
            $"broadcaster_id={_auth.UserId}&id={rewardId}", RewardBody(r), ct);

    /// <summary>Показывает или скрывает награду на канале (остальные настройки не трогает).</summary>
    public Task SetRewardEnabledAsync(string rewardId, bool enabled, CancellationToken ct)
        => RequestAsync(HttpMethod.Patch, "/channel_points/custom_rewards",
            $"broadcaster_id={_auth.UserId}&id={rewardId}",
            new Dictionary<string, object> { ["is_enabled"] = enabled }, ct);

    /// <summary>Меняет только цену награды (для «дорожающих» наград).</summary>
    public Task SetRewardCostAsync(string rewardId, int cost, CancellationToken ct)
        => RequestAsync(HttpMethod.Patch, "/channel_points/custom_rewards",
            $"broadcaster_id={_auth.UserId}&id={rewardId}",
            new Dictionary<string, object> { ["cost"] = Math.Max(1, cost) }, ct);

    /// <summary>status: FULFILLED (выполнено) или CANCELED (отмена и возврат баллов).</summary>
    public Task UpdateRedemptionAsync(string rewardId, string redemptionId, string status, CancellationToken ct)
        => RequestAsync(HttpMethod.Patch, "/channel_points/custom_rewards/redemptions",
            $"id={redemptionId}&broadcaster_id={_auth.UserId}&reward_id={rewardId}",
            new Dictionary<string, string> { ["status"] = status }, ct);
}
