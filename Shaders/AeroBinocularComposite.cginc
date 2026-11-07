// Keep the inputs, defocus and blend shared so the developer views can show individual terms of the same composite the release uses.
#ifndef AERO_BINOCULAR_COMPOSITE_INCLUDED
#define AERO_BINOCULAR_COMPOSITE_INCLUDED

#include "UnityCG.cginc"

sampler2D _WeaponA;
sampler2D _WeaponB;
// AeroWeaponDefocusBlur outputs unpremultiplied average colour. Use sharp coverage to premultiply it again and keep the original silhouette when edge softening is off.
sampler2D _BlurA;
sampler2D _BlurB;
sampler2D _CoverageA;
sampler2D _CoverageB;

// C# sets this to 1 for top-down render textures (D3D, Metal), otherwise 0.
float _AeroFlipY;

// Give overlap and the single-eye crescents separate opacities. With eye weights, alpha = saturate(wA*mA + wB*mB) and rgb = wA*cA + wB*cB. Raising both crescents then raises the overlap, where alpha can saturate at 1 while colour reaches 1.6× the weapon brightness.
//
// In overlap, use alpha = O and rgb = O*(ShareA*cA + ShareB*cB), with ShareA + ShareB = 1. In a crescent, use alpha = S1 and rgb = S1*c. The blend keeps rgb <= alpha in both regions.
float _OverlapOpacity;
float _SingleOpacity;
float _ShareA;
float _ShareB;

// Eased ADS fade. At zero, only the opaque dominant weapon contributes, so the transition starts and ends cleanly.
float _FadeT;

// Run the takeover crossfade before _FadeT. Scale the premultiplied composite by Takeover and keep the native weapon in the main pass until it reaches 1. The two renders have slightly different lighting, so this avoids a jump at aim-in.
float _Takeover;

float _DefocusEnabled;
float _FocusDepth;
float _DefocusClearBand;
float _DefocusFalloff;
float _ForceDefocusB;

// Keep weapon pixels within a 3D radius of EFT's front marker sharp. Blur beyond that radius so unrelated surfaces at the same depth stay blurred.
//
// _IronSightAnchor holds anchor UV in xy and camera depth in metres in z. _IronSightRadii holds sharp radius in x, fade distance in metres in y (positive when active) and curve exponent in z. _ViewFromNdc holds 1/P00, 1/P11, P02 and P12 from the eye projection.
float4 _IronSightAnchor;
float4 _IronSightRadii;
float4 _ViewFromNdc;

// C# sets a minimum blur fraction of 0 or 1 per sight type. An optic's reticle stays sharp in EFT's later pass, so blur the whole weapon. For irons, leave the floor at zero so the depth ramp can keep the front post sharp.
float _DefocusFloor;

// Edge softness from 0 to 1, scaled by the pixel's defocus. Zero keeps the original silhouette. The default of 1 blurs the outline with the interior so a heavily blurred weapon doesn't have an artificially sharp edge.
float _OutlineSoftness;

struct appdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct v2f
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

// _WeaponA is bound via Material.SetTexture. Only Unity's Blit and image-effect path provides a negative _TexelSize.y, so use _AeroFlipY from C#, which follows PPv2's graphicsUVStartsAtTop rule.
//
// Flip clip Y and leave the UVs alone, the same convention as AeroWeaponPremultiply.shader, so every source is sampled in one space.
v2f vert(appdata input)
{
    v2f output;
    float2 clip = input.vertex.xy * 2.0 - 1.0;
    clip.y = lerp(clip.y, -clip.y, _AeroFlipY);
    output.position = float4(clip, UNITY_NEAR_CLIP_VALUE, 1.0);
    output.uv = input.uv;
    return output;
}

// Blur fraction in [0,1] for mixing the sharp and blurred images. ResolveEye handles coverage separately as alpha.
float DefocusFraction(float4 coverageDepth)
{
    float outside = max(
        0.0,
        abs(coverageDepth.g - _FocusDepth) - _DefocusClearBand);
    float ramp = saturate(outside / max(_DefocusFalloff, 0.0001));
    return saturate(max(ramp, _DefocusFloor) * _DefocusEnabled);
}

// Apply this floor only to the secondary image. The off eye doesn't resolve the weapon while aiming. If a sharp patch from that image overlaps blur from the dominant eye, the result can look sharp. A uniform floor removes that overlap artefact.
float SecondaryDefocus(float4 coverageDepth)
{
    return max(DefocusFraction(coverageDepth),
               saturate(_ForceDefocusB * _DefocusEnabled));
}

// Reconstruct eye-A view position from camera depth. With d = -z, invert ndc.x = (P00*x + P02*z) / -z to get x = (ndc.x + P02) * d / P00. Use the same form for Y.
float3 EyeViewPosition(float2 quadUV, float depth)
{
    float2 ndc = quadUV * 2.0 - 1.0;
    return float3((ndc + _ViewFromNdc.zw) * _ViewFromNdc.xy, 1.0) * depth;
}

// With iron-sight focus active, keep pixels within the anchor's eye-space 3D radius sharp. Beyond it, ramp up blur as pow(t, curve) over the fade distance.
float IronSightDefocus(float2 quadUV, float4 coverageDepth)
{
    float onWeapon = step(0.5, coverageDepth.r);
    float distanceM = distance(EyeViewPosition(quadUV, coverageDepth.g),
                               EyeViewPosition(_IronSightAnchor.xy, _IronSightAnchor.z));
    float t = saturate((distanceM - _IronSightRadii.x) / max(_IronSightRadii.y, 1e-4));
    float ramp = pow(t, max(_IronSightRadii.z, 0.01));
    float clear = onWeapon * (1.0 - ramp);
    return saturate((1.0 - clear) * _DefocusEnabled);
}

// Keep eye colour in the finite fp16 range (max 65504). Dividing HDR colour by coverage as low as 1e-4 can produce Inf and then NaN. EFT's upscaler and bloom can spread that into a black square. Replace non-finite values with zero.
float3 AeroFinite(float3 c)
{
    return all(isfinite(c)) ? clamp(c, 0.0, 60000.0) : float3(0.0, 0.0, 0.0);
}

// Blur stores unpremultiplied RGB and blurred coverage in A. Choose the final alpha first, then premultiply by it. Scale softness by the blur fraction so sharp pixels keep their original silhouette.
//
// Keep both sides of the blur ramp. At radius 10 it spans roughly ±14 full-resolution pixels. Keeping just the inward erosion means a 30 px barrel never reaches alpha 1 and can disappear over a dark background with Blend One OneMinusSrcAlpha. Including the outer halo gives that lost coverage back as a wider, partly transparent silhouette.
float4 ResolveEye(float4 sharp, float4 blur, float fraction)
{
    // Blend in unpremultiplied colour space, then multiply once by the emitted alpha. The blur pass already divided by accumulated coverage, so only the sharp colour needs to be divided here.
    float3 sharpColour = AeroFinite(sharp.rgb / max(sharp.a, 1e-4));

    // Use the blur colour directly outside the sharp silhouette. The sharp texture is black there, and blending from it would darken the halo we just restored.
    float colourBlend = lerp(1.0, fraction, sharp.a);
    float3 colour = lerp(sharpColour, blur.rgb, colourBlend);

    // At softness 0, alpha follows sharp coverage. At 1, it follows blurred coverage on both sides of the original outline.
    float alpha = lerp(sharp.a, blur.a, saturate(_OutlineSoftness * fraction));
    return float4(colour * alpha, alpha);
}

// Keep the intermediate terms for the development views. Production outputs combined.
struct AeroCompositeTerms
{
    float4 combined;
    float4 eyeA;
    float4 eyeB;
    float4 coverageA;
    float4 coverageB;
    float defocusA;
    float defocusB;
};

AeroCompositeTerms AeroComposite(float2 uv)
{
    AeroCompositeTerms terms;
    float4 sharpA = tex2D(_WeaponA, uv);
    float4 sharpB = tex2D(_WeaponB, uv);
    terms.coverageA = tex2D(_CoverageA, uv);
    terms.coverageB = tex2D(_CoverageB, uv);

    float4 blurA = tex2D(_BlurA, uv);
    float4 blurB = tex2D(_BlurB, uv);

    float defocusA = _IronSightRadii.y > 0.0
        ? IronSightDefocus(uv, terms.coverageA)
        : DefocusFraction(terms.coverageA);
    float defocusB = SecondaryDefocus(terms.coverageB);
    // Ease defocus with the ADS fade so the weapon starts as sharp as hipfire and blurs over the same Fade Time as the second image.
    terms.defocusA = defocusA * _FadeT;
    terms.defocusB = defocusB * _FadeT;

    terms.eyeA = ResolveEye(sharpA, blurA, terms.defocusA);
    terms.eyeB = ResolveEye(sharpB, blurB, terms.defocusB);

    // Scale the secondary colour and coverage with the fade. At zero the blend reduces to the opaque dominant weapon.
    float4 fadedB = terms.eyeB * _FadeT;

    float overlap = lerp(1.0, _OverlapOpacity, _FadeT);
    float single  = lerp(1.0, _SingleOpacity,  _FadeT);
    float shareA  = lerp(1.0, _ShareA, _FadeT);
    float shareB  = lerp(0.0, _ShareB, _FadeT);

    float mA = terms.eyeA.a;
    float mB = fadedB.a;
    float both = mA * mB;

    float kA = overlap * shareA * mB + single * (1.0 - mB);
    float kB = overlap * shareB * mA + single * (1.0 - mA);

    float4 combined;
    combined.rgb = AeroFinite(kA * terms.eyeA.rgb + kB * fadedB.rgb);
    combined.a = saturate(overlap * both + single * (mA + mB - 2.0 * both));
    terms.combined = combined * _Takeover;
    return terms;
}

#endif
