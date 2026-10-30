// [AI修改] 提供悬浮窗拖动、紧凑切换、隐藏到托盘和位置持久化交互。
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodeXPedal.Windows.App.ViewModels;
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.App.Views;

public partial class FloatingWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Func<AppSettings, Task> _saveSettings;
    private readonly Action _openSettings;
    private readonly Action _openDetails;
    private readonly Action _exit;
    private readonly DispatcherTimer _positionSaveTimer;
    private bool _allowClose;
    private bool _positionInitialized;
#if DEBUG
    // [AI修改] 验收截图状态只参与 DEBUG 构建，避免 Release 产生未赋值字段。
    private readonly bool _isVerificationCapture;
#endif

    public FloatingWindow(
        MainViewModel viewModel,
        Func<AppSettings, Task> saveSettings,
        Action openSettings,
        Action openDetails,
        Action exit)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _saveSettings = saveSettings;
        _openSettings = openSettings;
        _openDetails = openDetails;
        _exit = exit;
        DataContext = viewModel;
        _positionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _positionSaveTimer.Tick += async (_, _) =>
        {
            _positionSaveTimer.Stop();
            await _saveSettings(_viewModel.Settings);
        };
#if DEBUG
        // [AI修改] DEBUG 构建可按显式环境变量渲染实际 WPF 视觉树，便于无边框窗口自动验收。
        var capturePath = Environment.GetEnvironmentVariable("CODEXPEDAL_UI_CAPTURE");
        _isVerificationCapture = !string.IsNullOrWhiteSpace(capturePath);
        if (!string.IsNullOrWhiteSpace(capturePath))
        {
            var compactValue = Environment.GetEnvironmentVariable("CODEXPEDAL_CAPTURE_COMPACT");
            if (compactValue is "0" or "1")
            {
                _viewModel.ApplySettings(_viewModel.Settings with { CompactMode = compactValue == "1" });
            }

            ContentRendered += async (_, _) => await CaptureForVerificationAsync(capturePath);
        }
#endif
    }

    public void ApplySettings(AppSettings settings)
    {
        Topmost = settings.AlwaysOnTop;
        if (!_positionInitialized)
        {
            _positionInitialized = true;
            if (double.IsFinite(settings.WindowLeft) && double.IsFinite(settings.WindowTop))
            {
                Left = settings.WindowLeft;
                Top = settings.WindowTop;
            }
            else
            {
                Left = SystemParameters.WorkArea.Right - Width - 18;
                Top = SystemParameters.WorkArea.Top + 18;
            }
        }
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e) => _viewModel.ToggleCompact();

    private void CompactButton_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleCompact();

    private void CompactMenuItem_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleCompact();

    private void DetailsButton_Click(object sender, RoutedEventArgs e) => _openDetails();

    private void DetailsMenuItem_Click(object sender, RoutedEventArgs e) => _openDetails();

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e) => _openSettings();

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => _exit();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? RefreshRequested;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
#if DEBUG
        // [AI修改] 自动截图不覆盖用户保存的窗口位置。
        if (_isVerificationCapture)
        {
            return;
        }
#endif
        if (!_positionInitialized || !double.IsFinite(Left) || !double.IsFinite(Top))
        {
            return;
        }

        _viewModel.UpdateWindowPosition(Left, Top);
        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

#if DEBUG
    private async Task CaptureForVerificationAsync(string path)
    {
        // [AI修改] 等待首次额度和本地数据库刷新结束，再捕获用户实际看到的内容。
        await Task.Delay(TimeSpan.FromSeconds(14));
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        encoder.Save(stream);
    }
#endif
}
