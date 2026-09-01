// WPF-compatible port of the shipped ForzaUIMaterialAnalogNeedle PS 6.6 DXIL.
// FH6 authors the combustion and electric needles at different dimensions.
// WPF is shipped one bytecode variant per authored material so the only live
// constant remains the native blur amount.

sampler2D ImplicitInput : register(s0);
float BlurAmount : register(c0);

#ifdef ELECTRIC_NEEDLE
#define NATIVE_ASPECT_RATIO (180.0 / 94.0)
#else
#define NATIVE_ASPECT_RATIO (180.0 / 110.0)
#endif

float NativeAtan2(float y, float x)
{
    float angle = atan(y / x);
    if (x < 0.0)
    {
        angle += y >= 0.0 ? 3.1415927410125732 : -3.1415927410125732;
    }
    else if (x == 0.0)
    {
        angle = y < 0.0 ? -1.5707963705062866 : 1.5707963705062866;
    }
    return angle;
}

float4 main(float2 uv : TEXCOORD0) : COLOR0
{
    float inputOpacity = tex2D(ImplicitInput, uv).a;
    float x = uv.x + 0.2800000011920929;
    float y = (uv.y - 0.5) * NATIVE_ASPECT_RATIO;
    float yWidth = length(float2(ddx(y), ddy(y)));
    float angle = NativeAtan2(y, x);
    float radius = length(float2(x, y));
    float radialWidth = length(float2(ddx(radius), ddy(radius)));

    float lowerEdge = sin(angle - min(0.0, BlurAmount)) * radius;
    float upperEdge = sin(angle - max(0.0, BlurAmount)) * radius;
    float blurExpansion = pow(saturate(abs(y) / (lowerEdge + 0.019999999552965164 - upperEdge)), 2.5);
    blurExpansion *= 0.02000001072883606;

    float radialDistance = radius - (0.7070000171661377 - blurExpansion);
    float halfWidth = 0.029999999329447746 - (radialDistance * 0.017452005296945572);
    float lineCoverage;
    float shadowCoverage;

    if (abs(BlurAmount) >= 0.0010000000474974513)
    {
        float leadingOuter = (halfWidth + lowerEdge) / yWidth;
        float leadingInner = (halfWidth + upperEdge) / yWidth;
        float trailingOuter = (halfWidth - lowerEdge) / yWidth;
        float trailingInner = (halfWidth - upperEdge) / yWidth;
        float edgeSpan = lowerEdge - upperEdge;
        float clippedCenter = max(
            saturate(-(leadingInner * yWidth) / edgeSpan),
            saturate((trailingOuter * yWidth) / -edgeSpan));
        lineCoverage = (1.0 - clippedCenter) * saturate(leadingOuter) * saturate(trailingInner);

        float softLeadingOuter = (leadingOuter * 0.20000000298023224) + 0.6000000238418579;
        float softLeadingInner = (leadingInner * 0.20000000298023224) + 0.6000000238418579;
        float softTrailingOuter = (trailingOuter * 0.20000000298023224) + 0.6000000238418579;
        float softTrailingInner = (trailingInner * 0.20000000298023224) + 0.6000000238418579;
        float clippedShadowCenter = max(
            saturate(-softLeadingInner / (softLeadingOuter - softLeadingInner)),
            saturate(softTrailingOuter / (softTrailingOuter - softTrailingInner)));
        shadowCoverage =
            (1.0 - clippedShadowCenter) * saturate(softLeadingOuter) * saturate(softTrailingInner);
    }
    else
    {
        float leading = (halfWidth + y) / yWidth;
        float trailing = (halfWidth - y) / yWidth;
        lineCoverage = saturate(leading) * saturate(trailing);
        shadowCoverage =
            saturate((leading * 0.20000000298023224) + 0.6000000238418579) *
            saturate((trailing * 0.20000000298023224) + 0.6000000238418579);
    }

    float radialStart = saturate(radialDistance / radialWidth);
    float radialEnd = saturate((0.5730000138282776 - radialDistance) / radialWidth);
    float endMask = max(
        saturate((radialDistance - 0.27300000190734863) / radialWidth),
        saturate((radius - 0.7070000171661377) * 3.66300368309021));
    float commonMask = radialStart * radialEnd * endMask;
    float lineAlpha = commonMask * lineCoverage;
    float shadowAlpha = commonMask * 0.05000000074505806 * shadowCoverage;
    float alpha = max(lineAlpha, shadowAlpha);

    // The original default material color is opaque white. Its secondary
    // lobe contributes alpha only, producing the native dark motion trail.
    return float4(lineAlpha, lineAlpha, lineAlpha, alpha) * inputOpacity;
}
