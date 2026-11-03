// [AI修改] 组织单实例、悬浮窗、托盘、任务栏栏位与定时刷新生命周期。
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using CodeXPedal.Windows.App.Services;
using CodeXPedal.Windows.App.ViewModels;
using CodeXPedal.Windows.App.Views;
using CodeXPedal.Windows.Core.Models;
using CodeXPedal.Windows.Core.Services;

namespace CodeXPedal.Windows.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\CodeXPedal.Windows.Client";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly UsageSnapshotService _snapshotService = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private Mutex? _mutex;
    private MainViewModel? _viewModel;
    private FloatingWindow? _floatingWindow;
    private TrayService? _tray;
    private TaskbarStripService? _taskbarStrip;
    // [AI修改] 只在模式首次应用或发生切换时改变悬浮窗可见性，保留用户临时显示或隐藏的状态。
    private bool? _appliedTaskbarOnlyMode;
    private bool _isExiting;
    private bool _appliedClaudeLiveQuota;
    // [AI修改] 安装/登录 Claude Code 后轮询登录状态，最多 10 分钟。
    private readonly DispatcherTimer _claudeSetupTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DateTimeOffset _claudeSetupDeadline;

    public App()
    {
        // [AI修改] 必须在首次创建 WPF Window 前补齐字体缓存依赖的进程环境。
        WindowsEnvironmentBootstrap.EnsureWindirForWpf();
#if DEBUG
        // [AI修改] DEBUG 验收失败时仅在显式截图路径旁记录异常，Release 不写诊断文件。
        DispatcherUnhandledException += (_, args) =>
        {
            var capturePath = Environment.GetEnvironmentVariable("CODEXPEDAL_UI_CAPTURE");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                File.WriteAllText(capturePath + ".error.txt", args.Exception.ToString());
            }
        };
#endif
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // [AI修改] 作为 Claude Code statusLine 命令被调用时只处理一次输入并立即退出，不创建窗口、不占用单实例锁。
        if (e.Args.Length > 0 && e.Args[0] == ClaudeStatusLineHook.Argument)
        {
            RunClaudeStatusLineHook();
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("CodeXPedal 已经在运行。", "CodeXPedal", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var settings = await _settingsStore.LoadAsync(_lifetime.Token);
#if DEBUG
        // [AI修改] 截图验收始终显示悬浮窗，避免用户保存的“仅任务栏”设置阻止渲染。
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEXPEDAL_UI_CAPTURE")))
        {
            settings = settings with { TaskbarOnlyMode = false };
        }

        // [AI修改] DEBUG 验收可临时覆盖为仅任务栏模式，不写入用户设置文件。
        if (Environment.GetEnvironmentVariable("CODEXPEDAL_TASKBAR_ONLY") == "1")
        {
            settings = settings with { TaskbarOnlyMode = true, ShowTaskbarStrip = true };
        }
#endif
        _viewModel = new MainViewModel(settings);
        // [AI修改] 启动时先探测安装情况，避免只装 Claude 的用户首帧看到空的 Codex 行。
        var (codexDetected, claudeDetected) = ProviderDetection.Detect();
        _viewModel.ApplySnapshot(UsageSnapshot.Empty(DateTimeOffset.Now) with { CodexDetected = codexDetected, ClaudeDetected = claudeDetected });
        _appliedClaudeLiveQuota = settings.ClaudeLiveQuota;
        _floatingWindow = new FloatingWindow(_viewModel, SaveSettingsAsync, OpenSettings, OpenDetails, ExitApplication);
        _floatingWindow.RefreshRequested += async (_, _) => await RefreshNowAsync();

        _tray = new TrayService(ShowFloatingWindow, () => _floatingWindow?.Hide(), () => _floatingWindow?.IsVisible == true,
            RefreshNowAsync, OpenDetails, OpenSettings, ExitApplication);
        _taskbarStrip = new TaskbarStripService(ShowFloatingWindow);
        ApplySettings(settings);

        _viewModel.SettingsChanged += OnSettingsChanged;
        _viewModel.SnapshotChanged += OnSnapshotChanged;
        _refreshTimer.Tick += async (_, _) => await RefreshNowAsync();
        _refreshTimer.Start();
#if DEBUG
        // [AI修改] DEBUG 验收可定时走正式退出路径，验证任务栏预留空间和恢复凭据会被清理。
        ScheduleVerificationExit();
        // [AI修改] DEBUG 验收可直接打开设置页，检查真实 WPF 布局且不影响 Release 行为。
        if (Environment.GetEnvironmentVariable("CODEXPEDAL_OPEN_SETTINGS") == "1")
        {
            _ = Dispatcher.BeginInvoke(OpenSettings);
        }
        // [AI修改] 详情窗口验收走实际打开入口，覆盖曾经的详情页崩溃路径。
        if (Environment.GetEnvironmentVariable("CODEXPEDAL_OPEN_DETAILS") == "1")
        {
            _ = Dispatcher.BeginInvoke(OpenDetails);
        }
#endif
        _claudeSetupTimer.Tick += async (_, _) => await CheckClaudeSetupAsync();
        // [AI修改] 在用 Claude（有配置目录或桌面版）但 Claude Code 未安装/未登录时，启动后询问一次。
        if (claudeDetected && !settings.ClaudeSetupPromptDismissed
#if DEBUG
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEXPEDAL_UI_CAPTURE"))
#endif
            )
        {
            _ = Dispatcher.BeginInvoke(PromptClaudeSetup, DispatcherPriority.ApplicationIdle);
        }

        await RefreshNowAsync();
    }

    private void PromptClaudeSetup()
    {
        var status = ClaudeCliSetup.GetStatus();
        if (status == ClaudeCliStatus.Ready || _viewModel is null || _isExiting)
        {
            return;
        }

        var dialog = new ClaudeSetupWindow(status);
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case ClaudeSetupChoice.Install:
                StartClaudeSetup();
                break;
            case ClaudeSetupChoice.Never:
                _viewModel.ApplySettings(_viewModel.Settings with { ClaudeSetupPromptDismissed = true });
                break;
        }
    }

    // [AI修改] 打开可见的安装/登录窗口并开始轮询；设置页按钮与启动弹窗共用此入口。
    private bool StartClaudeSetup()
    {
        try
        {
            ClaudeCliSetup.Launch();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(
                $"无法打开 PowerShell 安装窗口：{exception.Message}\n\n也可以手动在 PowerShell 中运行：\n{ClaudeCliSetup.InstallCommand}\n然后运行 claude auth login",
                "CodeXPedal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _claudeSetupDeadline = DateTimeOffset.UtcNow.AddMinutes(10);
        _claudeSetupTimer.Start();
        return true;
    }

    // [AI修改] 发现登录完成后自动开启实时读取并立即刷新；超时只停止轮询，不弹错误。
    private async Task CheckClaudeSetupAsync()
    {
        if (_viewModel is null || DateTimeOffset.UtcNow > _claudeSetupDeadline)
        {
            _claudeSetupTimer.Stop();
            return;
        }

        if (ClaudeCliSetup.GetStatus() != ClaudeCliStatus.Ready)
        {
            return;
        }

        _claudeSetupTimer.Stop();
        _snapshotService.ResetClaudeLiveThrottle();
        if (_viewModel.Settings.ClaudeLiveQuota)
        {
            await RefreshNowAsync();
        }
        else
        {
            // 设置变化处理中会保存并立即刷新。
            _viewModel.ApplySettings(_viewModel.Settings with { ClaudeLiveQuota = true });
        }
    }

    private void RunClaudeStatusLineHook()
    {
        try
        {
            using var input = Console.OpenStandardInput();
            var text = ClaudeStatusLineHook.Run(input, ClaudeExecutableResolver.ConfigDirectory(), DateTimeOffset.UtcNow);
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            output.Write(text);
        }
        catch (IOException)
        {
            // [AI修改] Claude Code 提前关闭管道时静默退出，不弹窗。
        }

        Shutdown(0);
    }

#if DEBUG
    private void ScheduleVerificationExit()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("CODEXPEDAL_UI_EXIT_AFTER_SECONDS"), out var seconds)
            || seconds <= 0)
        {
            return;
        }

        var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        exitTimer.Tick += (_, _) =>
        {
            exitTimer.Stop();
            ExitApplication();
        };
        exitTimer.Start();
    }
#endif

    protected override void OnExit(ExitEventArgs e)
    {
        _lifetime.Cancel();
        _refreshTimer.Stop();
        _claudeSetupTimer.Stop();
        _taskbarStrip?.Dispose();
        _tray?.Dispose();
        _snapshotService.Dispose();
        _lifetime.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private async Task RefreshNowAsync()
    {
        if (_viewModel is null || _viewModel.IsRefreshing)
        {
            return;
        }

        _viewModel.IsRefreshing = true;
        try
        {
            var snapshot = await _snapshotService.RefreshAsync(_viewModel.Settings.ClaudeLiveQuota, _lifetime.Token);
#if DEBUG
            // [AI修改] 自动验收可稳定模拟 Plus 或 Pro 的额度拓扑，不写入用户配置或 Release 行为。
            snapshot = ApplyCaptureQuotaOverride(snapshot);
#endif
            _viewModel.ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // [AI修改] 正常退出时取消刷新，不向用户显示错误。
        }
        finally
        {
            if (_viewModel is not null)
            {
                _viewModel.IsRefreshing = false;
            }
        }
    }

#if DEBUG
    private static UsageSnapshot ApplyCaptureQuotaOverride(UsageSnapshot snapshot)
    {
        var mode = Environment.GetEnvironmentVariable("CODEXPEDAL_CAPTURE_QUOTA_MODE")?.Trim().ToLowerInvariant();
        if (mode is not "plus" and not "pro")
        {
            return snapshot;
        }

        var now = DateTimeOffset.Now;
        return snapshot with
        {
            FiveHourQuota = mode == "plus"
                ? new QuotaWindow(QuotaWindowKind.FiveHour, 7D, RateLimitParser.FiveHourMinutes, now.AddHours(4))
                : null,
            SevenDayQuota = new QuotaWindow(QuotaWindowKind.SevenDay, 18D, RateLimitParser.SevenDayMinutes, now.AddDays(6)),
            QuotaSource = QuotaDataSource.OfficialAppServer,
            QuotaObservedAt = now,
            Diagnostics = [],
        };
    }
#endif

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        ApplySettings(_viewModel.Settings);
        _ = SaveSettingsAsync(_viewModel.Settings);
        // [AI修改] 切换 Claude 实时读取后立即刷新，其余设置变化不额外触发读取。
        if (_appliedClaudeLiveQuota != _viewModel.Settings.ClaudeLiveQuota)
        {
            _appliedClaudeLiveQuota = _viewModel.Settings.ClaudeLiveQuota;
            _ = RefreshNowAsync();
        }
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _taskbarStrip?.Update(_viewModel);
        _tray?.UpdateTooltip(_viewModel);
    }

    private void ApplySettings(AppSettings settings)
    {
        _refreshTimer.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);
        _floatingWindow?.ApplySettings(settings);
        _taskbarStrip?.SetVisible(settings.ShowTaskbarStrip, _viewModel);
        // [AI修改] 启动时按模式决定是否创建可见悬浮窗，切换模式时立即隐藏或恢复，避免启动闪窗。
        if (_floatingWindow is not null && _appliedTaskbarOnlyMode != settings.TaskbarOnlyMode)
        {
            _appliedTaskbarOnlyMode = settings.TaskbarOnlyMode;
            if (settings.TaskbarOnlyMode)
            {
                _floatingWindow.Hide();
            }
            else
            {
                _floatingWindow.Show();
            }
        }

#if DEBUG
        // [AI修改] 自动验收不改写真实开机启动项，避免把用户入口改成 Debug 构建路径。
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEXPEDAL_UI_CAPTURE"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEXPEDAL_TASKBAR_VERIFY_PATH")))
#endif
        {
            StartupService.SetEnabled(settings.StartWithWindows);
        }
    }

    private async Task SaveSettingsAsync(AppSettings settings)
    {
        try
        {
            await _settingsStore.SaveAsync(settings, _lifetime.Token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"设置保存失败：{exception.Message}");
        }
    }

    private void ShowFloatingWindow()
    {
        if (_floatingWindow is null)
        {
            return;
        }

        _floatingWindow.Show();
        if (_floatingWindow.WindowState == WindowState.Minimized)
        {
            _floatingWindow.WindowState = WindowState.Normal;
        }

        _floatingWindow.Activate();
    }

    private void OpenDetails()
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            // [AI修改] 辅助窗口独立创建，避免详情页加载异常直接终止主程序。
            var details = new DetailsWindow(_viewModel);
            AttachOwner(details);
            details.Show();
        }
        catch (Exception exception) when (exception is InvalidOperationException or XamlParseException)
        {
            System.Windows.MessageBox.Show(
                $"详情窗口打开失败：{exception.Message}",
                "CodeXPedal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenSettings()
    {
        if (_viewModel is null)
        {
            return;
        }

        var window = new SettingsWindow(_viewModel.Settings, StartClaudeSetup);
        AttachOwner(window);
        if (window.ShowDialog() == true)
        {
            _viewModel.ApplySettings(window.Result);
        }
    }

    // [AI修改] 仅任务栏模式下悬浮窗从未显示过，WPF 不允许把它设为 Owner（会抛 InvalidOperationException 导致崩溃）；
    // 此时不设 Owner，改为屏幕居中显示。悬浮窗可见时仍居中于悬浮窗。
    private void AttachOwner(Window window)
    {
        if (_floatingWindow is { IsVisible: true })
        {
            window.Owner = _floatingWindow;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _floatingWindow?.CloseForExit();
        Shutdown();
    }
}
