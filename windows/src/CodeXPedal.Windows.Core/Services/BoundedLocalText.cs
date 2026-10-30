// [AI修改] 可选本地统计统一冻结文件长度；过大时明确降级，不能无界读入或暗截断计数。
using System.Text;
namespace CodeXPedal.Windows.Core.Services;
internal static class BoundedLocalText
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal static string ReadSnapshot(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length > MaximumBytes) { throw new IOException("本地统计文件过大，本次不作完整统计"); }
        var data = new byte[(int)length];
        var count = 0;
        while (count < data.Length) { var read = stream.Read(data, count, data.Length - count); if (read == 0) { break; } count += read; }
        return Encoding.UTF8.GetString(data, 0, count);
    }
}
