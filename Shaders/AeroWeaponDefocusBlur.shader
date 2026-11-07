// Run two horizontal/vertical Gaussian pairs at half resolution for each eye. Five bilinear samples implement the nine-tap kernel by combining each neighbouring pair into one weighted fetch. The offsets are 0, ±1.38461538 and ±3.23076923 texels, with weights 0.2270270270, 0.3162162162 and 0.0702702703. Keep AERO_OFFSET_2 in sync with DefocusKernel.cs.
//
// Weight each tap by weapon coverage and divide by the surviving weight so the scene cannot bleed into the weapon. Return unpremultiplied colour and blurred coverage. The composite chooses alpha using Outline Softness and premultiplies once.
Shader "Aero/WeaponDefocusBlur"
{
    // Declare _AeroBlurSource only in CGPROGRAM. Later passes bind temporary targets by name ID with CommandBuffer.SetGlobalTexture because there is no RenderTexture object for Material.SetTexture.
    //
    // A Properties entry would give every material a local default that takes precedence over the global. The black default previously left coverage at zero and turned defocused regions black, including arms, suppressor tips and the whole second image with Blur The Second Image Fully enabled.

    SubShader
    {
        Cull Off
        ZTest Always
        ZWrite Off
        Blend Off

        Pass
        {
            Name "AERO_DEFOCUS_SEPARABLE"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            #define AERO_OFFSET_1 1.38461538
            #define AERO_OFFSET_2 3.23076923
            #define AERO_WEIGHT_0 0.2270270270
            #define AERO_WEIGHT_1 0.3162162162
            #define AERO_WEIGHT_2 0.0702702703

            sampler2D _AeroBlurSource;

            // C# passes the tap spacing in UVs as (n, 0) or (0, n). Globally bound textures do not populate _MainTex_TexelSize like material-bound textures do. All four passes share the same half resolution, so C# can fold the texel size into the uniform.
            float2 _AeroBlurStep;

            // C# sets this from SystemInfo.graphicsUVStartsAtTop for top-down render textures. Use it directly for orientation.
            float _AeroFlipY;

            // The first pass reads premultiplied RGB. Later passes read unpremultiplied RGB from this shader and need to apply coverage explicitly.
            float _SourceIsPremultiplied;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata input)
            {
                v2f output;
                float2 clip = input.vertex.xy * 2.0 - 1.0;
                clip.y = lerp(clip.y, -clip.y, _AeroFlipY);
                output.position = float4(clip, UNITY_NEAR_CLIP_VALUE, 1.0);
                output.uv = input.uv;
                return output;
            }

            void Accumulate(
                float2 uv, float weight, inout float3 colour, inout float coverage)
            {
                float4 texel = tex2D(_AeroBlurSource, saturate(uv));
                float texelCoverage = texel.a;
                float weighted = weight * texelCoverage;

                // A premultiplied sample already contains colour × coverage. Apply coverage here only for straight colour so both paths accumulate weight × coverage × colour. Dividing by the accumulated coverage recovers the average.
                colour += texel.rgb * lerp(weighted, weight, _SourceIsPremultiplied);
                coverage += weighted;
            }

            float4 frag(v2f input) : SV_Target
            {
                float2 texelStep = _AeroBlurStep;

                float3 colour = 0.0;
                float coverage = 0.0;
                Accumulate(input.uv, AERO_WEIGHT_0, colour, coverage);
                Accumulate(input.uv + texelStep * AERO_OFFSET_1, AERO_WEIGHT_1, colour, coverage);
                Accumulate(input.uv - texelStep * AERO_OFFSET_1, AERO_WEIGHT_1, colour, coverage);
                Accumulate(input.uv + texelStep * AERO_OFFSET_2, AERO_WEIGHT_2, colour, coverage);
                Accumulate(input.uv - texelStep * AERO_OFFSET_2, AERO_WEIGHT_2, colour, coverage);

                // Divide by the surviving weight. If every tap misses, the result is empty. A tiny weight can push HDR colour above the fp16 limit of 65504 and produce Inf or NaN, which EFT's upscaler spreads into a black square. Replace non-finite values with zero and clamp to range.
                float3 renormalised = colour / max(coverage, 0.0001);
                renormalised = all(isfinite(renormalised)) ? clamp(renormalised, 0.0, 60000.0) : float3(0.0, 0.0, 0.0);
                return float4(renormalised, saturate(coverage));
            }
            ENDCG
        }
    }
    Fallback Off
}
