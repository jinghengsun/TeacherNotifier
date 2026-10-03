using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeacherNotifier.Models;

namespace TeacherNotifier.Services;

/// <summary>
/// 调用机器人框架（NapCat / SnowLuma）的 OneBot 11 HTTP API。
///
/// <para>两个框架的行为已核对一致：</para>
/// <list type="bullet">
/// <item>HTTP 上报签名：<c>x-signature: sha1=&lt;HMAC-SHA1(key=accessToken, data=body)&gt;</c></item>
/// <item>反向 WebSocket 握手：<c>Authorization: Bearer &lt;accessToken&gt;</c></item>
/// <item>业务失败以 HTTP 200 + <c>status: "failed"</c> + 非零 <c>retcode</c> 返回</item>
/// </list>
/// </summary>
public sealed class NapCatApiClient
{
    /// <summary>单例 HttpClient，避免每条消息 new 一个实例导致连接池与 socket 耗尽。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly AppSettings _settings;
    private readonly ILogger<NapCatApiClient> _logger;

    public NapCatApiClient(AppSettings settings, ILogger<NapCatApiClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>API 基地址（来自设置，去掉末尾斜杠）。未配置时返回 null。</summary>
    private string? BaseUrl
    {
        get
        {
            var url = _settings.NapCatApiUrl?.Trim();
            return string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/');
        }
    }

    /// <summary>是否已配置 API 地址（决定要不要尝试联网查询）。</summary>
    public bool IsConfigured => BaseUrl is not null;

    /// <summary>
    /// 调用一个 OneBot action，返回 <c>data</c> 字段；失败（网络异常、非 200、status != ok、retcode != 0）返回 null。
    /// </summary>
    /// <param name="action">action 名称，如 <c>get_friend_list</c>。</param>
    /// <param name="payload">请求参数对象。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<JsonElement?> CallAsync(string action, object? payload = null, CancellationToken ct = default)
    {
        var baseUrl = BaseUrl;
        if (baseUrl is null)
            return null;

        try
        {
            var json = JsonSerializer.Serialize(payload ?? new { });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{action}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var token = _settings.NapCatApiToken?.Trim();
            if (!string.IsNullOrEmpty(token))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

            using var response = await Http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("调用 {Action} 失败：HTTP {Status}，响应：{Body}",
                    action, (int)response.StatusCode, Truncate(body));
                return null;
            }

            // OneBot 的业务失败是 HTTP 200 + status=failed + 非零 retcode，
            // 只看 IsSuccessStatusCode 会把失败当成功、静默吞掉错误信息。
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            var retcode = root.TryGetProperty("retcode", out var rc) && rc.ValueKind == JsonValueKind.Number
                ? rc.GetInt32()
                : -1;

            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) || retcode != 0)
            {
                var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : "";
                _logger.LogWarning("调用 {Action} 返回失败：status={Status} retcode={Retcode} message={Message}",
                    action, status ?? "(无)", retcode, message);
                return null;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            // 复制成独立文档，脱离 using(doc) 的生命周期
            return data.Clone();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "调用 {Action} 时发生异常", action);
            return null;
        }
    }

    /// <summary>
    /// 取机器人账号的好友列表，返回 QQ号 → (昵称, 备注) 的字典。
    ///
    /// <para>这是获取「备注」最可靠的途径：<c>get_stranger_info</c> 面向非好友，
    /// 对好友的备注经常为空；而 <c>get_friend_list</c> 的 remark 是好友备注本身。</para>
    /// </summary>
    /// <param name="noCache">是否绕过框架缓存。</param>
    public async Task<Dictionary<long, FriendInfo>?> GetFriendListAsync(bool noCache = false, CancellationToken ct = default)
    {
        var data = await CallAsync("get_friend_list", new { no_cache = noCache }, ct);
        if (data is not { ValueKind: JsonValueKind.Array })
            return null;

        var map = new Dictionary<long, FriendInfo>();
        foreach (var item in data.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadLong(item, "user_id");
            if (id == 0)
                continue;

            map[id] = new FriendInfo(ReadString(item, "nickname"), ReadString(item, "remark"));
        }

        return map;
    }

    /// <summary>取陌生人信息（含 remark 字段，但仅对非好友或框架能解析到的账号可靠）。</summary>
    public async Task<string> GetStrangerRemarkAsync(long userId, CancellationToken ct = default)
    {
        // 两个框架的参数类型不一致，见方法内注释。
        var data = await CallAsync("get_stranger_info",
            new { user_id = userId.ToString(), no_cache = true }, ct);
        if (data is not { ValueKind: JsonValueKind.Object })
            return "";

        return ReadString(data.Value, "remark");
    }

    /// <summary>取群成员信息，返回群名片（card）。群消息场景下的回退来源。</summary>
    public async Task<string> GetGroupMemberCardAsync(long groupId, long userId, CancellationToken ct = default)
    {
        var data = await CallAsync("get_group_member_info",
            new { group_id = groupId.ToString(), user_id = userId.ToString(), no_cache = true }, ct);
        if (data is not { ValueKind: JsonValueKind.Object })
            return "";

        return ReadString(data.Value, "card");
    }

    /// <summary>安全读取字符串属性：属性不存在、值为 null 或类型不符时返回空串。</summary>
    private static string ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
            return "";
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.ToString(),
            _ => ""
        };
    }

    /// <summary>安全读取整数属性：兼容框架返回数字或字符串两种形态。</summary>
    private static long ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
            return 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out var v) ? v : 0,
            JsonValueKind.String => long.TryParse(el.GetString(), out var v) ? v : 0,
            _ => 0
        };
    }

    private static string Truncate(string text, int max = 200)
        => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>好友列表中的一项。</summary>
/// <param name="Nickname">好友昵称。</param>
/// <param name="Remark">机器人账号给该好友设置的备注，可能为空。</param>
public readonly record struct FriendInfo(string Nickname, string Remark);
