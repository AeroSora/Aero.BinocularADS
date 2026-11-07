Shader "Aero/CoverageDepthCutout"
{
    Properties
    {
        _MainTex ("Alpha", 2D) = "white" {}
        _Cutoff ("Cutoff", Range(0,1)) = 0.5
        _Color ("Tint", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Pass
        {
            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask RGBA

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            // Write the renderer's part ID to blue for iron-sight focus. IDs start at 1. Zero means no weapon.
            float _PartId;

            sampler2D _MainTex;
            float4 _MainTex_ST;
            half _Cutoff;
            half4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float depth : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.depth = -UnityObjectToViewPos(v.vertex).z;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half alpha = tex2D(_MainTex, i.uv).a * _Color.a;
                clip(alpha - _Cutoff);
                return half4(1.0, i.depth, _PartId, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
