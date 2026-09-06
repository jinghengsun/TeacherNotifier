using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using TeacherNotifier.Models;

namespace TeacherNotifier.Services.NotificationProviders;

/// <summary>
/// 提醒提供方：收到 NapCat 上报的老师消息后，通过 ClassIsland 提醒系统弹出通知。
/// </summary>
[NotificationProviderInfo(
    "3f7a8c1e-5b2d-4a6e-9c0f-1d2e3f4a5b6c",
    "老师消息通知",
    "接收 NapCat 上报的老师 QQ 消息并提醒")]
public class TeacherNotificationProvider : NotificationProviderBase
{
    private readonly AppSettings _settings;

    public TeacherNotificationProvider(AppSettings settings, NapCatConnectionService connection)
    {
        _settings = settings;
        connection.TeacherMessageReceived += OnTeacherMessageReceived;
    }

    private void OnTeacherMessageReceived(object? sender, TeacherMessage msg)
    {
        // 提醒内容的构造（CreateTwoIconsMask/CreateRollingTextContent）会创建 Avalonia 控件，
        // 必须在 UI 线程执行
        Dispatcher.UIThread.Post(() =>
        {
            var title = Format(_settings.TitleTemplate, msg);
            var body = string.IsNullOrWhiteSpace(msg.Message) ? "(空消息)" : msg.Message;
            var speech = Format(_settings.SpeechTemplate, msg);

            var request = new NotificationRequest
            {
                MaskContent = NotificationContent.CreateTwoIconsMask(title, factory: x =>
                {
                    x.Duration = TimeSpan.FromSeconds(Math.Max(0.5, _settings.TitleDuration));
                    if (_settings.SpeechEnabled)
                        x.SpeechContent = speech;
                }),
                OverlayContent = NotificationContent.CreateRollingTextContent(
                    body,
                    TimeSpan.FromSeconds(Math.Max(1, _settings.BodyDuration)),
                    Math.Max(1, (int)_settings.BodyRepeatCount),
                    factory: x =>
                    {
                        if (_settings.SpeechEnabled)
                            x.SpeechContent = speech;
                    })
            };

            ShowNotification(request);
        });
    }

    private static string Format(string template, TeacherMessage msg)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        // {name} 优先用备注（机器人给发送者留的），没有备注则回退到群名片/昵称
        var name = string.IsNullOrWhiteSpace(msg.Remark) ? msg.SenderName : msg.Remark;
        return template
            .Replace("{name}", name)
            .Replace("{remark}", msg.Remark)
            .Replace("{qq}", msg.UserId.ToString())
            .Replace("{group}", msg.GroupId == 0 ? "" : msg.GroupId.ToString())
            .Replace("{message}", msg.Message);
    }
}
