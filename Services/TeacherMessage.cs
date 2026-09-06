namespace TeacherNotifier.Services;

/// <summary>一条来自老师（白名单发送者）的 QQ 消息。</summary>
public class TeacherMessage
{
    /// <summary>发送者 QQ 号。</summary>
    public long UserId { get; init; }

    /// <summary>群号，私聊时为 0。</summary>
    public long GroupId { get; init; }

    /// <summary>发送者显示名（群名片优先，其次昵称，最后 QQ 号）。</summary>
    public string SenderName { get; init; } = "";

    /// <summary>机器人给发送者留的备注名（主动查 NapCat API 获得，可能为空）。</summary>
    public string Remark { get; init; } = "";

    /// <summary>消息文本内容。</summary>
    public string Message { get; init; } = "";

    /// <summary>消息类型：private 或 group。</summary>
    public string MessageType { get; init; } = "";
}
