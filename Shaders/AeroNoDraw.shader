Shader "Aero/NoDraw"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0
        }
    }
    Fallback Off
}
