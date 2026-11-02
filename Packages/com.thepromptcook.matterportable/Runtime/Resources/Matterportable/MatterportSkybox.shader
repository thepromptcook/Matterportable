// Background seen where there is no mesh (windows, outdoors, unscanned areas): the current panorama(s) by view
// direction, fading to a flat colour in dollhouse/floor plan views. Assigned to RenderSettings.skybox.
Shader "Matterportable/Skybox"
{
    Properties { }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma require 2darray
            #include "UnityCG.cginc"
            #include "MatterportCommon.hlsl"

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float3 lA = mul((float3x3)_MP_RotA, d);
                float3 lB = mul((float3x3)_MP_RotB, d);
                float3 lAdx = ddx(lA), lAdy = ddy(lA), lBdx = ddx(lB), lBdy = ddy(lB);
                // Through the holes a moved prop leaves in the scan, the clean photos, like the room's.
                bool cleanA = _MP_Clean.x > 0.5 && (_MP_Clean.z > 0.5 || MP_PaintedMoved(_MP_PaintedA, sampler_MP_PaintedA, lA));
                bool cleanB = _MP_Clean.y > 0.5 && (_MP_Clean.z > 0.5 || MP_PaintedMoved(_MP_PaintedB, sampler_MP_PaintedB, lB));
                float3 cA, cB;
                if (cleanA) cA = MP_SamplePano(_MP_CleanA, sampler_MP_CleanA, lA, lAdx, lAdy);
                else cA = MP_SamplePano(_MP_PanoA, sampler_MP_PanoA, lA, lAdx, lAdy);
                if (cleanB) cB = MP_SamplePano(_MP_CleanB, sampler_MP_CleanB, lB, lBdx, lBdy);
                else cB = MP_SamplePano(_MP_PanoB, sampler_MP_PanoB, lB, lBdx, lBdy);

                float wA, wB;
                MP_BlendWeights(_MP_State.x, _MP_Valid.x, _MP_Valid.y, wA, wB);
                float3 pano = (cA * wA + cB * wB) / max(wA + wB, 1e-5);
                float coverage = saturate(max(_MP_Valid.x, _MP_Valid.y)) * _MP_State.y;
                return fixed4(lerp(_MP_Background.rgb, pano, coverage), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
