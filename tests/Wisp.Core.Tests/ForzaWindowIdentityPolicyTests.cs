using Xunit;

namespace Wisp.Core.Tests;

public sealed class ForzaWindowIdentityPolicyTests
{
    private static readonly IntPtr ForegroundWindow = new(0x101);
    private static readonly IntPtr LastConfirmedForzaWindow = new(0x202);
    private static readonly IReadOnlySet<int> ForzaProcessIds = new HashSet<int> { 600 };

    [Theory]
    [InlineData(600, 0, 0)]
    [InlineData(700, 600, 0)]
    [InlineData(700, 700, 600)]
    public void RecognizesOnlyAWindowChainLinkedToAKnownForzaProcess(
        int foregroundProcessId,
        int rootProcessId,
        int rootOwnerProcessId)
    {
        var result = ForzaWindowIdentityPolicy.Evaluate(
            ForegroundWindow,
            foregroundProcessId,
            rootProcessId,
            rootOwnerProcessId,
            ForzaProcessIds,
            IntPtr.Zero);

        Assert.True(result.IsForzaForeground);
        Assert.Equal(ForegroundWindow, result.ConfirmedForzaWindow);
    }

    [Fact]
    public void RecognizesAHostedSurfaceWithAKnownForzaDescendant()
    {
        var result = ForzaWindowIdentityPolicy.Evaluate(
            ForegroundWindow,
            foregroundProcessId: 700,
            rootProcessId: 700,
            rootOwnerProcessId: 700,
            ForzaProcessIds,
            IntPtr.Zero,
            hasKnownForzaDescendant: true);

        Assert.True(result.IsForzaForeground);
        Assert.Equal(ForegroundWindow, result.ConfirmedForzaWindow);
    }

    [Theory]
    [InlineData(701, 701, 701)] // Discord
    [InlineData(702, 702, 702)]
    [InlineData(0, 0, 0)]
    public void ConcreteUnrelatedForegroundWindowFailsClosedWhileForzaIsRunning(
        int foregroundProcessId,
        int rootProcessId,
        int rootOwnerProcessId)
    {
        var result = ForzaWindowIdentityPolicy.Evaluate(
            ForegroundWindow,
            foregroundProcessId,
            rootProcessId,
            rootOwnerProcessId,
            ForzaProcessIds,
            LastConfirmedForzaWindow);

        Assert.False(result.IsForzaForeground);
        Assert.Equal(IntPtr.Zero, result.ConfirmedForzaWindow);
    }

    [Fact]
    public void MissingForegroundWindowRetainsPreviouslyConfirmedForzaWindowDuringTransition()
    {
        var result = ForzaWindowIdentityPolicy.Evaluate(
            IntPtr.Zero,
            0,
            0,
            0,
            ForzaProcessIds,
            LastConfirmedForzaWindow);

        Assert.True(result.IsForzaForeground);
        Assert.Equal(LastConfirmedForzaWindow, result.ConfirmedForzaWindow);
    }

    [Fact]
    public void MissingForegroundWindowDoesNotInferForzaWithoutPreviousConfirmation()
    {
        var result = ForzaWindowIdentityPolicy.Evaluate(
            IntPtr.Zero,
            0,
            0,
            0,
            ForzaProcessIds,
            IntPtr.Zero);

        Assert.False(result.IsForzaForeground);
        Assert.Equal(IntPtr.Zero, result.ConfirmedForzaWindow);
    }

    [Fact]
    public void MissingForegroundWindowRejectsStaleConfirmationAfterForzaExits()
    {
        var result = ForzaWindowIdentityPolicy.Evaluate(
            IntPtr.Zero,
            0,
            0,
            0,
            new HashSet<int>(),
            LastConfirmedForzaWindow);

        Assert.False(result.IsForzaForeground);
        Assert.Equal(IntPtr.Zero, result.ConfirmedForzaWindow);
    }

    [Fact]
    public void InvalidKnownProcessIdCannotKeepAStaleWindowAlive()
    {
        var result = ForzaWindowIdentityPolicy.Evaluate(
            IntPtr.Zero,
            0,
            0,
            0,
            new HashSet<int> { 0 },
            LastConfirmedForzaWindow);

        Assert.False(result.IsForzaForeground);
        Assert.Equal(IntPtr.Zero, result.ConfirmedForzaWindow);
    }

    [Fact]
    public void ReusedProcessIdNotInCurrentForzaSetFailsClosed()
    {
        var result = ForzaWindowIdentityPolicy.Evaluate(
            ForegroundWindow,
            599,
            599,
            599,
            ForzaProcessIds,
            LastConfirmedForzaWindow);

        Assert.False(result.IsForzaForeground);
        Assert.Equal(IntPtr.Zero, result.ConfirmedForzaWindow);
    }
}
