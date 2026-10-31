#!/usr/bin/env python3
"""Syntax/type-checks the package's shader programs without Unity.

Extracts each CGPROGRAM block, replaces Unity's built-in includes with small stand-ins, and compiles the
vertex and fragment entry points with glslangValidator's HLSL front end (apt install glslang-tools).
This catches HLSL errors early; it does not replace testing in Unity.

Usage: python3 tools/check_shaders.py
"""
import pathlib
import re
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
SHADERS = ROOT / "Packages/com.thepromptcook.matterportable/Runtime/Resources/Matterportable"

UNITY_STUB = r"""
#define fixed float
#define fixed2 float2
#define fixed3 float3
#define fixed4 float4
float4x4 unity_ObjectToWorld;
float4x4 unity_MatrixVP;
float4 UnityObjectToClipPos(float4 v) { return mul(unity_MatrixVP, mul(unity_ObjectToWorld, float4(v.xyz, 1.0))); }
struct appdata_base { float4 vertex : POSITION; float3 normal : NORMAL; float4 texcoord : TEXCOORD0; };
struct appdata_img { float4 vertex : POSITION; float2 texcoord : TEXCOORD0; };
struct v2f_img { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
v2f_img vert_img(appdata_img v) { v2f_img o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }
#define TRANSFORM_TEX(tex, name) (tex.xy * name##_ST.xy + name##_ST.zw)
"""


def programs(text):
    return re.findall(r"CGPROGRAM(.*?)ENDCG", text, re.S)


def main():
    failures = 0
    for shader in sorted(SHADERS.glob("*.shader")):
        for index, body in enumerate(programs(shader.read_text())):
            pragmas = dict(re.findall(r"#pragma\s+(vertex|fragment)\s+(\w+)", body))
            src = re.sub(r"#pragma[^\n]*", "", body)
            src = src.replace('#include "UnityCG.cginc"', UNITY_STUB)
            with tempfile.NamedTemporaryFile("w", suffix=".hlsl", delete=False) as f:
                f.write(src)
                path = f.name
            for stage, key in (("vert", "vertex"), ("frag", "fragment")):
                entry = pragmas.get(key)
                if not entry:
                    continue
                cmd = ["glslangValidator", "-D", "-V", "-S", stage, "-e", entry, "-I" + str(SHADERS), path, "-o", "/dev/null"]
                r = subprocess.run(cmd, capture_output=True, text=True)
                ok = r.returncode == 0
                failures += 0 if ok else 1
                print(f"{'ok  ' if ok else 'FAIL'} {shader.name} pass {index} {stage} ({entry})")
                if not ok:
                    print("    " + (r.stdout + r.stderr).strip().replace("\n", "\n    "))
    print("all shader programs compiled" if failures == 0 else f"{failures} shader stage(s) failed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
