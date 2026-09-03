# Tire Temperature

Wisp 1.0.5 adds front and rear tire-temperature monitoring to both Native HUD
layouts. The readings come directly from the four tire-temperature values in
FH6 Data Out.

## Axle averages

Wisp averages the front-left and front-right temperatures for the Front value,
then averages the rear-left and rear-right temperatures for the Rear value. The
calculation is performed on each accepted telemetry sample. No tire temperature
is inferred from slip, speed, brake input, or ambient conditions.

The display range is 50°F to 350°F. A value above 350°F is held at the end of
the gauge and shown as 350°F. The marker remains at that endpoint until the
source value falls below 345°F, which prevents a saturated marker from
flickering against the end of the scale.

When Celsius is selected, the clamped Fahrenheit value is converted for the
readout and scale labels.

## Digital layout

Digital mode uses one neutral rail with two thin marker lines. Front and Rear
remain separate readings, but the rail is not filled and has no temperature
redline. The markers use two neighboring solid colors from the selected boost
palette when reactive colors are enabled.

When attached, the tire-temperature rail sits directly below the boost rail and
uses the same connector, spacing, slanted ends, and Native HUD line treatment.

## Analogue layout

Analogue mode uses one circular housing with two short needles. Front and Rear
have separate numeric readouts and separate solid needle colors. Needle position
communicates temperature independently of color.

The dial can attach below the boost gauge or run as a detached overlay. Its size
can be adjusted without changing the speedometer or boost-gauge scale.

## Controls

Open **Appearance > Tire temperature** to change:

- gauge visibility;
- attached or detached placement;
- Fahrenheit or Celsius;
- reactive marker and needle colors;
- gauge size.

The tire-temperature gauge uses the palette selected for the boost gauge. This
keeps the stacked Native layout coordinated without adding a second competing
palette setting.
