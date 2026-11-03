// [AI修改] 一键安装并登录 Claude Code 的确认窗口，按当前状态（未安装/未登录）显示不同说明。
using System.Windows;
using CodeXPedal.Windows.Core.Services;

namespace CodeXPedal.Windows.App.Views;

public enum ClaudeSetupChoice
{
    Later,
    Install,
    Never,
}

public partial class ClaudeSetupWindow : Window
{
    public ClaudeSetupWindow(ClaudeCliStatus status)
    {
        InitializeComponent();
        var installed = status == ClaudeCliStatus.NotLoggedIn;
        TitleText.Text = installed ? "Claude Code 尚未登录" : "未安装 Claude Code";
        BodyText.Text = (installed
                ? "检测到已安装 Claude Code，但还没有登录，所以暂时无法显示 Claude 剩余额度。"
                : "检测到你在使用 Claude，但本机没有安装 Claude Code（命令行版），所以暂时无法显示 Claude 剩余额度。")
            + "\n\n点击“" + (installed ? "登录" : "安装并登录") + "”后会打开一个 PowerShell 窗口"
            + (installed ? "" : "，用官方脚本安装 Claude Code")
            + "，随后在浏览器中打开 Claude 登录页，你只需点击“授权”。登录成功后 CodeXPedal 会自动开启实时读取并显示 5 小时/7 天额度。"
            + "\n\n需要 Claude Pro 或 Max 订阅。本软件不会代填账号或读取密码。";
        InstallButton.Content = installed ? "登录" : "安装并登录";
    }

    public ClaudeSetupChoice Choice { get; private set; } = ClaudeSetupChoice.Later;

    private void InstallButton_Click(object sender, RoutedEventArgs e) => Finish(ClaudeSetupChoice.Install);

    private void LaterButton_Click(object sender, RoutedEventArgs e) => Finish(ClaudeSetupChoice.Later);

    private void NeverButton_Click(object sender, RoutedEventArgs e) => Finish(ClaudeSetupChoice.Never);

    private void Finish(ClaudeSetupChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
