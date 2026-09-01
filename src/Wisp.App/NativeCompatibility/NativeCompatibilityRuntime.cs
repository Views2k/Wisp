using System.Collections.Frozen;
using System.IO;

namespace Wisp.App;

internal static class NativeCompatibilityRuntime
{
    // Release-owned trust roots, never keys supplied by a downloaded pack or a user-writable cache.
    // No publisher has been configured yet. The verified embedded map works without network access.
    internal static Uri? PublisherEndpoint => null;
    private static readonly FrozenDictionary<string, byte[]> PublisherKeys =
        new Dictionary<string, byte[]>(StringComparer.Ordinal).ToFrozenDictionary();
    private static readonly Lazy<NativeCompatibilityCatalog> DefaultCatalog = new(() => new(
        NativeHudBuildContract.BuiltIn,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wisp", "NativeCompatibility"),
        PublisherKeys));

    public static NativeCompatibilityCatalog Catalog => DefaultCatalog.Value;
    public static NativeCompatibilityUpdateClient CreateUpdateClient() => new(PublisherEndpoint, Catalog);

    internal static string DescribeCatalog(NativeCompatibilityCatalog catalog)
    {
        var diagnostics = catalog.Diagnostics;
        return diagnostics.Count == 0
            ? catalog.Status
            : $"{catalog.Status} {diagnostics[0]}";
    }

    internal static async Task<NativeCompatibilityInstallResult> ImportFileAsync(
        NativeCompatibilityCatalog catalog,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.HasTrustedPublishers)
        {
            return new(false, false, NativeCompatibilityInstallCode.UntrustedPublisher,
                "No compatibility publisher is configured. The bundled map remains available.");
        }

        try
        {
            await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length <= 0 || input.Length > NativeCompatibilityEnvelope.MaximumEnvelopeBytes)
            {
                return InvalidFile();
            }

            // Bound the actual bytes, not just the initial file length. No source file is ever written.
            var bytes = new byte[NativeCompatibilityEnvelope.MaximumEnvelopeBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await input.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return count == 0 || count > NativeCompatibilityEnvelope.MaximumEnvelopeBytes
                ? InvalidFile()
                : catalog.Install(bytes.AsSpan(0, count), DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return new(false, false, NativeCompatibilityInstallCode.CacheUnavailable,
                "The selected compatibility pack could not be read. The existing catalog is unchanged.");
        }
    }

    private static NativeCompatibilityInstallResult InvalidFile() => new(
        false, false, NativeCompatibilityInstallCode.InvalidEnvelope,
        "The selected pack is empty or exceeds the 128 KiB limit. The existing catalog is unchanged.");
}
