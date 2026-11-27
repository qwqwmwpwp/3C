Shader "Hidden/ThreeC/CharacterForecastGhost"
{
    Properties { _Color ("Tint", Color) = (0.08,0.87,0.87,0.34) }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Cull Back
            ZTest LEqual
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Color;
            struct Varyings { float4 vertex : SV_POSITION; half shade : TEXCOORD0; };
            Varyings vert(appdata_base input)
            {
                Varyings output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                half3 normal = UnityObjectToWorldNormal(input.normal);
                output.shade = 0.68h + 0.32h * saturate(dot(normal, half3(0.35h, 0.82h, 0.45h)));
                return output;
            }
            fixed4 frag(Varyings input) : SV_Target { return fixed4(_Color.rgb * input.shade, _Color.a); }
            ENDCG
        }
    }
}
