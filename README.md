![Wisp header](docs/images/wisp-header.png)

<p align="center">
  <strong>A wheel-indicated speed, boost, tire-temperature, and G-force companion for Forza Horizon 6.</strong><br><br>
  <a href="https://github.com/Views2k/Wisp/releases/latest"><strong>Download</strong></a> ·
  <a href="CHANGELOG.md">Changelog</a> ·
  <a href="docs/HOW-WISP-WAS-BUILT.md">Architecture</a>
</p>

## New in 1.0.12

[Download 1.0.12](https://github.com/Views2k/Wisp/releases/tag/v1.0.12) ·
[Release notes](Wisp-1.0.12-release-notes.md)

Local debug reports now separate telemetry, UI, native-data, and composition
problems with timestamped evidence and practical next steps. Background collection
continues during UI stalls and stays local, opt-in, and bounded. This release also
improves native race-provider recovery after settings transitions and removes the
update-confirmation outline.

## The 1.0.10 quality-of-life update

**Save your HUD setups, choose your own colors, and track more of each drive.**
Version 1.0.10 brings the following additions and fixes since 1.0.8.

### New features

- **Named HUD profiles.** Save a Drift, Racing, Minimal, or Screenshot setup
  from Appearance, then apply, update, rename, or delete it from Profiles.
  Profiles include the layout, gauges, units, sizing, opacity, orientation,
  and complete color combination. Tire calibration, saved screen positions,
  telemetry, startup, update, debug, and hotkey settings stay separate.
- **A rebuilt color editor in Extras.** Select an element on the left and edit
  it on the right. Choose any point on the color wheel, adjust saturation,
  brightness and opacity, or enter an exact ARGB color. Targets include the app
  accent, backgrounds and surfaces, HUD border, and all three gauge-gradient
  colors. The **traction hook cue now has its own color control**.
- **Live torque and session peaks.** Torque joins horsepower on the Wheel Speed
  Ready card with matching smoothing and typography. Choose Nm or lb-ft. Top
  speed, peak power, and peak torque sit below their live readings; reset them
  together or let them reset when you change cars.
- **Update notifications and release details.** An optional startup check runs
  no more than once every 24 hours. A Dashboard banner announces an available
  update, and its GitHub release summary appears before you confirm the
  download. Downloads and installation are never automatic.
- **A customizable overlay hotkey.** Assign a global shortcut to show or hide
  the HUD without opening Wisp.
- **Local debug logging.** Enable it in Diagnostics to record bounded telemetry
  and application-health samples once per second, then export an issue-ready
  ZIP. Logging stops after 24 hours and files are retained for no more than
  seven days. You choose whether to share the export.
- **Release notes inside Wisp.** A dedicated sidebar page covers the documented
  public releases. Extras also includes a direct link to star Wisp on GitHub.

### Bug fixes and refinements

- Detached boost and tire-temperature gauges retain their saved positions
  across restarts and updates, including placements on secondary displays.
- Applying a profile restores the correct saved placement when changing
  layouts or switching between Native Digital and Native Analogue. The selected
  torque unit is also saved and applied.
- Expired native tachometer samples no longer repeatedly interrupt smooth
  RPM fallback motion when the native reader stalls.
- The traction hook cue works across Native HUD styles, and stale slip evidence
  is cleared after stopping.
- Color-wheel clicks and drags work throughout the wheel. Slider adjustments
  no longer move the selected wheel position, and very dark background colors
  remain visible and editable without sacrificing readable surfaces.
- Color customization uses a themed element list and a larger editor instead
  of the unstyled dropdown. The local debug logging control is styled to match.
- The duplicate profile-save button is removed, and the save confirmation
  dialog no longer has an outer border.
- Simultaneous debug-log actions no longer wait indefinitely, and a failed
  telemetry-listener start no longer leaves UI callbacks running.

[Download 1.0.10](https://github.com/Views2k/Wisp/releases/tag/v1.0.10) ·
[Full release notes](Wisp-1.0.10-release-notes.md) ·
[What's new on the website](https://wispoverlay.com/releases/1.0.10/)

![Wisp dashboard](docs/images/dashboard.png)

<p align="center"><sub>Dashboard shown with deterministic sample telemetry.</sub></p>

Wisp shows the speed implied by the driven wheels rather than only the car's
ground speed. The difference becomes visible during wheelspin, burnouts,
drifting, lockup, and loss of grip.

FH6 Data Out supplies the local telemetry stream. Wisp learns the effective
rolling radius of the current tires, applies the correct driven-wheel model for
FWD, RWD, or AWD, and presents the result in a lightweight Windows overlay.

## Features

- Wheel-indicated speed with separate front and rear calibration for staggered
  AWD setups.
- Digital and Analogue Native HUD layouts for combustion and electric cars.
- A live boost gauge for confirmed turbocharged and supercharged cars. Digital
  mode adds a rail below the tachometer, while Analogue mode adds a 0 to 70 PSI
  dial with 5 PSI ticks.
- Absolute PSI readouts, a learned per-car color scale, optional colored PSI
  numbers, attached or detached Analogue placement, a custom three-point gauge
  gradient, and an independent Digital option that uses the stock tachometer material.
- Front and rear tire-temperature gauges for both Native layouts. Digital mode
  uses two markers in one neutral rail with no colored fill, while Analogue mode
  uses two solid-color needles in one dial. Values support Fahrenheit and Celsius.
- Live RPM, gear, driver assists, electric power, regeneration, and redline
  state when the installed FH6 build supports those sources.
- Standalone or Native-attached G-force display with a longer motion trail.
- A vehicle dashboard for speed, RPM, drivetrain, power, torque, controls, and
  connection status.
- One continuous color editor for the application accent, background surfaces,
  HUD border, shared gauge gradient, and traction hook cue, plus named visual profiles.
- Optional once-daily update discovery, a customizable HUD visibility shortcut,
  and bounded local debug logging with ZIP export for issue reports.

## Gallery

<table>
  <tr>
    <td width="50%">
      <img src="docs/images/themes.png" alt="Wisp Appearance page showing the Native Analogue HUD preview and overlay controls">
      <br><sub>Appearance controls with the complete Native Analogue HUD preview.</sub>
    </td>
    <td width="50%">
      <img src="docs/images/native-digital-hud.png" alt="Wisp Digital Native HUD with boost and tire-temperature rails during FH6 gameplay">
      <br><sub>Digital Native HUD with attached boost and front/rear tire-temperature rails.</sub>
    </td>
  </tr>
  <tr>
    <td width="50%">
      <img src="docs/images/native-analogue-hud.png" alt="Wisp Analogue Native HUD with attached boost and tire-temperature dials during FH6 gameplay">
      <br><sub>Analogue Native HUD with attached boost and dual-needle tire-temperature dials.</sub>
    </td>
    <td width="50%">
      <img src="docs/images/diagnostics.png" alt="Wisp diagnostics with populated telemetry data">
      <br><sub>Diagnostics populated with deterministic telemetry and Native HUD capability data.</sub>
    </td>
  </tr>
</table>

## Requirements

- Windows 10 or Windows 11, 64-bit.
- Forza Horizon 6 with Data Out enabled.
- Borderless fullscreen, windowed, or another desktop-composited display mode.
  Exclusive fullscreen can cover ordinary Windows overlays.

Native process-derived HUD state currently supports the recorded Steam FH6
build `6.430.771.0` and its exact executable fingerprint. Data Out reception and
dashboard calculations remain independent of that compatibility contract.

The installer is self-contained and installs for the current user. It does not
require administrator access or a separate .NET runtime.

## Install

1. Open [Releases](https://github.com/Views2k/Wisp/releases/latest).
2. Download and extract the current `Wisp-Setup-*.zip` package.
3. Keep the installer and its `.sha256` file together.
4. Verify the installer checksum, then run the installer.
5. Complete the required setup wizard on first launch.

**[WINDOWS POWERSHELL]**

```powershell
$installer = Get-ChildItem .\Wisp-Setup-*.exe | Select-Object -First 1
(Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash
```

Compare the result with the value in the adjacent `.sha256` file. The current
installer is unsigned, so Windows may show an unfamiliar-publisher warning.

## Application updates

Application updates are manual. Open **Extras** and select **Check for updates**
when you want Wisp to query the latest GitHub Release. Wisp does not check in
the background.

An accepted release must be public, stable, and immutable. Its tag and
versioned installer name must match, and GitHub must provide the installer's
exact byte length and SHA-256 digest. Downloads are limited to the canonical
GitHub release URL and GitHub's HTTPS release-asset hosts. Wisp verifies the
length and digest before asking whether to install and restart. The separate
update helper repeats those checks, waits for Wisp to exit, runs the current-user
installer silently, validates the installed version, and then restarts Wisp.

The updater uses GitHub's anonymous release endpoint and contains no repository
credential. If GitHub's release API or artifact delivery is unavailable, the
check reports that the update service is unavailable. The installer remains
unsigned whether it is started manually or through Wisp.

A verified in-place update preserves a completed setup. A fresh installation
always opens the setup wizard before the dashboard or overlays are available.

## Connect FH6

In **Settings > HUD and Gameplay**:

1. Enable **Data Out**.
2. Set the IP address to `127.0.0.1`.
3. Set the port to `5500`, or to the listener port selected in Wisp.
4. Keep the car moving briefly while the setup wizard validates the stream.

The wizard also confirms the display mode and stock HUD setting before it opens
the dashboard or driving overlays.

## Speed sources

**Wheel-indicated** is the default. FH6 does not report tire size, so Wisp learns
effective rolling radius from clean, straight driving with grip. The value stays
unavailable until the current tire profile is trustworthy.

**FH6 speed** uses the packet's vehicle-speed value directly and does not require
tire learning. See [Wheel-Speed Model](docs/WHEEL-SPEED-MODEL.md) for the complete
calibration and drivetrain rules.

## Native HUD compatibility

Native HUD layouts start at FH6's bottom-right HUD position. Disable the stock
speedometer to avoid overlap, then use **Edit HUD layout** if a custom display
needs different placement or scale.

The Native provider opens the supported FH6 process with query/read
access only. It does not inject code, hook rendering, call game functions, or
write process memory. Changed or unknown executable fingerprints disable the
affected process-derived state rather than reusing data from another build.

See [Compatibility and Update Safety](docs/COMPATIBILITY.md) for the supported
build, validation boundary, and update behavior.

## Privacy and limitations

- Telemetry is accepted only from `127.0.0.1`.
- Settings and tire profiles remain in the current user's local application data.
- The installer is not code-signed.
- A changed FH6 executable fingerprint requires a reviewed Wisp update.
- FH6 exposes no tune identifier. Relearn the current tires after changing wheel
  or tire diameter.
- Software-only WPF captures do not reproduce the live Native HUD shaders.

## Build from source

The repository uses the .NET 8 SDK selected by `global.json`. Python 3.12 or
later runs the offline compatibility-audit tests; CI pins Python 3.14.7. Inno
Setup 6 is needed only to package an installer.

**[WINDOWS POWERSHELL]**

```powershell
dotnet restore Wisp.sln --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all
dotnet format Wisp.sln --verify-no-changes --no-restore --verbosity minimal
dotnet test Wisp.sln --configuration Release --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
python -m unittest discover -s tools/tests -p "test_*.py" -v
```

To build the self-contained installer:

**[WINDOWS POWERSHELL]**

```powershell
.\installer\Build-Installer.ps1
```

## Documentation

- [Boost Gauge](docs/BOOST-GAUGE.md)
- [Tire Temperature](docs/TIRE-TEMPERATURE.md)
- [How Wisp Was Built](docs/HOW-WISP-WAS-BUILT.md)
- [Wheel-Speed Model](docs/WHEEL-SPEED-MODEL.md)
- [Compatibility and Update Safety](docs/COMPATIBILITY.md)
- [Validation](docs/VALIDATION.md)
- [Contributing](CONTRIBUTING.md)
- [Security Policy](SECURITY.md)

## License and game content

Wisp is proprietary, source-available software. Official unmodified binaries
may be used personally and non-commercially under the
[Wisp Proprietary Source License](LICENSE).

Forza Horizon 6 © Microsoft Corporation. Wisp is an unofficial community
project and is not endorsed by or affiliated with Microsoft.

The Native HUD content under `src/Wisp.App/Assets/Native` is based on publicly
circulated Forza Horizon 6 material. Views2k claims no ownership of Microsoft
Game Content. See [Third-party notices](THIRD-PARTY-NOTICES.md) and the
[Microsoft Game Content Usage Rules](https://www.xbox.com/en-us/developers/rules).
