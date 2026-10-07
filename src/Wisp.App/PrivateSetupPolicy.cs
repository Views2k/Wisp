namespace Wisp.App;

internal static class PrivateSetupPolicy
{
    internal static bool IsAvailable => CanSkip(ApplicationVersionInfo.DiagnosticBuildId);

    internal static bool CanSkip(string? diagnosticBuildId) => !string.IsNullOrWhiteSpace(diagnosticBuildId);

    internal static bool RequiresSetup(bool verifiedSetupRequired, bool skippedForSession, string? diagnosticBuildId) =>
        verifiedSetupRequired && !(skippedForSession && CanSkip(diagnosticBuildId));
}
