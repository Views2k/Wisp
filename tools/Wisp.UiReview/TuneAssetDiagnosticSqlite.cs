using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Wisp.App.Tunes;

namespace Wisp.UiReview;

internal static class TuneAssetDiagnosticSqlite
{
    internal sealed record Projection(string Kind, int Count, string Hash, string Schema, bool SchemaMatches,
        TuneAssetRow[] Rows);
    internal sealed record WriteProtectionCheck(int ReportedReadonly, int BeginWriteResult,
        int HeaderWriteResult, bool BufferUnchanged);

    internal static Projection[] Project(byte[] decoded, Projection[]? expected, CancellationToken token)
    {
        TuneAssetDiagnostic.Need(decoded.Length is >= 1024 and <= TuneAssetDiagnostic.MaximumLength, "sqlite-size-invalid");
        var beforeHash = SHA256.HashData(decoded);
        nint database = 0;
        var pinned = GCHandle.Alloc(decoded, GCHandleType.Pinned);
        Progress? progress = null;
        Authorizer? authorizer = null;
        try
        {
            Check(sqlite3_open_v2(":memory:", out database, 2 | 4 | 0x10000, 0), "sqlite-open");
            _ = sqlite3_limit(database, 0, 1024 * 1024);
            _ = sqlite3_limit(database, 1, 16384);
            _ = sqlite3_limit(database, 2, 2000);
            _ = sqlite3_limit(database, 7, 0);
            Check(sqlite3_db_config(database, 1005, 0, out _), "sqlite-disable-extensions");
            Check(sqlite3_db_config(database, 1010, 1, out _), "sqlite-defensive");
            Check(sqlite3_db_config(database, 1017, 0, out _), "sqlite-untrusted-schema");
            var started = Stopwatch.GetTimestamp();
            progress = _ => token.IsCancellationRequested || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5) ? 1 : 0;
            sqlite3_progress_handler(database, 1000, progress, 0);
            Check(sqlite3_deserialize(database, "main", pinned.AddrOfPinnedObject(), decoded.Length, decoded.Length, 4),
                "sqlite-readonly-deserialize");
            // memdb enforces DESERIALIZE_READONLY at write-lock acquisition. db_readonly reports
            // the separately opened B-tree mode, which can remain read/write for this VFS.
            using (var enable = Prepare(database, "PRAGMA query_only=ON"))
                TuneAssetDiagnostic.Need(sqlite3_step(enable.Value) == 101, "sqlite-query-only-failed");
            using (var verify = Prepare(database, "PRAGMA query_only"))
                TuneAssetDiagnostic.Need(sqlite3_step(verify.Value) == 100 && Integer(verify.Value, 0) == 1 &&
                    sqlite3_step(verify.Value) == 101, "sqlite-query-only-not-enabled");
            var allowed = TuneAssetMetadata.Tables.Select(row => row.Table).ToHashSet(StringComparer.Ordinal);
            authorizer = (_, action, first, second, _, origin) =>
            {
                if (origin != 0) return 1; // No view or trigger evaluation.
                if (action == 21) return 0; // SELECT.
                if (action == 20)
                {
                    var name = Marshal.PtrToStringUTF8(first);
                    return name is "sqlite_master" or "sqlite_schema" || name is not null && allowed.Contains(name) ? 0 : 1;
                }
                if (action == 19 && Marshal.PtrToStringUTF8(first) == "integrity_check" &&
                    Marshal.PtrToStringUTF8(second) is { } table && allowed.Contains(table)) return 0;
                return 1;
            };
            Check(sqlite3_set_authorizer(database, authorizer, 0), "sqlite-authorizer");
            var result = new List<Projection>();
            var totalRows = 0;
            foreach (var (kind, table, parent, child) in TuneAssetMetadata.Tables)
            {
                token.ThrowIfCancellationRequested();
                string schema;
                using (var query = Prepare(database, $"SELECT type,sql FROM sqlite_schema WHERE name='{table}'"))
                {
                    TuneAssetDiagnostic.Need(sqlite3_step(query.Value) == 100 && Text(query.Value, 0) == "table", "required-table-missing");
                    schema = Text(query.Value, 1);
                    TuneAssetDiagnostic.Need(schema.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase) &&
                        sqlite3_step(query.Value) == 101, "required-table-not-ordinary");
                }
                var baseline = expected?.Single(item => item.Kind == kind.ToString());
                if (baseline is not null && schema != baseline.Schema)
                {
                    result.Add(new(kind.ToString(), 0, "", schema, false, []));
                    continue;
                }
                using (var integrity = Prepare(database, $"PRAGMA integrity_check(\"{table}\")"))
                    TuneAssetDiagnostic.Need(sqlite3_step(integrity.Value) == 100 && Text(integrity.Value, 0) == "ok" &&
                        sqlite3_step(integrity.Value) == 101, "required-table-integrity-failed");
                var rows = new List<TuneAssetRow>();
                var identities = new HashSet<(int Parent, int Id)>();
                var childExpression = child is null ? "NULL" : $"\"{child}\"";
                using (var query = Prepare(database, $"SELECT Id,\"{parent}\",Level,{childExpression} FROM \"{table}\""))
                {
                    while (true)
                    {
                        var code = sqlite3_step(query.Value);
                        if (code == 101) break;
                        Check(code == 100 ? 0 : code, "sqlite-read-rows");
                        TuneAssetDiagnostic.Need(++totalRows <= 250000, "projection-row-limit");
                        var id = Integer(query.Value, 0);
                        var owner = Integer(query.Value, 1);
                        TuneAssetDiagnostic.Need(id.HasValue && owner.HasValue, "projection-null-identity");
                        TuneAssetDiagnostic.Need(identities.Add((owner!.Value, id!.Value)), "projection-duplicate-identity");
                        rows.Add(new(kind, owner.Value, id.Value, Integer(query.Value, 2), Integer(query.Value, 3)));
                    }
                }
                var sorted = rows.OrderBy(row => row.Parent).ThenBy(row => row.Id).ToArray();
                result.Add(new(kind.ToString(), sorted.Length, Hash(sorted, (int)kind), schema, true, sorted));
            }
            token.ThrowIfCancellationRequested();
            return result.ToArray();
        }
        finally
        {
            // SQLite borrows the pinned buffer; it must close before the pin is released.
            var closed = database == 0 || sqlite3_close(database) == 0;
            GC.KeepAlive(progress);
            GC.KeepAlive(authorizer);
            if (closed) pinned.Free();
            TuneAssetDiagnostic.Need(closed, "sqlite-close-failed");
            TuneAssetDiagnostic.Need(beforeHash.AsSpan().SequenceEqual(SHA256.HashData(decoded)), "sqlite-borrowed-buffer-changed");
        }
    }

    internal static WriteProtectionCheck VerifyDeserializeWriteProtection(byte[] reference, CancellationToken token)
    {
        // Offline only: test the VFS flag without query_only or an authorizer masking its effect.
        var copy = (byte[])reference.Clone();
        var beforeHash = SHA256.HashData(copy);
        var pinned = GCHandle.Alloc(copy, GCHandleType.Pinned);
        nint database = 0;
        Progress? progress = null;
        var reportedReadonly = -1;
        var beginResult = -1;
        var headerResult = -1;
        var unchanged = false;
        try
        {
            Check(sqlite3_open_v2(":memory:", out database, 2 | 4 | 0x10000, 0), "offline-sqlite-open");
            var started = Stopwatch.GetTimestamp();
            progress = _ => token.IsCancellationRequested || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5) ? 1 : 0;
            sqlite3_progress_handler(database, 1000, progress, 0);
            Check(sqlite3_deserialize(database, "main", pinned.AddrOfPinnedObject(), copy.Length, copy.Length, 4),
                "offline-readonly-deserialize");
            reportedReadonly = sqlite3_db_readonly(database, "main");
            using (var begin = Prepare(database, "BEGIN IMMEDIATE")) beginResult = sqlite3_step(begin.Value);
            using (var header = Prepare(database, "PRAGMA user_version=1")) headerResult = sqlite3_step(header.Value);
            TuneAssetDiagnostic.Need(beginResult == 8 && headerResult == 8, "offline-deserialize-did-not-reject-write");
        }
        finally
        {
            var closed = database == 0 || sqlite3_close(database) == 0;
            GC.KeepAlive(progress);
            if (closed) pinned.Free();
            unchanged = beforeHash.AsSpan().SequenceEqual(SHA256.HashData(copy));
            CryptographicOperations.ZeroMemory(copy);
            TuneAssetDiagnostic.Need(closed, "offline-sqlite-close-failed");
            TuneAssetDiagnostic.Need(unchanged, "offline-readonly-buffer-changed");
        }
        return new(reportedReadonly, beginResult, headerResult, unchanged);
    }

    private static string Hash(TuneAssetRow[] rows, int kind)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("WISP-TUNE-PARTS\0v1\0"));
        Span<byte> bytes = stackalloc byte[18];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, kind);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], rows.Length);
        hash.AppendData(bytes[..8]);
        foreach (var row in rows)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes, row.Parent);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], row.Id);
            bytes[8] = row.Level.HasValue ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(bytes[9..], row.Level ?? 0);
            bytes[13] = row.ChildParent.HasValue ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(bytes[14..], row.ChildParent ?? 0);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static int? Integer(nint statement, int column)
    {
        var type = sqlite3_column_type(statement, column);
        if (type == 5) return null;
        TuneAssetDiagnostic.Need(type == 1, "projection-not-integer");
        var value = sqlite3_column_int64(statement, column);
        TuneAssetDiagnostic.Need(value is >= int.MinValue and <= int.MaxValue, "projection-integer-overflow");
        return (int)value;
    }
    private static string Text(nint statement, int column)
    {
        TuneAssetDiagnostic.Need(sqlite3_column_type(statement, column) == 3 && sqlite3_column_bytes(statement, column) <= 16384,
            "sqlite-text-invalid");
        return Marshal.PtrToStringUTF8(sqlite3_column_text(statement, column)) ?? "";
    }
    private static Statement Prepare(nint database, string sql)
    {
        var code = sqlite3_prepare_v2(database, sql, -1, out var statement, 0);
        if (code != 0)
        {
            if (statement != 0) _ = sqlite3_finalize(statement);
            Check(code, "sqlite-prepare");
        }
        return new(statement);
    }
    private static void Check(int code, string stage) => TuneAssetDiagnostic.Need(code == 0, $"{stage}-code-{code}");
    private sealed class Statement(nint value) : IDisposable
    {
        internal nint Value { get; } = value;
        public void Dispose() { _ = sqlite3_finalize(Value); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Progress(nint state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Authorizer(nint state, int action, nint first, nint second, nint database, nint origin);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, nint vfs);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_deserialize(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema,
        nint buffer, long size, long capacity, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_db_config(nint database, int operation, int value, out int result);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_db_readonly(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(nint database);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_limit(nint database, int id, int value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_progress_handler(nint database, int instructions, Progress callback, nint state);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_set_authorizer(nint database, Authorizer callback, nint state);
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
    private static extern int sqlite3_column_type(nint statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(nint statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint sqlite3_column_text(nint statement, int column);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(nint statement, int column);
}
