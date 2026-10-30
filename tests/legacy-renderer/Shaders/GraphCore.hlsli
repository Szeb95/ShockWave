// Shared graph drawing math. No Unity or Windows bindings in this file.
float roundedBox(float2 p, float4 bounds, float radius)
{
    float2 q = abs(p - (bounds.xy + bounds.zw * 0.5)) - bounds.zw * 0.5 + radius;
    float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
    return 1.0 - smoothstep(-0.7, 0.7, d);
}
float segmentDistance(float2 p, float2 a, float2 b)
{
    float2 delta = b - a;
    float t = saturate(dot(p - a, delta) / max(dot(delta, delta), 0.00001));
    return length(p - (a + delta * t));
}
float lineCoverage(float distance, float halfWidth)
{
    return 1.0 - smoothstep(halfWidth - 0.6, halfWidth + 0.6, distance);
}
