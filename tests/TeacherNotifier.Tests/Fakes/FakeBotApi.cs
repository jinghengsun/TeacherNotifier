using System.Net;
using System.Text;
using System.Text.Json;

namespace TeacherNotifier.Tests.Fakes;

/// <summary>
/// 模拟 NapCat / SnowLuma 的 OneBot 11 HTTP API。
/// 只实现被测代码会调用的 action，并允许测试用例随时改返回值、注入失败。
/// </summary>
public sealed class FakeBotApi : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private Task? _loop;

    /// <summary>get_friend_list 的返回内容（裸 JSON 数组）。</summary>
    public volatile string FriendList = "[]";

    /// <summary>get_stranger_info 返回的 remark。</summary>
    public volatile string StrangerRemark = "";

    /// <summary>get_group_member_info 返回的 card。</summary>
    public volatile string GroupCard = "";

    /// <summary>为 true 时 get_friend_list 返回业务失败（HTTP 200 + status=failed）。</summary>
    public volatile bool FriendListFails;

    private int _friendListCalls;
    private int _strangerCalls;
    private int _groupMemberCalls;

    public int FriendListCalls => _friendListCalls;
    public int StrangerCalls => _strangerCalls;
    public int GroupMemberCalls => _groupMemberCalls;

    public FakeBotApi(int port) => _port = port;

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }

                _ = Task.Run(() => HandleAsync(ctx));
            }
        });
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var action = ctx.Request.Url?.AbsolutePath.Trim('/') ?? "";
        string payload;

        switch (action)
        {
            case "get_friend_list":
                Interlocked.Increment(ref _friendListCalls);
                payload = FriendListFails
                    ? """{"status":"failed","retcode":1400,"data":null,"message":"请求参数错误或业务逻辑执行失败"}"""
                    : "{\"status\":\"ok\",\"retcode\":0,\"data\":" + FriendList + "}";
                break;

            case "get_stranger_info":
                Interlocked.Increment(ref _strangerCalls);
                payload = "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"user_id\":0,\"nickname\":\"某人\",\"remark\":" +
                          JsonSerializer.Serialize(StrangerRemark) + ",\"sex\":\"unknown\"}}";
                break;

            case "get_group_member_info":
                Interlocked.Increment(ref _groupMemberCalls);
                payload = "{\"status\":\"ok\",\"retcode\":0,\"data\":{\"group_id\":0,\"user_id\":0,\"nickname\":\"某人\",\"card\":" +
                          JsonSerializer.Serialize(GroupCard) + ",\"role\":\"member\"}}";
                break;

            default:
                payload = """{"status":"failed","retcode":1404,"data":null,"message":"不支持的Api"}""";
                break;
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
        if (_loop is not null)
            await Task.WhenAny(_loop, Task.Delay(1000));
    }
}
