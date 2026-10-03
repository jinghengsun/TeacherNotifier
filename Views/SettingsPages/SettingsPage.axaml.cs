using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using TeacherNotifier.Models;

namespace TeacherNotifier.Views.SettingsPages;

[SettingsPageInfo("teachernotifier.settings", "老师消息通知")]
public partial class SettingsPage : SettingsPageBase
{
    /// <summary>
    /// 公共无参构造函数。Avalonia 运行时加载器要求类型有公共构造函数才能访问 XAML 资源
    /// （否则编译告警 AVLN3001）。实际运行由 DI 注入 <see cref="AppSettings"/>。
    /// </summary>
    public SettingsPage() : this(new AppSettings())
    {
    }

    public SettingsPage(AppSettings settings)
    {
        InitializeComponent();
        DataContext = settings;
    }
}
