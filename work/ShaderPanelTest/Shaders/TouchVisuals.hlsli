// Shared, stateless HLSL: usable by a Windows or Unity/PC-avatar wrapper.
// Coordinates are supplied by the host/Animator; this code never measures a finger.
void heldLevelLine(inout float3 color, float2 graphPixel, float2 graphSize,
                   float level, float enabled, float3 functionColor)
{
    if (enabled < 0.5 || level <= 0.00001) return;
    float distance = abs(graphPixel.y - (1.0 - saturate(level)) * graphSize.y);
    // Dim broken lines sit behind the live trace; an endpoint shows the exact level.
    float dash = step(frac(graphPixel.x / 14.0), 0.72);
    float coverage = lineCoverage(distance, 0.8) * (0.28 + dash * 0.28);
    color = lerp(color, functionColor, coverage);
    float endpoint = lineCoverage(length(graphPixel - float2(graphSize.x - 6.0, (1.0 - saturate(level)) * graphSize.y)), 2.7);
    color = lerp(color, functionColor, endpoint * 0.75);
}
void touchMarker(inout float3 color, float2 graphPixel, float2 graphSize,
                 float2 touchPosition, float touching, float3 liveColor)
{
    if (touching < 0.5) return;
    // The centre follows the exact supplied XY. A ring stays visible at the edges.
    float2 centre = float2(saturate(touchPosition.x), 1.0 - saturate(touchPosition.y)) * graphSize;
    float distance = length(graphPixel - centre);
    color = lerp(color, liveColor, exp(-distance * 0.10) * 0.15);
    color = lerp(color, liveColor, lineCoverage(abs(distance - 11.5), 1.1));
    color = lerp(color, liveColor, lineCoverage(distance, 7.5));
    color = lerp(color, float3(0.95, 0.95, 0.96), lineCoverage(distance, 2.1));
}
