# Explicit retained-fixture regression. All SQLite mutations target private memory copies.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppAssemblyPath,
    [Parameter(Mandatory)][string]$ResearchDirectory
)
$ErrorActionPreference = 'Stop'
$appPath = (Resolve-Path -LiteralPath $AppAssemblyPath).Path
$researchWork = Join-Path (Resolve-Path -LiteralPath $ResearchDirectory).Path 'work'
$null = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path -Parent $appPath) 'Wisp.Core.dll'))
$assembly = [Reflection.Assembly]::LoadFrom($appPath)
$flags = [Reflection.BindingFlags]'NonPublic, Static'
$decode = $assembly.GetType('Wisp.App.Tunes.TuneAssetCapture', $true).GetMethod('Decode', $flags)
$extract = $assembly.GetType('Wisp.App.Tunes.TuneAssetSqlite', $true).GetMethod('Extract', $flags)
$buffers = [Collections.Generic.List[byte[]]]::new()

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class TuneOfflineMutation
{
    public static byte[] Change(byte[] reference, int variant)
    {
        string sql;
        switch (variant)
        {
            case 0: sql = "UPDATE List_UpgradeEngine SET Level=Level+1 WHERE rowid=(SELECT rowid FROM List_UpgradeEngine WHERE Level IS NOT NULL LIMIT 1)"; break;
            case 1: sql = "DELETE FROM List_UpgradeEngine WHERE rowid=(SELECT rowid FROM List_UpgradeEngine LIMIT 1)"; break;
            case 2: sql = "ALTER TABLE List_UpgradeEngine ADD COLUMN WispInvalid INTEGER"; break;
            case 3: sql = "DROP TABLE List_UpgradeEngine; CREATE VIEW List_UpgradeEngine AS SELECT 1 AS Id,1 AS Ordinal,1 AS Level"; break;
            default: throw new ArgumentOutOfRangeException("variant");
        }
        byte[] copy = new byte[17269760];
        Buffer.BlockCopy(reference, 0, copy, 0, reference.Length);
        GCHandle pin = GCHandle.Alloc(copy, GCHandleType.Pinned);
        IntPtr db = IntPtr.Zero, serialized = IntPtr.Zero, error = IntPtr.Zero;
        Progress callback = null;
        try
        {
            Check(sqlite3_open_v2(":memory:", out db, 2 | 4 | 0x10000, IntPtr.Zero));
            var started = Stopwatch.StartNew();
            callback = delegate(IntPtr state) { return started.ElapsedMilliseconds > 5000 ? 1 : 0; };
            sqlite3_progress_handler(db, 1000, callback, IntPtr.Zero);
            Check(sqlite3_deserialize(db, "main", pin.AddrOfPinnedObject(), reference.Length, copy.Length, 0));
            Check(sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, out error));
            long length;
            serialized = sqlite3_serialize(db, "main", out length, 0);
            if (serialized == IntPtr.Zero || length < 16221184 || length > 17269760)
                throw new InvalidOperationException("Offline mutation size invalid.");
            byte[] result = new byte[(int)length];
            Marshal.Copy(serialized, result, 0, result.Length);
            // Preserve the supported header shape while exercising the actual schema/row guard.
            Buffer.BlockCopy(reference, 28, result, 28, 4);
            result[24] = result[25] = result[26] = 0; result[27] = 1;
            result[92] = result[93] = result[94] = 0; result[95] = 2;
            return result;
        }
        finally
        {
            if (error != IntPtr.Zero) sqlite3_free(error);
            if (serialized != IntPtr.Zero) sqlite3_free(serialized);
            bool closed = db == IntPtr.Zero || sqlite3_close(db) == 0;
            GC.KeepAlive(callback);
            if (closed) { pin.Free(); Array.Clear(copy, 0, copy.Length); }
            if (!closed) throw new InvalidOperationException("Offline mutation cleanup failed.");
        }
    }
    private static void Check(int code) { if (code != 0) throw new InvalidOperationException("Offline SQLite mutation code " + code); }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Progress(IntPtr state);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)]
    private static extern int sqlite3_deserialize(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema, IntPtr bytes, long size, long capacity, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_serialize(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema, out long size, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr state, out IntPtr error);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)]
    private static extern void sqlite3_progress_handler(IntPtr db, int instructions, Progress callback, IntPtr state);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
}
'@

function Assert-Extraction([byte[]]$bytes, [bool]$shouldPass, [string]$reason) {
    $before = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    $accepted = $false
    try {
        $null = $extract.Invoke($null, [object[]]@($bytes, [Threading.CancellationToken]::None))
        $accepted = $true
    } catch {
        $cause = $_.Exception.GetBaseException()
        if ($cause -isnot [IO.InvalidDataException] -or ($reason -and -not $cause.Message.Contains($reason))) {
            throw 'Offline extraction failed at an unexpected validation boundary.'
        }
    }
    if ($accepted -ne $shouldPass) { throw 'Offline metadata acceptance differed from its expected result.' }
    if ($before -ne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))) {
        throw 'Extraction changed its private input buffer.'
    }
}

try {
    $metadata = Get-Content -LiteralPath (Join-Path $researchWork 'tune-game-asset-metadata.json') -Raw | ConvertFrom-Json
    $encoded = [IO.File]::ReadAllBytes((Join-Path $researchWork 'tune-game-asset-encoded.bin')); $buffers.Add($encoded)
    $crc = [Convert]::FromHexString($metadata.crcTableHex); $buffers.Add($crc)
    $fold = [Convert]::FromHexString($metadata.foldTableHex); $buffers.Add($fold)
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($encoded)) -ne '8A529AAFC28DFC39EC18230E75297D56CF219F4E0A64EEE2C1134E8526BCC369') {
        throw 'Retained encoded fixture provenance did not match.'
    }
    $decoded = $decode.Invoke($null, [object[]]@($encoded, $crc, $fold, [Threading.CancellationToken]::None)); $buffers.Add($decoded)
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($decoded)) -ne 'E0E5979B99ED4BEABA8405634E0484CF1C46BB51FEC4F1E8F22109535242F9C6') {
        throw 'Retained decoded fixture provenance did not match.'
    }
    Assert-Extraction $decoded $true ''
    $grown = [byte[]]::new($decoded.Length + 1024); $buffers.Add($grown)
    [Array]::Copy($decoded, $grown, $decoded.Length)
    $grown[24] = 0; $grown[25] = 0; $grown[26] = 12; $grown[27] = 95
    $grown[40] = 0; $grown[41] = 0; $grown[42] = 1; $grown[43] = 226
    Assert-Extraction $grown $true ''
    for ($variant = 0; $variant -lt 4; $variant++) {
        $changed = [TuneOfflineMutation]::Change($decoded, $variant); $buffers.Add($changed)
        $reason = if ($variant -le 1) { 'required tuning values' } elseif ($variant -eq 2) { 'table schema' } else { 'required-table-missing' }
        Assert-Extraction $changed $false $reason
    }
    [ordered]@{ Result='pass'; OriginalAccepted=$true; MutableHeaderAndExtraPageAccepted=$true; ChangedValueRejected=$true;
        MissingRowRejected=$true; ChangedSchemaRejected=$true; RequiredViewRejected=$true; InputBuffersUnchanged=$true;
        RawDatabaseWritten=$false; LiveGameAccess=$false } | ConvertTo-Json -Compress
} finally {
    foreach ($buffer in $buffers) { [Array]::Clear($buffer) }
}
