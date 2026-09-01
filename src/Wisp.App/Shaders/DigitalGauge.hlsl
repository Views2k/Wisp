// WPF-compatible port of the shipped ForzaUIMaterialDigitalGauge PS 6.6 DXIL.
// Operation order, constants, 302 x 24 reference box, and 283 px amount span
// intentionally match the original FH6 material.

sampler2D InputSampler : register(s0);
float2 GaugeParameters : register(c0); // x: GaugeLargeDashAmount/current RPM, y: GaugeRedlineAmount

float4 main(float2 uv : TEXCOORD0) : COLOR0
{
    float sourceAlpha = tex2D(InputSampler, uv).a;
    float horizontal = (uv.x * 302.0) - 17.0 + (uv.y * 4.8000001907348633);
    float vertical = uv.y * 24.0;
    float current = GaugeParameters.x * 283.0;
    float redline = GaugeParameters.y * 283.0;

    float verticalOffset = vertical - 12.5;
    float redlineHalf = redline * 0.5;
    float trackHalf = 140.5 - redlineHalf;

    float2 trackDistance = max(
        float2(
            abs(horizontal - 2.0 - redline - trackHalf) - trackHalf,
            abs(verticalOffset) - 6.5),
        0.0);
    float trackEdge = saturate(length(trackDistance) - 0.5);
    float currentSide = 0.5 + (saturate(current - horizontal) * 0.5);
    float currentTrack = currentSide * (1.0 - trackEdge);

    float2 inactiveDistance = max(
        float2(
            (1.0 - redlineHalf) + abs(horizontal - redlineHalf + 1.0),
            abs(verticalOffset) - 6.5),
        0.0);
    float inactiveEdge = saturate(length(inactiveDistance) - 0.5);
    float currentRatio = saturate(horizontal / min(current, redline));
    float inactiveLevel = 0.25 + (currentRatio * 0.25);
    inactiveLevel += (-0.099999994039535522 - (currentRatio * 0.25)) *
        saturate(horizontal - current);
    float inactiveTrack = inactiveLevel * (1.0 - inactiveEdge);

    float redlineSide = saturate(horizontal - redline);
    float redlineGreen = redlineSide * -0.46666663885116577;
    float combinedTrack = inactiveTrack + ((currentTrack - inactiveTrack) * redlineSide);

    float2 markerDistance = max(
        float2(
            abs(horizontal - current) - 0.5,
            abs(verticalOffset) - 7.0),
        0.0);
    float marker = 1.0 - saturate(length(markerDistance));

    float redFactor = (1.0 - redlineSide) + (marker * redlineSide);
    float greenFactor = (1.0 + redlineGreen) - (marker * redlineGreen);
    float alpha = combinedTrack + ((1.0 - combinedTrack) * marker);
    float greenChannel = alpha * redFactor;
    float blueBase = alpha * greenFactor;

    float2 haloDistance = max(
        float2(
            abs(horizontal - current) - 2.0,
            abs(vertical - 13.0) - 5.0),
        0.0);
    float halo = 1.0 - saturate((length(haloDistance) + 11.0) * 0.058823529630899429);
    float alphaWithHalo = alpha + ((1.0 - alpha) * halo);
    float greenWithHalo = greenChannel + ((1.0 - greenChannel) * halo);
    float blueBaseWithHalo = blueBase + ((1.0 - blueBase) * halo);

    float differenceHalf = (GaugeParameters.x - GaugeParameters.y) * 141.5;
    float midpoint = (GaugeParameters.x + GaugeParameters.y) * 141.5;
    float2 redlineGlowDistance = max(
        float2(
            8.0 - differenceHalf + abs(horizontal - midpoint),
            abs(vertical + 20.0) - 7.0),
        0.0);
    float redlineGlow = 1.0 - saturate((length(redlineGlowDistance) - 1.0) * 0.10000000149011612);
    float blueWithHalo = blueBaseWithHalo +
        (((alphaWithHalo * 0.30000001192092896) + 0.69999998807907104) * redlineGlow);

    float denominator = alphaWithHalo + 0.000009999999747378752;
    float3 straightColor = float3(alphaWithHalo, greenWithHalo, blueWithHalo) / denominator;
    return float4(straightColor * alphaWithHalo, alphaWithHalo) * sourceAlpha;
}
