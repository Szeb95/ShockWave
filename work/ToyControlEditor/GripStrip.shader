Shader "ShockWave/Floating Grab Strip"
{
    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" "VRCFallback"="Hidden" }
        Cull Off ZWrite On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            #include "UnityCG.cginc"
            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata v)
            {
                v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o;
            }
            float4 frag(v2f i):SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p=(i.uv-0.5)*float2(28,352);
                float distance=length(float2(p.x,max(abs(p.y)-162,0)))-14;
                clip(-distance);
                float3 color=float3(56,58,64)/255.0;
                #ifndef UNITY_COLORSPACE_GAMMA
                color=GammaToLinearSpace(color);
                #endif
                return float4(color,1);
            }
            ENDCG
        }
    }
}
