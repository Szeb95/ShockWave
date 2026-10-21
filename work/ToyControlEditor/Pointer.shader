Shader "ShockWave/Toy Pointer"
{
    Properties
    {
        _Disc ("Surface dot",Float)=0
        [HideInInspector] _ZWrite ("Depth write",Float)=1
        [HideInInspector] _OffsetFactor ("Depth bias slope",Float)=0
        [HideInInspector] _OffsetUnits ("Depth bias units",Float)=0
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" "VRCFallback"="Hidden" }
        Cull Off
        ZWrite [_ZWrite]
        Offset [_OffsetFactor], [_OffsetUnits]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            #include "UnityCG.cginc"
            float _Disc;
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
                if(_Disc>.5) clip(.5-length(i.uv-.5));
                float3 color=float3(16,163,127)/255.0;
                #ifndef UNITY_COLORSPACE_GAMMA
                color=GammaToLinearSpace(color);
                #endif
                return float4(color,1);
            }
            ENDCG
        }
    }
}
