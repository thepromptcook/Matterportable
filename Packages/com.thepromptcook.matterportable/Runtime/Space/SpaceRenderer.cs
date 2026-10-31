using System;
using Matterportable.Core;
using UnityEngine;

namespace Matterportable
{
    /// <summary>
    /// Owns the global shader state every Matterportable shader reads (see MatterportCommon.hlsl): which panoramas
    /// to project, their poses, the cross-fade, projection amount, clip height and debug view. Also installs the
    /// panorama skybox while a space is loaded.
    /// </summary>
    internal sealed class SpaceRenderer : IDisposable
    {
        /// <summary>Shader-safe stand-in for "no clipping" (some GPUs mishandle infinities in uniforms).</summary>
        public const float NoClip = 1e9f;
        public static readonly string[] DebugViewNames = { "Final", "Mesh texture", "Panorama only", "Visibility A/B", "Blend weights" };

        static readonly int IdPanoA = Shader.PropertyToID("_MP_PanoA");
        static readonly int IdPanoB = Shader.PropertyToID("_MP_PanoB");
        static readonly int IdDepthA = Shader.PropertyToID("_MP_DepthA");
        static readonly int IdDepthB = Shader.PropertyToID("_MP_DepthB");
        static readonly int IdPosA = Shader.PropertyToID("_MP_PosA");
        static readonly int IdPosB = Shader.PropertyToID("_MP_PosB");
        static readonly int IdRotA = Shader.PropertyToID("_MP_RotA");
        static readonly int IdRotB = Shader.PropertyToID("_MP_RotB");
        static readonly int IdSpaceRot = Shader.PropertyToID("_MP_SpaceRot");
        static readonly int IdFaceRight = Shader.PropertyToID("_MP_FaceRight");
        static readonly int IdFaceUp = Shader.PropertyToID("_MP_FaceUp");
        static readonly int IdState = Shader.PropertyToID("_MP_State");
        static readonly int IdValid = Shader.PropertyToID("_MP_Valid");
        static readonly int IdBackground = Shader.PropertyToID("_MP_Background");

        // View state, set by the navigator through MatterportSpace.SetView.
        public int ViewA = -1, ViewB = -1;
        public float Blend, Projection = 1f, ClipHeight = float.PositiveInfinity;
        int _debugView;

        readonly Texture2DArray _empty;
        readonly Material _skybox;
        readonly Material _previousSkybox;

        public int DebugView
        {
            get => _debugView;
            set => _debugView = ((value % DebugViewNames.Length) + DebugViewNames.Length) % DebugViewNames.Length;
        }

        public SpaceRenderer(bool installSkybox)
        {
            _empty = new Texture2DArray(1, 1, 6, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = "Matterport empty pano" };
            var px = new[] { new Color32(128, 128, 128, 255) };
            for (int i = 0; i < 6; i++) _empty.SetPixels32(px, i);
            _empty.Apply(false, true);

            var skyShader = installSkybox ? Shader.Find("Matterportable/Skybox") : null;
            if (skyShader != null)
            {
                _skybox = new Material(skyShader) { hideFlags = HideFlags.DontSave, name = "Matterport Skybox" };
                _previousSkybox = RenderSettings.skybox;
                RenderSettings.skybox = _skybox;
            }
        }

        /// <summary>Pushes a panorama face layout (from calibration) to the shaders.</summary>
        public static void SetLayout(SpaceCalibration cal, ILogSink log)
        {
            if (cal == null || !CubeFaces.TryBuildLookup(cal.Faces, out var slice, out var right, out var up))
            {
                log?.Warn("Invalid panorama face layout; using the default.");
                CubeFaces.TryBuildLookup(CubeFaces.DefaultMatterportLayout(), out slice, out right, out up);
            }
            var r = new Vector4[6];
            var u = new Vector4[6];
            for (int c = 0; c < 6; c++)
            {
                r[c] = new Vector4(right[c].X, right[c].Y, right[c].Z, slice[c]);
                u[c] = new Vector4(up[c].X, up[c].Y, up[c].Z, 0);
            }
            Shader.SetGlobalVectorArray(IdFaceRight, r);
            Shader.SetGlobalVectorArray(IdFaceUp, u);
        }

        /// <summary>Edit-mode preview: plain mesh textures, nothing clipped.</summary>
        public static void ShowMeshTexturesOnly()
        {
            Shader.SetGlobalVector(IdState, Vector4.zero);
            Shader.SetGlobalVector(IdValid, new Vector4(0, 0, NoClip, 0));
        }

        /// <summary>Writes the current view state to the shader globals (once per frame, after navigation).</summary>
        public void Apply(MatterportSpace space)
        {
            var cache = space.Panoramas;
            var a = ViewA >= 0 && cache != null ? cache.Get(ViewA) : null;
            var b = ViewB >= 0 && cache != null ? cache.Get(ViewB) : null;
            bool validA = a?.Faces != null, validB = b?.Faces != null;
            bool depth = space.Settings.depthMapSize > 0 && a?.Depth != null && (b == null || b.Depth != null || Blend <= 0f);

            // If only one of the two panoramas is ready, both slots show it.
            Texture texA = validA ? a.Faces : validB ? (Texture)b.Faces : _empty;
            Texture texB = validB ? b.Faces : texA;
            Shader.SetGlobalTexture(IdPanoA, texA);
            Shader.SetGlobalTexture(IdPanoB, texB);
            Shader.SetGlobalTexture(IdDepthA, a?.Depth != null ? (Texture)a.Depth : _empty);
            Shader.SetGlobalTexture(IdDepthB, b?.Depth != null ? (Texture)b.Depth : a?.Depth != null ? (Texture)a.Depth : _empty);

            int ia = validA ? ViewA : validB ? ViewB : Mathf.Max(0, ViewA);
            int ib = validB ? ViewB : ia;
            int count = space.Sweeps.Count;
            if (ia >= 0 && ia < count)
            {
                Shader.SetGlobalVector(IdPosA, space.SweepWorldPosition(ia));
                Shader.SetGlobalMatrix(IdRotA, space.WorldToPano(ia));
            }
            if (ib >= 0 && ib < count)
            {
                Shader.SetGlobalVector(IdPosB, space.SweepWorldPosition(ib));
                Shader.SetGlobalMatrix(IdRotB, space.WorldToPano(ib));
            }
            Shader.SetGlobalMatrix(IdSpaceRot, Matrix4x4.Rotate(Quaternion.Inverse(space.transform.rotation)));
            Shader.SetGlobalVector(IdState, new Vector4(validA && validB ? Blend : validB ? 1f : 0f, Projection, depth ? 1f : 0f, _debugView));
            Shader.SetGlobalVector(IdValid, new Vector4(validA || validB ? 1f : 0f, validA && validB ? 1f : 0f, Mathf.Min(ClipHeight, NoClip), 0f));
            Shader.SetGlobalColor(IdBackground, space.Settings.backgroundColor);
        }

        public void Dispose()
        {
            if (_skybox != null)
            {
                if (RenderSettings.skybox == _skybox) RenderSettings.skybox = _previousSkybox;
                MpUtil.SafeDestroy(_skybox);
            }
            MpUtil.SafeDestroy(_empty);
        }
    }
}
