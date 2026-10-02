using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Wisp.App.Tunes;

// A bounded private copy is borrowed read-only by Windows SQLite. Only exact required
// schemas and numeric projections become metadata; mutable unrelated game state is discarded.
internal static class TuneAssetSqlite
{
    internal static TuneAssetMetadata Extract(byte[] decoded, CancellationToken token,
        NativeTuneCompatibilityLayout.AssetContract? asset = null)
    {
        token.ThrowIfCancellationRequested();
        TuneAssetCapture.ValidateHeader(decoded, asset);
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
                Require(sqlite3_step(enable.Value) == 101, "sqlite-query-only-failed");
            using (var verify = Prepare(database, "PRAGMA query_only"))
                Require(sqlite3_step(verify.Value) == 100 && Integer(verify.Value, 0) == 1 &&
                    sqlite3_step(verify.Value) == 101, "sqlite-query-only-not-enabled");
            var allowed = TuneAssetMetadata.Tables.Select(row => row.Table).ToHashSet(StringComparer.Ordinal);
            allowed.Add("Data_Car");
            allowed.Add("List_CarMake");
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
            var result = new List<TuneAssetRow>();
            var totalRows = 0;
            foreach (var (kind, table, parent, child) in TuneAssetMetadata.Tables)
            {
                token.ThrowIfCancellationRequested();
                string schema;
                using (var query = Prepare(database, $"SELECT type,sql FROM sqlite_schema WHERE name='{table}'"))
                {
                    Require(sqlite3_step(query.Value) == 100 && Text(query.Value, 0) == "table", "required-table-missing");
                    schema = Text(query.Value, 1);
                    Require(schema.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase) &&
                        sqlite3_step(query.Value) == 101, "required-table-not-ordinary");
                }
                TuneAssetContract.ValidateSchema(kind, schema);
                using (var integrity = Prepare(database, $"PRAGMA integrity_check(\"{table}\")"))
                    Require(sqlite3_step(integrity.Value) == 100 && Text(integrity.Value, 0) == "ok" &&
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
                        Require(++totalRows <= 250000, "projection-row-limit");
                        var id = Integer(query.Value, 0);
                        var owner = Integer(query.Value, 1);
                        Require(id.HasValue && owner.HasValue, "projection-null-identity");
                        Require(identities.Add((owner!.Value, id!.Value)), "projection-duplicate-identity");
                        rows.Add(new(kind, owner.Value, id.Value, Integer(query.Value, 2), Integer(query.Value, 3)));
                    }
                }
                TuneAssetContract.ValidateRows(kind, rows, asset);
                result.AddRange(rows);
            }
            token.ThrowIfCancellationRequested();
            return new TuneAssetMetadata(result, TryReadCarNames(database, token));
        }
        finally
        {
            // SQLite borrows the pinned buffer; it must close before the pin is released.
            var closed = database == 0 || sqlite3_close(database) == 0;
            GC.KeepAlive(progress);
            GC.KeepAlive(authorizer);
            if (closed) pinned.Free();
            Require(closed, "sqlite-close-failed");
            Require(beforeHash.AsSpan().SequenceEqual(SHA256.HashData(decoded)), "sqlite-borrowed-buffer-changed");
        }
    }

    private static IReadOnlyList<TuneCarNameKey> TryReadCarNames(nint database, CancellationToken token)
    {
        try
        {
            foreach (var (table, hash) in new[]
            {
                ("Data_Car", "E51C352B57860E6571351B252F21A9A5D32A0F5B37207CB9CBF73EBDCB639336"),
                ("List_CarMake", "8F3AB5FFF14F82043BF0565FFAE313C998CE6EFD01A8EF22475C2FE147B67127")
            })
            {
                using var schema = Prepare(database, $"SELECT type,sql FROM sqlite_schema WHERE name='{table}'");
                Require(sqlite3_step(schema.Value) == 100 && Text(schema.Value, 0) == "table" &&
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text(schema.Value, 1)))) == hash &&
                    sqlite3_step(schema.Value) == 101, "name-schema-unavailable");
                using var integrity = Prepare(database, $"PRAGMA integrity_check(\"{table}\")");
                Require(sqlite3_step(integrity.Value) == 100 && Text(integrity.Value, 0) == "ok" &&
                    sqlite3_step(integrity.Value) == 101, "name-integrity-unavailable");
            }
            var result = new List<TuneCarNameKey>();
            var identities = new HashSet<int>();
            using var query = Prepare(database, "SELECT c.Id,c.Year,c.DisplayName,m.DisplayName FROM Data_Car c JOIN List_CarMake m ON m.ID=c.MakeID");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var code = sqlite3_step(query.Value);
                if (code == 101) break;
                Check(code == 100 ? 0 : code, "name-read");
                var ordinal = Integer(query.Value, 0);
                var year = Integer(query.Value, 1);
                Require(result.Count < 4096 && ordinal is > 0 && year is >= 1800 and <= 9999 &&
                    identities.Add(ordinal.Value), "name-identity-unavailable");
                var model = NameToken(Text(query.Value, 2), 0x434455F2);
                var make = NameToken(Text(query.Value, 3), 0x788FB611);
                result.Add(new(ordinal!.Value, year!.Value, model, make));
            }
            return result;
        }
        catch (InvalidDataException)
        {
            token.ThrowIfCancellationRequested();
            return []; // Names are optional; verified tuning controls remain usable.
        }
    }

    internal static ulong NameToken(string text, uint domain)
    {
        if (!text.StartsWith("_&", StringComparison.Ordinal) || text.Length > 22 ||
            !ulong.TryParse(text.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value >> 32 != domain)
            throw new InvalidDataException("The car name token is unavailable.");
        return value;
    }

    private static int? Integer(nint statement, int column)
    {
        var type = sqlite3_column_type(statement, column);
        if (type == 5) return null;
        Require(type == 1, "projection-not-integer");
        var value = sqlite3_column_int64(statement, column);
        Require(value is >= int.MinValue and <= int.MaxValue, "projection-integer-overflow");
        return (int)value;
    }
    private static string Text(nint statement, int column)
    {
        var length = sqlite3_column_bytes(statement, column);
        Require(sqlite3_column_type(statement, column) == 3 && length is >= 0 and <= 16384,
            "sqlite-text-invalid");
        return Marshal.PtrToStringUTF8(sqlite3_column_text(statement, column), length) ?? "";
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
    private static void Check(int code, string stage) => Require(code == 0, $"{stage}-code-{code}");
    private static void Require(bool condition, string stage)
    {
        if (!condition) throw new InvalidDataException($"Local tuning metadata could not be verified ({stage}).");
    }
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
