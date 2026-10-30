// [AI修改] 使用 Windows 自带 winsqlite3.dll 只读访问 Codex 数据库，避免引入额外运行时依赖。
using System.Runtime.InteropServices;

namespace CodeXPedal.Windows.Core.Infrastructure;

public sealed partial class WinSqliteDatabase : IDisposable
{
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private IntPtr _database;

    public WinSqliteDatabase(string path)
    {
        var result = NativeMethods.sqlite3_open_v2(path, out _database, SqliteOpenReadOnly, IntPtr.Zero);
        if (result != 0)
        {
            var message = GetErrorMessage();
            Dispose();
            throw new InvalidOperationException($"SQLite 只读打开失败（{result}）：{message}");
        }
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql)
    {
        ThrowIfDisposed();
        var prepareResult = NativeMethods.sqlite3_prepare_v2(_database, sql, -1, out var statement, IntPtr.Zero);
        if (prepareResult != 0)
        {
            throw new InvalidOperationException($"SQLite 查询准备失败（{prepareResult}）：{GetErrorMessage()}");
        }

        try
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (true)
            {
                var stepResult = NativeMethods.sqlite3_step(statement);
                if (stepResult == SqliteDone)
                {
                    return rows;
                }

                if (stepResult != SqliteRow)
                {
                    throw new InvalidOperationException($"SQLite 查询执行失败（{stepResult}）：{GetErrorMessage()}");
                }

                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                // [AI修改] 可选本地统计超上限整体失败，不保留截断汇总冒充完整结果。
                if (rows.Count >= 4096) { throw new InvalidOperationException("本地统计行数超过上限"); }
                var columnCount = NativeMethods.sqlite3_column_count(statement);
                for (var index = 0; index < columnCount; index++)
                {
                    var name = Marshal.PtrToStringUTF8(NativeMethods.sqlite3_column_name(statement, index))
                        ?? $"column{index}";
                    row[name] = ReadColumn(statement, index);
                }

                rows.Add(row);
            }
        }
        finally
        {
            NativeMethods.sqlite3_finalize(statement);
        }
    }

    public void Dispose()
    {
        if (_database == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.sqlite3_close_v2(_database);
        _database = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    private static object? ReadColumn(IntPtr statement, int index)
    {
        // [AI修改] 路径等文本列不允许带入超长本地记录。
        if (NativeMethods.sqlite3_column_bytes(statement, index) > 4096) { throw new InvalidOperationException("本地统计字段过大"); }
        return NativeMethods.sqlite3_column_type(statement, index) switch
        {
            1 => NativeMethods.sqlite3_column_int64(statement, index),
            2 => NativeMethods.sqlite3_column_double(statement, index),
            3 => Marshal.PtrToStringUTF8(NativeMethods.sqlite3_column_text(statement, index)),
            _ => null,
        };
    }

    private string GetErrorMessage() => _database == IntPtr.Zero
        ? "未知错误"
        : Marshal.PtrToStringUTF8(NativeMethods.sqlite3_errmsg(_database)) ?? "未知错误";

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_database == IntPtr.Zero, this);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("winsqlite3.dll", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int sqlite3_open_v2(string filename, out IntPtr database, int flags, IntPtr vfs);

        [LibraryImport("winsqlite3.dll", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int sqlite3_prepare_v2(IntPtr database, string sql, int byteCount, out IntPtr statement, IntPtr tail);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_step(IntPtr statement);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_finalize(IntPtr statement);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_close_v2(IntPtr database);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_column_count(IntPtr statement);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_column_bytes(IntPtr statement, int index);

        [LibraryImport("winsqlite3.dll")]
        internal static partial IntPtr sqlite3_column_name(IntPtr statement, int index);

        [LibraryImport("winsqlite3.dll")]
        internal static partial int sqlite3_column_type(IntPtr statement, int index);

        [LibraryImport("winsqlite3.dll")]
        internal static partial long sqlite3_column_int64(IntPtr statement, int index);

        [LibraryImport("winsqlite3.dll")]
        internal static partial double sqlite3_column_double(IntPtr statement, int index);

        [LibraryImport("winsqlite3.dll")]
        internal static partial IntPtr sqlite3_column_text(IntPtr statement, int index);

        [LibraryImport("winsqlite3.dll")]
        internal static partial IntPtr sqlite3_errmsg(IntPtr database);
    }
}
