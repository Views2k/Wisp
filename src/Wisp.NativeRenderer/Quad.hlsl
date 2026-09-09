cbuffer DrawConstants : register(b0)
{
    float4 TransformX; // axisXX, axisYX, originX, target width
    float4 TransformY; // axisXY, axisYY, originY, target height
    float4 UvBounds;
    float4 Tint;
    float4 MaterialParameters;
};
Texture2D<float4> InputTexture : register(t0);
SamplerState InputSampler : register(s0);

struct VertexOutput
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};
VertexOutput vs_main(float2 local : POSITION)
{
    VertexOutput output;
    float x = dot(float3(local, 1.0), TransformX.xyz);
    float y = dot(float3(local, 1.0), TransformY.xyz);
    output.position = float4((x / TransformX.w) * 2.0 - 1.0, 1.0 - (y / TransformY.w) * 2.0, 0.0, 1.0);
    output.uv = lerp(UvBounds.xy, UvBounds.zw, local);
    return output;
}
float4 TintPremultiplied(float4 color)
{
    return color * float4(Tint.rgb * Tint.a, Tint.a);
}
float4 image_main(VertexOutput input) : SV_TARGET
{
    return TintPremultiplied(InputTexture.Sample(InputSampler, input.uv));
}
