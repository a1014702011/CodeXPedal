// [AI修改] 集中保存 Windows 界面配置，默认不占用任务栏空间。
namespace CodeXPedal.Windows.Core.Models;

public sealed record AppSettings
{
    public bool CompactMode { get; init; }

    public bool AlwaysOnTop { get; init; } = true;

    public bool ShowTaskbarStrip { get; init; }

    // [AI修改] 仅任务栏模式隐藏悬浮窗，但始终保留任务栏信息栏作为入口。
    public bool TaskbarOnlyMode { get; init; }

    public bool StartWithWindows { get; init; }

    public int RefreshIntervalSeconds { get; init; } = 60;

    public double WindowLeft { get; init; } = double.NaN;

    public double WindowTop { get; init; } = double.NaN;

    // [AI修改] 归一化刷新范围，并避免仅任务栏模式被手工配置成没有可见入口。
    public AppSettings Normalize() => this with
    {
        ShowTaskbarStrip = TaskbarOnlyMode || ShowTaskbarStrip,
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 15, 3600),
    };
}
