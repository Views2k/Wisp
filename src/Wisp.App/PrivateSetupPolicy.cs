using System.Reflection;

namespace Wisp.App;

internal static class PrivateSetupPolicy
{
    internal static bool IsAvailable { get; } = CanSkip(typeof(PrivateSetupPolicy).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "WispDiagnosticBuildId")?.Value);

    internal static bool RequiresSetup(bool verifiedSetupRequired, bool skippedForSession) =>
        verifiedSetupRequired && !(skippedForSession && IsAvailable);

    internal static bool CanSkip(string? diagnosticBuildId) => !string.IsNullOrWhiteSpace(diagnosticBuildId);

    internal static bool RequiresSetup(bool verifiedSetupRequired, bool skippedForSession, string? diagnosticBuildId) =>
        verifiedSetupRequired && !(skippedForSession && CanSkip(diagnosticBuildId));
}
