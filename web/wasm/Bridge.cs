using System;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Matterportable.Core;

/// <summary>
/// The shared Core, exported to the browser client (web/client/js/Core.js wraps it). Geometry comes ready-made from
/// the server (positions, indices, BVH hierarchy, snapping features), so nothing slow is built here. Vectors are in
/// the Core frame; the client converts to three.js (Z mirrored), as Godot does.
/// </summary>
public static partial class Bridge
{
    static SpaceGeometry _geo;
    static MeshFeatures _features;
    static MeasureTool _tool;
    static readonly MeasureDrawList Draw = new MeasureDrawList();

    public static void Main() { }

    /// <summary>Loads geometry.bin, bvh.bin and features.bin. Returns the triangle count.</summary>
    [JSExport]
    public static int Load(byte[] geometry, byte[] hierarchy, byte[] features)
    {
        int vertices = BitConverter.ToInt32(geometry, 4), indexCount = BitConverter.ToInt32(geometry, 8);
        bool uvs = BitConverter.ToInt32(geometry, 12) != 0;
        var positions = new Vec3[vertices];
        int o = 16;
        for (int i = 0; i < vertices; i++, o += 12)
            positions[i] = new Vec3(BitConverter.ToSingle(geometry, o), BitConverter.ToSingle(geometry, o + 4), BitConverter.ToSingle(geometry, o + 8));
        if (uvs) o += vertices * 8;
        var indices = new int[indexCount];
        Buffer.BlockCopy(geometry, o, indices, 0, indexCount * 4);
        _geo = new SpaceGeometry { Positions = positions, Bvh = Bvh.FromHierarchy(positions, indices, hierarchy) };
        _geo.Parts.Add(new GeometryPart { Material = "", Indices = indices });
        _features = features.Length > 0 ? MeshFeatures.Deserialize(features) : null;
        _tool = null;
        return indexCount / 3;
    }

    /// <summary>[origin xyz, direction xyz, max distance] → [hit 0/1, point xyz, normal xyz, distance] (two-sided).</summary>
    [JSExport]
    public static double[] Raycast(double[] ray)
    {
        if (_geo == null || !_geo.Bvh.Raycast(V(ray, 0), V(ray, 3).Normalized, (float)ray[6], out var hit)) return new double[] { 0 };
        return new double[] { 1, hit.Point.X, hit.Point.Y, hit.Point.Z, hit.Normal.X, hit.Normal.Y, hit.Normal.Z, hit.Distance };
    }

    /// <summary>[a xyz, b xyz, margin] → true if anything lies between a and b.</summary>
    [JSExport]
    public static bool Occluded(double[] segment) => _geo != null && _geo.Bvh.Occluded(V(segment, 0), V(segment, 3), (float)segment[6]);

    // ------------------------------------------------------------------ measure mode (Core MeasureTool)

    [JSExport]
    public static void MeasureReset(int units) => _tool = new MeasureTool(_geo, _features) { Units = (MeasureUnits)units };

    /// <summary>[origin xyz, direction xyz, pixel angle, shift, alt, pointer blocked, view, clip height].</summary>
    [JSExport]
    public static void MeasureUpdate(double[] input) => _tool?.Update(new MeasureInput
    {
        RayOrigin = V(input, 0),
        RayDirection = V(input, 3).Normalized,
        PixelAngle = (float)input[6],
        AxisLock = input[7] != 0,
        NoSnap = input[8] != 0,
        PointerBlocked = input[9] != 0,
        View = (MeasureView)(int)input[10],
        ClipHeight = input[11] > 1e8 ? float.PositiveInfinity : (float)input[11],
    });

    [JSExport] public static void MeasureClick() => _tool?.Click();
    [JSExport] public static void MeasureFinish() => _tool?.Finish();
    [JSExport] public static bool MeasureEscape() => _tool != null && _tool.Escape();
    [JSExport] public static void MeasureDelete(int line) => _tool?.Delete(line);

    [JSExport]
    public static void MeasureSelect(int line)
    {
        if (_tool != null) _tool.Selected = line;
    }

    [JSExport]
    public static void MeasureOptions(bool snapping, bool continuous, int units)
    {
        if (_tool == null) return;
        _tool.Snapping = snapping;
        _tool.Continuous = continuous;
        _tool.Units = (MeasureUnits)units;
    }

    /// <summary>
    /// Everything to draw this frame as JSON: seg [ax,ay,az,bx,by,bz,occluded,axis,line], pts [x,y,z,line],
    /// lab [x,y,z,line,text], cur [valid,x,y,z,snap,axis], hov, sel, draft, lines [total text, segments, floor plan].
    /// </summary>
    [JSExport]
    public static string MeasureFrame(double[] eye)
    {
        if (_tool == null) return "null";
        _tool.BuildDrawList(V(eye, 0), Draw);
        var sb = new StringBuilder(1024);
        sb.Append("{\"seg\":[");
        for (int i = 0; i < Draw.Segments.Count; i++)
        {
            var s = Draw.Segments[i];
            if (i > 0) sb.Append(',');
            sb.Append('[');
            Num(sb, s.A); sb.Append(','); Num(sb, s.B);
            sb.Append(',').Append(s.Occluded ? 1 : 0).Append(',').Append((int)s.Axis).Append(',').Append(s.Line).Append(']');
        }
        sb.Append("],\"pts\":[");
        for (int i = 0; i < Draw.Points.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('['); Num(sb, Draw.Points[i].At); sb.Append(',').Append(Draw.Points[i].Line).Append(']');
        }
        sb.Append("],\"lab\":[");
        for (int i = 0; i < Draw.Labels.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('['); Num(sb, Draw.Labels[i].At); sb.Append(',').Append(Draw.Labels[i].Line).Append(',');
            Str(sb, Draw.Labels[i].Text); sb.Append(']');
        }
        var c = _tool.Cursor;
        sb.Append("],\"cur\":[").Append(c.Valid ? 1 : 0).Append(','); Num(sb, c.Point);
        sb.Append(',').Append((int)c.Snap).Append(',').Append((int)c.Axis).Append(']');
        sb.Append(",\"hov\":").Append(_tool.Hovered).Append(",\"sel\":").Append(_tool.Selected);
        sb.Append(",\"draft\":").Append(_tool.Draft != null ? "true" : "false");
        sb.Append(",\"snapping\":").Append(_tool.Snapping ? "true" : "false");
        sb.Append(",\"continuous\":").Append(_tool.Continuous ? "true" : "false");
        sb.Append(",\"units\":").Append((int)_tool.Units).Append(",\"lines\":[");
        for (int i = 0; i < _tool.Lines.Count; i++)
        {
            var line = _tool.Lines[i];
            if (i > 0) sb.Append(',');
            sb.Append('['); Str(sb, _tool.Format(line.TotalLength));
            sb.Append(',').Append(line.SegmentCount).Append(',').Append(line.FloorPlan ? "true" : "false").Append(']');
        }
        return sb.Append("]}").ToString();
    }

    static Vec3 V(double[] a, int i) => new Vec3((float)a[i], (float)a[i + 1], (float)a[i + 2]);

    static void Num(StringBuilder sb, Vec3 v) =>
        sb.Append(v.X.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
          .Append(v.Y.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
          .Append(v.Z.ToString("0.####", CultureInfo.InvariantCulture));

    static void Str(StringBuilder sb, string s) => sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
}
