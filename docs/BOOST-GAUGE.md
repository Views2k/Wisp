# Boost Gauge

Wisp 1.0.5 adds boost pressure to the Native Digital and Native Analogue HUDs.
The gauge reads the boost channel already present in the FH6 Data Out packet.
It does not estimate pressure from throttle, RPM, speed, or gear.

## Availability

The gauge remains hidden until the current car produces a non-zero boost-channel
sample. FH6 reports vacuum on supported forced-induction cars before positive
boost, so Wisp can identify the car before the driver builds pressure. Electric
cars and cars whose boost channel remains at zero do not show the gauge. The
availability state resets when the car changes.

Vacuum is used only for availability. The visible readout and gauge position
start at 0 PSI and do not show a negative pressure value.

## Digital layout

Digital mode adds one slim rail beneath the existing tachometer rail. The rail
uses the same slanted geometry and marker treatment as the Native HUD. A short
connector keeps the two rails aligned when attachment is enabled.

The rail can use the custom three-point gauge gradient or the neutral stock
tachometer material. The PSI readout has its own color toggle, so the number
can remain white while the rail uses the gradient.

## Analogue layout

Analogue mode adds a circular 0 to 70 PSI gauge with numbered 10 PSI intervals
and intermediate 5 PSI ticks. The pressure appears as a two-digit value in the
center circle. The needle and readout use the Native HUD's existing typography,
materials, and motion style.

The readout color can follow the needle position or remain white. The Analogue
gauge also has independent size and attached-placement controls.

## Color scaling

The PSI number remains an absolute pressure reading. Only the gradient position
adapts to the current car. Wisp remembers the highest positive pressure observed
for that car during the current run and uses it as the color-range ceiling, with
a 5 PSI minimum. This lets a low-boost and high-boost car use the full selected
gradient without changing the displayed PSI value.

The gradient start, middle, and end colors are independently adjustable. The
Stock choice keeps the Digital gauge neutral. Tire-temperature markers use
neighboring colors from the same gradient so attached gauges remain coordinated.

## Controls

Open **Appearance > Boost gauge** to change:

- gauge visibility;
- attached or detached placement;
- Analogue gauge size;
- shared gauge-gradient colors;
- Analogue PSI-number color;
- Digital PSI-number color;
- Digital stock-material mode.

Detached placement is saved separately and can be adjusted through **Edit HUD
layout**.
