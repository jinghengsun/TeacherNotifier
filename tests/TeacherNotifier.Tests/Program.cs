using System.Text;
using Microsoft.Extensions.Logging;
using TeacherNotifier.Models;
using TeacherNotifier.Services;
using TeacherNotifier.Tests.Fakes;

namespace TeacherNotifier.Tests;

/// <summary>
/// TeacherNotifier 的端到端测试。
///
/// <para>测试方式是「真接收发」：真实启动 <see cref="NapCatConnectionService"/> 的
/// <c>HttpListener</c>，用与 NapCat / SnowLuma 相同的签名算法发上报，
/// 再由 <see cref="FakeBotApi"/> 扮演机器人框架的 OneBot HTTP API 响应备注查询。</para>
///
/// <para>覆盖重点：</para>
/// <list type="bullet">
/// <item>上报鉴权（HMAC-SHA1 接受、旧算法的错误签名必须被拒绝）</item>
/// <item>备注解析优先级与缓存行为</item>
/// <item>异常事件不丢消息（null 字段、缺字段）</item>
/// <item>自己发送的消息被忽略</item>
/// <item>API 失败后的恢复能力</item>
/// </list>
/// </summary>
public static class Program
{
    private const string Token = "TestToken123";
    private const int PluginPort = 18090;

    private static NapCatConnectionService _service = null!;
    private static FakeBotApi _fake = null!;
    private static List<TeacherMessage> _received = null!;

    public static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("TeacherNotifier 端到端测试");

        const int fakeApiPort = 18899;

        await using var fake = new FakeBotApi(fakeApiPort);
        fake.Start();
        _fake = fake;

        var settings = new AppSettings
        {
            ConnectionMode = 0,                          // HTTP POST 上报
            Port = PluginPort,
            AccessToken = Token,                         // 校验上报用的令牌
            NapCatApiUrl = $"http://127.0.0.1:{fakeApiPort}",
            NapCatApiToken = "",
            AllowAllWhenEmpty = true
        };

        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var api = new NapCatApiClient(settings, loggerFactory.CreateLogger<NapCatApiClient>());
        _service = new NapCatConnectionService(settings, loggerFactory.CreateLogger<NapCatConnectionService>(), api);
        await _service.StartAsync(CancellationToken.None);

        _received = [];
        _service.TeacherMessageReceived += (_, m) =>
        {
            lock (_received) _received.Add(m);
        };

        await Task.Delay(500); // 等监听器就绪

        TestKit.Section("0. API 客户端直连（确认好友列表解析本身正常）");
        await TestApiClientAsync(settings, loggerFactory);

        TestKit.Section("1. 上报签名与鉴权");
        await TestSignatureAndAuthAsync();

        TestKit.Section("2. 备注解析：好友列表优先、缓存、昵称回退");
        await TestRemarkFromFriendListAsync();

        TestKit.Section("3. 备注回退：非好友的群消息用群名片");
        await TestGroupCardFallbackAsync();

        TestKit.Section("4. 健壮性：字段为 null / 缺失时不丢消息");
        await TestNullFieldsAsync();

        TestKit.Section("5. 自己发送的消息与缓存行为");
        await TestSelfMessageAndCacheAsync();

        TestKit.Section("6. 好友列表拉取失败后可恢复");
        await TestFriendListRecoveryAsync();

        await _service.StopAsync(CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"================ 通过 {TestKit.PassCount}，失败 {TestKit.FailCount} ================");
        return TestKit.FailCount == 0 ? 0 : 1;
    }

    /// <summary>直接调用 API 客户端，验证 OneBot 响应解析（区别于通过上报链路验证）。</summary>
    private static async Task TestApiClientAsync(AppSettings settings, ILoggerFactory loggerFactory)
    {
        var api = new NapCatApiClient(settings, loggerFactory.CreateLogger<NapCatApiClient>());
        TestKit.Check("NapCatApiUrl 已配置时 IsConfigured 为 true", api.IsConfigured);

        _fake.FriendList = """
        [{"user_id":222,"nickname":"昵称甲","remark":"王老师"}]
        """;
        var list = await api.GetFriendListAsync(noCache: true);
        TestKit.Check("get_friend_list 能被正确解析",
            list is not null && list.ContainsKey(222) && list[222].Remark == "王老师");

        // 业务失败必须是 HTTP 200 + status=failed：只看状态码会把失败当成功
        _fake.FriendListFails = true;
        var failed = await api.GetFriendListAsync(noCache: true);
        TestKit.Check("HTTP 200 + status=failed 被识别为失败（返回 null）", failed is null);
        _fake.FriendListFails = false;
    }

    private static async Task TestSignatureAndAuthAsync()
    {
        var body = TestKit.PrivateMessage(111, "签名正确应被接受");

        var (code, _) = await TestKit.PostAsync(PluginPort, body, TestKit.SignCorrect(Token, body));
        await TestKit.WaitUntil(() => TestKit.CountFor(_received, 111) > 0);
        TestKit.Check("正确的 HMAC-SHA1 签名 → 200 且收到消息",
            code == 200 && TestKit.CountFor(_received, 111) == 1);

        // 反向验证：这是修复前的算法，必须被拒绝，否则说明签名校验形同虚设
        var (code2, _) = await TestKit.PostAsync(PluginPort, body, TestKit.SignWrongOldWay(Token, body));
        TestKit.Check("旧算法 SHA1(token+body) 的签名 → 401 且不产生消息",
            code2 == 401 && TestKit.CountFor(_received, 111) == 1);

        var (code3, _) = await TestKit.PostAsync(PluginPort, body, signature: null);
        TestKit.Check("不带任何签名 → 401", code3 == 401);

        var (code4, _) = await TestKit.PostAsync(PluginPort, body, "sha1=deadbeef");
        TestKit.Check("签名错误 → 401", code4 == 401);

        var (code5, _) = await TestKit.PostAsync(PluginPort, body, signature: null, authorization: "Bearer " + Token);
        TestKit.Check("兼容 Authorization: Bearer <token> → 200", code5 == 200);

        var (code6, _) = await TestKit.PostAsync(PluginPort, body, signature: null, authorization: "Bearer wrong");
        TestKit.Check("错误的 Bearer → 401", code6 == 401);
    }

    private static async Task TestRemarkFromFriendListAsync()
    {
        _service.InvalidateCachesForTest();

        // 好友 222 有备注；好友 223 无备注
        _fake.FriendList = """
        [{"user_id":222,"nickname":"昵称甲","remark":"王老师"},
         {"user_id":223,"nickname":"昵称乙","remark":""}]
        """;
        _fake.StrangerRemark = "";
        _fake.GroupCard = "";

        var body = TestKit.PrivateMessage(222, "备注应取好友备注");
        await TestKit.PostAsync(PluginPort, body, TestKit.SignCorrect(Token, body));
        await TestKit.WaitUntil(() => TestKit.CountFor(_received, 222) > 0);

        TestKit.Check("好友备注优先：{remark} = 王老师", TestKit.LastFor(_received, 222)?.Remark == "王老师");
        TestKit.Check("确实调用了 get_friend_list", _fake.FriendListCalls > 0);

        // 备注命中缓存后不应再拉好友列表
        var callsAfterFirst = _fake.FriendListCalls;
        var body2 = TestKit.PrivateMessage(222, "第二条");
        await TestKit.PostAsync(PluginPort, body2, TestKit.SignCorrect(Token, body2));
        await TestKit.WaitUntil(() => TestKit.CountFor(_received, 222) > 1);
        TestKit.Check("备注缓存生效：第二条消息不重复拉好友列表",
            _fake.FriendListCalls == callsAfterFirst);

        // 好友没设备注 → 回退昵称（旧实现这里会返回空）
        var body3 = TestKit.PrivateMessage(223, "好友无备注应回退昵称");
        await TestKit.PostAsync(PluginPort, body3, TestKit.SignCorrect(Token, body3));
        await TestKit.WaitUntil(() => TestKit.CountFor(_received, 223) > 0);
        TestKit.Check("好友无备注时回退昵称：{remark} = 昵称乙",
            TestKit.LastFor(_received, 223)?.Remark == "昵称乙");
    }

    private static async Task TestGroupCardFallbackAsync()
    {
        // 444 不在好友列表里，但在群里发言 → 应回退到群名片
        _fake.GroupCard = "李老师（群名片）";
        var body = TestKit.GroupMessage(999, 444, "群里发言的老师");
        await TestKit.PostAsync(PluginPort, body, TestKit.SignCorrect(Token, body));
        await TestKit.WaitUntil(() => TestKit.CountFor(_received, 444) > 0);

        TestKit.Check("非好友的群消息回退群名片：{remark} = 李老师（群名片）",
            TestKit.LastFor(_received, 444)?.Remark == "李老师（群名片）");
    }

    private static async Task TestNullFieldsAsync()
    {
        // SnowLuma 的群消息在成员没有群名片时 card 可能为 null；
        // 旧实现直接 GetString() 会抛异常并导致整条消息被丢弃。
        var body = """
        {"post_type":"message","message_type":"group","self_id":10001,"user_id":555,
         "group_id":888,"sender":{"user_id":555,"nickname":"昵称丙","card":null,"role":"member"},
         "message":[{"type":"text","data":{"text":"card 为 null 也要能收到"}}],"raw_message":"..."}
        """;
        var (code, _) = await TestKit.PostAsync(PluginPort, body, TestKit.SignCorrect(Token, body));
        await TestKit.WaitUntil(() => TestKit.CountFor(_received, 555) > 0);

        TestKit.Check("sender.card 为 null 时不抛异常、消息正常送达",
            code == 200 && TestKit.CountFor(_received, 555) == 1);
        TestKit.Check("card 为 null 时回退昵称", TestKit.LastFor(_received, 555)?.SenderName == "昵称丙");

        // 完全没有 sender 节点
        var body2 = """
        {"post_type":"message","message_type":"private","self_id":10001,"user_id":556,
         "message":[{"type":"text","data":{"text":"无 sender 节点"}}]}
        """;
        var (code2, _) = await TestKit.PostAsync(PluginPort, body2, TestKit.SignCorrect(Token, body2));
        await TestKit.WaitUntil(() => TestKit.CountFor(_received, 556) > 0);
        TestKit.Check("缺少 sender 节点时不抛异常", code2 == 200 && TestKit.CountFor(_received, 556) == 1);
    }

    private static async Task TestSelfMessageAndCacheAsync()
    {
        // SnowLuma 用 post_type=message_sent 标记机器人自己发的消息
        var sent = """
        {"post_type":"message_sent","message_type":"private","self_id":10001,"user_id":777,
         "sender":{"user_id":10001,"nickname":"机器人"},"message":[{"type":"text","data":{"text":"自己发的"}}]}
        """;
        await TestKit.PostAsync(PluginPort, sent, TestKit.SignCorrect(Token, sent));
        await Task.Delay(300);
        TestKit.Check("post_type=message_sent 被忽略", TestKit.CountFor(_received, 777) == 0);

        // NapCat 若上报自身消息：post_type=message 且 self_id == user_id
        var selfEcho = """
        {"post_type":"message","message_type":"private","self_id":10001,"user_id":10001,
         "sender":{"user_id":10001,"nickname":"机器人"},"message":[{"type":"text","data":{"text":"自己发的"}}]}
        """;
        await TestKit.PostAsync(PluginPort, selfEcho, TestKit.SignCorrect(Token, selfEcho));
        await Task.Delay(300);
        TestKit.Check("self_id == user_id 被忽略", TestKit.CountFor(_received, 10001) == 0);

        // 查不到备注的 QQ 不应被写入缓存，后续消息仍会重试
        _fake.StrangerRemark = "";
        _fake.FriendList = "[]";
        _fake.GroupCard = "";

        var b1 = TestKit.PrivateMessage(888, "查不到备注");
        await TestKit.PostAsync(PluginPort, b1, TestKit.SignCorrect(Token, b1));
        await TestKit.WaitUntil(() => TestKit.CountFor(_received, 888) > 0);

        var strangerBefore = _fake.StrangerCalls;
        var b2 = TestKit.PrivateMessage(888, "再发一条");
        await TestKit.PostAsync(PluginPort, b2, TestKit.SignCorrect(Token, b2));
        await TestKit.WaitUntil(() => TestKit.CountFor(_received, 888) > 1);

        TestKit.Check("查不到备注时不缓存空结果，后续消息仍会重试",
            _fake.StrangerCalls > strangerBefore);
    }

    private static async Task TestFriendListRecoveryAsync()
    {
        _service.InvalidateCachesForTest();

        _fake.FriendListFails = true;
        _fake.StrangerRemark = "陌生人接口的备注";

        var body = TestKit.PrivateMessage(321, "好友列表失败应回退");
        await TestKit.PostAsync(PluginPort, body, TestKit.SignCorrect(Token, body));
        await TestKit.WaitUntil(() => TestKit.CountFor(_received, 321) > 0);

        TestKit.Check("好友列表拉取失败时不缓存失败状态，能回退到其他来源",
            !string.IsNullOrWhiteSpace(TestKit.LastFor(_received, 321)?.Remark));

        _fake.FriendListFails = false;
        _fake.FriendList = """
        [{"user_id":321,"nickname":"昵称丁","remark":"赵老师"}]
        """;

        var body2 = TestKit.PrivateMessage(321, "恢复后");
        await TestKit.PostAsync(PluginPort, body2, TestKit.SignCorrect(Token, body2));
        await Task.Delay(500);

        TestKit.Check("好友列表恢复后服务仍正常工作", TestKit.CountFor(_received, 321) >= 2);
    }
}
