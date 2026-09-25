using System.Windows;
using Wisp.Core;

namespace Wisp.App.Laps;

// Layout only. All visible artwork is submitted by the existing native HUD host.
internal sealed class LapDeltaView : FrameworkElement
{
    internal required LapDeltaService Service { get; init; }
    internal LapDeltaReference Reference { get; set; }
    internal string? AheadColor { get; set; }
    internal string? BehindColor { get; set; }
    internal bool ShowBar { get; set; } = true;
}
