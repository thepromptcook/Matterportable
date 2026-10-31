// Copies a decoded face image (or one tile of it) into a slice of a panorama texture array.
Shader "Hidden/Matterportable/FaceBlit"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Rect ("Destination rect (x, y, w, h) in UV", Vector) = (0, 0, 1, 1)
    }
    SubShader
    {
        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Rect;

            fixed4 frag (v2f_img i) : SV_Target
            {
                float2 local = (i.uv - _Rect.xy) / _Rect.zw;
                if (local.x < 0 || local.y < 0 || local.x > 1 || local.y > 1) discard;
                return tex2D(_MainTex, local);
            }
            ENDCG
        }
    }
}
