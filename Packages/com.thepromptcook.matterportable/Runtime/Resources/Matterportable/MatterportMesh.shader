// Renders the scanned mesh. Inside: the current sweep's panorama is projected onto the geometry (exactly the
// photo when the camera sits at the sweep) and cross-faded to the next sweep during moves, with occlusion
// awareness. Dollhouse/floor plan: the baked mesh texture. Unlit - all lighting is in the photos.
// Works in the Built-in pipeline and URP.
Shader "Matterportable/Mesh"
{
    Properties
    {
        _MainTex ("Mesh Texture", 2D) = "gray" {}
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma require 2darray
            #include "UnityCG.cginc"
            #include "MatterportCommon.hlsl"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MP_PropTint; // Walk mode: the prop under the pointer, or every prop in the "Props" view (alpha: how much)

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 wpos : TEXCOORD1;
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // Photos are projected from where the vertex was scanned (the space's transform, set every frame; before
                // that the object's own), so a moved prop keeps its own pixels.
                float4x4 space = _MP_SpaceToWorld._m33 > 0.5 ? _MP_SpaceToWorld : unity_ObjectToWorld;
                o.wpos = mul(space, v.vertex).xyz;
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 tex = tex2D(_MainTex, i.uv).rgb;

                float3 dA = i.wpos - _MP_PosA.xyz;
                float3 dB = i.wpos - _MP_PosB.xyz;
                float3 lA = mul((float3x3)_MP_RotA, dA);
                float3 lB = mul((float3x3)_MP_RotB, dB);
                float3 cA = MP_SamplePano(_MP_PanoA, sampler_MP_PanoA, lA, ddx(lA), ddy(lA));
                float3 cB = MP_SamplePano(_MP_PanoB, sampler_MP_PanoB, lB, ddx(lB), ddy(lB));

                float visA = _MP_Valid.x;
                float visB = _MP_Valid.y;
                if (_MP_State.z > 0.5)
                {
                    visA *= MP_DepthVisibility(_MP_DepthA, sampler_MP_DepthA, mul((float3x3)_MP_SpaceRot, dA), length(dA));
                    visB *= MP_DepthVisibility(_MP_DepthB, sampler_MP_DepthB, mul((float3x3)_MP_SpaceRot, dB), length(dB));
                }

                float wA, wB;
                MP_BlendWeights(_MP_State.x, visA, visB, wA, wB);
                float3 pano = (cA * wA + cB * wB) / max(wA + wB, 1e-5);
                float coverage = saturate(max(visA, visB));
                float3 col = lerp(tex, lerp(tex, pano, coverage), _MP_State.y);

                int view = (int)(_MP_State.w + 0.5);
                if (view == 1) col = tex;
                else if (view == 2) col = pano;
                else if (view == 3) col = float3(visA, visB, 0);
                else if (view == 4) col = float3(wA, wB, 1 - coverage);

                clip(_MP_Valid.z - i.wpos.y);
                return fixed4(lerp(col, _MP_PropTint.rgb, _MP_PropTint.a), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
