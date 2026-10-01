using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Wisp.App;
using Wisp.App.Tunes;
using Wisp.Core.Tunes;

namespace Wisp.UiReview;

// Explicit diagnostic only. These comparisons never authorize a production tune snapshot.
internal static class TuneAssetDiagnostic
{
    internal const int MaximumLength = TuneAssetCapture.ExpectedLength + 1024 * 1024;

    internal static int Run(string referenceDirectory, string output, bool live)
    {
        using var deadline = new System.Threading.Timer(_ => Environment.Exit(124), null,
            TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var report = new Report { Live = live };
        var privateBuffers = new List<byte[]>();
        var started = Stopwatch.GetTimestamp();
        try
        {
            report.Stage = "reference-verification";
            var reference = Reference.Load(referenceDirectory, cancellation.Token, privateBuffers);
            report.ReferenceVerified = true;
            if (!live)
            {
                report.Stage = "offline-readonly-contract";
                report.SqliteWriteProtection = TuneAssetDiagnosticSqlite.VerifyDeserializeWriteProtection(reference.Decoded, cancellation.Token);
            }
            report.Stage = "reference-projection";
            var baseline = TuneAssetDiagnosticSqlite.Project(reference.Decoded, null, cancellation.Token);
            if (live)
            {
                report.ForegroundBefore = ShiftCaptureSession.IsForzaForeground();
                Need(report.ForegroundBefore == true, "forza-not-foreground");
                var factory = new NativeHudProcessMemoryFactory();
                report.Stage = "open-game";
                Need(factory.TryOpen(out var memory, out _) && memory is not null, "game-open-failed");
                try
                {
                    Need(NativeTuneCapture.Supports(memory!.CompatibilityPack) && memory.SessionIdentity.Length != 0,
                        "unsupported-game-identity");
                    var generation = factory.CompatibilityGeneration;
                    report.Stage = "read-live-stream";
                    var captured = Read(memory, cancellation.Token, privateBuffers);
                    report.Stream = captured.Shape;
                    report.TransformTablesMatch = captured.Crc.AsSpan().SequenceEqual(reference.Crc) &&
                        captured.Fold.AsSpan().SequenceEqual(reference.Fold);
                    Need(report.TransformTablesMatch, "transform-tables-changed");
                    report.Stage = "decode-live-stream";
                    var decoded = Transform(captured.Encoded, reference.Crc, reference.Fold, cancellation.Token, privateBuffers);
                    report.Comparison = Compare(reference.Encoded, reference.Decoded, captured.Encoded, decoded);
                    ValidateHeader(decoded);
                    report.Stage = "live-projection";
                    var current = TuneAssetDiagnosticSqlite.Project(decoded, baseline, cancellation.Token);
                    report.Tables = CompareTables(baseline, current);
                    if (report.Tables.All(table => table.Equal))
                    {
                        report.Stage = "native-values";
                        Need(ShiftCaptureSession.IsForzaForeground(), "foreground-before-values-changed");
                        var metadata = new TuneAssetMetadata(baseline.SelectMany(table => table.Rows));
                        var firstInput = NativeTuneCapture.Read(memory, metadata, cancellation.Token);
                        report.FirstDecodeSucceeded = TuneDecoder.TryDecode(firstInput, out var first, out _);
                        if (first is not null)
                        {
                            report.FirstComplete = first.IsComplete;
                            report.FirstSettingCount = first.Fields.Length;
                        }
                        report.Stage = "native-values-confirmation";
                        Need(ShiftCaptureSession.IsForzaForeground(), "foreground-before-confirmation-changed");
                        var secondInput = NativeTuneCapture.Read(memory, metadata, cancellation.Token);
                        report.ConfirmationDecodeSucceeded = TuneDecoder.TryDecode(secondInput, out var second, out _);
                        if (second is not null)
                        {
                            report.ConfirmationComplete = second.IsComplete;
                            report.ConfirmationSettingCount = second.Fields.Length;
                        }
                        report.NativeSetupEqual = first is not null && second is not null &&
                            TuneComparison.HaveSameSetupIdentity(first, second);
                    }
                    report.Stage = "session-confirmation";
                    Need(factory.CompatibilityGeneration == generation, "compatibility-changed");
                    Need(factory.TryOpen(out var confirmation, out _) && confirmation is not null, "game-identity-expired");
                    try
                    {
                        Need(confirmation!.SessionIdentity == memory.SessionIdentity, "game-session-changed");
                    }
                    finally { confirmation?.Dispose(); report.ConfirmationHandleDisposed = confirmation is not null; }
                }
                finally { memory?.Dispose(); report.GameHandleDisposed = memory is not null; }
                report.ForegroundAfter = ShiftCaptureSession.IsForzaForeground();
                Need(report.ForegroundAfter == true, "foreground-changed");
            }
            else
            {
                // A mutable header may differ while all required numeric records remain identical.
                report.Stage = "offline-header-mutation";
                var changed = Own(privateBuffers, (byte[])reference.Decoded.Clone());
                changed[24] ^= 1;
                report.Comparison = Compare(reference.Encoded, reference.Decoded,
                    Transform(changed, reference.Crc, reference.Fold, cancellation.Token, privateBuffers), changed);
                var current = TuneAssetDiagnosticSqlite.Project(changed, baseline, cancellation.Token);
                report.Tables = CompareTables(baseline, current);
                Need(report.Comparison.DecodedChangedBytes == 1 && report.Comparison.DecodedChangedPages == 1 &&
                    report.Tables.All(table => table.Equal), "offline-header-comparison-failed");
                changed[0] = 0;
                try { ValidateHeader(changed); throw new DiagnosticFailure("offline-corruption-was-accepted"); }
                catch (DiagnosticFailure error) when (error.Code == "invalid-sqlite-header")
                { report.CorruptHeaderRejected = true; }
                try { Reference.Validate(Own(privateBuffers, new byte[4]), reference.Crc, reference.Fold, cancellation.Token, privateBuffers); }
                catch (InvalidDataException) { report.InvalidReferenceRejected = true; }
                Need(report.InvalidReferenceRejected, "offline-reference-check-failed");
                report.TransformTablesMatch = true;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            report.Completed = true;
        }
        catch (DiagnosticFailure error) { report.Failure = error.Code; }
        catch (OperationCanceledException) { report.Failure = "diagnostic-timeout"; }
        catch (Exception error)
        {
            report.Failure = "diagnostic-failed";
            report.ExceptionType = error.GetType().Name;
            report.ExceptionHResult = error.HResult;
            report.FailureFrames = new StackTrace(error, true).GetFrames()
                .Where(frame => frame.GetMethod()?.DeclaringType?.Namespace == "Wisp.App.Tunes")
                .Take(8).Select(frame => new FailureFrame(frame.GetMethod()!.DeclaringType!.Name,
                    frame.GetMethod()!.Name, frame.GetFileLineNumber())).ToArray();
        }
        finally
        {
            foreach (var bytes in privateBuffers) CryptographicOperations.ZeroMemory(bytes);
            report.OwnedByteBuffersCleared = true;
            if (live)
            {
                report.ForegroundAfter = ShiftCaptureSession.IsForzaForeground();
                if (report.Completed && report.ForegroundAfter != true)
                { report.Completed = false; report.Failure = "foreground-changed"; }
            }
        }
        report.ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        File.WriteAllText(Path.Combine(output, "tune-asset-diagnostic.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report.Completed ? "Tune asset diagnostic completed; no production acceptance changed."
            : $"Tune asset diagnostic failed: {report.Failure}.");
        return report.Completed ? 0 : 2;
    }

    private static Captured Read(INativeHudProcessMemory memory, CancellationToken token, List<byte[]> buffers)
    {
        var read = new NativeTuneRead(new ClearingMemory(memory), token, 20 * 1024 * 1024, TimeSpan.FromSeconds(5));
        var module = memory.ModuleBase;
        var owner = read.Pointer(module + 0xA8AF088);
        var wrapper = read.Pointer(owner + 0x160);
        Table(wrapper, module + 0x6C7A570);
        var connection = read.Pointer(wrapper + 0x10);
        Need(read.UInt32(connection + 0x48) is 0x4B771290 or 0xA029A697 or 0xF03B7906, "connection-layout-changed");
        Need(read.Int32(connection + 8) is >= 1 and <= 4, "connection-count-invalid");
        var entries = read.Pointer(connection + 0x10);
        var btree = read.Pointer(entries + 8);
        var shared = read.Pointer(btree + 8);
        var pager = read.Pointer(shared);
        var file = read.Pointer(pager + 0x40);
        Table(file, module + 0x6C79E70);
        var stream = read.Pointer(read.Pointer(file + 8));
        Table(stream, module + 0x6C7B3D8);
        var adapter = read.Pointer(stream + 0x30);
        Table(adapter, module + 0x6C7BC10);
        var inner = read.Pointer(adapter + 0x30);
        Table(inner, module + 0x6C7B9B0);
        var chunkSize = read.UInt32(inner + 0x30);
        var allocated = read.UInt32(inner + 0x34);
        var length = read.UInt64(inner + 0x40);
        var begin = read.Pointer(inner + 0x48);
        var end = read.Pointer(inner + 0x50);
        var flag = read.Bytes(inner + 0x60, 1)[0];
        TuneAssetCapture.ValidateStreamShape(flag, chunkSize, allocated, length, (long)end - (long)begin);
        Need(length <= MaximumLength && length % 1024 == 0, "diagnostic-stream-size-outside-bound");
        var count = checked((int)((end - begin) / 8));
        Need((ulong)count * chunkSize >= length, "stream-vector-too-short");
        var chunks = new ulong[count];
        for (var i = 0; i < count; i++) chunks[i] = read.Pointer(begin + (ulong)i * 8);
        var crc = Own(buffers, read.Bytes(module + 0x8F75670, 1024));
        var fold = Own(buffers, read.Bytes(module + 0x8F75A70, 256));
        var encoded = Own(buffers, new byte[checked((int)length)]);
        var filled = 0;
        try
        {
            foreach (var chunk in chunks)
            {
                var amount = Math.Min(checked((int)chunkSize), encoded.Length - filled);
                for (var offset = 0; offset < amount; offset += 4096)
                {
                    var block = read.Bytes(chunk + (ulong)offset, Math.Min(4096, amount - offset), remember: false);
                    try { block.CopyTo(encoded, filled + offset); }
                    finally { CryptographicOperations.ZeroMemory(block); }
                }
                filled += amount;
                if (filled == encoded.Length) break;
            }
            read.VerifyStable();
            return new(encoded, crc, fold, new(flag, chunkSize, allocated, length, (long)end - (long)begin));
        }
        catch { CryptographicOperations.ZeroMemory(encoded); throw; }
        void Table(ulong address, ulong expected) => Need(read.UInt64(address) == expected, "stream-vtable-changed");
    }

    private static byte[] Transform(byte[] input, byte[] crcBytes, byte[] fold, CancellationToken token, List<byte[]> buffers)
    {
        Need(input.Length >= 1024 && input.Length <= MaximumLength && input.Length % 4 == 0 &&
            crcBytes.Length == 1024 && fold.Length == 256, "transform-size-invalid");
        var table = new uint[256];
        for (var i = 0; i < table.Length; i++) table[i] = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes.AsSpan(i * 4));
        var keyBytes = BitConverter.GetBytes(0x9DFBA741U);
        for (var i = 0; i < 4; i++) keyBytes[i] = unchecked((byte)((keyBytes[i] ^ 0x35) - (i ^ 0xBC)));
        var key = BinaryPrimitives.ReadUInt32LittleEndian(keyBytes);
        var output = Own(buffers, new byte[input.Length]);
        try
        {
            for (var i = 0; i < input.Length / 4; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                var value = unchecked(((uint)i + 1) * key + (uint)i);
                var mask = uint.MaxValue;
                for (var shift = 0; shift < 32; shift += 8)
                    mask = (mask >> 8) ^ table[(fold[(value >> shift) & 255] ^ mask) & 255];
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4),
                    BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(i * 4)) ^ ~mask);
            }
            return output;
        }
        catch { CryptographicOperations.ZeroMemory(output); throw; }
    }

    private static Comparison Compare(byte[] referenceEncoded, byte[] reference, byte[] encoded, byte[] current)
    {
        var common = Math.Min(reference.Length, current.Length);
        var changedBytes = Math.Abs(reference.Length - current.Length);
        var changedPages = 0;
        for (var offset = 0; offset < Math.Max(reference.Length, current.Length); offset += 1024)
        {
            var changed = false;
            for (var i = offset; i < Math.Min(offset + 1024, common); i++)
                if (reference[i] != current[i]) { changedBytes++; changed = true; }
            if (offset + 1024 > common) changed = true;
            if (changed) changedPages++;
        }
        var end = reference.Length;
        var regions = new[] { Region("header", 0, 100), Region("first-page-payload", 100, 1024),
            Region("original-body", 1024, 15409 * 1024), Region("original-tail", 15409 * 1024, end) };
        return new(reference.Length, current.Length, referenceEncoded.AsSpan().SequenceEqual(encoded),
            changedBytes, changedPages, Header(reference), Header(current), regions);
        RegionEquality Region(string name, int from, int to) => new(name, current.Length >= to &&
            SHA256.HashData(reference.AsSpan(from, to - from)).AsSpan().SequenceEqual(
                SHA256.HashData(current.AsSpan(from, to - from))));
    }

    private static HeaderScalars Header(byte[] bytes)
    {
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));
        uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
        return new(U16(16), U32(24), U32(28), U32(32), U32(36), U32(40), U32(44), U32(56), U32(60), U32(92), U32(96));
    }

    private static void ValidateHeader(byte[] bytes)
    {
        Need(bytes.Length is >= 1024 and <= MaximumLength && bytes.Length % 1024 == 0 &&
            bytes.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) &&
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(16)) == 1024 &&
            bytes[18] == 1 && bytes[19] == 1 && bytes[20] == 0 &&
            bytes[21] == 64 && bytes[22] == 32 && bytes[23] == 32 &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(44)) == 4 &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(56)) == 1, "invalid-sqlite-header");
    }

    private static TableComparison[] CompareTables(TuneAssetDiagnosticSqlite.Projection[] reference,
        TuneAssetDiagnosticSqlite.Projection[] current) => reference.Zip(current, (a, b) =>
        new TableComparison(a.Kind, a.Count, b.Count, a.Hash, b.Hash, b.SchemaMatches,
            b.SchemaMatches && a.Count == b.Count && a.Hash == b.Hash)).ToArray();

    internal static void Need([DoesNotReturnIf(false)] bool value, string reason) { if (!value) throw new DiagnosticFailure(reason); }
    private static byte[] Own(List<byte[]> buffers, byte[] bytes) { buffers.Add(bytes); return bytes; }
    private static byte[] ReadOwned(string path, int length, List<byte[]> buffers)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Need(stream.Length == length, "reference-length-changed");
        var bytes = Own(buffers, new byte[length]);
        stream.ReadExactly(bytes);
        return bytes;
    }
    private sealed class ClearingMemory(IReadOnlyProcessMemory source) : IReadOnlyProcessMemory
    {
        public bool TryReadBytes(ulong address, Span<byte> destination)
        {
            try
            {
                if (source.TryReadBytes(address, destination)) return true;
                CryptographicOperations.ZeroMemory(destination);
                return false;
            }
            catch { CryptographicOperations.ZeroMemory(destination); throw; }
        }
        public bool TryReadByte(ulong address, out byte value) => source.TryReadByte(address, out value);
        public bool TryReadUInt32(ulong address, out uint value) => source.TryReadUInt32(address, out value);
        public bool TryReadUInt64(ulong address, out ulong value) => source.TryReadUInt64(address, out value);
        public bool TryReadSingle(ulong address, out float value) => source.TryReadSingle(address, out value);
    }
    internal sealed class DiagnosticFailure(string code) : Exception(code) { internal string Code { get; } = code; }
    private sealed record Captured(byte[] Encoded, byte[] Crc, byte[] Fold, StreamShape Shape);
    private sealed record Reference(byte[] Encoded, byte[] Decoded, byte[] Crc, byte[] Fold)
    {
        internal static Reference Load(string directory, CancellationToken token, List<byte[]> buffers)
        {
            var metadataPath = Path.Combine(directory, "tune-game-asset-metadata.json");
            var encodedPath = Path.Combine(directory, "tune-game-asset-encoded.bin");
            var metadataLength = new FileInfo(metadataPath).Length;
            Need(metadataLength is > 0 and <= 65536 &&
                new FileInfo(encodedPath).Length == TuneAssetCapture.ExpectedLength, "reference-size-invalid");
            using var json = JsonDocument.Parse(ReadOwned(metadataPath, (int)metadataLength, buffers));
            Need(json.RootElement.GetProperty("executableSha256").GetString() == TuneDecoder.SupportedExecutableSha256,
                "reference-build-invalid");
            var crc = Own(buffers, Convert.FromHexString(json.RootElement.GetProperty("crcTableHex").GetString()!));
            var fold = Own(buffers, Convert.FromHexString(json.RootElement.GetProperty("foldTableHex").GetString()!));
            var encoded = ReadOwned(encodedPath, TuneAssetCapture.ExpectedLength, buffers);
            var decoded = Validate(encoded, crc, fold, token, buffers);
            ValidateHeader(decoded);
            return new(encoded, decoded, crc, fold);
        }
        internal static byte[] Validate(byte[] encoded, byte[] crc, byte[] fold, CancellationToken token, List<byte[]> buffers)
        {
            if (encoded.Length != TuneAssetCapture.ExpectedLength || crc.Length != 1024 || fold.Length != 256 ||
                Convert.ToHexString(SHA256.HashData(encoded)) != TuneAssetCapture.EncodedHash)
                throw new InvalidDataException("The retained encoded reference could not be verified.");
            var decoded = Transform(encoded, crc, fold, token, buffers);
            if (Convert.ToHexString(SHA256.HashData(decoded)) != TuneAssetCapture.DecodedHash ||
                BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(28)) != 15409)
                throw new InvalidDataException("The retained decoded reference could not be verified.");
            ValidateHeader(decoded);
            return decoded;
        }
    }

    private sealed record StreamShape(byte Flag, uint ChunkSize, uint Allocated, ulong Length, long VectorBytes);
    private sealed record FailureFrame(string Type, string Method, int Line);
    private sealed record HeaderScalars(ushort PageSize, uint ChangeCounter, uint HeaderPages, uint FirstFreelist,
        uint FreelistPages, uint SchemaCookie, uint SchemaFormat, uint TextEncoding, uint UserVersion,
        uint VersionValidFor, uint SqliteVersion);
    private sealed record RegionEquality(string Region, bool HashEqual);
    private sealed record Comparison(int ReferenceBytes, int CurrentBytes, bool EncodedEqual,
        int DecodedChangedBytes, int DecodedChangedPages, HeaderScalars ReferenceHeader, HeaderScalars CurrentHeader,
        RegionEquality[] Regions);
    private sealed record TableComparison(string Kind, int ReferenceRows, int CurrentRows, string ReferenceHash,
        string CurrentHash, bool SchemaEqual, bool Equal);
    private sealed class Report
    {
        public bool Live { get; set; }
        public bool Completed { get; set; }
        public bool ReferenceVerified { get; set; }
        public TuneAssetDiagnosticSqlite.WriteProtectionCheck? SqliteWriteProtection { get; set; }
        public string Stage { get; set; } = "initialize";
        public bool? ForegroundBefore { get; set; }
        public bool? ForegroundAfter { get; set; }
        public bool TransformTablesMatch { get; set; }
        public StreamShape? Stream { get; set; }
        public Comparison? Comparison { get; set; }
        public TableComparison[]? Tables { get; set; }
        public bool? FirstDecodeSucceeded { get; set; }
        public bool? ConfirmationDecodeSucceeded { get; set; }
        public bool? FirstComplete { get; set; }
        public bool? ConfirmationComplete { get; set; }
        public int? FirstSettingCount { get; set; }
        public int? ConfirmationSettingCount { get; set; }
        public bool? NativeSetupEqual { get; set; }
        public bool GameHandleDisposed { get; set; }
        public bool ConfirmationHandleDisposed { get; set; }
        public bool OwnedByteBuffersCleared { get; set; }
        public bool CorruptHeaderRejected { get; set; }
        public bool InvalidReferenceRejected { get; set; }
        public string? Failure { get; set; }
        public string? ExceptionType { get; set; }
        public int? ExceptionHResult { get; set; }
        public FailureFrame[]? FailureFrames { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public bool ProductionAcceptanceChanged => false;
        public bool RawDatabaseWritten => false;
        public int VisibleWindows => 0;
        public int GameWrites => 0;
    }
}
