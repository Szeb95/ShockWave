#include "UnityCG.cginc"
#include "GraphCore.hlsli"
#include "TouchVisuals.hlsli"
sampler2D _Labels, _Glyphs, _GlyphMetrics, _Status, _ShockerLabels;
float _LinkOnline, _Clock;
struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
v2f vert(appdata v) { v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_OUTPUT(v2f,o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o); o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
float2 unpackBytes(float word) { float value=floor(saturate(word)*65535.0+0.5); return float2(fmod(value,256.0),floor(value/256.0)); }
float3 rgb(float rg,float b) { return float3(unpackBytes(rg)/255.0,b); }
float glyphAdvance(int code) {return round(tex2Dlod(_GlyphMetrics,float4((clamp(code,0,94)+0.5)/95.0,0.5,0,0)).r*255.0)*0.5;}
float glyph(float2 p,float2 origin,float2 size,int code)
{
    float scale=size.y/32.0;
    float2 local=p-origin;
    [branch] if(local.y<0 || local.y>=size.y || local.x< -12*scale || local.x>=24*scale || code<=0 || code>94) return 0;
    float2 uv=(p-origin+float2((24-glyphAdvance(code))*0.5*scale,0))/float2(24*scale,size.y);
    if(any(uv<0) || any(uv>=1) || code<=0 || code>94) return 0;
    float2 atlas=(float2(code%16,code/16)+float2(uv.x,1-uv.y))/float2(16,6);
    return tex2Dlod(_Glyphs,float4(atlas,0,0)).a;
}
float nameText(float2 p,float2 origin,float2 size,int row,bool functionName)
{
    float2 local=p-origin;
    if(local.x<0 || local.x>=208 || local.y<0 || local.y>=size.y) return 0;
    float alpha=0; float cursor=origin.x; float scale=size.y/32.0; float word=0;
    [loop] for(int character=0;character<18;character++)
    {
        if(character%2==0) word=floor((functionName ? rowFunction(row,character/2) : rowToy(row,character/2))*16383.0+0.5);
        int code=(int)(character%2==0 ? fmod(word,128.0) : floor(word/128.0));
        float advance=glyphAdvance(code)*scale;
        [branch] if(p.x>=cursor-2*scale && p.x<cursor+advance+2*scale) alpha=max(alpha,glyph(p,float2(cursor,origin.y),size,code));
        cursor+=advance;
        if(cursor>p.x+2*scale) break;
    }
    return alpha;
}
float number(float2 p,float2 origin,int value,float2 size)
{
    [branch] if(p.y<origin.y || p.y>=origin.y+size.y || p.x<origin.x-12*size.y/32.0 || p.x>=origin.x+2*size.x+24*size.y/32.0) return 0;
    float alpha=0;
    [unroll] for(int digit=0;digit<3;digit++)
    {
        int divisor=digit==0 ? 100 : digit==1 ? 10 : 1;
        if(digit<2 && value<divisor) continue;
        alpha=max(alpha,glyph(p,origin+float2(digit*size.x,0),size,16+(value/divisor)%10));
    }
    return alpha;
}
float statusText(float2 p,float2 origin,int flags)
{
    [branch] if(p.y<origin.y || p.y>=origin.y+14 || p.x<origin.x-6 || p.x>=origin.x+45) return 0;
    int holding[7]={40,79,76,68,73,78,71};
    int looping[7]={44,79,79,80,73,78,71};
    int editing[7]={37,68,73,84,73,78,71};
    float alpha=0; float cursor=origin.x;
    [unroll] for(int i=0;i<7;i++) {int code=(flags&4)!=0 ? looping[i] : (flags&2)!=0 ? editing[i] : holding[i];alpha=max(alpha,glyph(p,float2(cursor,origin.y),float2(5.8,14),code));cursor+=glyphAdvance(code)*14.0/32.0;}
    return alpha;
}
float batteryText(float2 p,float2 origin)
{
    [branch] if(p.y<origin.y || p.y>=origin.y+14 || p.x<origin.x-6 || p.x>=origin.x+43) return 0;
    int letters[7]={66,65,84,84,69,82,89}; float alpha=0; float cursor=origin.x;
    [unroll] for(int i=0;i<7;i++) {alpha=max(alpha,glyph(p,float2(cursor,origin.y),float2(5.8,14),letters[i]));cursor+=glyphAdvance(letters[i])*14.0/32.0;}
    return alpha;
}
float timeLabel(float2 p,float2 origin,int tick)
{
    [branch] if(p.y<origin.y || p.y>=origin.y+15 || p.x<origin.x-6 || p.x>=origin.x+44) return 0;
    float2 size=float2(6.3,15); float alpha=0;
    if(_Mode>0.5)
    {
        int tenths=(int)round(_Seconds*600*tick/4.0);
        alpha=number(p,origin,tenths/10,size);
        alpha=max(alpha,glyph(p,origin+float2(19,0),size,14));
        alpha=max(alpha,glyph(p,origin+float2(25,0),size,16+tenths%10));
        alpha=max(alpha,glyph(p,origin+float2(31,0),size,83));
    }
    else if(tick==4)
    {
        float second=glyphAdvance(46)*15.0/32.0; float third=second+glyphAdvance(47)*15.0/32.0;
        alpha=glyph(p,origin,size,46); alpha=max(alpha,glyph(p,origin+float2(second,0),size,47)); alpha=max(alpha,glyph(p,origin+float2(third,0),size,55));
    }
    else {alpha=glyph(p,origin,size,13); alpha=max(alpha,glyph(p,origin+float2(6.3,0),size,16+8-tick*2)); alpha=max(alpha,glyph(p,origin+float2(12.6,0),size,83));}
    return alpha;
}
void box(inout float3 color,float2 p,float4 rect,float radius,float3 fill) {color=lerp(color,fill,roundedBox(p,rect,radius));}
float4 curveQuartet(int base,int slot,bool background)
{
    int pair=base/2;
    float2 centre=unpackBytes(background ? loopWord(slot,pair) : curveWord(pair))/255.0;
    float2 next=unpackBytes(background ? loopWord(slot,min(pair+1,15)) : curveWord(min(pair+1,31)))/255.0;
    if(base%2!=0) return float4(centre.x,centre.y,next.x,next.y);
    float previous=base==0 ? centre.x : unpackBytes(background ? loopWord(slot,pair-1) : curveWord(pair-1)).y/255.0;
    return float4(previous,centre.x,centre.y,pair==(background ? 15 : 31) ? centre.y : next.x);
}
void loopLine(inout float3 color,float2 pixel,float2 size,int slot,float level,float3 ink,float phase,float cycles,float currentX)
{
    float at=frac(phase+saturate(pixel.x/size.x)*cycles); int base=clamp((int)(at*31),0,30);
    float4 levels=curveQuartet(base,slot,true); float width=size.x/(31*cycles); float x=pixel.x+(base/31.0-at)*size.x/cycles;
    float2 a=float2(x,(1-levels.y)*size.y), b=float2(x+width,(1-levels.z)*size.y);
    float distance=segmentDistance(pixel,a,b);
    if(base>0) distance=min(distance,segmentDistance(pixel,float2(x-width,(1-levels.x)*size.y),a));
    if(base<30) distance=min(distance,segmentDistance(pixel,b,float2(x+width*2,(1-levels.w)*size.y)));
    color=lerp(color,ink,lineCoverage(distance,1.15)*0.68);
    float2 marker=float2(clamp(currentX*size.x,3,size.x-3),(1-level)*size.y);
    color=lerp(color,ink,lineCoverage(length(pixel-marker),3.2)*0.90);
}
float4 frag(v2f input):SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    float2 p=float2(input.uv.x,1-input.uv.y)*float2(1240,608)+float2(0,100);
    // The existing rounded sections are the silhouette; the gap and margins
    // are transparent rather than a separate outer backdrop/frame.
    float outer=max(roundedBox(p,float4(24,105,300,598),8),roundedBox(p,float4(342,105,874,598),8)); clip(outer-0.5);
    float3 bg=float3(30,31,34)/255.0, side=float3(17,8,20)/255.0, panel=float3(43,45,49)/255.0, muted=float3(56,58,64)/255.0;
    float3 accent=float3(16,163,127)/255.0, white=float3(242,243,245)/255.0, faint=float3(181,186,193)/255.0;
    float online=step(0.5,min(_Online,_LinkOnline)); float3 color=bg;
    box(color,p,float4(24,105,300,598),8,side); box(color,p,float4(342,105,874,598),8,panel);
    float3 live=_Count>0 ? rgb(_LiveRG,_LiveB) : accent;
    int pages=max(1,(int)round(_Pages*43)); int page=clamp((int)round(_Page*42),0,pages-1); int count=(int)round(_Count*256);
    [branch] if(_ShockerPage>0.5)
    {
        box(color,p,float4(1052,127,152,42),8,muted); box(color,p,float4(658,127,130,42),8,muted);
        box(color,p,float4(176,176,132,60),8,lerp(panel,float3(237,106,106)/255.0,0.35));
        [branch] if(p.x>=37 && p.x<312 && p.y>=240 && p.y<684)
        {
            int row=clamp((int)((p.y-240)/64),0,6);
            if(row<min(count<=7 ? 7 : 6,count-page*6))
            {
                int flags=(int)unpackBytes(rowState(row)).y; float selected=(flags&2)!=0 ? 1 : 0; float connected=(flags&1)!=0 ? 1 : 0;
                float4 rect=float4(40,240+row*64,268,60); box(color,p,rect,8,lerp(panel,lerp(muted,accent,0.18),selected));
                color=lerp(color,connected>0.5 ? accent : faint,nameText(p,float2(54,rect.y+2),float2(9,22),row,false));
                color=lerp(color,faint,nameText(p,float2(54,rect.y+29),float2(7,16),row,true));
                color=lerp(color,lerp(muted,accent,selected),lineCoverage(length(p-(rect.xy+float2(243,20))),8));
            }
        }
        [unroll] for(int slider=0;slider<2;slider++)
        {
            float value=slider==0 ? _ShockIntensity : _ShockDuration; float x=454+slider*160;
            box(color,p,float4(x-5,240,10,350),5,muted); box(color,p,float4(x-5,590-value*350,10,value*350),5,accent);
            color=lerp(color,white,lineCoverage(length(p-float2(x,590-value*350)),14));
        }
        float ready=_ShockReady*online;
        color=lerp(color,lerp(muted,accent,_ShockArmed*ready),lineCoverage(length(p-float2(878,308)),68));
        color=lerp(color,lerp(muted,accent,(1-_ShockArmed)*ready),lineCoverage(length(p-float2(1088,308)),68));
        box(color,p,float4(810,430,346,64),10,muted); box(color,p,float4(810,516,346,64),10,muted);
        if(_ShockArmed>0.5 && online>0.5) box(color,p,float4(810,653,346*_ShockRemaining,4),2,accent);
        if(pages>1) {box(color,p,float4(40,648,52,36),8,muted);box(color,p,float4(256,648,52,36),8,muted);}
        float4 text=tex2D(_ShockerLabels,float2(p.x/1240,1-p.y/800));
        if(pages==1 && p.x<324 && p.y>=642 && p.y<684) text=0;
        if(_Pointer>0.5 && p.x>=658 && p.x<788 && p.y>=127 && p.y<169) text=tex2D(_ShockerLabels,float2(p.x/1240,1-(p.y+631)/800));
        color=lerp(color,text.rgb,text.a);
        color=lerp(color,white,number(p,float2(418,600),(int)round(_ShockIntensity*100),float2(13,30)));
        color=lerp(color,white,glyph(p,float2(463,600),float2(13,30),5));
        int tenths=(int)round(3+_ShockDuration*147);
        color=lerp(color,white,number(p,float2(558,600),tenths/10,float2(13,30)));
        color=lerp(color,white,glyph(p,float2(600,600),float2(13,30),14)); color=lerp(color,white,glyph(p,float2(614,600),float2(13,30),16+tenths%10)); color=lerp(color,white,glyph(p,float2(628,600),float2(13,30),83));
        if(pages>1) {color=lerp(color,faint,number(p,float2(141,654),page+1,float2(7,17)));color=lerp(color,faint,glyph(p,float2(168,654),float2(7,17),15));color=lerp(color,faint,number(p,float2(172,654),pages,float2(7,17)));}
        float4 badge=tex2D(_Status,float2((p.x-418)/232,1-(p.y-635+(online<0.5 ? 80 : _Online<0.75 ? 40 : 0))/160));
        if(p.x>=418 && p.x<650 && p.y>=635 && p.y<675) color=lerp(color,badge.rgb,badge.a);
        #ifndef UNITY_COLORSPACE_GAMMA
        color=GammaToLinearSpace(color);
        #endif
        return float4(color,1);
    }
    // Only the card under this pixel needs its dynamic state/text.
    [branch] if(p.x>=37 && p.x<312 && p.y>=240 && p.y<684)
    {
        int row=clamp((int)((p.y-240)/64),0,6);
        if(row<min(count<=7 ? 7 : 6,count-page*6))
        {
        float2 state=unpackBytes(rowState(row)); int flags=(int)state.y; float level=state.x/255;
        float3 ink=rgb(rowRG(row),rowB(row)); float selected=(flags&2)!=0 ? 1 : 0; float connected=(flags&1)!=0 ? 1 : 0;
        float4 rect=float4(40,240+row*64,268,60);
        box(color,p,rect,8,lerp(panel,lerp(muted,ink,0.18),selected)*lerp(0.65,1,connected));
        box(color,p,float4(rect.xy+float2(0,12),3,38),1,ink);
        float2 centre=rect.xy+float2(243,20); color=lerp(color,lerp(ink*0.45,ink,selected),lineCoverage(length(p-centre),8));
        if(selected>0.5) {float d=min(segmentDistance(p,centre+float2(-4,0),centre+float2(-1,3)),segmentDistance(p,centre+float2(-1,3),centre+float2(4,-3))); color=lerp(color,side,lineCoverage(d,1));}
        box(color,p,float4(rect.x+12,rect.y+54,244*level,2),1,ink);
        color=lerp(color,faint,nameText(p,float2(54,rect.y+1),float2(7.4,17),row,false));
        color=lerp(color,ink,nameText(p,float2(54,rect.y+17),float2(9,21),row,true));
        color=lerp(color,faint,statusText(p,float2(54,rect.y+38),flags));
        color=lerp(color,faint,number(p,float2(99,rect.y+38),(int)round(level*100),float2(5.8,14)));
        color=lerp(color,faint,glyph(p,float2(117,rect.y+38),float2(5.8,14),5));
        int battery=(int)round(rowBattery(row)*101)-1;
        if(battery>=0 && connected>0.5) {
        color=lerp(color,faint,batteryText(p,float2(146,rect.y+38)));
        color=lerp(color,faint,number(p,float2(189,rect.y+38),battery,float2(5.8,14)));
        color=lerp(color,faint,glyph(p,float2(207,rect.y+38),float2(5.8,14),5));
        }
        }
    }
    if(pages>1) { box(color,p,float4(40,648,52,36),8,muted); box(color,p,float4(256,648,52,36),8,muted); }
    box(color,p,float4(1052,127,152,42),8,muted);
    box(color,p,float4(365,127,130,42),8,lerp(muted,float3(0.45,0.17,0.22),step(0.25,_Record)));
    box(color,p,float4(499,127,155,42),8,lerp(muted,lerp(muted,accent,0.24),_Mode));
    box(color,p,float4(658,127,130,42),8,lerp(muted,lerp(muted,accent,0.28),_Pointer));
    float4 graph=float4(405,230,765,352); float mask=roundedBox(p,graph,8);
    [branch] if(mask>0 && _Selected>0 && _Count>0)
    {
        float2 g=(p-graph.xy)/graph.zw; float2 grid=abs(frac(g*float2(8,4)+0.5)-0.5)*graph.zw/float2(8,4);
        float3 graphColor=lerp(side,muted,1-smoothstep(0.35,0.9,min(grid.x,grid.y)));
        [loop] for(int slot=0;slot<16;slot++)
        {
            float2 state=unpackBytes(slotState(slot)); int flags=(int)state.y;
            [branch] if((flags&1)==0 || (flags&2)!=0) continue;
            float3 ink=rgb(slotRG(slot),slotB(slot));
            float cycles=slotCycles(slot)*300;
            [branch] if((flags&4)!=0 && cycles>0) loopLine(graphColor,p-graph.xy,graph.zw,slot,state.x/255,ink,slotPhase(slot),cycles,_Mode>0.5 ? _Phase : 1);
            else heldLevelLine(graphColor,p-graph.xy,graph.zw,state.x/255,1,ink);
        }
        [branch] if(g.x>=_CurveStart)
        {
            float sx=saturate(g.x)*63; int base=clamp((int)sx,0,62); float4 levels=curveQuartet(base,0,false); float value=lerp(levels.y,levels.z,frac(sx));
            graphColor=lerp(graphColor,lerp(side,live,0.30),step(1-value,g.y)*0.28);
            float2 local=p-graph.xy; float2 a=float2(base/63.0,1-levels.y)*graph.zw, b=float2((base+1)/63.0,1-levels.z)*graph.zw;
            float distance=segmentDistance(local,a,b);
            if(base>0) distance=min(distance,segmentDistance(local,float2((base-1)/63.0,1-levels.x)*graph.zw,a));
            if(base<62) distance=min(distance,segmentDistance(local,b,float2((base+2)/63.0,1-levels.w)*graph.zw));
            graphColor=lerp(graphColor,live,lineCoverage(distance,1.6));
        }
        if(_Mode>0.5) {float x=saturate(_Phase)*graph.z; graphColor=lerp(graphColor,live,lineCoverage(abs(p.x-graph.x-x),0.65)*0.35); graphColor=lerp(graphColor,live,lineCoverage(length(p-graph.xy-float2(x,(1-_Level)*graph.w)),4));}
        touchMarker(graphColor,p-graph.xy,graph.zw,float2(_TouchX,_TouchY),_Touch*online,live);
        color=lerp(color,graphColor*lerp(0.55,1,online),mask);
    }
    box(color,p,float4(40,176,132,60),8,muted); box(color,p,float4(176,176,132,60),8,lerp(panel,float3(237,106,106)/255.0,0.35)); box(color,p,float4(950,630,232,45),8,lerp(muted,lerp(muted,accent,0.08),online));
    float4 text=tex2D(_Labels,float2(p.x/1240,1-p.y/800));
    if((_Selected<=0 || _Count<=0) && p.x>=342 && p.y>=218 && p.y<620) {
        text=0; if(p.x>=405 && p.x<1170 && p.y>=382 && p.y<430) text=tex2D(_Labels,float2(p.x/1240,1-(p.y+338)/800));
    }
    if(pages==1 && p.x<324 && p.y>=642 && p.y<684) text=0;
    if(_Record>0.25 && p.x>=365 && p.x<495 && p.y>=127 && p.y<169) text=tex2D(_Labels,float2(p.x/1240,1-(p.y+631)/800));
    if(_Pointer>0.5 && p.x>=658 && p.x<788 && p.y>=127 && p.y<169) text=tex2D(_Labels,float2(p.x/1240,1-(p.y+631)/800));
    color=lerp(color,text.rgb,text.a);
    color=lerp(color,live,number(p,float2(637,174),(int)round(_Level*100),float2(16,36))); color=lerp(color,live,glyph(p,float2(685,174),float2(16,36),5));
    color=lerp(color,faint,number(p,float2(838,178),(int)round(_LoopCount*256),float2(7,17)));
    if(_Selected>0 && _Count>0) { [unroll] for(int tick=0;tick<5;tick++) color=lerp(color,faint,timeLabel(p,float2(399+tick*186,590),tick)); }
    color=lerp(color,faint,number(p,float2(41,687),count,float2(5.8,14)));
    if(pages>1) { color=lerp(color,faint,number(p,float2(141,654),page+1,float2(7,17))); color=lerp(color,faint,glyph(p,float2(168,654),float2(7,17),15)); color=lerp(color,faint,number(p,float2(172,654),pages,float2(7,17))); }
    int badgeRow=online<0.5 ? 2 : _Online<0.75 ? 1 : 0;
    if(p.x>=950 && p.x<1182 && p.y>=632 && p.y<672) {float4 badge=tex2D(_Status,float2((p.x-950)/232,1-(p.y-632+badgeRow*40)/160));color=lerp(color,badge.rgb,badge.a);}
    if(_Overflow>0) color=lerp(color,float3(1,0.65,0.3),roundedBox(p,float4(1148,188,16,16),8));
    // Compose the authored display palette and data textures in sRGB like the
    // desktop viewer, then supply linear output when Unity expects it.
    #ifndef UNITY_COLORSPACE_GAMMA
    color=GammaToLinearSpace(color);
    #endif
    return float4(color,1);
}
