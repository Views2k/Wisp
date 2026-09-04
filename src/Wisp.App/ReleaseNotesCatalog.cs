namespace Wisp.App;

public sealed record ReleaseNoteGroup(string Heading, IReadOnlyList<string> Items);

public sealed record ReleaseNoteEntry(
    string Version,
    string Date,
    string Label,
    string Summary,
    bool IsCurrent,
    IReadOnlyList<ReleaseNoteGroup> Groups);

public static class ReleaseNotesCatalog
{
    public static IReadOnlyList<ReleaseNoteEntry> Entries { get; } =
    [
        new(
            "1.0.11",
            "September 4, 2026",
            "MAINTENANCE",
            "Corrects the repository's build-author alias. Application behavior is unchanged from 1.0.10.",
            true,
            [
                Group("Maintenance",
                    "Maps the temporary build-author identity to Views2k in repository tools that support author aliases.",
                    "Updates the application and installer version to 1.0.11. All 1.0.10 features, settings, and telemetry behavior are retained.")
            ]),
        new(
            "1.0.10",
            "September 4, 2026",
            "FEATURE UPDATE",
            "A quality-of-life update focused on clearer live information, easier customization, safer updates, and practical diagnostics.",
            false,
            [
                Group("Added",
                    "Live torque now appears on the Wheel Speed Ready card with the same restrained smoothing and typography as horsepower. Torque can be shown in Nm or lb-ft.",
                    "Session top speed, peak power, and peak torque are shown on the Dashboard. Peaks reset from one action or when the current car changes.",
                    "Wisp can check for a newer immutable GitHub release at startup, no more than once every 24 hours. An update-available banner appears without downloading or installing anything automatically.",
                    "A customizable global hotkey can show or hide the overlay.",
                    "Local debug logging records a limited health sample once per second, expires after 24 hours, retains files for no more than seven days, and exports a ZIP suitable for a GitHub issue.",
                    "Continuous color controls cover the app accent, background surfaces, HUD border, all three gauge-gradient colors, and the traction hook cue, including saturation, brightness, and opacity.",
                    "Named HUD profiles save a complete visual combination and support Apply, Update, Rename, and Delete. Tire calibration, overlay positions, telemetry, startup, update, and debug settings stay separate.",
                    "Update confirmation now shows the short release summary supplied by the matching GitHub release before a download begins.",
                    "In-app release notes summarize every documented public release.",
                    "A direct, unobtrusive GitHub star shortcut is available in Extras."),
                Group("Fixed and refined",
                    "Preserved detached boost and tire-temperature gauge positions across restarts and updates, including placements saved on a secondary display.",
                    "Preserved the correct saved placement when a HUD profile changes layouts or switches between Native Digital and Native Analogue. Profiles now include the selected torque unit.",
                    "Rejected expired native tachometer needle samples so a stale process-memory pair cannot repeatedly interrupt the smooth RPM fallback during a reader stall.",
                    "Color-wheel clicks and drags now work throughout the wheel. Slider adjustments no longer move the selected wheel position.",
                    "Combined color customization into a themed element list and one large focused editor.",
                    "Kept very dark background choices visible and editable while preserving the app's readable surface hierarchy.",
                    "Restored the traction-loss hook cue across all Native HUD styles and cleared stale slip evidence after stopping.",
                    "Styled the local debug logging control consistently with the rest of Wisp.",
                    "Prevented simultaneous debug-log actions from waiting indefinitely.",
                    "Prevented a failed telemetry-listener start from leaving UI callbacks running.",
                    "Removed the duplicate profile-save action and simplified the profile confirmation dialog.")
            ]),
        new(
            "1.0.8",
            "September 3, 2026",
            "UPDATE",
            "Metric boost-pressure readouts for every Native boost layout.",
            false,
            [
                Group("Added",
                    "Added a PSI or bar setting for boost pressure.",
                    "Applied the selected unit to Digital and Analogue gauges, attached and detached layouts, and the Appearance preview.",
                    "Added a 0 to 5 bar scale to the Analogue boost gauge while keeping FH6 telemetry in PSI internally.")
            ]),
        new(
            "1.0.7",
            "September 3, 2026",
            "HOTFIX",
            "A focused reliability and responsiveness hotfix.",
            false,
            [
                Group("Fixed",
                    "Decoupled live HUD telemetry delivery from WPF presentation callbacks so background compositor throttling cannot stall HUD state.",
                    "Smoothed and rate-limited the Dashboard horsepower readout without altering raw power telemetry.",
                    "Corrected native tachometer source discovery across race and menu transitions so stale unrelated HUD sources cannot invalidate the active car's tachometer.",
                    "Refreshed gallery image identities so browsers do not reuse stale 1.0.5 screenshots.")
            ]),
        new(
            "1.0.6",
            "September 2, 2026",
            "HOTFIX",
            "A focused correction for Native HUD speed smoothing.",
            false,
            [
                Group("Fixed",
                    "Made the speed-smoothing control work in Native Digital and Native Analogue layouts.",
                    "Kept the existing response curve and 1.5 MPH live-speed deviation limit unchanged.")
            ]),
        new(
            "1.0.5",
            "September 2, 2026",
            "FEATURE UPDATE",
            "A major Native HUD expansion with boost pressure, tire temperature, and a complete attached gauge stack.",
            false,
            [
                Group("Added",
                    "Added confirmed forced-induction boost gauges. Digital uses a slim rail below the tachometer, while Analogue uses a 0 to 70 PSI dial with 5 PSI ticks and a centered readout.",
                    "Added independent PSI-number color controls, fifteen boost palettes, a stock no-color style, attachment controls, and Analogue sizing.",
                    "Added front and rear axle tire-temperature gauges. Digital uses two markers in one neutral rail, while Analogue uses two needles and exact readings in one dial.",
                    "Added Fahrenheit and Celsius tire temperatures, separate front and rear gauge colors, attachment, sizing, and Appearance preview support.",
                    "Added a Native HUD attachment option for the G-force meter and included the full attached arrangement in the preview."),
                Group("Changed",
                    "Organized the longer Appearance page into focused sections.",
                    "Restored forward and reverse gear state on electric Native HUD layouts.",
                    "Extended the G-force motion trail by half a second."),
                Group("Fixed",
                    "Kept boost hidden for naturally aspirated and electric cars while showing confirmed boost with the speedometer.",
                    "Corrected boost and tire-gauge clipping, rail spacing, connectors, marker glow, label alignment, needle length, and attached layout boundaries.",
                    "Clamped tire-temperature values and markers to the authored 50 F to 350 F range and held saturated markers at the endpoint.",
                    "Prevented rapid shifts or RPM bounce from briefly blanking the stable native tachometer texture.")
            ]),
        new(
            "1.0.4",
            "September 1, 2026",
            "PACKAGING",
            "A packaging-only release that retained the stable 1.0.3 application behavior.",
            false,
            [
                Group("Changed",
                    "Reduced uploaded release files to one versioned installer and one installer archive.",
                    "Kept GitHub's generated source archives available.")
            ]),
        new(
            "1.0.3",
            "September 1, 2026",
            "UPDATE",
            "A motion and public-project maintenance update.",
            false,
            [
                Group("Added",
                    "Added a short connected trajectory trail to standard and Native G-force meters, with eight meaningful samples, fading, tapering, jiggle rejection, and stale-telemetry clearing."),
                Group("Changed",
                    "Updated public issue, security-reporting, and release-validation guidance.",
                    "Aligned public version examples and reserved sample values.")
            ]),
        new(
            "1.0.2",
            "September 1, 2026",
            "UPDATE",
            "A setup-presentation and release-download update.",
            false,
            [
                Group("Changed",
                    "Made the setup backdrop visibly dynamic while preserving its grouped particle composition and lightweight WPF renderer.",
                    "Kept setup animation running while the visible wizard is inactive, while still pausing when hidden, minimized, disabled, or using reduced motion.",
                    "Added a stable Wisp-Setup.exe release asset so the website can link to the latest installer without a site update.")
            ]),
        new(
            "1.0.1",
            "August 31, 2026",
            "UPDATE",
            "The first post-launch customization and presentation update.",
            false,
            [
                Group("Added",
                    "Added an independent HUD border palette for Combined and Two boxes layouts."),
                Group("Changed",
                    "Replaced the decorative diamond backdrop with a slower layered particle field.",
                    "Added a CI-generated Appearance capture for reviewed public screenshots."),
                Group("Fixed",
                    "Accepted Inno Setup 6.7 version-resource padding while preserving exact product, description, and semantic-version checks.")
            ])
    ];

    private static ReleaseNoteGroup Group(string heading, params string[] items) =>
        new(heading, items);
}
