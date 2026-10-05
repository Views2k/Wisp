using Wisp.Core;

namespace Wisp.App;

internal static class NativeCompatibilityStatusText
{
    internal static string Compose(string? catalogStatus, string? catalogDiagnostic, string? operationStatus,
        NativeAssistProviderStatus nativeStatus = NativeAssistProviderStatus.Unavailable) =>
        string.Join(" ", new[] {
            nativeStatus == NativeAssistProviderStatus.UnsupportedBuild
                ? "The current Forza build is not supported by the installed compatibility packs." : null,
            catalogStatus, catalogDiagnostic, operationStatus }
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => message!.Trim())
            .Distinct(StringComparer.Ordinal));
}
