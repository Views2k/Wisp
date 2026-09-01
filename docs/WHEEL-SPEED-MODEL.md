# Wheel-Speed Model

## Purpose

Wheel-indicated speed represents the surface speed implied by the driven wheels,
not the car's movement over the ground. The values can differ during wheelspin,
lockup, drifting, airborne events, or loss of grip.

Wisp also offers an explicit **FH6 speed** source. That mode uses the packet's
vehicle-speed field directly and does not participate in tire learning. The
rules below apply to **Wheel-indicated** mode.

On a supported electric Native HUD, FH6 also exposes its final displayed speed
digits and display-unit state through the fingerprint-gated read-only path.
Those digits may be used only when the FH6 speed source and Wisp's selected unit
match the validated Native unit. They never replace a wheel-indicated result.

## Inputs

The calculation uses validated Data Out fields for:

- wheel angular velocity in radians per second;
- drivetrain type;
- vehicle ground speed during calibration only;
- tire slip, steering, acceleration, braking, and suspension travel;
- current car identity and telemetry freshness.

FH6 does not include wheel diameter or tire circumference, so wheel angular
velocity cannot become a linear speed until Wisp has learned effective rolling
radius.

## Initial calibration

A candidate is considered only when the car is moving between 3 and 125 m/s,
wheel speed is high enough to be meaningful, and the driven tires appear loaded.
The default gates also require:

- normalized absolute slip ratio no greater than `0.12`;
- steering input no greater than `16` in magnitude;
- lateral acceleration no greater than `1.5 m/s²` in magnitude;
- longitudinal acceleration no greater than `3.5 m/s²` in magnitude;
- deceleration no greater than `0.75 m/s²`;
- no brake input;
- driven-wheel disagreement no greater than 10 percent.

Trust requires at least 12 candidates in a dominant cluster containing at least
60 percent of the current sample window, with a radius spread no greater than
1.5 percent. Samples do not need to be consecutive, so one rejected packet does
not erase otherwise consistent evidence.

## Trusted-profile replacement

FH6 does not expose a tune identifier. A different wheel or tire diameter can
therefore arrive under the same car ID. Wisp keeps the trusted profile active
while it evaluates a possible replacement under stricter conditions.

Replacement requires at least 96 samples, 90 percent consensus, and no more than
0.3 percent radius spread. Slip, steering, acceleration, and wheel disagreement
limits are also tightened. A replacement is accepted only when the difference
is large enough to be meaningful.

The user can clear the current profile immediately with **Relearn current
tires** in Diagnostics.

## Drivetrain calculation

For one axle, Wisp computes the mechanical mean wheel angular velocity and
multiplies by that axle's learned radius:

```text
axle speed = ((left rad/s + right rad/s) / 2) * radius
```

The selected linear speed is:

```text
FWD = absolute(front axle speed)
RWD = absolute(rear axle speed)
AWD = absolute((front axle speed + rear axle speed) / 2)
```

AWD learns and applies front and rear radii independently. A staggered setup is
not reduced to a single compromise radius.

The final value is converted with the exact constants used by the core model:

```text
mph  = m/s * 2.2369362920544
km/h = m/s * 3.6
```

## Availability and smoothing

A missing, incompatible, or implausible profile makes wheel-indicated speed
unavailable. Wisp does not substitute ground speed, a non-driven axle, or a
generic tire radius. A brief impossible wheel sample can retain the last valid
same-car value instead of presenting a believable but fabricated replacement.

The default smoothing value is zero. Optional smoothing is applied after the
physical calculation and is deliberately bounded so it cannot move more than
1.5 mph away from the current raw result.

## Stored profiles

Trusted profiles are keyed by car identity and drivetrain schema. Current-format
profiles survive menus, loading, alt-tab, telemetry gaps, car changes, and Wisp
restarts. Legacy, incomplete, or physically implausible records are discarded
instead of migrated into a trusted result.
