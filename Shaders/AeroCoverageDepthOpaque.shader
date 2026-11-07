Shader "Aero/CoverageDepthOpaque"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
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

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float depth : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.depth = -UnityObjectToViewPos(v.vertex).z;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                return half4(1.0, i.depth, _PartId, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
