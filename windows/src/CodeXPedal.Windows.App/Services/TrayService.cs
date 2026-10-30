// [AI修改] 使用 Windows 原生通知区域图标提供稳定入口和快捷菜单。
using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using CodeXPedal.Windows.App.ViewModels;

namespace CodeXPedal.Windows.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;

    public TrayService(Action show, Func<Task> refresh, Action details, Action settings, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示悬浮窗", null, (_, _) => show());
        menu.Items.Add("立即刷新", null, async (_, _) => await refresh());
        menu.Items.Add("详情", null, (_, _) => details());
        menu.Items.Add("设置", null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "CodeXPedal",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => show();
    }

    public void UpdateTooltip(MainViewModel viewModel)
    {
        // [AI修改] 通知区域根据真实窗口拓扑决定是否加入 5 小时额度。
        var fiveHour = viewModel.HasFiveHourQuota ? $"5小时 {viewModel.FiveHourQuota}  " : string.Empty;
        var text = $"CodeXPedal  {fiveHour}7天 {viewModel.SevenDayQuota}  {viewModel.RefreshedAt}";
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            return Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? Drawing.SystemIcons.Application;
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException)
        {
            return Drawing.SystemIcons.Application;
        }
    }
}
