using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeacherNotifier.Services;

namespace TeacherNotifier.Tests;

/// <summary>
/// 测试辅助：构造上报事件、按框架的真实算法签名、发请求、断言与统计。
/// </summary>
internal static class TestKit
{
    private static int _pass;
    private static int _fail;

    public static int PassCount => _pass;
    public static int FailCount => _fail;

    /// <summary>输出一个小节标题。</summary>
    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ===");
    }

    /// <summary>断言并记录结果。失败不会中断测试，最后统一由退出码反映。</summary>
    public static void Check(string name, bool ok)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  [FAIL] {name}");
        }
    }

    // ==================== 事件构造 ====================

    public static string PrivateMessage(long userId, string text) =>
        $$"""
        {"post_type":"message","message_type":"private","self_id":10001,"user_id":{{userId}},
         "sender":{"user_id":{{userId}},"nickname":"某人"},
         "message":[{"type":"text","data":{"text":"{{text}}"} }],
         "raw_message":"{{text}}"}
        """;

    public static string GroupMessage(long groupId, long userId, string text) =>
        $$"""
        {"post_type":"message","message_type":"group","self_id":10001,"user_id":{{userId}},
         "group_id":{{groupId}},"sender":{"user_id":{{userId}},"nickname":"某人","card":"某人群名片"},
         "message":[{"type":"text","data":{"text":"{{text}}"} }],"raw_message":"{{text}}"}
        """;

    // ==================== 签名 ====================

    /// <summary>
    /// NapCat（packages/napcat-onebot/network/http-client.ts）与
    /// SnowLuma（packages/onebot/src/network/http-post-adapter.ts）
    /// 的真实算法：sha1=HMAC-SHA1(key=token, data=body)。
    /// </summary>
    public static string SignCorrect(string token, string body)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(token));
        return "sha1=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    /// <summary>
    /// 修复前的错误算法 sha1=SHA1(token + body)。
    /// 保留它用于反向验证：这个签名必须被拒绝。
    /// </summary>
    public static string SignWrongOldWay(string token, string body)
    {
        using var sha1 = SHA1.Create();
        var combined = Encoding.UTF8.GetBytes(token).Concat(Encoding.UTF8.GetBytes(body)).ToArray();
        return "sha1=" + Convert.ToHexString(sha1.ComputeHash(combined)).ToLowerInvariant();
    }

    // ==================== 发请求 ====================

    /// <summary>把一条上报 POST 给插件，返回状态码与响应体。</summary>
    public static async Task<(int Code, string Body)> PostAsync(
        int port, string body, string? signature, string? authorization = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (signature is not null)
            req.Headers.TryAddWithoutValidation("x-signature", signature);
        if (authorization is not null)
            req.Headers.TryAddWithoutValidation("Authorization", authorization);

        try
        {
            using var resp = await client.SendAsync(req);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    // ==================== 收到的消息 ====================

    public static int CountFor(List<TeacherMessage> list, long userId)
    {
        lock (list) return list.Count(m => m.UserId == userId);
    }

    public static TeacherMessage? LastFor(List<TeacherMessage> list, long userId)
    {
        lock (list) return list.LastOrDefault(m => m.UserId == userId);
    }

    /// <summary>轮询等待条件成立，避免依赖固定延时。</summary>
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
    }
}
