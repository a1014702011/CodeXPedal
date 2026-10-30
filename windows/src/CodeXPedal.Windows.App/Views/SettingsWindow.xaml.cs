// [AI修改] 校验设置输入，并在用户点击保存后一次性应用。
using System.Globalization;
using System.Windows;
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _original = settings;
        Result = settings;
        CompactModeCheckBox.IsChecked = settings.CompactMode;
        AlwaysOnTopCheckBox.IsChecked = settings.AlwaysOnTop;
        TaskbarStripCheckBox.IsChecked = settings.ShowTaskbarStrip;
        // [AI修改] 在任务栏开关之后还原仅任务栏模式，确保依赖关系初始化一致。
        TaskbarOnlyModeCheckBox.IsChecked = settings.TaskbarOnlyMode;
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        RefreshIntervalTextBox.Text = settings.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
#if DEBUG
        // [AI修改] DEBUG 验收可渲染真实设置窗口，避免无边框 WPF 窗口无法被外部截图工具捕获。
        var capturePath = Environment.GetEnvironmentVariable("CODEXPEDAL_SETTINGS_CAPTURE");
        if (!string.IsNullOrWhiteSpace(capturePath))
        {
            ContentRendered += (_, _) => CaptureForVerification(capturePath);
        }
#endif
    }

    public AppSettings Result { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RefreshIntervalTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 15 or > 3600)
        {
            ValidationText.Text = "请输入 15 到 3600 之间的整数。";
            return;
        }

        // [AI修改] 仅任务栏模式必须同时保存任务栏栏位，避免下次启动没有可见入口。
        var taskbarOnlyMode = TaskbarOnlyModeCheckBox.IsChecked == true;
        Result = _original with
        {
            CompactMode = CompactModeCheckBox.IsChecked == true,
            AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true,
            ShowTaskbarStrip = taskbarOnlyMode || TaskbarStripCheckBox.IsChecked == true,
            TaskbarOnlyMode = taskbarOnlyMode,
            StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
            RefreshIntervalSeconds = seconds,
        };
        DialogResult = true;
    }

    // [AI修改] 开启仅任务栏模式时自动打开其依赖的任务栏栏位。
    private void TaskbarOnlyModeCheckBox_Checked(object sender, RoutedEventArgs e) => TaskbarStripCheckBox.IsChecked = true;

    // [AI修改] 主动关闭任务栏栏位时同步退出仅任务栏模式，保持界面状态可解释。
    private void TaskbarStripCheckBox_Unchecked(object sender, RoutedEventArgs e) => TaskbarOnlyModeCheckBox.IsChecked = false;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

#if DEBUG
    private void CaptureForVerification(string path)
    {
        // [AI修改] 直接渲染 WPF 视觉树到临时图片，用于检查设置项截断、重叠和边距。
        UpdateLayout();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width,
            height,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }
#endif
}
