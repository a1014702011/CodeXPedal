// [AI修改] 将 Windows 客户端设置保存在当前用户目录，并采用原子替换避免配置写坏。
using System.Text.Json;
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.Core.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // [AI修改] 初次保存时窗口可能还没有坐标，允许保留 NaN，避免同时丢失额度偏好。
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // [AI修改] 快速保存与窗口定位写入串行，避免抢占同一个原子替换临时文件。
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    // [AI修改] 测试使用隔离文件验证旧配置和原子保存，不接触真实用户设置。
    public SettingsStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodeXPedal", "settings.json")) { }
    internal SettingsStore(string settingsPath) => SettingsPath = settingsPath;
    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        // [AI修改] 只读取当前产品设置，不再迁移旧品牌数据。
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(SettingsPath);
            return (await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? new AppSettings()).Normalize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        // [AI修改] 串行完成写入，确保两个开关和时间模式落入同一份配置。
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = SettingsPath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings.Normalize(), JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally { _saveGate.Release(); }
    }
}
