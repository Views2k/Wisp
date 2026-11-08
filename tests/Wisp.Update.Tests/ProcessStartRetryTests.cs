using System.ComponentModel;
using Xunit;

namespace Wisp.Update.Tests;

public sealed class ProcessStartRetryTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    public void RefusedStartIsTriedAgainAfterEachDelay(int code)
    {
        var waits = new List<TimeSpan>();
        var attempts = 0;

        var result = ProcessStartRetry.Start(() => ++attempts < 3 ? throw new Win32Exception(code) : "started",
            () => true, waits.Add);

        Assert.Equal("started", result);
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);
    }

    [Fact]
    public void RefusalsStopAfterAboutFifteenSeconds()
    {
        var waits = new List<TimeSpan>();
        var attempts = 0;

        var error = Assert.Throws<Win32Exception>(() => ProcessStartRetry.Start<string>(() =>
        {
            attempts++;
            throw new Win32Exception(5);
        }, () => true, waits.Add));

        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal(ProcessStartRetry.Delays.Count + 1, attempts);
        Assert.Equal(TimeSpan.FromSeconds(15), waits.Aggregate(TimeSpan.Zero, (sum, wait) => sum + wait));
    }

    [Fact]
    public void OtherFailuresAreNotRetried()
    {
        var waits = new List<TimeSpan>();
        var attempts = 0;

        Assert.Throws<Win32Exception>(() => ProcessStartRetry.Start<string>(() =>
        {
            attempts++;
            throw new Win32Exception(2);
        }, () => true, waits.Add));
        Assert.Throws<InvalidOperationException>(() => ProcessStartRetry.Start<string>(
            () => throw new InvalidOperationException(), () => true, waits.Add));

        Assert.Equal(1, attempts);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task ARetryThatMayNoLongerRunRethrowsTheRefusal()
    {
        var waits = new List<TimeSpan>();
        var attempts = 0;

        var error = await Assert.ThrowsAsync<Win32Exception>(() => ProcessStartRetry.StartAsync<string>(() =>
        {
            attempts++;
            throw new Win32Exception(5);
        }, () => false, wait =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        }));

        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal(1, attempts);
        Assert.Equal([TimeSpan.FromSeconds(1)], waits);
    }

    [Fact]
    public async Task AsyncRetryReturnsTheFirstStartThatSucceeds()
    {
        var attempts = 0;

        var result = await ProcessStartRetry.StartAsync(() => ++attempts == 1 ? throw new Win32Exception(32) : attempts,
            () => true, _ => Task.CompletedTask);

        Assert.Equal(2, result);
    }
}
