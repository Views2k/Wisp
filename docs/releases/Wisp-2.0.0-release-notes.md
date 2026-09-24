# Wisp 2.0

Wisp 2.0 adds a dashboard designed for a second screen, drift angle guidance,
and a more flexible workspace for recorded runs. The application has a new
interface with more control over its appearance. Existing HUDs, profiles,
calibration, colors, and recorded runs carry over.

## A dashboard you can drive with

The curved tachometer and large speed and gear readings keep the main instruments
together. Power, torque, session peaks, vehicle details, and connection information
have their own clear groups. Driver assists distinguish **Disabled**, **Enabled**,
and **Active**.

Choose **Display mode** to use Wisp as a borderless dashboard on another monitor.
Fill the screen or select **Resizable** to use part of it. **F11** switches Display
mode from Dashboard; **Esc** returns to the application. The game overlays remain
separate from this dashboard.

## Drift angle guidance

Enable the drift gauge in **Appearance > Gauges**. It shows your angle above the
gameplay, with native-style ticks, clear readings, and no outer frame. Adjust its
size and position, add dark shading for bright scenes, or enable a black background
and set its opacity.

Both modes hide the angle, marker, and bonus below **5 mph ground speed** (about
**8 km/h**). This avoids misleading drift readings when the car is stationary
on a slope or spinning its wheels.

**Drift Zone angle bonus** uses a verified scoring profile for Steam FH6
**6.440.853.0**. The scoring-angle range starts at **10°** and the angle bonus
reaches its maximum at **59.4°**. At **20–40°**, you are already using roughly
**80–90%** of that maximum angle bonus. Balance your angle with speed and line
while the game is awarding points.

The percentage shows your share of the maximum angle bonus. Final Drift Zone
scores also depend on speed, line, zone activity and scoring eligibility.

**Custom target** lets you choose your own angle and tolerance. Use it on builds
without a verified scoring profile, including Xbox app / Microsoft Store PC.
Drift Zone guidance uses its own verified scoring profile.

## Arrange Runs around what you want to review

Open **Show graphs**, then choose **Overview**, **Engine**, or **Tires & handling**.
The overlapping Overview, Acceleration, and Drifting pages are combined. Customize
each workspace by showing or hiding graphs, changing their order, and choosing
their width. Cards fill their rows and stack in narrower windows.

Compare runs **overlaid** or **side by side**, and read statistics as **cards** or a
**table**. Run colors and line styles remain distinct. Use the shared cursor to
inspect a moment across graphs, select a section for its own statistics, or match
an acceleration speed range. Power and torque by RPM, G-force plots, and tire
temperature changes remain available alongside the time graphs.

Switching Run A keeps the graph workspace open. The customization panel, nested
scrolling, and disabled library styling are corrected.

Search the saved-run list by name or tune label. Names, tune labels, and notes
save automatically, with **Saving… / Saved** feedback and **Retry** if a save
fails. Switching runs preserves pending edits, and recovery drafts protect
changes that could not yet be saved to the run file.

The visible **Export** menu contains single-run and CSV exports. Use
**Save image** in the graph workspace for a report image.

**Import runs** accepts multiple `.wisprun` files or a Wisp library ZIP.
**Export all** makes a library backup. **Delete all** asks for confirmation and
offers Undo; removed runs are retained locally for recovery. Imports validate
before changing the library, skip identical runs, and reject conflicting
duplicates without replacing existing data.

Recording shortcuts, countdowns, timed stops, markers, names, tune labels, notes,
report images, CSV, and individual run exports are retained. The documented
[library archive format](https://github.com/Views2k/Wisp/blob/v2.0.0/docs/run-library-format.md) contains the existing run-file
format. Save telemetry recordings of up to ten minutes locally.

## Appearance and everyday controls

- Click the existing connection status for game detection, telemetry, and HUD
  visibility details with the relevant next step. The status stays in place.
- Common controls stay visible; detailed explanations and fine adjustments
  use themed **More options** sections.
- **Layout**, **Gauges**, **Colors**, and **Behaviour** organize Appearance beside
  a larger live HUD preview.
- Set border color and width, glow, surface opacity, corner rounding, spacing,
  text colors, and background colors.
- Show, pause, or hide background particles. Use the accent color or choose a
  separate particle color while preserving your background color. The dashboard
  rim adds accent lighting and outward-moving particles.
- Set G-force dot and trail colors independently. Disable the meter in any
  layout, including Minimal and **Box**.
- Use the previous interface through **Appearance > Layout > Use legacy
  interface**, then restart Wisp.
- Fresh installs use the setup wizard's color palette. Updates keep your colors.
- The three colored window controls return; the Wisp name and accent-colored
  logo sit on the right. Connection status stays in its established position.
- RPM ticks, clipped Display mode controls, card borders, and Extras spacing are
  corrected. Overflowing scroll areas now fade at their edges while scrollbars
  remain visible.
- The navigation bar keeps its theme while a profile dialog is open, fixing
  the white background reported in [#67](https://github.com/Views2k/Wisp/issues/67).
- The redundant Setup page is removed. Connection instructions remain in
  Diagnostics, and the wizard uses FH6's **Fullscreen** setting name.

## Updating

Install over your existing Wisp installation. Your settings, profiles, tire
calibration, and saved runs are retained. The existing Native HUD artwork,
needle behavior, Analogue renderer, and optional CPU rendering mode are preserved.

Windows 10 and 11, 64-bit, with the Steam or Xbox app / Microsoft Store edition of
FH6 on the same PC are supported. Native HUD support and the Drift Zone scoring
profile have separate build requirements; see [Compatibility](https://github.com/Views2k/Wisp/blob/v2.0.0/docs/COMPATIBILITY.md)
for the native HUD details.
