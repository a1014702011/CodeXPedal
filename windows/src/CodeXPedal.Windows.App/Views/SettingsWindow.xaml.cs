// [AI修改] 校验设置输入，并在用户点击保存后一次性应用。
using System.Globalization;
using System.IO;
using System.Windows;
using CodeXPedal.Windows.Core.Models;
using CodeXPedal.Windows.Core.Services;

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
        ClaudeLiveQuotaCheckBox.IsChecked = settings.ClaudeLiveQuota;
        RefreshClaudeBridgeState();
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
            ClaudeLiveQuota = ClaudeLiveQuotaCheckBox.IsChecked == true,
        };
        DialogResult = true;
    }

    // [AI修改] 开启仅任务栏模式时自动打开其依赖的任务栏栏位。
    private void TaskbarOnlyModeCheckBox_Checked(object sender, RoutedEventArgs e) => TaskbarStripCheckBox.IsChecked = true;

    // [AI修改] 主动关闭任务栏栏位时同步退出仅任务栏模式，保持界面状态可解释。
    private void TaskbarStripCheckBox_Unchecked(object sender, RoutedEventArgs e) => TaskbarOnlyModeCheckBox.IsChecked = false;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // [AI修改] 桥接状态即时读取 Claude settings.json，按钮文字随状态切换“启用/停用/重新启用”。
    private void RefreshClaudeBridgeState()
    {
        var (state, _) = ClaudeStatusLineBridge.GetState(ClaudeExecutableResolver.ConfigDirectory(), Environment.ProcessPath ?? string.Empty);
        (ClaudeBridgeButton.Content, ClaudeBridgeStatus.Text) = state switch
        {
            ClaudeBridgeState.Enabled => ("停用 Claude 状态栏桥接", "已启用"),
            ClaudeBridgeState.EnabledOtherPath => ("重新启用（指向本程序）", "已启用，但指向其他位置的 CodeXPedal"),
            ClaudeBridgeState.OtherCommand => ("启用 Claude 状态栏桥接", "当前使用自定义状态栏，启用时会先备份"),
            ClaudeBridgeState.SettingsUnreadable => ("启用 Claude 状态栏桥接", "Claude settings.json 无法解析，请先手动修复"),
            _ => ("启用 Claude 状态栏桥接", "未启用"),
        };
        ClaudeBridgeButton.IsEnabled = state != ClaudeBridgeState.SettingsUnreadable;
    }

    private void ClaudeBridgeButton_Click(object sender, RoutedEventArgs e)
    {
        var configDirectory = ClaudeExecutableResolver.ConfigDirectory();
        var executable = Environment.ProcessPath ?? string.Empty;
        var (state, command) = ClaudeStatusLineBridge.GetState(configDirectory, executable);
        try
        {
            if (state == ClaudeBridgeState.Enabled)
            {
                ClaudeStatusLineBridge.Disable(configDirectory);
            }
            else
            {
                // [AI修改] 替换用户自定义状态栏前必须明确确认，并说明可恢复。
                if (state == ClaudeBridgeState.OtherCommand
                    && System.Windows.MessageBox.Show(
                        $"Claude Code 当前状态栏命令：\n{command}\n\n启用后会被替换为 CodeXPedal 额度显示，原配置会备份，停用时自动恢复。是否继续？",
                        "CodeXPedal", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }

                ClaudeStatusLineBridge.Enable(configDirectory, executable);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            System.Windows.MessageBox.Show($"修改 Claude 设置失败：{exception.Message}", "CodeXPedal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshClaudeBridgeState();
    }

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
