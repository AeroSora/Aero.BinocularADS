// Composite the two premultiplied weapon images after upscale and before EFT draws the optic. Blend into the scene target without sampling it.
Shader "Aero/BinocularProductionComposite"
{
    Properties
    {
        _WeaponA ("Dominant weapon", 2D) = "black" {}
        _WeaponB ("Secondary weapon", 2D) = "black" {}
        _BlurA ("Dominant defocused colour", 2D) = "black" {}
        _BlurB ("Secondary defocused colour", 2D) = "black" {}
        _CoverageA ("Dominant coverage/depth", 2D) = "black" {}
        _CoverageB ("Secondary coverage/depth", 2D) = "black" {}
    }

    CGINCLUDE
    #include "AeroBinocularComposite.cginc"

    half4 frag(v2f input) : SV_Target
    {
        return AeroComposite(input.uv).combined;
    }
    ENDCG

    SubShader
    {
        Cull Off
        ZTest Always
        ZWrite Off
        Blend One OneMinusSrcAlpha, Zero One

        Pass
        {
            Name "AERO_POST_UPSCALE"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            ENDCG
        }
    }
    Fallback Off
}
