// Matterport-style circles: sweep markers on the floor and the cursor that follows surfaces.
Shader "Matterportable/Marker"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 0.9)
        _Inner ("Ring inner radius", Range(0, 1)) = 0.72
        _Fill ("Fill opacity", Range(0, 1)) = 0.22
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Off
        Offset -2, -2

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Inner;
            float _Fill;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy * 2 - 1;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float r = length(i.uv);
                float aa = max(fwidth(r), 1e-4) * 1.5;
                float ring = smoothstep(_Inner - aa, _Inner, r) * (1 - smoothstep(1 - aa, 1, r));
                float fill = (1 - smoothstep(_Inner - aa, _Inner, r)) * _Fill;
                return fixed4(_Color.rgb, saturate(ring + fill) * _Color.a);
            }
            ENDCG
        }
    }
}
