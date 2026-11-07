// Fill eye B's exact silhouette with a depth-aware gather from eye A. This replaces premultiply for the derived eye.
//
// The eyes toe in at Convergence Distance, so disparity follows s*(1/d - 1/C) and changes sign at convergence. At s = 7 cm and C = 0.8 m, the rear sight needs about +730 px and the muzzle about -27 px. A constant shift cannot match both.
//
// _CoverageDepth comes from drawing through eye B's view-projection. _MainTex and _CoverageA come from eye A's Camera.Render. Keep sampling UVs in camera space and flip clip Y in vert(), as premultiply does, so the output aligns with _weaponA. _AeroFlipY also tells reconstruction whether row 0 is NDC Y = +1 (D3D, Metal) or -1 (OpenGL). C# sets it from SystemInfo.graphicsUVStartsAtTop.
//
// Both eyes use the main camera's projection. Four projection terms and a B-to-A view transform are enough to reconstruct the point. With depth d = -z, NDC x = P00*x/d - P02 and NDC y = P11*y/d - P12. Invert them as x = (ndc.x + P02)*d/P00 and y = (ndc.y + P12)*d/P11.

Shader "Aero/WeaponReproject"
{
    Properties
    {
        _MainTex ("Eye A colour", 2D) = "black" {}
        _CoverageDepth ("Eye B coverage and depth", 2D) = "black" {}
        _CoverageA ("Eye A coverage and depth", 2D) = "black" {}
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
            Name "AERO_REPROJECT"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _CoverageDepth;
            sampler2D _CoverageA;

            float _AeroFlipY;

            // (P00, P02, P11, P12) of the shared eye projection matrix.
            float4 _AeroProjTerms;
            // Transform from eye-B space to eye-A space with viewA * inverse(viewB).
            float4x4 _AeroBToA;
            // x is eye A's minimum coverage for accepting a sample. y is the horizontal search span in each direction in eye-A UVs. z flips eye-B coverage at lookup if its storage is mirrored. w is the depth tolerance in metres.
            float4 _AeroFillParams;

            // Keep the tap count fixed so [unroll] can expand the loop. D3D11 reported "loop does not appear to terminate in a timely manner (1024 iterations)" with a uniform bound. Use tex2Dlod because varying loops cannot supply implicit gradients. _AeroFillParams.y still controls the search width.
            #define AERO_FILL_TAPS 8

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

            // Texel row 0 is ndc.y = +1 on top-down platforms and ndc.y = -1 otherwise.
            float NdcYSign()
            {
                return lerp(1.0, -1.0, _AeroFlipY);
            }

            float2 UvToNdc(float2 uv)
            {
                float2 ndc = uv * 2.0 - 1.0;
                ndc.y *= NdcYSign();
                return ndc;
            }

            float2 NdcToUv(float2 ndc)
            {
                ndc.y *= NdcYSign();
                return ndc * 0.5 + 0.5;
            }

            half4 frag(v2f i) : SV_Target
            {
                // This coverage buffer runs through Graphics.ExecuteCommandBuffer without a camera. The attached AfterEverything pass can rely on the camera's render-target setup, but the standalone pass can have a different storage order on D3D and GL. Flip the coverage lookup if needed and keep the output UV unchanged for NDC reconstruction. Changing the projection would alter that math.
                float2 uvB = float2(i.uv.x, lerp(i.uv.y, 1.0 - i.uv.y, _AeroFillParams.z));
                half2 coverageB = tex2Dlod(_CoverageDepth, float4(uvB, 0.0, 0.0)).rg;

                // DrawRenderer bypasses culling and draws the silhouette through eye B's own view-projection. Reprojection only chooses the colour inside that silhouette.
                if (coverageB.r <= 0.0)
                    return half4(0.0, 0.0, 0.0, 0.0);

                float depthB = coverageB.g;
                float2 ndcB = UvToNdc(i.uv);

                float3 viewB;
                viewB.x = (ndcB.x + _AeroProjTerms.y) * depthB / _AeroProjTerms.x;
                viewB.y = (ndcB.y + _AeroProjTerms.w) * depthB / _AeroProjTerms.z;
                viewB.z = -depthB;

                float3 viewA = mul(_AeroBToA, float4(viewB, 1.0)).xyz;

                // A point behind eye A has no valid UV. Keep eye B's coverage and leave its colour unfilled.
                if (viewA.z >= -1e-4)
                    return half4(0.0, 0.0, 0.0, coverageB.r);

                float depthA = -viewA.z;
                float2 ndcA;
                ndcA.x = _AeroProjTerms.x * viewA.x / depthA - _AeroProjTerms.y;
                ndcA.y = _AeroProjTerms.z * viewA.y / depthA - _AeroProjTerms.w;
                float2 uvA = NdcToUv(ndcA);

                float threshold = _AeroFillParams.x;
                float depthTolerance = _AeroFillParams.w;

                // Check depth as well as coverage. A covered eye-A texel can belong to a different surface, such as the rear sight hiding the receiver. Read .rg in the same fetch to compare the observed depth in _CoverageA.g with the expected depthA.
                //
                // Reject both nearer occluders and farther samples from aliased silhouette texels. The tolerance is in metres. It must cover the depth change across one texel on a grazing barrel but still separate distinct weapon surfaces.
                half3 colourExact = half3(0.0, 0.0, 0.0);   // Depth agrees. Use this surface.
                half foundExact = 0.0;
                half3 colourAny = half3(0.0, 0.0, 0.0);     // Coverage exists, but the depth is for another surface.
                half foundAny = 0.0;

                bool insideA = uvA.x >= 0.0 && uvA.x <= 1.0 && uvA.y >= 0.0 && uvA.y <= 1.0;
                half2 sampleA = tex2Dlod(_CoverageA, float4(uvA, 0.0, 0.0)).rg;

                if (insideA && sampleA.r > threshold &&
                    abs(sampleA.g - depthA) <= depthTolerance)
                {
                    colourExact = tex2Dlod(_MainTex, float4(uvA, 0.0, 0.0)).rgb;
                    foundExact = 1.0;
                }
                else if (_AeroFillParams.y > 0.0)
                {
                    // Set the span to zero to isolate fill artefacts. Smears introduced by this walk disappear, while defects already in eye A's colour survive.
                    //
                    // Search left and right, nearest tap first, because the eyes are separated horizontally. The occluder at uvA can also cover nearby texels, so require matching depth to find where the requested surface reappears. Keep the nearest covered sample as a fallback for surfaces eye A genuinely cannot see, such as at a disoccluded silhouette.
                    //
                    // Use tapSpacing because step is an HLSL intrinsic.
                    float tapSpacing = _AeroFillParams.y / AERO_FILL_TAPS;

                    [unroll]
                    for (int t = 1; t <= AERO_FILL_TAPS; t++)
                    {
                        float offset = tapSpacing * t;

                        float2 right = float2(uvA.x + offset, uvA.y);
                        half2 coverRight = tex2Dlod(_CoverageA, float4(right, 0.0, 0.0)).rg;
                        if (right.x <= 1.0 && coverRight.r > threshold)
                        {
                            if (abs(coverRight.g - depthA) <= depthTolerance)
                            {
                                if (foundExact < 0.5)
                                {
                                    colourExact = tex2Dlod(_MainTex, float4(right, 0.0, 0.0)).rgb;
                                    foundExact = 1.0;
                                }
                            }
                            else if (foundAny < 0.5)
                            {
                                colourAny = tex2Dlod(_MainTex, float4(right, 0.0, 0.0)).rgb;
                                foundAny = 1.0;
                            }
                        }

                        float2 left = float2(uvA.x - offset, uvA.y);
                        half2 coverLeft = tex2Dlod(_CoverageA, float4(left, 0.0, 0.0)).rg;
                        if (left.x >= 0.0 && coverLeft.r > threshold)
                        {
                            if (abs(coverLeft.g - depthA) <= depthTolerance)
                            {
                                if (foundExact < 0.5)
                                {
                                    colourExact = tex2Dlod(_MainTex, float4(left, 0.0, 0.0)).rgb;
                                    foundExact = 1.0;
                                }
                            }
                            else if (foundAny < 0.5)
                            {
                                colourAny = tex2Dlod(_MainTex, float4(left, 0.0, 0.0)).rgb;
                                foundAny = 1.0;
                            }
                        }
                    }
                }

                half3 colour = foundExact > 0.5 ? colourExact : colourAny;

                // Premultiply by eye B's coverage even when the gather found no colour. The missing sample becomes a dark patch under defocus and the silhouette stays intact.
                return half4(colour * coverageB.r, coverageB.r);
            }
            ENDCG
        }
    }
    Fallback Off
}
