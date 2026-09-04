using Wisp.App;
using Xunit;

namespace Wisp.App.Tests;

public sealed class DebugLoggingSettingsTests
{
    [Fact]
    public void LoggingDefaultsOffWithoutChangingSettingsRevision()
    {
        var settings = new AppSettings();

        settings.MigrateSettings();

        Assert.Equal(9, settings.SettingsRevision);
        Assert.False(settings.DebugLoggingEnabled);
        Assert.Null(settings.DebugLoggingExpiresAtUtc);
    }

    [Fact]
    public void ExpiredEnablementIsClearedDuringRestartNormalization()
    {
        var settings = new AppSettings
        {
            SettingsRevision = 9,
            DebugLoggingEnabled = true,
            DebugLoggingExpiresAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1)
        };

        settings.MigrateSettings();

        Assert.False(settings.DebugLoggingEnabled);
        Assert.Null(settings.DebugLoggingExpiresAtUtc);
        Assert.Equal(9, settings.SettingsRevision);
    }

    [Fact]
    public void ActiveEnablementSurvivesRestartNormalizationUntilItsExpiry()
    {
        var expiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);
        var settings = new AppSettings
        {
            SettingsRevision = 9,
            DebugLoggingEnabled = true,
            DebugLoggingExpiresAtUtc = expiresAtUtc
        };

        settings.MigrateSettings();

        Assert.True(settings.DebugLoggingEnabled);
        Assert.Equal(expiresAtUtc, settings.DebugLoggingExpiresAtUtc);
        Assert.Equal(9, settings.SettingsRevision);
    }
}
