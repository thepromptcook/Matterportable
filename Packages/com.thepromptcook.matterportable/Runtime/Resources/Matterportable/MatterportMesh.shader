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
            float _MP_PropId;    // which piece this is: 0 the room, i + 1 prop i (the depth cubes record the piece seen)

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
                float3 lAdx = ddx(lA), lAdy = ddy(lA), lBdx = ddx(lB), lBdy = ddy(lB);

                // The depth test: -1 where a moved (or hidden) prop hid the point from that sweep.
                float a = 1, b = 1;
                if (_MP_State.z > 0.5)
                {
                    a = MP_DepthVisibility(_MP_DepthA, sampler_MP_DepthA, mul((float3x3)_MP_SpaceRot, dA), length(dA), _MP_PropId);
                    b = MP_DepthVisibility(_MP_DepthB, sampler_MP_DepthB, mul((float3x3)_MP_SpaceRot, dB), length(dB), _MP_PropId);
                }
                // The room shows the clean photos (furniture painted out) where a moved prop was painted out or hid it,
                // and everywhere in "empty rooms".
                bool room = _MP_PropId < 0.5;
                bool cleanA = room && _MP_Clean.x > 0.5 && (a < 0 || _MP_Clean.z > 0.5 || MP_PaintedMoved(_MP_PaintedA, sampler_MP_PaintedA, lA));
                bool cleanB = room && _MP_Clean.y > 0.5 && (b < 0 || _MP_Clean.z > 0.5 || MP_PaintedMoved(_MP_PaintedB, sampler_MP_PaintedB, lB));
                float3 cA, cB;
                if (cleanA) cA = MP_SamplePano(_MP_CleanA, sampler_MP_CleanA, lA, lAdx, lAdy);
                else cA = MP_SamplePano(_MP_PanoA, sampler_MP_PanoA, lA, lAdx, lAdy);
                if (cleanB) cB = MP_SamplePano(_MP_CleanB, sampler_MP_CleanB, lB, lBdx, lBdy);
                else cB = MP_SamplePano(_MP_PanoB, sampler_MP_PanoB, lB, lBdx, lBdy);
                float visA = _MP_Valid.x * (a < 0 ? (cleanA ? 1 : 0) : a);
                float visB = _MP_Valid.y * (b < 0 ? (cleanB ? 1 : 0) : b);
                bool holeA = a < 0 && !cleanA; // no clean photo: fill from the photo's surroundings

                float wA, wB;
                MP_BlendWeights(_MP_State.x, visA, visB, wA, wB);
                float3 pano = (cA * wA + cB * wB) / max(wA + wB, 1e-5);
                float coverage = saturate(max(visA, visB));
                // Where a moved prop hid this from the photo, fill from the photo's surroundings rather than the baked texture.
                float3 fallback = tex;
                if (holeA && coverage < 1)
                {
                    float found;
                    float3 fill = MP_HoleFill(_MP_PanoA, sampler_MP_PanoA, _MP_DepthA, sampler_MP_DepthA, dA, _MP_RotA, _MP_PropId, found);
                    if (found > 0) fallback = fill;
                }
                float3 col = lerp(tex, lerp(fallback, pano, coverage), _MP_State.y);

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
