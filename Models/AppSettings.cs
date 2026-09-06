using CommunityToolkit.Mvvm.ComponentModel;

namespace TeacherNotifier.Models;

/// <summary>
/// 插件全局设置。所有字段均可绑定，改动时自动触发 PropertyChanged。
/// </summary>
public class AppSettings : ObservableObject
{
    // ===== 连接设置 =====

    /// <summary>连接模式：0 = HTTP POST 上报，1 = 反向 WebSocket。</summary>
    private int _connectionMode = 0;
    public int ConnectionMode
    {
        get => _connectionMode;
        set => SetProperty(ref _connectionMode, value);
    }

    /// <summary>监听端口。</summary>
    private double _port = 8090;
    public double Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    /// <summary>访问令牌（NapCat 连接时生成的 Token）。留空则不校验；非空时请求头必须携带 Authorization: Bearer &lt;token&gt;。</summary>
    private string _accessToken = "";
    public string AccessToken
    {
        get => _accessToken;
        set => SetProperty(ref _accessToken, value);
    }

    /// <summary>NapCat 的 HTTP API 地址（用于主动查询发送者备注）。留空则不查询备注。</summary>
    private string _napCatApiUrl = "http://127.0.0.1:18801";
    public string NapCatApiUrl
    {
        get => _napCatApiUrl;
        set => SetProperty(ref _napCatApiUrl, value);
    }

    /// <summary>NapCat HTTP API 的 access token（对应 NapCat WebUI 里 HTTP 服务器配置的 token）。</summary>
    private string _napCatApiToken = "";
    public string NapCatApiToken
    {
        get => _napCatApiToken;
        set => SetProperty(ref _napCatApiToken, value);
    }

    /// <summary>手动备注映射（兜底，不依赖 NapCat API）。格式：QQ号=备注名，多组用英文逗号分隔。例如：123456789=王老师,987654321=李老师。</summary>
    private string _remarkMap = "";
    public string RemarkMap
    {
        get => _remarkMap;
        set => SetProperty(ref _remarkMap, value);
    }

    /// <summary>老师 QQ 白名单，英文逗号分隔，留空表示不限制发送者。</summary>
    private string _teacherQQs = "";
    public string TeacherQQs
    {
        get => _teacherQQs;
        set => SetProperty(ref _teacherQQs, value);
    }

    /// <summary>群号白名单，英文逗号分隔，留空表示接受所有群的消息。</summary>
    private string _groupIds = "";
    public string GroupIds
    {
        get => _groupIds;
        set => SetProperty(ref _groupIds, value);
    }

    /// <summary>白名单为空时是否接收所有发送者。</summary>
    private bool _allowAllWhenEmpty = true;
    public bool AllowAllWhenEmpty
    {
        get => _allowAllWhenEmpty;
        set => SetProperty(ref _allowAllWhenEmpty, value);
    }

    /// <summary>是否忽略机器人自己发送的消息。</summary>
    private bool _ignoreSelfMessages = true;
    public bool IgnoreSelfMessages
    {
        get => _ignoreSelfMessages;
        set => SetProperty(ref _ignoreSelfMessages, value);
    }

    // ===== 通知设置 =====

    /// <summary>标题（遮罩）模板。占位符：{name} 发送者昵称、{qq} QQ号、{group} 群号、{message} 消息内容。</summary>
    private string _titleTemplate = "{name} 老师";
    public string TitleTemplate
    {
        get => _titleTemplate;
        set => SetProperty(ref _titleTemplate, value);
    }

    /// <summary>标题（遮罩）显示时长，秒。</summary>
    private double _titleDuration = 3.0;
    public double TitleDuration
    {
        get => _titleDuration;
        set => SetProperty(ref _titleDuration, value);
    }

    /// <summary>正文显示时长，秒。</summary>
    private double _bodyDuration = 10.0;
    public double BodyDuration
    {
        get => _bodyDuration;
        set => SetProperty(ref _bodyDuration, value);
    }

    /// <summary>正文滚动重复次数，越大滚动越快。</summary>
    private double _bodyRepeatCount = 2;
    public double BodyRepeatCount
    {
        get => _bodyRepeatCount;
        set => SetProperty(ref _bodyRepeatCount, value);
    }

    /// <summary>是否启用语音朗读。</summary>
    private bool _speechEnabled = true;
    public bool SpeechEnabled
    {
        get => _speechEnabled;
        set => SetProperty(ref _speechEnabled, value);
    }

    /// <summary>语音朗读模板。占位符同标题模板。</summary>
    private string _speechTemplate = "{name} 发来消息：{message}";
    public string SpeechTemplate
    {
        get => _speechTemplate;
        set => SetProperty(ref _speechTemplate, value);
    }
}
