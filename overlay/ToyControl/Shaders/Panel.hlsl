#include "GraphCore.hlsli"
#include "TouchVisuals.hlsli"

// Windows host bindings. The graph math above can be reused by a Unity wrapper.
struct FunctionVisual { float4 state; float4 color; float4 loopView; };
cbuffer Panel : register(b0)
{
    float4 ResolutionTime; // pixel width/height, seconds, current input level
    float4 State;          // touch, hover target, destination, visible row count
    float4 PageInfo;       // page, page count, total function rows, accepted OSC packets
    float4 TouchData;      // normalized touch XY (Y=intensity), touching, all function count
    float4 LiveColor;      // one selected function's color, or white for a shared input
    float4 PatternData;    // loop preview, loop phase, record armed, recording
    float4 Rows[7];        // requested level, selected for drawing, connected, looping
    float4 RowColors[7];
    FunctionVisual Functions[256]; // all pages: state = level, selected, connected, looping
    float4 ShockData; // page, intensity, duration position, confirmation time / 3
    float4 ShockFlags; // armed, ready, reserved, reserved
};
Texture2D<float4> History : register(t0);
Texture2D<float4> Labels : register(t1);
Texture2D<float4> Pattern : register(t2);
Texture2D<float> LoopPatterns : register(t3); // one normalized recorded clip per function row
SamplerState LinearClamp : register(s0);

struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
Vertex VSMain(uint id : SV_VertexID)
{
    Vertex o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.position = float4(o.uv.x * 2 - 1, 1 - o.uv.y * 2, 0, 1);
    return o;
}

// Real GPU history: ping-pong textures retain old samples and shift one cell.
// 240 samples at 30 Hz = eight seconds. Green marks initialized samples.
float4 HistoryPS(Vertex input) : SV_Target
{
    int x = (int)input.position.x;
    return x == 239 ? float4(ResolutionTime.w, LiveColor.a, 0, 1) : History.Load(int3(x + 1, 0, 0));
}

// Match the ShockWave charcoal/green palette while keeping rounded geometry.
static const float3 Accent = float3(16, 163, 127) / 255.0;
static const float3 Background = float3(30, 31, 34) / 255.0;
static const float3 Sidebar = float3(17, 18, 20) / 255.0;
static const float3 PanelColor = float3(43, 45, 49) / 255.0;
static const float3 Muted = float3(56, 58, 64) / 255.0;
static const float3 Faint = float3(128, 132, 142) / 255.0;
void box(inout float3 color, float2 p, float4 rect, float radius, float3 fill)
{
    color = lerp(color, fill, roundedBox(p, rect, radius));
}
void button(inout float3 color, float2 p, float4 rect, int id, float3 fill)
{
    float hover = abs(State.y - id) < 0.5 ? 1 : 0;
    box(color, p, rect, 10, fill + hover * 0.045);
}

void backgroundLoop(inout float3 color, float2 pixel, float2 size, int row, FunctionVisual function)
{
    // loopView = phase at the left edge, visible cycles, current-time X, reserved.
    float phase = frac(function.loopView.x + saturate(pixel.x / size.x) * function.loopView.y);
    int base = clamp((int)(phase * 239.0), 0, 238);
    float distance = 1000;
    [unroll] for (int j = -1; j <= 1; j++)
    {
        int ix = clamp(base + j, 0, 238);
        float a = LoopPatterns.Load(int3(ix, row, 0));
        float b = LoopPatterns.Load(int3(ix + 1, row, 0));
        float2 pa = float2(pixel.x + (ix / 239.0 - phase) * size.x / function.loopView.y, (1.0 - a) * size.y);
        float2 pb = float2(pixel.x + ((ix + 1) / 239.0 - phase) * size.x / function.loopView.y, (1.0 - b) * size.y);
        // Do not bridge the end/start discontinuity with a false diagonal.
        distance = min(distance, segmentDistance(pixel, pa, pb));
    }
    color = lerp(color, function.color.rgb, lineCoverage(distance, 1.15) * 0.68);
    float2 marker = float2(clamp(function.loopView.z * size.x, 3, size.x - 3), (1.0 - function.state.x) * size.y);
    color = lerp(color, function.color.rgb, lineCoverage(length(pixel - marker), 3.2) * 0.90);
}

float4 PSMain(Vertex input) : SV_Target
{
    float2 p = input.uv * float2(1240, 608) + float2(0, 100);
    float3 color = Background;
    box(color, p, float4(24, 105, 300, 598), 18, Sidebar);
    box(color, p, float4(342, 105, 874, 598), 18, PanelColor);


    [unroll] for (int row = 0; row < 7; row++)
    {
        if (row >= (int)State.w) break;
        float4 rect = float4(40, 240 + row * 64, 268, 60);
        float selected = Rows[row].y;
        float connected = Rows[row].z;
        float3 functionColor = ShockData.x>0.5 ? Accent : RowColors[row].rgb;
        float3 fill = lerp(PanelColor, lerp(Muted, functionColor, 0.18), selected);
        button(color, p, rect, row + 1, fill * lerp(0.65, 1, connected));
        box(color, p, float4(rect.xy + float2(0, 12), 3, 38), 1, functionColor * lerp(0.7, 1, connected));
        float2 centre = rect.xy + float2(243, 20);
        float circle = lineCoverage(length(p - centre), 8);
        color = lerp(color, selected > 0.5 ? functionColor : lerp(Faint * 0.5, functionColor, 0.25), circle);
        if (selected > 0.5)
        {
            float check = min(segmentDistance(p, centre + float2(-4, 0), centre + float2(-1, 3)), segmentDistance(p, centre + float2(-1, 3), centre + float2(4, -3)));
            color = lerp(color, Sidebar, lineCoverage(check, 1));
        }
        box(color, p, float4(rect.x + 12, rect.y + 54, 244 * Rows[row].x, 2), 1, functionColor * lerp(0.75, 1, selected));
    }
    if(PageInfo.y>1) { button(color, p, float4(40, 648, 52, 36), 15, Muted);
    button(color, p, float4(256, 648, 52, 36), 16, Muted); }
    button(color,p,float4(1052,127,152,42),29,Muted);
    if(ShockData.x>0.5)
    {
        button(color,p,float4(658,127,130,42),28,Muted);
        button(color,p,float4(176,176,132,60),13,lerp(PanelColor,float3(237,106,106)/255.0,0.35));
        [unroll] for(int slider=0;slider<2;slider++)
        {
            float value=slider==0 ? ShockData.y : ShockData.z; float x=454+slider*160;
            box(color,p,float4(x-5,240,10,350),5,Muted);
            box(color,p,float4(x-5,590-value*350,10,value*350),5,Accent);
            color=lerp(color,float3(242,243,245)/255.0,lineCoverage(length(p-float2(x,590-value*350)),14));
        }
        color=lerp(color,lerp(Muted,Accent,ShockFlags.x*ShockFlags.y),lineCoverage(length(p-float2(878,308)),68));
        color=lerp(color,lerp(Muted,Accent,(1-ShockFlags.x)*ShockFlags.y),lineCoverage(length(p-float2(1088,308)),68));
        button(color,p,float4(810,430,346,64),34,Muted); button(color,p,float4(810,516,346,64),35,Muted);
        if(ShockFlags.x>0.5) box(color,p,float4(810,653,346*ShockData.w,4),2,Accent);
        float4 text=Labels.SampleLevel(LinearClamp,p/float2(1240,800),0); color=lerp(color,text.rgb,text.a);
        return float4(color,1);
    }
    float recording = max(PatternData.z,PatternData.w);
    button(color,p,float4(365,127,130,42),25,lerp(Muted,float3(0.45,0.17,0.22),recording));
    button(color,p,float4(499,127,155,42),26,lerp(Muted,lerp(Muted,Accent,0.24),PatternData.x));
    button(color,p,float4(658,127,130,42),28,Muted);


    float4 graph = float4(405, 230, 765, 352);
    float mask = roundedBox(p, graph, 8);
    if (mask > 0 && LiveColor.a > 0.5)
    {
        float2 g = (p - graph.xy) / graph.zw;
        float3 graphColor = Sidebar;
        float2 grid = abs(frac(g * float2(8, 4) + 0.5) - 0.5) * graph.zw / float2(8, 4);
        float gridLine = 1 - smoothstep(0.35, 0.9, min(grid.x, grid.y));
        graphColor = lerp(graphColor, Muted, gridLine);
        [loop] for (int f = 0; f < min((int)TouchData.w, 256); f++)
        {
            FunctionVisual function = Functions[f];
            float held = function.state.z * (1.0 - function.state.y);
            if (held > 0.5 && function.state.w > 0.5 && function.loopView.y > 0)
                backgroundLoop(graphColor, p - graph.xy, graph.zw, f, function);
            else
                heldLevelLine(graphColor, p - graph.xy, graph.zw, function.state.x, held, function.color.rgb);
        }
        float sampleX = saturate(g.x) * 239;
        int base = clamp((int)sampleX, 0, 238);
        float4 a = PatternData.x>0.5 ? Pattern.Load(int3(base,0,0)) : History.Load(int3(base, 0, 0));
        float4 b = PatternData.x>0.5 ? Pattern.Load(int3(base+1,0,0)) : History.Load(int3(base + 1, 0, 0));
        float value = lerp(a.x, b.x, frac(sampleX));
        if (a.y > 0.5 && b.y > 0.5)
        {
            float under = step(1 - value, g.y);
            graphColor = lerp(graphColor, lerp(Sidebar, LiveColor.rgb, 0.30), under * 0.28);
            float distance = 1000;
            // Check adjacent segments too so steep changes have consistent width.
            [unroll] for (int j = -1; j <= 1; j++)
            {
                int ix = clamp(base + j, 0, 238);
                float4 va = PatternData.x>0.5 ? Pattern.Load(int3(ix,0,0)) : History.Load(int3(ix, 0, 0));
                float4 vb = PatternData.x>0.5 ? Pattern.Load(int3(ix+1,0,0)) : History.Load(int3(ix + 1, 0, 0));
                if (va.y > 0.5 && vb.y > 0.5)
                {
                    float2 pa = float2(ix / 239.0, 1 - va.x) * graph.zw;
                    float2 pb = float2((ix + 1) / 239.0, 1 - vb.x) * graph.zw;
                    distance = min(distance, segmentDistance(p - graph.xy, pa, pb));
                }
            }
            graphColor += LiveColor.rgb * exp(-distance * 0.22) * 0.08;
            graphColor = lerp(graphColor, LiveColor.rgb, lineCoverage(distance, 1.6));
        }
        if(PatternData.x>0.5)
        {
            float playX=saturate(PatternData.y)*graph.z;
            graphColor=lerp(graphColor,LiveColor.rgb,lineCoverage(abs(p.x-graph.x-playX),0.65)*0.35);
            float2 playPoint=float2(playX,(1.0-ResolutionTime.w)*graph.w);
            graphColor=lerp(graphColor,LiveColor.rgb,lineCoverage(length(p-graph.xy-playPoint),4.0));
        }
        touchMarker(graphColor, p - graph.xy, graph.zw, TouchData.xy, TouchData.z, LiveColor.rgb);
        color = lerp(color, graphColor, mask);
    }
    button(color, p, float4(40, 176, 132, 60), 11, Muted);

    button(color, p, float4(176, 176, 132, 60), 13, lerp(PanelColor, float3(237, 106, 106) / 255.0, 0.35));
    box(color, p, float4(950, 630, 232, 45), 10, Muted);
    float4 text = Labels.SampleLevel(LinearClamp, p / float2(1240, 800), 0);
    color = lerp(color, text.rgb, text.a);
    float outer = roundedBox(p, float4(0,100,1240,608),8);
    return float4(lerp(Background,color,outer), outer);
}
