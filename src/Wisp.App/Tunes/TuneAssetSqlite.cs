using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Wisp.Core.Tunes;

namespace Wisp.App.Tunes;

// Windows supplies SQLite. SQL runs only against Wisp's verified local asset copy.
internal static class TuneAssetSqlite
{
    internal static TuneAssetMetadata Extract(byte[] decoded, CancellationToken cancellationToken,
        string? temporaryDirectory = null)
    {
        if (decoded.Length != TuneAssetCapture.ExpectedLength ||
            Convert.ToHexString(SHA256.HashData(decoded)) != TuneAssetCapture.DecodedHash)
            throw new InvalidDataException("The tuning database did not match the supported asset.");
        var directory = temporaryDirectory is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wisp", "TuneCache")
            : Path.GetFullPath(temporaryDirectory);
        EnsureOrdinaryPath(directory);
        Directory.CreateDirectory(directory);
        EnsureOrdinaryPath(directory);
        var path = Path.Combine(directory, $"read-{Guid.NewGuid():N}.tmp");
        nint database = 0;
        ProgressCallback? progress = null;
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                cancellationToken.ThrowIfCancellationRequested();
                file.Write(decoded);
                file.Flush(flushToDisk: false);
            }
            Check(sqlite3_open_v2(path, out database, 1 | 0x10000, 0), "open read-only asset"); // READONLY | FULLMUTEX
            if (sqlite3_db_readonly(database, "main") != 1) throw new InvalidDataException("The tuning database is not read-only.");
            _ = sqlite3_limit(database, 0, 1024 * 1024); // Maximum string/blob size.
            _ = sqlite3_limit(database, 1, 16384); // Maximum SQL length.
            // SQLite loads the asset's full schema, including wider tables outside our four-column projections.
            _ = sqlite3_limit(database, 2, 2000); // Keep SQLite's ordinary maximum column count.
            _ = sqlite3_limit(database, 7, 0); // No attached databases.
            var started = Stopwatch.GetTimestamp();
            progress = _ => cancellationToken.IsCancellationRequested || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(15) ? 1 : 0;
            sqlite3_progress_handler(database, 1000, progress, 0);
            Execute(database, "PRAGMA query_only=ON", "enable query-only mode");
            Execute(database, "PRAGMA trusted_schema=OFF", "disable trusted schema");
            var rows = new List<TuneAssetRow>();
            foreach (var (kind, table, parent, childParent) in TuneAssetMetadata.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var integrity = Prepare(database, $"PRAGMA integrity_check(\"{table}\")", $"prepare {kind} integrity check"))
                {
                    if (sqlite3_step(integrity.Value) != 100 || sqlite3_column_bytes(integrity.Value, 0) != 2 ||
                        Marshal.PtrToStringUTF8(sqlite3_column_text(integrity.Value, 0)) != "ok" ||
                        sqlite3_step(integrity.Value) != 101)
                        throw new InvalidDataException("The required tuning metadata failed its integrity check.");
                }
                var childExpression = childParent is null ? "NULL" : $"\"{childParent}\"";
                using var query = Prepare(database, $"SELECT Id, \"{parent}\", Level, {childExpression} FROM \"{table}\"", $"prepare {kind} rows");
                while (true)
                {
                    var code = sqlite3_step(query.Value);
                    if (code == 101) break;
                    if (code != 100) Check(code, $"read {kind} rows");
                    if (rows.Count >= 250000) throw new InvalidDataException("The tuning metadata exceeds its size limit.");
                    var id = Integer(query.Value, 0);
                    var owner = Integer(query.Value, 1);
                    if (!id.HasValue || !owner.HasValue)
                        throw new InvalidDataException("The tuning metadata contains an invalid identity.");
                    rows.Add(new TuneAssetRow(kind, owner.Value, id.Value,
                        Integer(query.Value, 2), Integer(query.Value, 3)));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new TuneAssetMetadata(rows);
        }
        finally
        {
            if (database != 0) _ = sqlite3_close(database);
            GC.KeepAlive(progress);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void EnsureOrdinaryPath(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The temporary tuning cache must use a local folder without links.");
    }
    private static int? Integer(nint statement, int column)
    {
        var type = sqlite3_column_type(statement, column);
        if (type == 5) return null;
        if (type != 1) throw new InvalidDataException("The tuning metadata contains an invalid number.");
        var value = sqlite3_column_int64(statement, column);
        if (value < int.MinValue || value > int.MaxValue) throw new InvalidDataException("The tuning metadata number exceeds its bounds.");
        return (int)value;
    }
    private static Statement Prepare(nint database, string sql, string stage)
    {
        var code = sqlite3_prepare_v2(database, sql, -1, out var statement, 0);
        if (code != 0)
        {
            if (statement != 0) _ = sqlite3_finalize(statement);
            Check(code, stage);
        }
        return new Statement(statement);
    }
    private static void Execute(nint database, string sql, string stage)
    {
        using var statement = Prepare(database, sql, stage);
        var code = sqlite3_step(statement.Value);
        if (code != 101) Check(code, stage);
    }
    private static void Check(int result, string stage)
    {
        if (result != 0) throw new InvalidDataException($"Local tuning metadata: {stage}; SQLite code {result}.");
    }
    private sealed class Statement(nint value) : IDisposable
    {
        internal nint Value { get; } = value;
        public void Dispose() { if (Value != 0) _ = sqlite3_finalize(Value); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProgressCallback(nint state);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, nint vfs);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(nint database);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_db_readonly(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_limit(nint database, int id, int value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_progress_handler(nint database, int instructions, ProgressCallback callback, nint state);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint statement, nint tail);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(nint statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(nint statement);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(nint statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(nint statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint sqlite3_column_text(nint statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(nint statement, int column);
}
