using System.IO;
using Wisp.UiReview;
using Xunit;

namespace Wisp.App.Tests;

internal static class LapReviewUiRuntimeTests
{
    internal static void AssertOnCurrentDispatcher() => LapReviewUiChecks.Run(
        Path.Combine(Path.GetTempPath(), "WispLapUiTests", Guid.NewGuid().ToString("N")),
        (condition, code) => Assert.True(condition, code));
}
