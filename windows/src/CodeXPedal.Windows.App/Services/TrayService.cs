// [AI修改] 使用 Windows 原生通知区域图标提供稳定入口和快捷菜单。
using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using CodeXPedal.Windows.App.ViewModels;

namespace CodeXPedal.Windows.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;

    // [AI修改] 第一项随悬浮窗状态切换“显示/隐藏悬浮窗”，打开菜单时读取实际可见性。
    public TrayService(Action show, Action hide, Func<bool> isFloatingVisible, Func<Task> refresh, Action details, Action settings, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        var toggle = new Forms.ToolStripMenuItem("显示悬浮窗");
        toggle.Click += (_, _) =>
        {
            if (isFloatingVisible()) { hide(); }
            else { show(); }
        };
        menu.Items.Add(toggle);
        menu.Opening += (_, _) => toggle.Text = isFloatingVisible() ? "隐藏悬浮窗" : "显示悬浮窗";
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
        // [AI修改] 通知区域根据真实窗口拓扑决定是否加入 5 小时额度；两家都安装时分两行显示。
        var lines = new List<string>();
        foreach (var row in new[] { viewModel.CodexRow, viewModel.ClaudeRow }.Where(row => row.IsVisible))
        {
            var fiveHour = row.HasFiveHourQuota ? $"5h {row.FiveHourQuota} " : string.Empty;
            lines.Add($"{(row.ShowLabel ? row.Label : "CodeXPedal")} {fiveHour}7d {row.SevenDayQuota} {row.RefreshedAt}");
        }

        var text = string.Join("\n", lines);
        // [AI修改] 沿用 63 字符上限，兼容所有 WinForms 版本的托盘提示长度限制。
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
