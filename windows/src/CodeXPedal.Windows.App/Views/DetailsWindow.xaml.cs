// [AI修改] 保持详情页绑定主快照，使刷新结果能实时同步。
using System.Windows;
#if DEBUG
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#endif
using CodeXPedal.Windows.App.ViewModels;

namespace CodeXPedal.Windows.App.Views;

public partial class DetailsWindow : Window
{
    public DetailsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
#if DEBUG
        // [AI修改] 仅在显式验收路径下渲染真实详情窗口，正式构建不生成截图。
        var path = Environment.GetEnvironmentVariable("CODEXPEDAL_DETAILS_CAPTURE");
        if (!string.IsNullOrWhiteSpace(path))
        {
            ContentRendered += async (_, _) => await CaptureForVerificationAsync(path);
        }
#endif
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

#if DEBUG
    private async Task CaptureForVerificationAsync(string path)
    {
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
