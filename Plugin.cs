using System.IO;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TeacherNotifier.Models;
using TeacherNotifier.Services;
using TeacherNotifier.Services.NotificationProviders;
using TeacherNotifier.Views.SettingsPages;

namespace TeacherNotifier;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 加载并自动保存插件配置
        var configPath = Path.Combine(PluginConfigFolder, "Settings.json");
        var settings = ConfigureFileHelper.LoadConfig<AppSettings>(configPath);
        settings.PropertyChanged += (_, _) =>
            ConfigureFileHelper.SaveConfig(configPath, settings);

        services.AddSingleton(settings);

        // NapCat 连接服务（作为托管服务，随主机启动/停止）
        services.AddSingleton<NapCatConnectionService>();
        services.AddHostedService(sp => sp.GetRequiredService<NapCatConnectionService>());

        // 设置页面 + 提醒提供方
        services.AddSettingsPage<SettingsPage>();
        services.AddNotificationProvider<TeacherNotificationProvider>();
    }
}
