// [AI修改] 以独立 Win32 子窗口附着任务栏，呈现 TrafficMonitor 式信息栏，不注入 Explorer 进程。
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodeXPedal.Windows.App.ViewModels;
using CodeXPedal.Windows.Core.Services;
using MediaColor = System.Windows.Media.Color;

namespace CodeXPedal.Windows.App.Services;

public sealed partial class TaskbarStripService : IDisposable
{
    private const int WsChild = 0x40000000;
    private const int WsClipSiblings = 0x04000000;
    private const int SwHide = 0;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    // [AI修改] 任务按钮区域过小时宁可隐藏信息栏，也不给系统交互制造拥挤。
    private const int MinimumTaskListLength = 240;
    // [AI修改] 底块与窗口清屏共用同一颜色，保证未被内容覆盖的像素也不是黑色。
    private static readonly MediaColor StripBackgroundColor = MediaColor.FromRgb(20, 27, 35);
    private readonly Action _showFloatingWindow;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly string _reservationRecoveryPath;
    private HwndSource? _source;
    private Border? _rootVisual;
    // [AI修改] Codex 与 Claude 各一行；两者同时显示时上下两行排列，只有一个时保持原单行外观。
    private StripRow? _codexRow;
    private StripRow? _claudeRow;
    private bool _dualLayout;
    private IntPtr _reservedTaskList;
    private IntPtr _reservedTaskListParent;
    private Rect _originalTaskListBounds;
#if DEBUG
    // [AI修改] DEBUG 验收保留预留前的屏幕坐标，用于证明任务栏不重叠且退出后已恢复。
    private Rect _originalTaskListScreenBounds;
#endif
    private bool _taskListReserved;
    private double _fontSize = TaskbarStripLayout.MinimumFontSize;
    private int _horizontalWidth = 180;
    private bool _visible;

    public TaskbarStripService(Action showFloatingWindow)
    {
        _showFloatingWindow = showFloatingWindow;
        // [AI修改] 恢复只处理本产品凭据，不再迁移其他产品目录。
        // [AI修改] 异常终止时保留恢复凭据，下次启动先修复同一 Explorer 实例的残留间隙。
        _reservationRecoveryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodeXPedal",
            "taskbar-strip-reservation.json");
        TryRecoverAbandonedReservation();
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _reconnectTimer.Tick += (_, _) => EnsureHost();
        _reconnectTimer.Start();
    }

    public void SetVisible(bool visible, MainViewModel? viewModel)
    {
        _visible = visible;
        if (!visible)
        {
            DestroyHost();
            return;
        }

        EnsureHost();
        if (viewModel is not null)
        {
            Update(viewModel);
        }
    }

    public void Update(MainViewModel viewModel)
    {
        if (!_visible)
        {
            return;
        }

        EnsureHost();
        // [AI修改] 只有官方或同一条本地快照真实返回 5 小时窗口时才占用任务栏空间。
        _codexRow?.Apply(viewModel.CodexRow);
        _claudeRow?.Apply(viewModel.ClaudeRow);
        _dualLayout = viewModel.ShowBoth;

        if (_source is not null && NativeMethods.IsWindow(_source.Handle))
        {
            PositionHost(_source.Handle);
        }
    }

    public void Dispose()
    {
        _reconnectTimer.Stop();
        DestroyHost();
    }

    private void EnsureHost()
    {
        if (!_visible)
        {
            return;
        }

        if (_source is not null && NativeMethods.IsWindow(_source.Handle))
        {
            PositionHost(_source.Handle);
            return;
        }

        DestroyHost();
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            return;
        }

        _rootVisual = CreateVisual();
        UpdateTypography(taskbar);
        UpdateHorizontalWidth(taskbar);
        if (!TryGetHostBounds(taskbar, _horizontalWidth, out var x, out var y, out var width, out var height))
        {
            DestroyHost();
            return;
        }

        var parameters = new HwndSourceParameters("CodeXPedal.TaskbarStrip")
        {
            ParentWindow = taskbar,
            // [AI修改] 先以隐藏状态创建，只有成功预留安全空间后才显示，避免启动瞬间覆盖任务按钮。
            WindowStyle = WsChild | WsClipSiblings,
            PositionX = x,
            PositionY = y,
            Width = width,
            Height = height,
            UsesPerPixelOpacity = false,
        };
        _source = new HwndSource(parameters) { RootVisual = _rootVisual };
        // [AI修改] HwndSource 默认用黑色清屏；DPI 换算或尺寸变化时内容与窗口差一两个像素就会露出黑边，清屏色改为与底块同色。
        if (_source.CompositionTarget is not null)
        {
            _source.CompositionTarget.BackgroundColor = StripBackgroundColor;
        }

        PositionHost(_source.Handle);
    }

    private Border CreateVisual()
    {
        // [AI修改] 创建阶段使用稳定占位，避免第一帧因空文字退化为极窄窗口；Claude 行默认隐藏。
        _codexRow = new StripRow("Codex", Colors.LightSkyBlue);
        _claudeRow = new StripRow("Claude", Colors.LightSkyBlue);
        _claudeRow.Panel.Visibility = Visibility.Collapsed;
        var panel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        // [AI修改] 任务栏保留当前实际存在的额度窗口和更新时间，不再显示月额度。
        panel.Children.Add(_codexRow.Panel);
        panel.Children.Add(_claudeRow.Panel);

        var border = new Border
        {
            Background = new SolidColorBrush(StripBackgroundColor),
            // [AI修改] 按设备像素对齐，避免缩放比例不是 100% 时边缘出现半透明暗线。
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(66, 82, 98)),
            BorderThickness = new Thickness(1, 0, 1, 0),
            Child = panel,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        border.MouseLeftButtonUp += (_, _) => _showFloatingWindow();
        return border;
    }

    private void PositionHost(IntPtr handle)
    {
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            RestoreTaskList();
            NativeMethods.ShowWindow(handle, SwHide);
            return;
        }

        // [AI修改] 字体先随任务栏高度和 DPI 放大，再以最终文字宽度计算安全占位。
        UpdateTypography(taskbar);
        UpdateHorizontalWidth(taskbar);
        if (!TryGetHostBounds(taskbar, _horizontalWidth, out var x, out var y, out var width, out var height)
            || !TryReserveTaskbarSpace(taskbar, x, width))
        {
            // [AI修改] 无法确认可安全预留空间时隐藏信息栏，不能覆盖其他应用按钮。
            RestoreTaskList();
            NativeMethods.ShowWindow(handle, SwHide);
            return;
        }

        NativeMethods.SetWindowPos(handle, IntPtr.Zero, x, y, width, height, SwpNoActivate | SwpShowWindow);
#if DEBUG
        WriteTaskbarVerification("reserved", handle);
#endif
    }

    private static bool TryGetHostBounds(IntPtr taskbar, int horizontalWidth, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (!NativeMethods.GetWindowRect(taskbar, out var taskbarRect))
        {
            return false;
        }

        var tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !NativeMethods.GetWindowRect(tray, out var trayRect))
        {
            return false;
        }

        var taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        var taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        if (taskbarWidth >= taskbarHeight)
        {
            // [AI修改] 使用实际内容宽度，并受托盘前可用空间限制。
            var availableWidth = Math.Max(0, trayRect.Left - taskbarRect.Left - 4);
            width = Math.Min(horizontalWidth, availableWidth);
            height = taskbarHeight;
            x = Math.Max(0, trayRect.Left - taskbarRect.Left - width - 4);
            y = 0;
        }
        else
        {
            // [AI修改] 当前横排内容不在竖向任务栏上强行显示，避免覆盖或截断系统按钮。
            return false;
        }

        return width > 0 && height > 0;
    }

    private bool TryReserveTaskbarSpace(IntPtr taskbar, int hostX, int hostWidth)
    {
        var bar = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
        if (bar == IntPtr.Zero)
        {
            return false;
        }

        var taskList = NativeMethods.FindWindowEx(bar, IntPtr.Zero, "MSTaskSwWClass", null);
        if (taskList == IntPtr.Zero)
        {
            taskList = NativeMethods.FindWindowEx(bar, IntPtr.Zero, "MSTaskListWClass", null);
        }

        if (taskList == IntPtr.Zero
            || !NativeMethods.GetWindowRect(taskbar, out var taskbarRect)
            || !NativeMethods.GetWindowRect(taskList, out var taskListRect))
        {
            return false;
        }

        var stripLeft = taskbarRect.Left + hostX;
        var stripRight = stripLeft + hostWidth;
        var targetTaskListRight = stripLeft - 2;
        var targetTaskListWidth = targetTaskListRight - taskListRect.Left;
        if (stripRight > taskbarRect.Right || targetTaskListWidth < MinimumTaskListLength)
        {
            return false;
        }

        if (!_taskListReserved || _reservedTaskList != taskList)
        {
            RestoreTaskList();
            var parent = NativeMethods.GetParent(taskList);
            if (parent == IntPtr.Zero || !TryConvertToParentBounds(parent, taskListRect, out var originalBounds))
            {
                return false;
            }

            // [AI修改] 只保存本次 Explorer 实例的原始区域，关闭功能或退出时完整恢复。
            _reservedTaskList = taskList;
            _reservedTaskListParent = parent;
            _originalTaskListBounds = originalBounds;
#if DEBUG
            _originalTaskListScreenBounds = taskListRect;
#endif
            _taskListReserved = true;
            if (!TryPersistReservationRecoveryState(taskbar))
            {
                // [AI修改] 无法写入异常恢复凭据时不修改 Explorer，保持失败关闭。
                _reservedTaskList = IntPtr.Zero;
                _reservedTaskListParent = IntPtr.Zero;
                _originalTaskListBounds = default;
                _taskListReserved = false;
                return false;
            }
        }

        var topLeft = new Point { X = taskListRect.Left, Y = taskListRect.Top };
        if (!NativeMethods.ScreenToClient(_reservedTaskListParent, ref topLeft))
        {
            return false;
        }

        var taskListHeight = taskListRect.Bottom - taskListRect.Top;
        if (!NativeMethods.MoveWindow(taskList, topLeft.X, topLeft.Y, targetTaskListWidth, taskListHeight, true)
            || !NativeMethods.GetWindowRect(taskList, out var reservedRect))
        {
            return false;
        }

        // [AI修改] 以真实窗口边界复核预留结果，失败时上层会隐藏信息栏并恢复任务列表。
        return reservedRect.Right <= targetTaskListRight + 1;
    }

    private static bool TryConvertToParentBounds(IntPtr parent, Rect screenBounds, out Rect parentBounds)
    {
        var topLeft = new Point { X = screenBounds.Left, Y = screenBounds.Top };
        var bottomRight = new Point { X = screenBounds.Right, Y = screenBounds.Bottom };
        if (!NativeMethods.ScreenToClient(parent, ref topLeft)
            || !NativeMethods.ScreenToClient(parent, ref bottomRight))
        {
            parentBounds = default;
            return false;
        }

        parentBounds = new Rect
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Right = bottomRight.X,
            Bottom = bottomRight.Y,
        };
        return true;
    }

    private void RestoreTaskList()
    {
        var recoveryStateCanBeCleared = !_taskListReserved;
        if (_taskListReserved
            && NativeMethods.IsWindow(_reservedTaskList)
            && NativeMethods.GetParent(_reservedTaskList) == _reservedTaskListParent)
        {
            // [AI修改] 恢复 Explorer 原始任务按钮区域，不把任务栏布局改动遗留到进程退出之后。
            recoveryStateCanBeCleared = NativeMethods.MoveWindow(
                _reservedTaskList,
                _originalTaskListBounds.Left,
                _originalTaskListBounds.Top,
                _originalTaskListBounds.Right - _originalTaskListBounds.Left,
                _originalTaskListBounds.Bottom - _originalTaskListBounds.Top,
                true);
        }
        else if (_taskListReserved)
        {
            // [AI修改] Explorer 已重启时旧句柄不再有效，旧恢复凭据可以安全丢弃。
            recoveryStateCanBeCleared = true;
        }

        if (recoveryStateCanBeCleared)
        {
#if DEBUG
            WriteTaskbarVerification("restored", IntPtr.Zero);
#endif
            TryDeleteReservationRecoveryState();
        }

        _reservedTaskList = IntPtr.Zero;
        _reservedTaskListParent = IntPtr.Zero;
        _originalTaskListBounds = default;
#if DEBUG
        _originalTaskListScreenBounds = default;
#endif
        _taskListReserved = false;
    }

#if DEBUG
    private void WriteTaskbarVerification(string stage, IntPtr strip)
    {
        var basePath = Environment.GetEnvironmentVariable("CODEXPEDAL_TASKBAR_VERIFY_PATH");
        if (string.IsNullOrWhiteSpace(basePath)
            || !NativeMethods.GetWindowRect(_reservedTaskList, out var taskListBounds))
        {
            return;
        }

        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        var taskbarBounds = default(Rect);
        var stripBounds = default(Rect);
        NativeMethods.GetWindowRect(taskbar, out taskbarBounds);
        var hasStripBounds = strip != IntPtr.Zero && NativeMethods.GetWindowRect(strip, out stripBounds);
        var nonOverlapping = !hasStripBounds || taskListBounds.Right <= stripBounds.Left;
        var restored = stage != "restored" || RectEquals(taskListBounds, _originalTaskListScreenBounds);
        var report = new
        {
            Stage = stage,
            Taskbar = taskbarBounds,
            TaskListBefore = _originalTaskListScreenBounds,
            TaskListCurrent = taskListBounds,
            Strip = hasStripBounds ? stripBounds : default,
            FiveHourVisible = _codexRow?.FiveHour.Visibility == Visibility.Visible,
            DualLayout = _dualLayout,
            FontSize = _fontSize,
            NonOverlapping = nonOverlapping,
            Restored = restored,
        };

        try
        {
            // [AI修改] 验收报告只写入用户显式指定的临时路径，不进入正式配置或日志。
            var path = $"{basePath}.{stage}.json";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                IncludeFields = true,
            }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"任务栏验收记录失败：{exception.Message}");
        }
    }

    private static bool RectEquals(Rect left, Rect right) =>
        Math.Abs(left.Left - right.Left) <= 1
        && Math.Abs(left.Top - right.Top) <= 1
        && Math.Abs(left.Right - right.Right) <= 1
        && Math.Abs(left.Bottom - right.Bottom) <= 1;
#endif

    private bool TryPersistReservationRecoveryState(IntPtr taskbar)
    {
        NativeMethods.GetWindowThreadProcessId(taskbar, out var explorerProcessId);
        if (explorerProcessId == 0)
        {
            return false;
        }

        var state = new ReservationRecoveryState
        {
            ExplorerProcessId = explorerProcessId,
            TaskListHandle = _reservedTaskList.ToInt64(),
            ParentHandle = _reservedTaskListParent.ToInt64(),
            Left = _originalTaskListBounds.Left,
            Top = _originalTaskListBounds.Top,
            Right = _originalTaskListBounds.Right,
            Bottom = _originalTaskListBounds.Bottom,
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_reservationRecoveryPath)!);
            File.WriteAllText(_reservationRecoveryPath, JsonSerializer.Serialize(state));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"任务栏恢复凭据写入失败：{exception.Message}");
            return false;
        }
    }

    private void TryRecoverAbandonedReservation()
    {
        if (!File.Exists(_reservationRecoveryPath))
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<ReservationRecoveryState>(File.ReadAllText(_reservationRecoveryPath));
            var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            var bar = taskbar == IntPtr.Zero
                ? IntPtr.Zero
                : NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
            var taskList = bar == IntPtr.Zero
                ? IntPtr.Zero
                : NativeMethods.FindWindowEx(bar, IntPtr.Zero, "MSTaskSwWClass", null);
            NativeMethods.GetWindowThreadProcessId(taskbar, out var explorerProcessId);

            if (state is not null
                && taskList != IntPtr.Zero
                && taskList.ToInt64() == state.TaskListHandle
                && NativeMethods.GetParent(taskList).ToInt64() == state.ParentHandle
                && explorerProcessId == state.ExplorerProcessId
                && NativeMethods.GetWindowRect(taskbar, out var taskbarRect)
                && state.Right > state.Left
                && state.Bottom > state.Top
                && state.Right - state.Left <= taskbarRect.Right - taskbarRect.Left
                && state.Bottom - state.Top <= taskbarRect.Bottom - taskbarRect.Top)
            {
                // [AI修改] 只恢复同一 Explorer 实例和同一任务列表句柄，拒绝把缓存坐标应用到其他窗口。
                NativeMethods.MoveWindow(
                    taskList,
                    state.Left,
                    state.Top,
                    state.Right - state.Left,
                    state.Bottom - state.Top,
                    true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"任务栏恢复凭据读取失败：{exception.Message}");
        }
        finally
        {
            TryDeleteReservationRecoveryState();
        }
    }

    private void TryDeleteReservationRecoveryState()
    {
        try
        {
            File.Delete(_reservationRecoveryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"任务栏恢复凭据清理失败：{exception.Message}");
        }
    }

    private void DestroyHost()
    {
        _source?.Dispose();
        RestoreTaskList();
        _source = null;
        _rootVisual = null;
        _codexRow = null;
        _claudeRow = null;
        _fontSize = TaskbarStripLayout.MinimumFontSize;
        _horizontalWidth = 180;
    }

    private void UpdateTypography(IntPtr taskbar)
    {
        if (_codexRow is null || _claudeRow is null
            || !NativeMethods.GetWindowRect(taskbar, out var taskbarRect))
        {
            return;
        }

        var dpi = NativeMethods.GetDpiForWindow(taskbar);
        var dpiScale = dpi > 0 ? dpi / 96D : 1D;
        var logicalTaskbarHeight = (taskbarRect.Bottom - taskbarRect.Top) / dpiScale;
        // [AI修改] 两行布局时每行字号约为任务栏高度三成，单行保持原放大规则。
        var fontSize = _dualLayout
            ? TaskbarStripLayout.CalculateDualFontSize(logicalTaskbarHeight)
            : TaskbarStripLayout.CalculateFontSize(logicalTaskbarHeight);
        if (Math.Abs(fontSize - _fontSize) < 0.05D)
        {
            return;
        }

        // [AI修改] 字体随任务栏逻辑高度放大，常见 40 像素任务栏约为 22–24 DIP。
        _fontSize = fontSize;
        var separatorHeight = Math.Min(logicalTaskbarHeight * (_dualLayout ? 0.31D : 0.62D), fontSize * 0.92D);
        _codexRow.SetTypography(fontSize, separatorHeight);
        _claudeRow.SetTypography(fontSize, separatorHeight);
    }

    private void UpdateHorizontalWidth(IntPtr taskbar)
    {
        if (_rootVisual is null)
        {
            return;
        }

        _rootVisual.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var dpi = NativeMethods.GetDpiForWindow(taskbar);
        var dpiScale = dpi > 0 ? dpi / 96D : 1D;
        // [AI修改] 以放大后的实际文字宽度紧贴内容，不再保留 132 像素固定下限。
        _horizontalWidth = TaskbarStripLayout.CalculatePhysicalWidth(_rootVisual.DesiredSize.Width, dpiScale,
            _dualLayout ? TaskbarStripLayout.MaximumDualPhysicalWidth : TaskbarStripLayout.MaximumPhysicalWidth);
    }

    private static TextBlock CreateTextBlock(MediaColor color) => new()
    {
        Foreground = new SolidColorBrush(color),
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"),
        // [AI修改] 初始字号使用确认后的放大下限，附着任务栏后再按高度和 DPI 调整。
        FontSize = TaskbarStripLayout.MinimumFontSize,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(3, 0, 3, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border CreateSeparator() => new()
    {
        Width = 1,
        // [AI修改] 初始分隔线与放大字体匹配，运行时继续随任务栏高度缩放。
        Height = TaskbarStripLayout.MinimumFontSize * 0.92D,
        Background = new SolidColorBrush(MediaColor.FromRgb(66, 82, 98)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // [AI修改] 一行任务栏读数：名称（同时显示两家时才出现）、5 小时、7 天和更新时间。
    private sealed class StripRow
    {
        public StripRow(string label, MediaColor fiveHourColor)
        {
            Tag = CreateTextBlock(Colors.WhiteSmoke);
            Tag.Text = label;
            Tag.Visibility = Visibility.Collapsed;
            FiveHour = CreateTextBlock(fiveHourColor);
            FiveHour.Text = "5时 --";
            FiveHour.Visibility = Visibility.Collapsed;
            SevenDay = CreateTextBlock(Colors.LightGreen);
            SevenDay.Text = "7天 --";
            UpdatedAt = CreateTextBlock(Colors.WhiteSmoke);
            UpdatedAt.Text = "--:--:--";
            FiveHourSeparator = CreateSeparator();
            FiveHourSeparator.Visibility = Visibility.Collapsed;
            Separator = CreateSeparator();
            Panel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            };
            foreach (var child in new UIElement[] { Tag, FiveHour, FiveHourSeparator, SevenDay, Separator, UpdatedAt })
            {
                Panel.Children.Add(child);
            }
        }

        public StackPanel Panel { get; }
        public TextBlock Tag { get; }
        public TextBlock FiveHour { get; }
        public TextBlock SevenDay { get; }
        public TextBlock UpdatedAt { get; }
        public Border FiveHourSeparator { get; }
        public Border Separator { get; }

        public void Apply(ProviderQuotaViewModel row)
        {
            Panel.Visibility = row.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            Tag.Visibility = row.ShowLabel ? Visibility.Visible : Visibility.Collapsed;
            FiveHour.Text = $"5时 {row.FiveHourQuota}";
            FiveHour.Visibility = row.HasFiveHourQuota ? Visibility.Visible : Visibility.Collapsed;
            FiveHourSeparator.Visibility = FiveHour.Visibility;
            SevenDay.Text = $"7天 {row.SevenDayQuota}";
            UpdatedAt.Text = row.RefreshedAt;
        }

        public void SetTypography(double fontSize, double separatorHeight)
        {
            Tag.FontSize = FiveHour.FontSize = SevenDay.FontSize = UpdatedAt.FontSize = fontSize;
            FiveHourSeparator.Height = Separator.Height = separatorHeight;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // [AI修改] Win32 坐标转换使用显式结构，确保恢复 Explorer 子窗口时仍采用父窗口坐标。
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    // [AI修改] 仅保存恢复 Explorer 原始布局所需的数值，不记录窗口文字或用户内容。
    private sealed class ReservationRecoveryState
    {
        public uint ExplorerProcessId { get; init; }
        public long TaskListHandle { get; init; }
        public long ParentHandle { get; init; }
        public int Left { get; init; }
        public int Top { get; init; }
        public int Right { get; init; }
        public int Bottom { get; init; }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr FindWindow(string? className, string? windowName);

        [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetWindowRect(IntPtr window, out Rect rectangle);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindow(IntPtr window);

        // [AI修改] 获取 Explorer 任务列表的直接父窗口，用于保存和恢复相对坐标。
        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetParent(IntPtr window);

        // [AI修改] 绑定恢复凭据与当前 Explorer 进程，避免句柄复用时误改其他窗口。
        [LibraryImport("user32.dll")]
        internal static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        // [AI修改] 把屏幕坐标转换为 Explorer 子窗口所需的父窗口客户区坐标。
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ScreenToClient(IntPtr window, ref Point point);

        // [AI修改] 仅调整经典任务栏的任务按钮区域，为 CodeXPedal 信息栏预留真实空间。
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool MoveWindow(
            IntPtr window,
            int x,
            int y,
            int width,
            int height,
            [MarshalAs(UnmanagedType.Bool)] bool repaint);

        // [AI修改] 无法证明安全占位时隐藏子窗口，避免覆盖系统任务按钮。
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ShowWindow(IntPtr window, int command);

        // [AI修改] 读取任务栏所在显示器 DPI，使字号在 Win10/Win11 缩放下保持一致比例。
        [LibraryImport("user32.dll")]
        internal static partial uint GetDpiForWindow(IntPtr window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}
