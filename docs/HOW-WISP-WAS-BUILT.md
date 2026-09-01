# How Wisp Was Built

Wisp began with a simple distinction: vehicle speed and driven-wheel speed are
not always the same. A normal speedometer answers how fast the car is moving.
Wisp shows how fast the driven wheels say it should be moving, which exposes
wheelspin, burnouts, and the changing relationship between grip and road speed.

Three decisions shaped the project from the beginning:

- drivetrain behavior should be visible without changing the game;
- Native HUD state should be exact when available and absent when it is not;
- telemetry, physics, rendering, and application state should remain separate.

## Architecture

The solution is split into five production projects:

```text
FH6 loopback UDP
    -> Wisp.Telemetry -> validated VehicleState
    -> Wisp.Core      -> tire model, speed, G-force, freshness
    -> Wisp.App       -> setup, settings, diagnostics, WPF overlays
        |                  `-> guarded read-only Native capability
        `-> Wisp.Update -> release validation and verified download
                         -> Wisp.Updater -> apply after Wisp exits
```

`Wisp.Telemetry` understands the fixed FH6 packet and owns the loopback UDP
receiver. `Wisp.Core` contains the deterministic vehicle calculations and has
no WPF dependency. `Wisp.App` owns startup, settings, application lifecycle,
the setup gate, and the Windows presentation layer. `Wisp.Update` owns release
metadata, transport, and artifact verification. `Wisp.Updater` is a small
separate executable that applies an already verified installer after Wisp has
closed.

Keeping the projects separate makes the important rules independently testable.
A packet parser test does not need a window, and a wheel-speed test does not
need a live game process.

## From packets to wheel speed

FH6 sends a 324-byte Horizon Data Out packet containing wheel angular velocity,
vehicle speed, acceleration, drivetrain type, RPM, gear, controls, and tire
state. Wisp rejects malformed lengths, non-finite values, invalid enum values,
and implausible powertrain data before a packet can become application state.

The receiver drains a bounded backlog and retains the newest valid datagram.
The HUD therefore does not work through a queue of stale frames when telemetry
arrives faster than it can be presented.

Data Out does not contain tire size. Wisp derives effective rolling radius only
from clean driving samples, then combines that radius with driven-wheel angular
velocity. FWD and RWD use their mechanical axle. AWD converts each axle with
its own learned radius before the results are averaged, which preserves
staggered setups.

Calibration is deliberately conservative. The first profile requires a stable
consensus. Replacing a trusted profile after a tire or wheel change requires a
larger, cleaner body of evidence. Missing or contradictory evidence produces an
unavailable wheel-speed value rather than a guessed radius or ground-speed
fallback.

The separate **FH6 speed** option is explicit: it displays the packet's vehicle
speed directly and bypasses tire learning. The two sources are never blended.

## Reconstructing the Native HUD

Native mode is composed from live controls rather than a static screenshot. It
uses 240 publicly circulated HiRes HUD PNGs derived from FH6 swatchbin files
for digits, gears, dial marks, assist states, and electric elements. Every image
has a manifest entry recording its original FH6-relative path, dimensions,
SHA-256, role, and rendering treatment.

The asset cache loads each image once, corrects the exported alpha
representation before WPF composition, freezes the result, and reuses tinted
variants. Digital, Analogue, Electric Digital, and Electric Analogue controls
then select the appropriate elements for the current state. Pixel shaders
provide the digital RPM material, analogue dial treatment, and tachometer
needle trail.

Wisp is a free, non-commercial companion and uses these assets for
interoperability and HUD presentation. The repository links to Microsoft's
[Game Content Usage Rules](https://www.xbox.com/en-us/developers/rules) and
includes a third-party notice identifying the game content.

The PNGs remain Microsoft Game Content and are not covered by Wisp's source-code
license. Public circulation does not itself grant rights, and Wisp does not
claim that Microsoft authorized their extraction or redistribution. The
manifest and bundled third-party notice preserve that boundary.

## Exact state that Data Out does not provide

Data Out includes current RPM but not FH6's tune-aware redline or compact
local-player assist state. It also does not expose the final Native EV gauge
digits, fade decisions, power/regeneration presentation, or electric needle
state. Wisp reads that limited state through guarded Windows query/read process
access only.

Before a value is accepted, the provider validates the executable version,
length, SHA-256, image bounds, compatibility contract, process generation,
vtable guards, unique local-player source, car identity, current RPM, and
maximum RPM. All reads are bounded. There is no process write, injection,
remote thread, debugger, driver, or game-function call.

Capabilities fail independently after the shared identity checks. A redline
failure removes the redline without disabling UDP reception or dashboard speed.
An assist failure removes the affected state instead of leaving an icon from a
previous car. Visible driving overlays separately require validated Native
gameplay visibility and fail closed when that state is unavailable.

The electric gauge uses a second, fingerprint-specific ownership chain from the
HUD registry to the exact Native child and provider. Wisp takes one bounded
child snapshot, validates its digits, booleans, ranges, vtables, and back
references, then rechecks the chain before publishing it. The final digit state
already includes FH6's unit conversion and leading-zero fade decisions. Normal
Native gear textures are selected directly from the captured electric gear
state; no generated gear artwork is substituted.

## Rendering without unnecessary work

The live HUD consumes at most one newest packet per WPF compositor frame. RPM
samples pass through a small receive-time interpolation buffer so the telemetry
needle can move continuously between real samples without predicting future
RPM. When an exact Native needle pair is available, a separate bounded playback
path follows its observed angle and blur samples on the same compositor clock.
It resets instead of extrapolating across stale input, a car change, or a hidden
render lifetime.

Native controls detach from `CompositionTarget.Rendering` while hidden,
collapsed, minimized, or unloaded. Diagnostics refresh at a much lower cadence,
and game-window visibility checks are bounded separately. Scrolling uses stable
parent containers so moving through a page does not replace the page's scale
transform on every offset change.

This lifecycle approach is more important than a high-frequency timer: work is
performed when a new packet or a visible compositor frame can change what the
user sees.

FH6 can continue sending plausible driving telemetry while a menu is open, so
packet activity alone is not a visibility signal. Wisp combines telemetry
freshness with the guarded game UI state and foreground-window relationship;
menus, loading transitions, cutscenes, and alt-tab hide or demote the overlays
without discarding the user's saved placement.

## Setup and design

The setup wizard is a startup boundary. It validates a live Data Out stream and
the required display confirmations before the dashboard or overlay windows are
created. A fresh install creates a one-time setup marker. Completing and saving
the wizard clears it; closing early leaves it in place for the next launch. A
verified in-place update preserves an already completed setup, but it cannot
bypass the wizard for a new installation.

The control center uses a compact sidebar, responsive page layouts, independent
accent, dark-background, and HUD-border palettes, and a larger live HUD preview.
The setup wizard and control center share a layered particle backdrop whose
motion pauses when its window is inactive or minimized and follows Windows
reduced-motion settings. The dashboard presents current speed, RPM, drivetrain,
horsepower, torque, driver inputs, and Native capability status without adding
work to the overlay render loop.

## Testing and release packaging

The test suite covers packet offsets, malformed data, listener lifecycle,
drivetrain calculations, calibration consensus, settings migration, setup
gating, WPF resources, native asset hashes and pixels, gauge geometry, shaders,
render lifetime, recorded RPM traces, compatibility validation, and installer
promotion rollback.

The opt-in UI review tool constructs the compiled WPF surfaces with isolated
settings and deterministic sample state. It checks page layouts at multiple
viewports and DPI levels, the four-step wizard, scroll behavior, native-control
lifetime, bindings, text overflow, and loaded theme resources. Software bitmap
captures cannot reproduce PS 3.0 shader output, so they are treated as layout
evidence rather than live-game visual proof.

The packaging script runs the Release .NET suite, publishes the self-contained
untrimmed `win-x64` application and the separate single-file update helper,
validates their PE headers and file versions, and builds the installer in a
unique staging directory. It validates the installer, inner checksum, two-file
ZIP, and outer checksum before promoting the four-file bundle with durable
recovery state.

Application updates are intentionally user-initiated. The client reads GitHub's
anonymous latest-release endpoint and accepts only a stable immutable release
with a strict tag, exact versioned installer name, uploaded state, byte length,
and SHA-256 digest. Download redirects are limited to GitHub's HTTPS
release-asset hosts. The staged helper verifies the installer again, waits for
the exact Wisp process, applies the current-user package silently, validates the
installed executable and version, and restarts the application. No repository
credential is bundled. The installer is still unsigned.

## Further reading

- [Wheel-Speed Model](WHEEL-SPEED-MODEL.md)
- [Compatibility and Update Safety](COMPATIBILITY.md)
- [Validation](VALIDATION.md)
- [UI Review Tool](../tools/Wisp.UiReview/README.md)
