using System;
using UnityEngine;

namespace Matterportable
{
    /// <summary>Draws marker circles and the surface cursor with Graphics.DrawMesh (no GameObjects to manage).</summary>
    public sealed class SweepMarkers : IDisposable
    {
        static readonly int IdColor = Shader.PropertyToID("_Color");
        static readonly int IdInner = Shader.PropertyToID("_Inner");
        static readonly int IdFill = Shader.PropertyToID("_Fill");

        readonly Mesh _quad;
        readonly Material _material;
        readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();

        public SweepMarkers()
        {
            _quad = new Mesh { name = "Matterport marker quad", hideFlags = HideFlags.HideAndDontSave };
            _quad.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 0, -0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(-0.5f, 0, 0.5f) };
            _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            _quad.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            _quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            _quad.RecalculateBounds();
            var shader = Shader.Find("Matterportable/Marker");
            _material = new Material(shader != null ? shader : Shader.Find("Unlit/Transparent")) { hideFlags = HideFlags.HideAndDontSave };
        }

        /// <summary>Draws a circle lying on a surface with the given normal.</summary>
        public void Draw(Camera camera, Vector3 position, Vector3 normal, float radius, Color color, float inner = 0.72f, float fill = 0.22f)
        {
            if (radius <= 0 || color.a <= 0.001f) return;
            var rotation = Quaternion.FromToRotation(Vector3.up, normal.sqrMagnitude > 1e-6f ? normal.normalized : Vector3.up);
            var matrix = Matrix4x4.TRS(position + rotation * Vector3.up * 0.01f, rotation, new Vector3(radius * 2, 1, radius * 2));
            _block.SetColor(IdColor, color);
            _block.SetFloat(IdInner, inner);
            _block.SetFloat(IdFill, fill);
            Graphics.DrawMesh(_quad, matrix, _material, camera.gameObject.layer, camera, 0, _block, false, false, false);
        }

        public void Dispose()
        {
            MpUtil.SafeDestroy(_quad);
            MpUtil.SafeDestroy(_material);
        }
    }
}
