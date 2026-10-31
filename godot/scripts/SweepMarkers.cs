using System.Collections.Generic;
using Godot;

namespace Matterportable
{
    /// <summary>Marker circles and the surface cursor: a pool of quads redrawn each frame (mirrors Unity SweepMarkers).</summary>
    public sealed class SweepMarkers
    {
        readonly Node _parent;
        readonly List<(MeshInstance3D node, ShaderMaterial mat)> _pool = new List<(MeshInstance3D, ShaderMaterial)>();
        readonly Shader _shader = GD.Load<Shader>("res://shaders/marker.gdshader");
        readonly PlaneMesh _quad = new PlaneMesh { Size = Vector2.One };
        int _used;

        public SweepMarkers(Node parent) { _parent = parent; }

        public void BeginFrame()
        {
            for (int i = 0; i < _pool.Count; i++) _pool[i].node.Visible = false;
            _used = 0;
        }

        /// <summary>Draws a circle lying on a surface with the given normal.</summary>
        public void Draw(Vector3 position, Vector3 normal, float radius, Color color, float inner = 0.72f, float fill = 0.22f)
        {
            if (radius <= 0 || color.A <= 0.001f) return;
            if (_used == _pool.Count)
            {
                var mat = new ShaderMaterial { Shader = _shader };
                var node = new MeshInstance3D { Mesh = _quad, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                node.TopLevel = true;
                _parent.AddChild(node);
                _pool.Add((node, mat));
            }
            var (n, m) = _pool[_used++];
            var up = normal.LengthSquared() > 1e-6f ? normal.Normalized() : Vector3.Up;
            var basis = new Basis(up.IsEqualApprox(Vector3.Up) ? Quaternion.Identity : new Quaternion(Vector3.Up, up)).Scaled(new Vector3(radius * 2, 1, radius * 2));
            n.GlobalTransform = new Transform3D(basis, position + up * 0.01f);
            m.SetShaderParameter("color", color);
            m.SetShaderParameter("inner", inner);
            m.SetShaderParameter("fill", fill);
            n.Visible = true;
        }
    }
}
