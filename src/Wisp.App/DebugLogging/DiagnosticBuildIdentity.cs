using System.Runtime.InteropServices;

namespace Wisp.App.DebugLogging;

internal sealed record DiagnosticBuildIdentity(string WispVersion, string? PrivateBuildId, Guid ModuleVersionId,
    string RuntimeVersion, string WindowsVersion, Architecture ProcessArchitecture)
{
    internal static DiagnosticBuildIdentity Current { get; } = new(
        ApplicationVersionInfo.MachineVersion, SafePrivateBuildId(ApplicationVersionInfo.DiagnosticBuildId),
        typeof(App).Assembly.ManifestModule.ModuleVersionId, Environment.Version.ToString(),
        Environment.OSVersion.Version.ToString(), RuntimeInformation.ProcessArchitecture);

    private static string? SafePrivateBuildId(string? value) => value is { Length: > 0 and <= 80 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ? value : null;
}
