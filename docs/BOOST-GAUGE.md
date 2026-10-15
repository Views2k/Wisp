# Boost Gauge

Wisp displays boost pressure in the Native Digital and Native Analogue HUDs.
The gauge reads pressure directly from the boost channel in the FH6 Data Out packet.

## Availability

The gauge visibility toggle controls the gauge on combustion cars.
Naturally aspirated cars show a stationary zero gauge, so you can leave it
enabled without knowing whether a tune has forced induction.

Pressure readings begin once the current car produces at least 0.5 PSI of
positive boost. Detection resets when the car changes or the HUD session resets.

Enable **Show vacuum pressure** to display negative telemetry after positive
boost is detected. The option is off by default and saves with settings and HUD
profiles. With it off, negative readings clamp to zero. Choose PSI or bar for
both layouts and the Appearance preview; FH6 telemetry remains in PSI internally.

Set **Maximum boost** from **20 to 200 PSI** in 10 PSI steps. The selected maximum
applies to the Digital rail, Analogue dial and Appearance preview, and saves with
settings and HUD profiles. The default is 70 PSI. In bar, the scale rounds up to
the next whole bar for clear markings.

## Digital layout

Digital mode adds one slim rail beneath the tachometer rail. The rail
uses the same slanted geometry and marker treatment as the Native HUD. A short
connector joins the two rails. Digital placement stays fixed to the HUD.

The rail can use the custom three-point gauge gradient or the neutral stock
tachometer material. The pressure readout has its own color toggle, so the number
can remain white while the rail uses the gradient.

With vacuum enabled, the rail includes a zero marker between negative and
positive pressure.

## Analogue layout

Analogue mode uses a circular gauge with numbered labels and intermediate tick
marks that adapt to the selected maximum. The default scale runs from 0 to
70 PSI or 0 to 5 bar.
The center readout shows whole PSI or bar to one decimal place. The readout
matches the Native HUD's typography and shading, and the needle uses the same
motion style.
With vacuum enabled, the scale extends to -20 PSI or -1 bar.

The readout color can follow the needle position or remain white. The Analogue
gauge also has independent size and attached-placement controls.

## Color scaling

The readout shows pressure in the selected unit. Only the gradient position
adapts to the current car. Wisp remembers the highest positive pressure observed
for that car during the current run and uses it as the color-range ceiling, with
a 5 PSI minimum. This lets a low-boost and high-boost car use the full selected
gradient without changing the displayed pressure value.

The gradient start, middle, and end colors are independently adjustable. The
Stock choice keeps the Digital gauge neutral. Tire-temperature markers use
neighboring colors from the same gradient.

## Controls

Open **Appearance > Gauges**, then **Boost gauge**, to change:

- gauge visibility;
- attached or detached Analogue placement and gauge size;
- PSI or bar;
- maximum boost from 20 to 200 PSI, in 10 PSI steps;
- vacuum pressure after positive boost is detected;
- Analogue pressure-number color;
- Digital pressure-number color;
- Digital stock-material mode.

Set the shared gradient in **Appearance > Colors** using **Gauge start**,
**Gauge middle**, and **Gauge end**. Analogue gauge size applies while attached
or detached. Detached placement is saved separately and can be adjusted through
**Edit HUD layout**.
