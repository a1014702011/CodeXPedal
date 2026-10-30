namespace CodeXPedal.Windows.Core.Services;

// [AI修改] 集中计算任务栏字号和物理宽度，避免 DPI、任务栏高度与视觉测量使用不同口径。
public static class TaskbarStripLayout
{
    public const double MinimumFontSize = 18D;
    public const double MaximumFontSize = 26D;

    // [AI修改] 字体约占任务栏逻辑高度的六成，并限制在用户确认的放大范围内。
    public static double CalculateFontSize(double logicalTaskbarHeight)
    {
        var normalizedHeight = double.IsFinite(logicalTaskbarHeight) ? Math.Max(0D, logicalTaskbarHeight) : 0D;
        return Math.Clamp(Math.Round(normalizedHeight * 0.58D, 1), MinimumFontSize, MaximumFontSize);
    }

    // [AI修改] WPF 先按逻辑像素测量内容，再换算为 Win32 使用的物理像素并只保留细边界。
    public static int CalculatePhysicalWidth(double desiredLogicalWidth, double dpiScale)
    {
        var normalizedWidth = double.IsFinite(desiredLogicalWidth) ? Math.Max(0D, desiredLogicalWidth) : 0D;
        var normalizedScale = double.IsFinite(dpiScale) && dpiScale > 0D ? dpiScale : 1D;
        return Math.Clamp((int)Math.Ceiling((normalizedWidth + 2D) * normalizedScale), 1, 320);
    }
}
