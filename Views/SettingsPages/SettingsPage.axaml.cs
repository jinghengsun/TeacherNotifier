using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using TeacherNotifier.Models;

namespace TeacherNotifier.Views.SettingsPages;

[SettingsPageInfo("teachernotifier.settings", "老师消息通知")]
public partial class SettingsPage : SettingsPageBase
{
    public SettingsPage(AppSettings settings)
    {
        InitializeComponent();
        DataContext = settings;
    }
}
