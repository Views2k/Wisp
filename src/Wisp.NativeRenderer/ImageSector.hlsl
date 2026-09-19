#include "Quad.hlsl"

// UV-centred sector. X is the clockwise start in radians, Y its sweep,
// and Z the natural image aspect ratio (zero selects a square).
float4 sector_main(VertexOutput input) : SV_TARGET
{
    const float twoPi = 6.283185307179586;
    float sweep = MaterialParameters.y;
    if (sweep <= 0.0) return 0.0;
    float4 color = TintPremultiplied(InputTexture.Sample(InputSampler, input.uv));
    if (sweep >= twoPi) return color;
    float aspect = MaterialParameters.z > 0.0 ? MaterialParameters.z : 1.0;
    float2 coordinate = (input.uv - 0.5) * float2(aspect, 1.0);
    float start = MaterialParameters.x;
    float end = start + sweep;
    float2 first = float2(cos(start), sin(start));
    float2 last = float2(cos(end), sin(end));
    float fromStart = first.x * coordinate.y - first.y * coordinate.x;
    float beforeEnd = coordinate.x * last.y - coordinate.y * last.x;
    float startCoverage = saturate(0.5 + fromStart / max(fwidth(fromStart), 0.000001));
    float endCoverage = saturate(0.5 + beforeEnd / max(fwidth(beforeEnd), 0.000001));
    float coverage = sweep <= 3.141592653589793
        ? min(startCoverage, endCoverage) : max(startCoverage, endCoverage);
    return color * coverage;
}
