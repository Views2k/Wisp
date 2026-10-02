using System.Text.Json;
using System.Text.Json.Serialization;
using Wisp.Core.Tunes;

namespace Wisp.UiReview;

internal static class TuneUiFixture
{
    internal static TuneSnapshot Read(string name = "miata")
    {
        using var stream = typeof(TuneUiFixture).Assembly.GetManifestResourceStream($"Wisp.TuneFixtures.{name}.json")
            ?? throw new InvalidOperationException("The embedded tune fixture is missing.");
        var input = JsonSerializer.Deserialize<TuneDecodeInput>(stream, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
        if (!TuneDecoder.TryDecode(input, out var snapshot, out _) || snapshot is null)
            throw new InvalidOperationException("The embedded tune fixture could not be decoded.");
        return snapshot;
    }
}
