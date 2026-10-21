// PC Unity/VRChat rendering proof for shared stateless visuals, not a full installer.
Shader "ShockWave/Curve Visual Proof"
{
    Properties
    {
        _TouchX ("Touch X", Range(0,1)) = 0.35
        _TouchY ("Touch intensity", Range(0,1)) = 0.65
        _Touching ("Touch present", Float) = 1
        _LiveColor ("Live function color", Color) = (0.36,0.72,0.94,1)
        _HeldLevel0 ("Held level 0", Range(0,1)) = 0.5
        _HeldEnabled0 ("Held visible 0", Float) = 1
        _HeldColor0 ("Held color 0", Color) = (0.95,0.42,0.62,1)
        _HeldLevel1 ("Held level 1", Range(0,1)) = 0.25
        _HeldEnabled1 ("Held visible 1", Float) = 1
        _HeldColor1 ("Held color 1", Color) = (0.92,0.74,0.32,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "VRCFallback"="Unlit" }
        Pass
        {
            Cull Off
            ZWrite On
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "GraphCore.hlsli"
            #include "TouchVisuals.hlsli"
            float _TouchX, _TouchY, _Touching;
            float _HeldLevel0, _HeldEnabled0, _HeldLevel1, _HeldEnabled1;
            float4 _LiveColor, _HeldColor0, _HeldColor1;
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 size = float2(765, 352);
                float2 pixel = float2(i.uv.x, 1.0 - i.uv.y) * size;
                float3 color = float3(17,18,20) / 255.0;
                heldLevelLine(color, pixel, size, _HeldLevel0, _HeldEnabled0, _HeldColor0.rgb);
                heldLevelLine(color, pixel, size, _HeldLevel1, _HeldEnabled1, _HeldColor1.rgb);
                touchMarker(color, pixel, size, float2(_TouchX,_TouchY), _Touching, _LiveColor.rgb);
                return float4(color,1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
