using System.ComponentModel;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeacherNotifier.Models;

namespace TeacherNotifier.Services;

/// <summary>
/// NapCat 消息接收服务。内置一个监听器：
/// - HTTP 模式：接收 NapCat 的 HTTP POST 事件上报；
/// - WebSocket 模式：作为反向 WebSocket 服务端，接受 NapCat 主动连接。
/// 收到符合条件的消息后触发 <see cref="TeacherMessageReceived"/> 事件。
/// </summary>
public class NapCatConnectionService : IHostedService
{
    private readonly AppSettings _settings;
    private readonly ILogger<NapCatConnectionService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    /// <summary>user_id → 备注 的缓存，避免每条消息都查 API。</summary>
    private readonly Dictionary<long, string> _remarkCache = new();

    /// <summary>收到老师消息时触发。</summary>
    public event EventHandler<TeacherMessage>? TeacherMessageReceived;

    public NapCatConnectionService(AppSettings settings, ILogger<NapCatConnectionService> logger)
    {
        _settings = settings;
        _logger = logger;
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

    private HttpListener? _listener;

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
                _logger.LogError(ex, "NapCat 监听启动失败，3 秒后重试。若为 Access Denied，请以管理员运行：netsh http add urlacl url=http://+:{Port}/ user=Everyone",
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
        _logger.LogInformation("NapCat 监听已启动：http://127.0.0.1:{Port}/ （模式：{Mode}）", port, mode);

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

            _ = Task.Run(() => HandleContextAsync(ctx), CancellationToken.None);
        }

        try { listener.Stop(); } catch { /* ignore */ }
        try { listener.Close(); } catch { /* ignore */ }
        if (ReferenceEquals(_listener, listener))
            _listener = null;

        // 端口/模式变更导致的退出：等端口完全释放再让外层循环重启，避免 183 冲突
        if (!token.IsCancellationRequested)
            await Task.Delay(500, token);
    }

    private async Task HandleContextAsync(HttpListenerContext ctx)
    {
        try
        {
            // WebSocket 握手：校验 Authorization / access_token
            if (_settings.ConnectionMode == 1 && ctx.Request.IsWebSocketRequest)
            {
                if (!CheckWebSocketToken(ctx))
                {
                    _logger.LogWarning("WebSocket 握手 Token 校验失败，已拒绝。");
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Close();
                    return;
                }
                await HandleWebSocketAsync(ctx);
                return;
            }

            if (ctx.Request.HttpMethod == "POST")
            {
                // 读原始字节，供 x-signature（sha1(token+body)）签名校验使用
                byte[] rawBody;
                using (var ms = new MemoryStream())
                {
                    await ctx.Request.InputStream.CopyToAsync(ms);
                    rawBody = ms.ToArray();
                }
                var body = Encoding.UTF8.GetString(rawBody);

                if (!CheckHttpToken(ctx, rawBody))
                {
                    _logger.LogWarning("收到未携带有效 Token 的请求，已拒绝。");
                    await RespondAsync(ctx, 401, "{\"error\":\"unauthorized\"}");
                    return;
                }

                await ProcessEventAsync(body);
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
            _logger.LogError(ex, "处理 NapCat 请求失败");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* ignore */ }
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext ctx)
    {
        var wsCtx = await ctx.AcceptWebSocketAsync(null);
        var ws = wsCtx.WebSocket;
        _logger.LogInformation("NapCat 反向 WebSocket 已连接。");

        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                await ProcessEventAsync(sb.ToString());
            }
            else if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            sb.Clear();
        }

        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { /* ignore */ }
        _logger.LogInformation("NapCat 反向 WebSocket 已断开。");
    }

    /// <summary>校验 HTTP 上报请求的 Token。Token 为空时不校验。</summary>
    private bool CheckHttpToken(HttpListenerContext ctx, byte[] rawBody)
    {
        var token = _settings.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return true;

        // 方式 1：NapCat HTTP Client 上报的签名 x-signature: sha1=sha1(token+body)
        var sig = ctx.Request.Headers["x-signature"];
        if (!string.IsNullOrEmpty(sig))
        {
            var expected = "sha1=" + ComputeSha1Hex(token, rawBody);
            if (string.Equals(sig.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 方式 2：标准 OneBot 11 的 Authorization: Bearer <token>（兼容其他实现）
        var auth = ctx.Request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(auth))
        {
            var provided = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? auth.Substring(7).Trim()
                : auth.Trim();
            if (provided == token)
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

        // Authorization: Bearer <token>
        var auth = ctx.Request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(auth))
        {
            var provided = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? auth.Substring(7).Trim()
                : auth.Trim();
            if (provided == token)
                return true;
        }

        // ?access_token=<token> 查询参数
        var qs = ctx.Request.QueryString["access_token"];
        if (!string.IsNullOrEmpty(qs) && qs == token)
            return true;

        return false;
    }

    /// <summary>计算 sha1(token + body) 的小写十六进制摘要，匹配 NapCat 的 x-signature。</summary>
    private static string ComputeSha1Hex(string token, byte[] body)
    {
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var combined = new byte[tokenBytes.Length + body.Length];
        Buffer.BlockCopy(tokenBytes, 0, combined, 0, tokenBytes.Length);
        Buffer.BlockCopy(body, 0, combined, tokenBytes.Length, body.Length);
        return Convert.ToHexString(sha1.ComputeHash(combined)).ToLowerInvariant();
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
    private async Task ProcessEventAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 只处理消息事件
            if (!root.TryGetProperty("post_type", out var postType) || postType.GetString() != "message")
                return;

            if (!root.TryGetProperty("message_type", out var mtEl))
                return;
            var messageType = mtEl.GetString() ?? "";

            // 忽略机器人自己发送的消息
            if (_settings.IgnoreSelfMessages && root.TryGetProperty("self_id", out var selfId)
                && root.TryGetProperty("user_id", out var uidSelf)
                && selfId.GetInt64() == uidSelf.GetInt64())
                return;

            long userId = root.TryGetProperty("user_id", out var uid) ? uid.GetInt64() : 0;
            long groupId = root.TryGetProperty("group_id", out var gid) ? gid.GetInt64() : 0;

            var senderName = ExtractSenderName(root, userId);
            var text = ExtractMessageText(root);

            if (!ShouldNotify(userId, groupId))
                return;

            // 主动查 NapCat API 获取机器人给发送者留的备注
            var remark = await ResolveRemarkAsync(userId);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析 NapCat 消息事件失败");
        }
    }

    /// <summary>通过 NapCat 的 get_stranger_info API 查询机器人给某 QQ 留的备注。查不到返回空串。</summary>
    private async Task<string> ResolveRemarkAsync(long userId)
    {
        // 手动映射兜底：优先用设置里配的「QQ=备注」
        var manual = ResolveRemarkFromMap(userId);
        if (!string.IsNullOrWhiteSpace(manual))
            return manual;

        var baseUrl = _settings.NapCatApiUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "";

        // 命中缓存直接返回
        if (_remarkCache.TryGetValue(userId, out var cached))
            return cached;

        try
        {
            var apiUrl = baseUrl.TrimEnd('/') + "/get_stranger_info";
            var payload = JsonSerializer.Serialize(new { user_id = userId.ToString() });
            var token = _settings.NapCatApiToken?.Trim() ?? "";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(token))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return "";

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // 标准 OneBot 响应：data.remark；兼容直接平铺的 remark
            string remark = "";
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("remark", out var r) && !string.IsNullOrWhiteSpace(r.GetString()))
                remark = r.GetString()!;
            else if (root.TryGetProperty("remark", out var r2) && !string.IsNullOrWhiteSpace(r2.GetString()))
                remark = r2.GetString()!;

            _remarkCache[userId] = remark;
            return remark;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询备注失败（user_id={UserId}），将回退到昵称/群名片", userId);
            return "";
        }
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
        if (root.TryGetProperty("sender", out var sender))
        {
            if (sender.TryGetProperty("card", out var card) && !string.IsNullOrWhiteSpace(card.GetString()))
                return card.GetString()!;
            if (sender.TryGetProperty("nickname", out var nick) && !string.IsNullOrWhiteSpace(nick.GetString()))
                return nick.GetString()!;
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
                    if (!seg.TryGetProperty("type", out var type) || type.GetString() != "text") continue;
                    if (seg.TryGetProperty("data", out var data) && data.TryGetProperty("text", out var text))
                        sb.Append(text.GetString());
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
        if (root.TryGetProperty("raw_message", out var raw) && !string.IsNullOrWhiteSpace(raw.GetString()))
            return raw.GetString()!;

        return "";
    }
}
