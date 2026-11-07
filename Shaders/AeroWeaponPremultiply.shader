// Isolate the weapon by multiplying eye colour by coverage.
//
// Blit can flip its quad for _MainTex and leave other textures mirrored at the same UV. Draw the shared [0,1] quad with CommandBuffer.DrawMesh instead. Leave the source UVs alone and flip clip Y when _AeroFlipY is set. C# uses SystemInfo.graphicsUVStartsAtTop, the same rule as PPv2's ApplyDefaultFlip.
//
// The composite and the reprojection use the same clip-space flip, so every pass in the chain samples its sources at untouched UVs.

Shader "Aero/WeaponPremultiply"
{
    Properties
    {
        _MainTex ("Eye colour", 2D) = "black" {}
        _CoverageDepth ("Coverage and depth", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass
        {
            // Name the pass so the bundle build log can verify it survived stripping.
            Name "AERO_PREMULTIPLY"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _CoverageDepth;

            // C# sets this to 1 for top-down render textures (D3D, Metal), otherwise 0.
            float _AeroFlipY;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float2 clip = v.vertex.xy * 2.0 - 1.0;
                clip.y = lerp(clip.y, -clip.y, _AeroFlipY);
                o.pos = float4(clip, UNITY_NEAR_CLIP_VALUE, 1.0);
                o.uv = v.uv;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // Sample colour and coverage at the same UV. They came from the same camera render.
                half coverage = tex2D(_CoverageDepth, i.uv).r;
                half3 colour = tex2D(_MainTex, i.uv).rgb;
                return half4(colour * coverage, coverage);
            }
            ENDCG
        }
    }
    Fallback Off
}
