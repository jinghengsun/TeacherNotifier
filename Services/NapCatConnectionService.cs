using System.ComponentModel;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeacherNotifier.Models;

namespace TeacherNotifier.Services;

/// <summary>
/// 机器人框架（NapCat / SnowLuma）的消息接收服务。内置一个监听器：
/// <list type="bullet">
/// <item>HTTP 模式：接收 HTTP POST 事件上报；</item>
/// <item>WebSocket 模式：作为反向 WebSocket 服务端，接受框架主动连接。</item>
/// </list>
/// 收到符合条件的消息后触发 <see cref="TeacherMessageReceived"/> 事件。
///
/// <para>两个框架的事件与鉴权格式均已核对一致，可互换使用：</para>
/// <list type="bullet">
/// <item>HTTP 上报：<c>x-signature: sha1=&lt;HMAC-SHA1(key=访问令牌, data=请求体)&gt;</c></item>
/// <item>反向 WS 握手：<c>Authorization: Bearer &lt;访问令牌&gt;</c></item>
/// <item>自己发送的消息：<c>post_type = "message_sent"</c>（SnowLuma 会这样标记）</item>
/// </list>
/// </summary>
public class NapCatConnectionService : IHostedService
{
    /// <summary>备注缓存有效期。只缓存非空结果，过期后会重新查询。</summary>
    private static readonly TimeSpan RemarkTtl = TimeSpan.FromMinutes(30);

    /// <summary>好友列表缓存有效期。一次拉全量好友，避免逐条消息查询。</summary>
    private static readonly TimeSpan FriendListTtl = TimeSpan.FromMinutes(10);

    private readonly AppSettings _settings;
    private readonly ILogger<NapCatConnectionService> _logger;
    private readonly NapCatApiClient _api;
    private readonly object _cacheLock = new();

    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private HttpListener? _listener;

    /// <summary>user_id → (备注, 过期时间)。仅缓存非空结果。</summary>
    private readonly Dictionary<long, (string Remark, DateTime ExpireAt)> _remarkCache = new();

    /// <summary>进行中的备注查询，用于并发去重（同一 QQ 连发多条消息时只查一次）。</summary>
    private readonly Dictionary<long, Task<string>> _inflight = new();

    private Dictionary<long, FriendInfo>? _friendList;
    private DateTime _friendListFetchedAt = DateTime.MinValue;

    /// <summary>收到老师消息时触发。</summary>
    public event EventHandler<TeacherMessage>? TeacherMessageReceived;

    public NapCatConnectionService(AppSettings settings, ILogger<NapCatConnectionService> logger, NapCatApiClient api)
    {
        _settings = settings;
        _logger = logger;
        _api = api;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AppSettings.ConnectionMode) or nameof(AppSettings.Port)))
            return;
        _logger.LogInformation("连接设置已更改，正在重启监听……");
        Restart();
    }

    private void Restart()
    {
        // 仅停止当前监听；RunLoopAsync 的 while 循环会退出当前 ListenAsync 并自动重新进入，
        // 避免旧循环与新循环并发竞争（否则会 ObjectDisposedException + 端口 183 冲突）
        var listener = _listener;
        if (listener != null)
        {
            try { listener.Stop(); } catch { /* ignore */ }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listenTask = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        var listener = _listener;
        if (listener != null)
        {
            try { listener.Stop(); } catch { /* ignore */ }
            try { listener.Close(); } catch { /* ignore */ }
        }
        if (_listenTask is not null)
        {
            await Task.WhenAny(_listenTask, Task.Delay(3000, cancellationToken));
        }
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "监听启动失败，3 秒后重试。若为 Access Denied，请以管理员运行：netsh http add urlacl url=http://+:{Port}/ user=Everyone",
                    _settings.Port);
                try { await Task.Delay(3000, token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        var port = (int)_settings.Port;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _listener = listener;

        var mode = _settings.ConnectionMode == 1 ? "反向 WebSocket" : "HTTP POST 上报";
        _logger.LogInformation("监听已启动：http://127.0.0.1:{Port}/ （模式：{Mode}）", port, mode);

        while (listener.IsListening && !token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (Exception)
            {
                break; // listener 被 Stop（Restart 触发）或出错，退出循环
            }

            _ = Task.Run(() => HandleContextAsync(ctx, token), CancellationToken.None);
        }

        try { listener.Stop(); } catch { /* ignore */ }
        try { listener.Close(); } catch { /* ignore */ }
        if (ReferenceEquals(_listener, listener))
            _listener = null;

        // 端口/模式变更导致的退出：等端口完全释放再让外层循环重启，避免 183 冲突
        if (!token.IsCancellationRequested)
            await Task.Delay(500, token);
    }

    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken token)
    {
        try
        {
            // WebSocket 握手：校验 Authorization / access_token
            if (_settings.ConnectionMode == 1 && ctx.Request.IsWebSocketRequest)
            {
                if (!CheckWebSocketToken(ctx))
                {
                    _logger.LogWarning("WebSocket 握手 Token 校验失败，已拒绝。请确认「访问令牌」与框架里反向 WS 的 access token 一致。");
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Close();
                    return;
                }
                await HandleWebSocketAsync(ctx, token);
                return;
            }

            if (ctx.Request.HttpMethod == "POST")
            {
                // 读原始字节，供 x-signature 校验使用（必须用原始字节，不能先转字符串再取字节）
                byte[] rawBody;
                using (var ms = new MemoryStream())
                {
                    await ctx.Request.InputStream.CopyToAsync(ms, token);
                    rawBody = ms.ToArray();
                }
                var body = Encoding.UTF8.GetString(rawBody);

                if (!CheckHttpToken(ctx, rawBody))
                {
                    _logger.LogWarning("收到未携带有效 Token 的请求，已拒绝。请确认「访问令牌」与框架里 HTTP 上报的 access token 一致。");
                    await RespondAsync(ctx, 401, "{\"error\":\"unauthorized\"}");
                    return;
                }

                await ProcessEventAsync(body, token);
                await RespondAsync(ctx, 200, "{\"status\":\"ok\"}");
                return;
            }

            // 浏览器访问 / 健康检查
            await RespondAsync(ctx, 200,
                _settings.ConnectionMode == 1
                    ? "TeacherNotifier 运行中（WebSocket 模式）"
                    : "TeacherNotifier 运行中（HTTP POST 模式）");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理上报请求失败");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* ignore */ }
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken token)
    {
        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        var ws = wsCtx.WebSocket;
        _logger.LogInformation("反向 WebSocket 已连接。");

        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        try
        {
            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                sb.Clear();
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType != WebSocketMessageType.Close)
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                    await ProcessEventAsync(sb.ToString(), token);
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("反向 WebSocket 连接异常中断：{Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理反向 WebSocket 消息失败");
        }

        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { /* ignore */ }
        _logger.LogInformation("反向 WebSocket 已断开。");
    }

    /// <summary>校验 HTTP 上报请求的 Token。Token 为空时不校验。</summary>
    private bool CheckHttpToken(HttpListenerContext ctx, byte[] rawBody)
    {
        var token = _settings.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return true;

        // NapCat 与 SnowLuma 的 HTTP 上报都用：x-signature: sha1=HMAC-SHA1(key=token, data=body)
        var sig = ctx.Request.Headers["x-signature"];
        if (!string.IsNullOrEmpty(sig))
        {
            var expected = "sha1=" + ComputeSignature(token, rawBody);
            if (string.Equals(sig.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 兼容其他 OneBot 实现的 Authorization: Bearer <token>
        var auth = ctx.Request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(auth))
        {
            var provided = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? auth.Substring(7).Trim()
                : auth.Trim();
            if (string.Equals(provided, token, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>校验 WebSocket 握手请求的 Token。Token 为空时不校验。</summary>
    private bool CheckWebSocketToken(HttpListenerContext ctx)
    {
        var token = _settings.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return true;

        // NapCat 与 SnowLuma 的反向 WS 客户端握手都发 Authorization: Bearer <token>
        var auth = ctx.Request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(auth))
        {
            var provided = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? auth.Substring(7).Trim()
                : auth.Trim();
            if (string.Equals(provided, token, StringComparison.Ordinal))
                return true;
        }

        // ?access_token=<token> 查询参数（SnowLuma 也支持这种形式）
        var qs = ctx.Request.QueryString["access_token"];
        if (!string.IsNullOrEmpty(qs) && string.Equals(qs, token, StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// 计算 <c>HMAC-SHA1(key = token, data = body)</c> 的小写十六进制摘要。
    ///
    /// <para>注意：这<b>不是</b> <c>SHA1(token + body)</c>。NapCat
    /// （<c>packages/napcat-onebot/network/http-client.ts</c>）与 SnowLuma
    /// （<c>packages/onebot/src/network/http-post-adapter.ts</c>）用的都是
    /// <c>createHmac('sha1', token).update(body).digest('hex')</c>。</para>
    /// </summary>
    private static string ComputeSignature(string token, byte[] body)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private static async Task RespondAsync(HttpListenerContext ctx, int code, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    /// <summary>解析 OneBot 11 事件 JSON，过滤、查备注并触发事件。</summary>
    private async Task ProcessEventAsync(string json, CancellationToken token)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return;

            // 只处理消息事件
            if (!root.TryGetProperty("post_type", out var postType) || postType.ValueKind != JsonValueKind.String)
                return;
            var postTypeText = postType.GetString();

            // SnowLuma 会把自己发送的消息标记为 message_sent，直接忽略
            if (postTypeText == "message_sent")
                return;

            if (postTypeText != "message")
                return;

            var messageType = ReadString(root, "message_type");
            var userId = ReadLong(root, "user_id");
            var groupId = ReadLong(root, "group_id");

            // 忽略机器人自己发送的消息。
            // SnowLuma 用 post_type=message_sent 标记（上面已拦掉），
            // NapCat 开启「上报自身消息」时可能仍用 post_type=message，故保留这条兜底。
            if (_settings.IgnoreSelfMessages && userId != 0 && ReadLong(root, "self_id") == userId)
                return;

            if (!ShouldNotify(userId, groupId))
                return;

            var senderName = ExtractSenderName(root, userId);
            var text = ExtractMessageText(root);

            // 查备注优先用好友列表（备注就在这里），必要时回退群名片 / 陌生人信息
            var remark = await ResolveRemarkAsync(userId, groupId, token);

            TeacherMessageReceived?.Invoke(this, new TeacherMessage
            {
                UserId = userId,
                GroupId = groupId,
                SenderName = senderName,
                Remark = remark,
                Message = text,
                MessageType = messageType
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("上报的事件不是合法 JSON，已忽略：{Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析消息事件失败");
        }
    }

    /// <summary>
    /// 解析发送者备注，按可靠性从高到低回退：
    /// <list type="number">
    /// <item>设置里的手动备注映射（不依赖任何 API）</item>
    /// <item>备注缓存</item>
    /// <item><c>get_friend_list</c> 的好友备注</item>
    /// <item>群消息场景下 <c>get_group_member_info</c> 的群名片</item>
    /// <item><c>get_stranger_info</c> 的 remark（面向非好友，可靠性最低）</item>
    /// <item>事件自带的群名片 / 昵称（判断逻辑在 <see cref="ExtractSenderName"/>）</item>
    /// </list>
    /// 查不到时返回空串，由通知侧回退到昵称。
    /// </summary>
    private async Task<string> ResolveRemarkAsync(long userId, long groupId, CancellationToken token)
    {
        // 1) 手动映射兜底：不依赖 API，永远最可靠
        var manual = ResolveRemarkFromMap(userId);
        if (!string.IsNullOrWhiteSpace(manual))
            return manual;

        if (userId == 0)
            return "";

        // 2) 缓存
        lock (_cacheLock)
        {
            if (_remarkCache.TryGetValue(userId, out var entry) && entry.ExpireAt > DateTime.UtcNow)
                return entry.Remark;
        }

        // 3) 并发去重：同一个 QQ 连发多条消息时只查一次
        Task<string> task;
        lock (_cacheLock)
        {
            if (!_inflight.TryGetValue(userId, out var existing))
            {
                existing = QueryRemarkAsync(userId, groupId, token);
                _inflight[userId] = existing;
            }
            task = existing;
        }

        try
        {
            return await task;
        }
        finally
        {
            lock (_cacheLock)
            {
                if (_inflight.TryGetValue(userId, out var t) && ReferenceEquals(t, task))
                    _inflight.Remove(userId);
            }
        }
    }

    /// <summary>实际的备注查询流程（不含缓存与去重）。</summary>
    private async Task<string> QueryRemarkAsync(long userId, long groupId, CancellationToken token)
    {
        if (!_api.IsConfigured)
            return "";

        // 1) 好友列表：备注的正确来源
        var friends = await GetFriendListCachedAsync(token);
        if (friends is not null && friends.TryGetValue(userId, out var friend))
        {
            var remark = friend.Remark;
            if (string.IsNullOrWhiteSpace(remark))
                remark = friend.Nickname; // 好友没设备注时，昵称也比 QQ 号好
            if (!string.IsNullOrWhiteSpace(remark))
                return Cache(userId, remark);
        }

        // 2) 群消息：退到群名片
        if (groupId != 0)
        {
            var card = await _api.GetGroupMemberCardAsync(groupId, userId, token);
            if (!string.IsNullOrWhiteSpace(card))
                return Cache(userId, card);
        }

        // 3) 陌生人信息兜底（面向非好友，好友场景经常返回空）
        var stranger = await _api.GetStrangerRemarkAsync(userId, token);
        if (!string.IsNullOrWhiteSpace(stranger))
            return Cache(userId, stranger);

        // 关键：不要把空结果写进缓存，否则该 QQ 的备注将永远不再重试
        return "";
    }

    private string Cache(long userId, string remark)
    {
        lock (_cacheLock)
        {
            _remarkCache[userId] = (remark, DateTime.UtcNow + RemarkTtl);
        }
        return remark;
    }

    /// <summary>清空好友列表与备注缓存，用于测试注入新数据。仅供同一程序集内的测试使用。</summary>
    internal void InvalidateCachesForTest()
    {
        lock (_cacheLock)
        {
            _friendList = null;
            _friendListFetchedAt = DateTime.MinValue;
            _remarkCache.Clear();
        }
    }

    /// <summary>取好友列表（带 TTL 缓存）。拉取失败返回 null 且不写缓存，以便下次重试。</summary>
    private async Task<Dictionary<long, FriendInfo>?> GetFriendListCachedAsync(CancellationToken token)
    {
        lock (_cacheLock)
        {
            if (_friendList is not null && DateTime.UtcNow - _friendListFetchedAt < FriendListTtl)
                return _friendList;
        }

        var fetched = await _api.GetFriendListAsync(noCache: true, token);
        if (fetched is null)
            return null; // 拉取失败：不缓存，下次重试

        lock (_cacheLock)
        {
            _friendList = fetched;
            _friendListFetchedAt = DateTime.UtcNow;
        }
        _logger.LogInformation("已获取好友列表，共 {Count} 个好友。", fetched.Count);
        return fetched;
    }

    /// <summary>从手动备注映射（QQ号=备注名）中解析某 QQ 的备注。未配置或未匹配返回空串。</summary>
    private string ResolveRemarkFromMap(long userId)
    {
        var map = _settings.RemarkMap;
        if (string.IsNullOrWhiteSpace(map))
            return "";

        foreach (var entry in map.Split(new[] { ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = entry.IndexOf('=');
            if (idx <= 0)
                continue;
            var qq = entry.Substring(0, idx).Trim();
            var name = entry.Substring(idx + 1).Trim();
            if (qq == userId.ToString())
                return name;
        }
        return "";
    }

    private bool ShouldNotify(long userId, long groupId)
    {
        var teacherQQs = ParseLongList(_settings.TeacherQQs);
        var groupIds = ParseLongList(_settings.GroupIds);

        // 群白名单：非空时，群消息必须来自白名单群；私聊不受群白名单限制
        if (groupId != 0 && groupIds.Count > 0 && !groupIds.Contains(groupId))
            return false;

        // 发送者白名单
        if (teacherQQs.Count > 0)
            return teacherQQs.Contains(userId);

        // 白名单为空
        return _settings.AllowAllWhenEmpty;
    }

    private static List<long> ParseLongList(string raw)
    {
        var list = new List<long>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var part in raw.Split(new[] { ',', '，', ' ', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part.Trim(), out var v))
                list.Add(v);
        }
        return list;
    }

    private static string ExtractSenderName(JsonElement root, long userId)
    {
        if (root.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object)
        {
            var card = ReadString(sender, "card");
            if (!string.IsNullOrWhiteSpace(card))
                return card;
            var nick = ReadString(sender, "nickname");
            if (!string.IsNullOrWhiteSpace(nick))
                return nick;
        }
        return userId.ToString();
    }

    private static string ExtractMessageText(JsonElement root)
    {
        // 优先从 message 数组提取 text 段
        if (root.TryGetProperty("message", out var message))
        {
            if (message.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var seg in message.EnumerateArray())
                {
                    if (seg.ValueKind != JsonValueKind.Object) continue;
                    if (!seg.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
                    if (type.GetString() != "text") continue;
                    if (!seg.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                    sb.Append(ReadString(data, "text"));
                }
                var joined = sb.ToString();
                if (!string.IsNullOrWhiteSpace(joined))
                    return joined;
            }
            else if (message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? "";
            }
        }

        // 回退到 raw_message
        var raw = ReadString(root, "raw_message");
        return string.IsNullOrWhiteSpace(raw) ? "" : raw;
    }

    /// <summary>
    /// 安全读取字符串属性。
    ///
    /// <para>必须判断 <c>ValueKind</c>：<c>GetString()</c> 遇到 JSON null 或非字符串会抛异常，
    /// 而异常会让整条消息被丢弃（例如 SnowLuma 的群消息在成员无群名片时 <c>card</c> 可能为 null）。</para>
    /// </summary>
    private static string ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return "";
        if (!obj.TryGetProperty(name, out var el))
            return "";
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.ToString(),
            _ => ""
        };
    }

    /// <summary>安全读取整数属性：兼容框架返回数字或字符串。</summary>
    private static long ReadLong(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return 0;
        if (!obj.TryGetProperty(name, out var el))
            return 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out var v) ? v : 0,
            JsonValueKind.String => long.TryParse(el.GetString(), out var v) ? v : 0,
            _ => 0
        };
    }
}
