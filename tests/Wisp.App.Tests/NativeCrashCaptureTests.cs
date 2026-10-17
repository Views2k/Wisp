using System.IO;
using Wisp.App.CrashDiagnostics;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeCrashCaptureTests
{
    private const string Record =
        "wisp-native-fault 1\n" +
        "time=0x1dc2a3f4b5c6d7e\n" +
        "code=0xc0000005\n" +
        "thread=4321\n" +
        "address=RTSSHooks64.dll+0x1a2b\n" +
        "access=read 0x10\n" +
        "frame=rtsshooks64.dll+0x1a2b\n" +
        "frame=wisp.nativerenderer.dll+0x4f20\n" +
        "frame=C:\\PRIVATE\\odd.dll+0x1\n" +
        "frame=unknown+0x7ff6a0001000\n" +
        "end\n";

    [Fact]
    public void TheLastCompleteRecordIsParsedAndSanitized()
    {
        var partial = "wisp-native-fault 1\ncode=0xc0000005\naddress=ntdll.dll+0x99\n";
        var record = NativeFaultRecord.Parse(Record + partial);
        Assert.NotNull(record);
        Assert.Equal(0xc0000005u, record.ExceptionCode);
        Assert.Equal("rtsshooks64.dll", record.Module);
        Assert.Equal(0x1a2bul, record.Offset);
        Assert.Equal("read", record.Access);
        Assert.Equal(0x10ul, record.AccessAddress);
        Assert.Equal(4321u, record.ThreadId);
        Assert.Equal(["rtsshooks64.dll+0x1a2b", "wisp.nativerenderer.dll+0x4f20", "unknown+0x7ff6a0001000"], record.Frames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wisp-native-fault 1\ncode=0xc0000005\nend\n")]
    [InlineData("wisp-native-fault 1\ncode=0xc0000005\naddress=C:\\PRIVATE\\x.dll+0x1\nend\n")]
    [InlineData("wisp-native-fault 1\ncode=zz\naddress=ntdll.dll+0x1\nend\n")]
    public void IncompleteOrUnsafeRecordsAreIgnored(string text)
    {
        Assert.Null(NativeFaultRecord.Parse(text));
    }

    [Fact]
    public void FramesAreBoundedAndEachNamesAModuleAndOffset()
    {
        var frames = string.Concat(Enumerable.Range(0, 60).Select(index => $"frame=a.dll+0x{index:x}\n"));
        var record = NativeFaultRecord.Parse("wisp-native-fault 1\ncode=0xc0000005\naddress=a.dll+0x1\n" + frames + "end\n");
        Assert.NotNull(record);
        Assert.Equal(NativeFaultRecord.MaximumFrames, record.Frames.Length);
        Assert.True(NativeFaultRecord.ValidFrame("wisp.exe+0xabcdef0123456789"));
        Assert.False(NativeFaultRecord.ValidFrame("wisp.exe"));
        Assert.False(NativeFaultRecord.ValidFrame("wisp.exe+0x"));
        Assert.False(NativeFaultRecord.ValidFrame("C:\\x\\wisp.exe+0x1"));
        Assert.False(NativeFaultRecord.ValidFrame("wisp.exe+0x" + new string('f', 17)));
    }

    [Fact]
    public void TheRecordFileIsReadOnceAndRemoved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wisp-native-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var runId = Guid.NewGuid();
            Assert.Null(NativeCrashCapture.TryRead(directory, runId));
            File.WriteAllText(NativeCrashCapture.PathFor(directory, runId), Record);
            var record = NativeCrashCapture.TryRead(directory, runId);
            Assert.NotNull(record);
            Assert.Equal("rtsshooks64.dll", record.Module);
            Assert.False(File.Exists(NativeCrashCapture.PathFor(directory, runId)));
            Assert.Null(NativeCrashCapture.TryRead(directory, runId));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
