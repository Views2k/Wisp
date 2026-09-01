// WPF-compatible port of the shipped ForzaUIMaterialAnalogGauge PS 6.6 DXIL.
// Operation order and constants intentionally match the original material.

sampler2D ImplicitInput : register(s0);
float2 GaugeParameters : register(c0); // x: normalized redline, y: normalized 1000 RPM major interval

float4 main(float2 uv : TEXCOORD0) : COLOR0
{
    float inputOpacity = tex2D(ImplicitInput, uv).a;
    float2 p = (uv * 2.0) - 1.0;
    float radius = length(p);

    float rawAngle;
    float ratioAngle = atan(p.x / -p.y);
    if (p.y > 0.0)
    {
        ratioAngle += p.x >= 0.0 ? 3.1415927410125732 : -3.1415927410125732;
    }

    rawAngle = (ratioAngle + 2.6179938316345215) * 0.23873241245746613;
    if (p.y == 0.0)
    {
        rawAngle = p.x >= 0.0 ? 1.0 : 0.2499999701976776;
    }

    float angle = saturate(rawAngle);
    float radialWidth = length(float2(ddx(radius), ddy(radius)));
    float angularWidth = length(float2(ddx(rawAngle), ddy(rawAngle)));

    float dashInterval = GaugeParameters.y;
    float dashOffset = rawAngle - (floor((rawAngle / dashInterval) + 0.5) * dashInterval);
    float dashLeading = saturate((dashOffset + 0.0022499999031424522) / angularWidth);
    float dashTrailing = saturate((0.0022499999031424522 - dashOffset) / angularWidth);
    float outerBandInner = saturate((radius - 0.9474999904632568) / radialWidth);
    float outerEdge = saturate((1.0 - radius) / radialWidth);
    float longDash = dashLeading * dashTrailing * outerBandInner;

    float notchOffset = angle - (floor((angle / dashInterval) + 0.5) * dashInterval);
    float notchLeading = saturate((notchOffset + 0.0022499999031424522) / angularWidth);
    float notchTrailing = saturate((0.013750000856816769 - notchOffset) / angularWidth);
    float notch = notchLeading * notchTrailing;
    float ringInner = saturate((radius - 0.9674999713897705) / radialWidth);
    float ringAndDashes = (((1.0 - notch) * ringInner) + longDash) * outerEdge;

    float shadeInner = saturate((radius - 0.75) / radialWidth);
    // The shipped material applies its sweep masks to the unclamped angle.
    // Using the saturated value makes the excluded 120-degree sector pass the
    // edge mask, wrapping the arc, redline, and minor dashes into a full circle.
    float sweepLeading = saturate((rawAngle + 0.0022499999031424522) / angularWidth);
    float sweepTrailing = saturate((1.0022499561309814 - rawAngle) / angularWidth);
    float sweepMask = sweepLeading * sweepTrailing;
    float shadeLeading = saturate((rawAngle + 0.0033749998547136784) / angularWidth);
    float shadeTrailing = saturate((1.0011249780654907 - rawAngle) / angularWidth);
    float beforeRedline = saturate((GaugeParameters.x - rawAngle) / angularWidth);

    float redlineBlue = (beforeRedline * 0.46666663885116577) + 0.5333333611488342;
    float coloredRing = (ringAndDashes * (1.0 - (0.699999988079071 * beforeRedline))) * sweepMask;
    float radialShade = outerEdge * (0.20000000298023224 - ((1.0 - radius) * 0.800000011920929));
    radialShade *= shadeInner * shadeLeading * shadeTrailing;
    float combinedAlpha = coloredRing + radialShade - (coloredRing * radialShade);

    float red = coloredRing * sweepMask;
    float green = coloredRing * sweepMask * beforeRedline;
    float blue = coloredRing * sweepMask * redlineBlue;
    float denominator = combinedAlpha + 0.000009999999747378752;
    float3 straightColor = float3(red, green, blue) / denominator;

    return float4(straightColor * combinedAlpha, combinedAlpha) * inputOpacity;
}
