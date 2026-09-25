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
            "2.4.1",
            "September 25, 2026",
            "GAME PASS HOTFIX",
            "Enable Drift Zone angle-bonus guidance on the supported Xbox app and Microsoft Store PC build.",
            true,
            [
                Group("Drift gauge",
                    "Recognize FH6 3.440.853.0 for Drift Zone angle-bonus guidance instead of showing UNVERIFIED BUILD.",
                    "Retain storefront-specific native compatibility checks and the existing Steam guidance.")
            ]),
        new(
            "2.4",
            "September 23, 2026",
            "PERFORMANCE HOTFIX",
            "Address the G-SYNC/VRR needle issue, correct playback and rendering bugs, and reduce unnecessary drawing, allocation and CPU work.",
            false,
            [
                Group("Window composition",
                    "Use a monitor-sized native host when the main HUD fits within one monitor, retaining the artwork's size and position.",
                    "Request passive updates for overlay windows so Windows can incorporate their changes when composition runs for other reasons."),
                Group("Needle delivery",
                    "Feed accepted RPM samples directly to bounded needle history before UI publication, and animate the needle on an independent compositor worker.",
                    "Reduce minimum analogue needle playback buffering to 20 ms while retaining adaptive protection for uneven input.",
                    "Preserve needle blur, layer order and validated native-angle priority through car, scale and session changes."),
                Group("Rendering fixes and efficiency",
                    "Correct queued-history playback, startup and occlusion readiness, and hidden-window resource handling.",
                    "Reuse command storage and unchanged needle pixels, and prevent fractional-timeout reader spinning.",
                    "Expand diagnostics for motion curves, presentation waits, build identity and passive-update acceptance."),
                Group("Troubleshooting and thanks",
                    "Keep the main HUD within one monitor and compare it with the stock needle while Forza is focused. Export a debug ZIP if uneven motion remains.",
                    "Thanks to fredemmott for sharing the Direct3D team's overlay and variable-refresh guidance. Full release notes link his post and explain the investigation.")
            ]),
        new(
            "2.3.4",
            "September 21, 2026",
            "ACCENT COLOR HOTFIX",
            "Fix the app accent-color crash, gauge attachment and update recovery; add optional beta shift guidance.",
            false,
            [
                Group("App appearance",
                    "Safely update accent colors while the dashboard is on an inactive tab.",
                    "Restore the dashboard oval effect in the selected color when returning to the dashboard."),
                Group("Included improvements",
                    "Keep supplementary gauges separate in digital layouts and retain update recovery improvements."),
                Group("Shift guidance (beta)",
                    "Optional calibrated full-load shift guidance is off by default. Calibrate each car and tune before use.",
                    "Requires the supported Steam build and a combustion-car configuration. The red stage marks your calibrated RPM target; driver reaction, grip and boost recovery also influence shift timing.")
            ]),
        new(
            "2.3.3",
            "September 21, 2026",
            "WISP 2.3.3",
            "Beta calibrated shift guidance, separate gauges in digital layouts, and clearer update recovery.",
            false,
            [
                Group("Calibrated shift guidance (beta)",
                    "Calibrate a supported combustion car and tune with a rolling full-throttle pull and a confirming upshift.",
                    "Compare measured full-load output in adjacent gears. The gear ring progresses through configurable green, yellow and flashing red stages.",
                    "Use matching calibration data only. Missing RPM coverage or a changed configuration requires calibration before guidance is available.",
                    "An upper-range target is identified separately from an acceleration crossover. Driver reaction, changing grip and boost recovery can affect the best time to shift."),
                Group("Gauge placement",
                    "Power and torque gauges attach only to Native Analogue. Minimal, Combined, Box and Native Digital show them as separate movable gauges.",
                    "Disable incompatible attachment controls without changing your saved preference. Switching back to Native Analogue restores attachment.",
                    "Consider the separate G-force meter when placing supplementary gauges by default, with a side placement when there is no clear space above."),
                Group("Installer and update recovery",
                    "Explain how to repair a missing update helper with the full installer. Keep Wisp open if the update cannot start.",
                    "Ask you to exit a running Wisp copy before installation. Preserve settings, profiles, tire calibration and saved runs.")
            ]),
        new(
            "2.3.2",
            "September 20, 2026",
            "GAUGE PREVIEW HOTFIX",
            "Standalone boost and tire-temperature gauges now appear in every layout preview.",
            false,
            [
                Group("Appearance preview",
                    "Show enabled boost and tire-temperature gauges alongside Minimal, Combined and Box previews in both app interfaces.",
                    "Use your selected gauge colors, individual sizes and tire-gauge style. Fit the preview to the available space.",
                    "Preserve in-game gauge behavior, Native previews, attachment preferences and saved data.")
            ]),
        new(
            "2.3.1",
            "September 20, 2026",
            "GAUGE LAYOUT HOTFIX",
            "Standalone boost and tire-temperature gauges in every HUD layout.",
            false,
            [
                Group("Gauge availability",
                    "Show boost and tire-temperature gauges separately alongside Minimal, Combined and Box layouts.",
                    "Attachment remains available only in compatible Native layouts. Your saved attachment preference returns when you switch back.",
                    "Keep the Native Digital boost rail, gauge artwork, sizes, colors and saved positions unchanged.")
            ]),
        new(
            "2.3",
            "September 19, 2026",
            "WISP 2.3",
            "Drift mode for power and torque, with adjustable flash color and frequency.",
            false,
            [
                Group("Drift mode",
                    "Hold power and torque numbers and needles during brief combustion-engine power cuts. Enable Drift mode in Appearance → Gauges → Engine output → More options.",
                    "Adjust Flash frequency from 0.5 to 3 Hz, starting at 1.25 Hz. Frequency changes keep the current pulse smooth.",
                    "Choose Drift cut flash under Appearance → Colors. The numbers pulse in that color without changing their opacity."),
                Group("Saved settings and readings",
                    "Save Drift mode, flash frequency and flash color with HUD profiles. Drift mode is off by default.",
                    "Keep original recorded telemetry and peaks. Signed-output mode and EV regeneration retain their existing behavior.")
            ]),
        new(
            "2.2",
            "September 18, 2026",
            "WISP 2.2",
            "Shared native HUD rendering, compact EV attachments, and consistent previews.",
            false,
            [
                Group("HUD rendering",
                    "EV and Digital HUDs, supplementary gauges, G-force and text layouts now use the native renderer already used by the Analogue speedometer.",
                    "Preserve existing artwork, colors, needle behavior and smoothing controls.",
                    "Keep the G-force trail aligned when its display scale changes, and interpolate the displayed dot between telemetry samples."),
                Group("EV layout and previews",
                    "Attached tire-temperature, power and torque gauges sit closer to the EV speedometer and start at a smaller size. You can resize or detach them individually.",
                    "Use the same EV attachment geometry in the HUD and Appearance preview.",
                    "Keep enabled detached gauges visible in the preview through car and tune changes."),
                Group("Existing settings",
                    "Preserve saved settings, HUD profiles, tire calibration and recorded runs.")
            ]),
        new(
            "2.1.2",
            "September 16, 2026",
            "PERFORMANCE HOTFIX",
            "Bounded memory use for colored boost pressure numbers.",
            false,
            [
                Group("Boost pressure numbers",
                    "Reuse digit images as boost colors and pulse opacity change, preventing cached images from accumulating throughout a session.",
                    "Preserve digit colors, shadows and sizing, along with existing needle behavior and saved settings.")
            ]),
        new(
            "2.1.1",
            "September 15, 2026",
            "GAUGE SIZING",
            "Aligned supplementary gauges, separate size controls, and adjustable G-force meter size.",
            false,
            [
                Group("Gauge sizing",
                    "Boost, tire temperature, power and torque use matching dial sizes at 100%. Rows and columns stay aligned when you choose different sizes.",
                    "Set each gauge size independently in Appearance → Gauges. Boost and tire size controls also work while attached to the speedometer.",
                    "Set G-force meter size from 50% to 200%. It applies to attached, detached and Combined layouts and their previews.",
                    "The Analogue preview uses the same supplementary gauge layout as the HUD.",
                    "Existing sizes, profiles and detached positions are preserved. The previous shared power and torque size becomes the starting value for both individual controls.")
            ]),
        new(
            "2.1",
            "September 14, 2026",
            "WISP 2.1",
            "Optional power and torque gauges beside your speedometer.",
            false,
            [
                Group("Power and torque",
                    "Turn on Power gauge and Torque gauge independently in Appearance → Gauges.",
                    "Adjust smoothing for steadier readings, starting at 250 ms by default. Negative output is hidden by default; enable Show negative power and torque to display it.",
                    "Attach each gauge to the speedometer or detach it and move it in Edit HUD layout. Gauge start, middle and end colors apply across boost, tire, power and torque gauges, with optional matching numbers.",
                    "Keep fresh power and torque readings when game timestamps repeat, and play the needles continuously between readings.",
                    "Native-style dials show live BHP and torque, with peak markers and peak readouts. Torque follows your Nm or lb-ft preference.",
                    "Choose a fixed range for each car, or select Set from this run to use its observed peaks plus 10%. Scales stay fixed while driving and remember your choice for that car.",
                    "Adjust gauge size under More options. Numbers remain readable beyond the scale limit; Reset peaks clears the peak markers without changing your fixed ranges.",
                    "Peak readings reset when you change cars. HUD profiles save gauge visibility, size and the ranges currently selected."),
                Group("Existing features",
                    "Includes the Wisp 2.0.1 interface hotfixes and optional quick tour. Existing settings are preserved; fresh installations start with Native Analogue and all gauges enabled.")
            ]),
        new(
            "2.0.1",
            "September 14, 2026",
            "UI HOTFIX",
            "A quick tour of Wisp 2.0, with fixes for interface pauses, particle timing and clipped top speed.",
            false,
            [
                Group("Quick tour",
                    "Adds an optional welcome banner and four-step tour of the drift gauge, Display mode, Runs and Appearance colors.",
                    "Skip or dismiss the tour at any time, or replay it from Release Notes. It does not interrupt automatic game-triggered launches."),
                Group("Interface and dashboard",
                    "Prevents game-detection scans from pausing the interface when Forza is not detected.",
                    "Corrects uneven timing in the background and dashboard-rim particles while keeping their animation work bounded.",
                    "Keeps the top-speed label inside the dashboard oval in wide windows, including after leaving Display mode."),
                Group("Existing settings",
                    "Preserves HUD artwork, settings, profiles, tire calibration and saved runs.",
                    "This is a maintenance update for Wisp 2.0. The bundled runtime remains .NET 8.0.31.")
            ]),
        new(
            "2.0",
            "September 13, 2026",
            "WISP 2.0",
            "Drift angle guidance, a dashboard for your second screen, and easier run analysis.",
            false,
            [
                Group("Everyday use",
                    "Automatically saves run names, tune labels and notes, keeps edits when switching runs, and offers Retry if saving fails.",
                    "Search saved runs by name or tune label and use the Export menu for a shareable run file or CSV.",
                    "Click the connection status for game detection, telemetry and HUD visibility details, with help for the current connection state.",
                    "Keeps common settings visible and groups detailed explanations and fine adjustments under More options.",
                    "Keeps navigation in its selected theme while the Save HUD profile dialog is open, including in the legacy interface.",
                    "The drift gauge clears its angle, marker and bonus below 5 mph ground speed, including while stationary on slopes."),
                Group("Dashboard",
                    "Keeps the original red, yellow and green window controls at the top left.",
                    "Rebuilds the dashboard around a wide RPM scale, large driving readouts, readable assist states and grouped vehicle data.",
                    "Adds fill-screen and resizable borderless Display mode for a second monitor. Press F11 on Dashboard to enter or leave, or Escape to return.",
                    "Distinguishes Disabled, Enabled and Active assists, aligns the RPM ticks and bar segments, removes the tachometer glow and keeps the exit control outside the scaled dashboard."),
                Group("Controls and Runs",
                    "Groups Appearance around a persistent HUD preview, layout, gauges, colors and behaviour. Diagnostics contains the connection and UDP port controls.",
                    "Uses subtler card backgrounds and a smaller navigation dock, with visible selection and keyboard focus. The HUD preview grows into available space without stretching the gauges.",
                    "Switching Run A updates the summary in place and retains the comparison, graph position and valid selected range. Closing graph controls returns to the previous scroll position.",
                    "Adds eleven graph modules with driving presets, saved order and widths, and overlaid or side-by-side comparisons. Statistics can be viewed as cards or an A/B table.",
                    "Keeps Record and Stop within reach, puts saved runs in a drawer in smaller windows and retains the dark list theme while recording.",
                    "Adds application border, text color, glow, surface fill, corner and spacing controls. Setup instructions are in Diagnostics."),
                Group("Gauges",
                    "Adds a borderless drift angle gauge with a dark visibility option. Verified Drift Zone mode marks the scoring-angle threshold around 10 degrees and shows the angle bonus increasing through ordinary 20–40 degree drifts.",
                    "The angle bonus reaches its ceiling near 59.4 degrees. Speed, distance and scoring eligibility also affect points. Use Custom target for measured angle and an adjustable target on builds outside the verified scoring profile.",
                    "Allows the G-force meter to be disabled in every layout and gives its dot and trail independent color controls. Two boxes is now named Box."),
                Group("Existing settings",
                    "Preserves saved settings, HUD profiles, tire calibration and recorded runs.",
                    "Appearance includes Use legacy interface to restore the original layouts and styling on the next launch.",
                    "Fresh installs use the wizard palette. Updates retain saved colors.",
                    "The bundled runtime remains .NET 8.0.31.")
            ]),
        new(
            "1.2.2",
            "September 11, 2026",
            "MAINTENANCE",
            "G-force visibility correction and bundled runtime servicing.",
            false,
            [
                Group("G-force meter",
                    "Keeps the main Native HUD in place when the attached G-force meter is enabled or disabled.",
                    "Preserves saved HUD placement and profiles. A placement at the top screen edge may move inward once to keep the meter visible."),
                Group("Runtime",
                    "Updates the bundled .NET 8 runtime to 8.0.31.")
            ]),
        new(
            "1.2.1",
            "September 10, 2026",
            "ANALOGUE RENDERING",
            "Optional CPU rendering for Analogue tachometer lag or hitching, with fixes for queued needle updates and busy frame submissions.",
            false,
            [
                Group("CPU rendering",
                    "If the Analogue tachometer lags or hitches, enable CPU rendering in Diagnostics.",
                    "To apply the change, right-click Wisp's tray icon, choose Exit Wisp, then reopen the app.",
                    "CPU mode reuses the unchanged dial background to reduce repeated drawing work while the needle and live readings continue updating.",
                    "GPU rendering remains the default. CPU mode can increase CPU usage; other gauges keep their existing renderers, and Windows still uses the GPU to compose the overlay."),
                Group("Fixes and diagnostics",
                    "Keeps queued telemetry arrival times separate from the render clock, preventing a late-consumed sample from falsely resetting needle playback.",
                    "Retries a busy frame submission using the already drawn HUD, avoiding repeated drawing work while Windows cannot accept the frame.",
                    "Bounded local debug exports identify the active rendering mode and report CPU-side timings for native frame waits, their surrounding checks, drawing and presentation attempts.",
                    "Preserves the existing needle interpolation, artwork, HUD settings and Record and Compare Runs features.")
            ]),
        new(
            "1.2",
            "September 9, 2026",
            "RECORD & COMPARE RUNS",
            "Record a drive, review what happened, and compare it with another run inside Wisp.",
            false,
            [
                Group("Record a run",
                    "Start and stop from Dashboard or Runs, with an optional keyboard shortcut while driving.",
                    "Choose a countdown or timed stop, and mark moments to find them again after the run.",
                    "Save recordings locally with a name, tune label and notes. Existing HUD profiles and settings stay in place."),
                Group("Review and compare",
                    "Open Show graphs for all eight views. Read concise findings alongside speed, inputs, engine and tire charts, or select a section of the run.",
                    "Compare two recordings over time or through the same speed range. Distinct run colors and line styles, starting conditions, and missing data keep the comparison clear.",
                    "Switch to power and torque against RPM, a G-force plot, or tire-temperature changes. Filter RPM plots by gear and throttle.",
                    "Export a report image, raw telemetry CSV, or a Wisp recording for another person to open and compare.")
            ]),
        new(
            "1.1.4",
            "September 8, 2026",
            "TACHOMETER HOTFIX",
            "Addresses choppy Analogue tachometer motion while Forza is focused, with corrected motion blur when native needle data is unavailable.",
            false,
            [
                Group("Analogue tachometer",
                    "Moves the live combustion Analogue HUD to Direct3D11 and DirectComposition on a dedicated render thread, keeping the original gauge artwork, native needle angle and blur, and existing playback timing.",
                    "Corrects RPM fallback motion blur to use FH6's combustion needle shutter calculation when native needle data is unavailable.",
                    "Digital and electric HUDs and Appearance previews continue using WPF. No new setting is required."),
                Group("Debug reports",
                    "Adds needle-source and render-submission timing to local debug exports, grouped by logging period to help investigate remaining smoothness reports.")
            ]),
        new(
            "1.1.3",
            "September 8, 2026",
            "COMPATIBILITY HOTFIX",
            "Xbox app / Microsoft Store FH6 3.440.853.0 support, optional boost vacuum pressure, and reliable automatic update checks.",
            false,
            [
                Group("Compatibility",
                    "Adds a reviewed Native HUD map for Xbox app / Microsoft Store FH6 3.440.853.0 on Windows PC, retaining Store 3.430.771.0 and existing Steam maps.",
                    "Retains signed compatibility-map updates introduced in 1.1.2, with exact identity checks, atomic installation, and offline reuse. Future reviewed maps within the supported reader can be delivered without reinstalling Wisp; changed native layouts may still require an application update."),
                Group("Boost gauges",
                    "Adds Show vacuum pressure in Appearance > Boost. It displays negative pressure reported by FH6 and is off by default.",
                    "The existing boost-gauge toggle can show a stationary zero gauge on naturally aspirated cars, so you can leave it enabled without knowing a tune's induction setup. The gauge is for combustion cars.",
                    "Requires positive boost before enabling pressure readings for a car. After detection, the vacuum option follows negative pressure at idle and cruise.",
                    "Supports PSI and bar in attached and detached Digital and Analogue gauges and the Appearance preview. Enabled scales run from -20 to 70 PSI or -1 to 5 bar, with a zero marker on the Digital rail.",
                    "Saves the option with settings and HUD profiles. Existing settings and profiles leave vacuum disabled."),
                Group("Application updates",
                    "Checks for updates whenever Wisp opens and every 24 hours while it remains running, including while waiting in the tray, when automatic checks are enabled.",
                    "Keeps an available-update banner visible if a later automatic refresh fails. Downloads and installation still require confirmation.")
            ]),
        new(
            "1.1.2",
            "September 7, 2026",
            "COMPATIBILITY HOTFIX",
            "Support for Steam FH6 6.440.853.0 and signed compatibility updates for future reviewed builds.",
            false,
            [
                Group("Compatibility",
                    "Adds a separate Native HUD map for Steam FH6 build 6.440.853.0, retaining Steam 6.430.771.0 and Xbox app / Microsoft Store 3.430.771.0 support.",
                    "Enables background checks and manual import of signed compatibility maps. Verified maps can be delivered without reinstalling Wisp and remain available offline.",
                    "Retains exact build identity, read-only access, and menu visibility checks. Native layouts that change beyond the supported reader still require an application update.")
            ]),
        new(
            "1.1.1",
            "September 6, 2026",
            "COMPATIBILITY HOTFIX",
            "Xbox app and Microsoft Store editions of Forza Horizon 6 on Windows are now supported.",
            false,
            [
                Group("Compatibility",
                    "Adds a separate Native HUD compatibility path for Xbox app and Microsoft Store FH6 build 3.430.771.0. Requires an exact match to the reviewed Store build.",
                    "Checks the Store package and targeted code guards, including Windows executable-path aliases, without opening protected executable contents as a file.",
                    "Restores Native HUD attachment, gameplay visibility, exact redline, driver-assist data, and stock needle data on the supported Store build."),
                Group("Unchanged",
                    "The existing Steam compatibility path is preserved.",
                    "Wisp runs without administrator permissions and does not modify Forza or its game files.")
            ]),
        new(
            "1.1",
            "September 6, 2026",
            "MAINTENANCE",
            "Focused corrections to settings saves, calibration persistence, and connection timing.",
            false,
            [
                Group("Fixed",
                    "Profile changes are confirmed only after the settings write succeeds. Failed writes can be retried without creating duplicate profiles.",
                    "Saved calibration changes now include drivetrain and calibration revision, even when tire radii are unchanged.",
                    "Windows clock changes no longer affect telemetry expiry.",
                    "Release details remain available when retrying a downloaded update.",
                    "Setup uses the website's particle background, with a different particle layout and smoother faint gradients.",
                    "Wheel-indicated speed smoothing now honors the selected amount during large wheel-speed changes, instead of staying within 1.5 mph of the raw reading.",
                    "Release version labels no longer clip in the release history.",
                    "Update checks support shortened release versions and stable release labels while retaining installer verification.")
            ]),
        new(
            "1.0.12",
            "September 4, 2026",
            "RELIABILITY",
            "More useful local diagnostic reports and improved Native HUD recovery after race settings.",
            false,
            [
                Group("Diagnostics",
                    "Collects telemetry reception, UI processing, native-data freshness, composition callbacks, focus transitions, and Wisp CPU and memory usage on a background sampler.",
                    "Exported reports show sustained findings with timestamps, supporting measurements, the likely affected component, remaining uncertainty, and a useful next step.",
                    "Logging stays local, opt-in, time-limited, and size-limited. Reports cover Wisp's recorded telemetry and application timing, with observations and measurements to guide troubleshooting."),
                Group("Fixed",
                    "Allows Native HUD data to recover when a race provider clears its secondary local-provider flag. Recovery still requires a validated provider and a unique live car, RPM, and maximum-RPM match.",
                    "Removed the outer outline from the update confirmation dialog.")
            ]),
        new(
            "1.0.11",
            "September 4, 2026",
            "MAINTENANCE",
            "Corrects my repository author metadata. Application behavior is unchanged from 1.0.10.",
            false,
            [
                Group("Maintenance",
                    "Maps my previous build-author alias to Views2k in repository tools that support author aliases.",
                    "Updates the application and installer version to 1.0.11. All 1.0.10 features, settings, and telemetry behavior are retained.")
            ]),
        new(
            "1.0.10",
            "September 4, 2026",
            "FEATURE UPDATE",
            "Named HUD profiles, a rebuilt color editor, live torque and session peaks, update notifications, and local debug reports.",
            false,
            [
                Group("Added",
                    "Live torque now appears beside horsepower on the Wheel Speed Ready card, with a smoothed reading in the same typeface. Torque can be shown in Nm or lb-ft.",
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
                    "Kept very dark background choices visible and editable.",
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
