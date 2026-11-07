// Redraw EFT's optic lens immediately after OutputOptic draws it in SSAAPropagator. CommandBuffer.DrawRenderer uses the same lens mesh, HDR target and depth buffer, so EFT's depth prepass limits the blend to visible glass.
//
// Sample _AeroPreLens at the current pixel. PostUpscaleComposite.Append saved that frame before EFT drew the lens. The blend leaves opacity * lens + (1 - opacity) * pre-lens frame. Use EFT's GPU-adjusted NonJitteredProj so rasterisation and depth tests match the original lens draw.
Shader "Aero/LensBlend"
{
    Properties
    {
        _AeroPreLens ("Pre-lens frame", 2D) = "black" {}
        _AeroLensAlpha ("Share of the pre-lens frame (1 - opacity)", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _AeroPreLens;
            float4 _AeroPreLens_TexelSize;
            float _AeroLensAlpha;
            float4x4 NonJitteredProj;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float4 world = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0));
                o.pos = mul(NonJitteredProj, mul(UNITY_MATRIX_V, world));
                return o;
            }

            // The copy matches the target's size and memory layout. Use the fragment position directly on both top-down APIs such as D3D and bottom-up APIs such as GL.
            half4 frag(v2f i) : SV_Target
            {
                float2 uv = i.pos.xy * _AeroPreLens_TexelSize.xy;
                float3 pre = tex2Dlod(_AeroPreLens, float4(uv, 0, 0)).rgb;
                return half4(pre, _AeroLensAlpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
