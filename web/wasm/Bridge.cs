using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Matterportable.Core;

/// <summary>
/// The shared Core, exported to the browser client (web/client/js). The page runs the same Navigator,
/// PanoramaScheduler and MeasureTool as Unity and Godot: it sends input once per frame (<see cref="Frame"/>) and
/// applies what comes back (camera, view binding, markers, loader commands). Geometry arrives ready-made from the
/// server, so nothing slow is built here. Vectors are in the Core frame; the page mirrors Z for three.js.
/// </summary>
public static partial class Bridge
{
    /// <summary>Panorama work for the page: [command (0 load, 1 depth, 2 unload), sweep, tier index] triples.</summary>
    sealed class PageLoader : IPanoramaLoader
    {
        public readonly List<double> Commands = new List<double>();
        public void Load(int sweep, PanoTier tier) => Commands.AddRange(new double[] { 0, sweep, _model.Sweeps[sweep].Entry.Tiers.IndexOf(tier) });
        public void ComputeDepth(int sweep) => Commands.AddRange(new double[] { 1, sweep, 0 });
        public void Unload(int sweep) => Commands.AddRange(new double[] { 2, sweep, 0 });
    }

    static readonly ViewerSettings Settings = new ViewerSettings();
    static SpaceModel _model;
    static MeshFeatures _features;
    static PageLoader _loader;
    static PanoramaScheduler _panos;
    static Navigator _nav;
    static MeasureTool _tool;
    static readonly MeasureOverlay Overlay = new MeasureOverlay();

    public static void Main() { }

    /// <summary>The page's rendering settings, from the shared defaults: [depth cube size, background r, g, b, anisotropy].</summary>
    [JSExport]
    public static double[] RenderSettings() =>
        new double[] { Settings.DepthMapSize, Settings.BackgroundColor.R, Settings.BackgroundColor.G, Settings.BackgroundColor.B, Settings.Anisotropy };

    /// <summary>Loads a space: geometry.bin, bvh.bin, features.bin (empty without a mesh) and space.json.</summary>
    [JSExport]
    public static void Load(byte[] geometry, byte[] hierarchy, byte[] features, string spaceJson)
    {
        var info = JsonValue.Parse(spaceJson);
        SpaceGeometry geo = null;
        if (geometry.Length > 0)
        {
            int vertices = BitConverter.ToInt32(geometry, 4), indexCount = BitConverter.ToInt32(geometry, 8);
            var positions = new Vec3[vertices];
            for (int i = 0, o = 16; i < vertices; i++, o += 12)
                positions[i] = new Vec3(BitConverter.ToSingle(geometry, o), BitConverter.ToSingle(geometry, o + 4), BitConverter.ToSingle(geometry, o + 8));
            var indices = new int[indexCount];
            Buffer.BlockCopy(geometry, 16 + vertices * 12 + (BitConverter.ToInt32(geometry, 12) != 0 ? vertices * 8 : 0), indices, 0, indexCount * 4);
            geo = new SpaceGeometry { Positions = positions, Bvh = Bvh.FromHierarchy(positions, indices, hierarchy) };
            geo.BoundsMin = geo.Bvh.BoundsMin;
            geo.BoundsMax = geo.Bvh.BoundsMax;
            geo.Parts.Add(new GeometryPart { Material = "", Indices = indices });
        }
        var manifest = new SpaceManifest { ModelId = info["id"].AsString(), Name = info["name"].AsString() };
        foreach (var p in info["parts"].Items)
            if (p["texture"].AsString() != null) manifest.Textures.Add(new TextureEntry { Material = p["material"].AsString(), File = p["texture"].AsString() });
        var cal = new SpaceCalibration { Method = info["calibration"]["method"].AsString(), Score = info["calibration"]["score"].AsDouble(double.NaN) };
        var sweeps = new List<SweepInfo>();
        foreach (var s in info["sweeps"].Items)
        {
            var entry = new SweepEntry { Id = s["id"].AsString(), Index = sweeps.Count };
            foreach (var t in s["tiers"].Items)
                entry.Tiers.Add(new PanoTier { Size = t["size"].AsInt(), Tiles = t["tiles"].AsInt(1), Directory = t["dir"].AsString(), Extension = t["ext"].AsString() });
            var r = s["rotation"].AsDoubleArray();
            var sweep = new SweepInfo
            {
                Index = sweeps.Count, Id = entry.Id, Label = s["label"].AsString(), Floor = s["floor"].AsInt(), Entry = entry,
                Position = V(s["position"].AsDoubleArray(), 0), FloorPoint = V(s["floorPoint"].AsDoubleArray(), 0),
                WorldFromPano = Mat3.FromColumns(V(r, 0), V(r, 3), V(r, 6)),
            };
            foreach (var n in s["neighbors"].Items) sweep.Neighbors.Add(n.AsInt());
            sweeps.Add(sweep);
        }
        _model = new SpaceModel(manifest, cal, geo, sweeps);
        var objects = info["objects"].AsString();
        if (geo != null && objects != null) _model.Props = PropFinder.FromJson(JsonValue.Parse(objects), geo) ?? new List<Prop>();
        _features = features.Length > 0 ? MeshFeatures.Deserialize(features) : null;
        _loader = new PageLoader();
        _panos = new PanoramaScheduler(sweeps, Settings, _loader, geo != null);
        _nav = new Navigator(_model, Settings, _panos);
        _tool = null;
    }

    /// <summary>
    /// One frame. Input: [dt, pointer x, y, over, buttons down, held, up, ray origin xyz, ray direction xyz, pixel
    /// angle, viewport height, scroll, keys pressed, keys held]. Output: [camera xyz, rotation xyzw, fov, mode, sweep,
    /// hover, floor, flags (1 moving, 2 changing mode, 4 waiting, 8 tour, 16 cull back, 32 clicked), pano A, B,
    /// depth A, B, pose A, B, blend, projection, use depth, debug view, valid A, B, clip height, floor count, view clip
    /// height, active loads, queued, marker count, markers (position, normal, radius, rgba, inner, fill), command
    /// count, commands (see <see cref="PageLoader"/>), highlighted prop, prop flags (1 carrying, 2 drop refused), prop
    /// count, props (translation xyz, rotation xyzw, tint rgba)].
    /// </summary>
    [JSExport]
    public static double[] Frame(double[] i)
    {
        _nav.Update(new NavigatorInput
        {
            DeltaTime = (float)i[0], PointerX = (float)i[1], PointerY = (float)i[2], PointerOver = i[3] != 0,
            ButtonsDown = (int)i[4], ButtonsHeld = (int)i[5], ButtonsUp = (int)i[6], RayOrigin = V(i, 7), RayDirection = V(i, 10).Normalized,
            PixelAngle = (float)i[13], ViewportHeight = (float)i[14], Scroll = (float)i[15], Pressed = (NavKeys)(int)i[16], Held = (NavKeys)(int)i[17],
        });
        _panos.Update((float)i[0]);
        var n = _nav;
        var v = n.View;
        var q = n.CameraRotation;
        int flags = (n.IsMoving ? 1 : 0) | (n.IsChangingMode ? 2 : 0) | (n.IsWaitingForPanorama ? 4 : 0) | (n.AutoTour ? 8 : 0) | (n.CullBack ? 16 : 0) | (n.Clicked ? 32 : 0);
        var o = new List<double>(64 + n.Markers.Count * 13 + _loader.Commands.Count)
        {
            n.CameraPosition.X, n.CameraPosition.Y, n.CameraPosition.Z, q.X, q.Y, q.Z, q.W, n.Fov, (int)n.Mode, n.CurrentSweep, n.HoverSweep,
            n.SelectedFloor, flags, v.PanoA, v.PanoB, v.DepthA, v.DepthB, v.PoseA, v.PoseB, v.Blend, v.Projection, v.UseDepth ? 1 : 0,
            v.DebugView, v.ValidA ? 1 : 0, v.ValidB ? 1 : 0, v.ClipHeight, _model.FloorCount, Math.Min(n.ViewClipHeight, 1e9f), _panos.ActiveLoads,
            _panos.Queued, n.Markers.Count,
        };
        foreach (var m in n.Markers)
            o.AddRange(new double[] { m.Position.X, m.Position.Y, m.Position.Z, m.Normal.X, m.Normal.Y, m.Normal.Z, m.Radius, m.Color.R, m.Color.G, m.Color.B, m.Color.A, m.Inner, m.Fill });
        o.Add(_loader.Commands.Count / 3);
        o.AddRange(_loader.Commands);
        _loader.Commands.Clear();
        var props = n.Props;
        o.Add(n.HighlightProp);
        o.Add(props == null ? 0 : (props.Carried >= 0 ? 1 : 0) | (props.DropRefused ? 2 : 0));
        o.Add(props?.Props.Count ?? 0);
        for (int p = 0; props != null && p < props.Props.Count; p++)
        {
            var pose = props.Poses[p];
            var tint = props.Tint(p, n.HighlightProp, v.DebugView);
            o.AddRange(new double[] { pose.Translation.X, pose.Translation.Y, pose.Translation.Z, pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W, tint.R, tint.G, tint.B, tint.A });
        }
        return o.ToArray();
    }

    /// <summary>A panorama load finished; false means a larger tier is already there (discard these faces).</summary>
    [JSExport] public static bool PanoLoaded(int sweep, int size) => _panos.Loaded(sweep, size);
    [JSExport] public static void PanoFailed(int sweep) => _panos.Failed(sweep);
    [JSExport] public static void DepthReady(int sweep) => _panos.DepthReady(sweep);

    /// <summary>Navigation commands: 0 go to sweep, 1 set mode, 2 set floor (-1 all), 3 teleport (sweep, yaw, pitch),
    /// 4 toggle tour, 5 render view, 6 measuring on/off, 7 start sweep and mode, 8 look (yaw, pitch).</summary>
    [JSExport]
    public static void Navigate(int command, int a, double b, double c)
    {
        switch (command)
        {
            case 0: _nav.GoTo(a); break;
            case 1: _nav.SetMode((ViewMode)a); break;
            case 2: _nav.SetFloor(a); break;
            case 3: _nav.Teleport(a, (float)b, (float)c); break;
            case 4: _nav.AutoTour = !_nav.AutoTour; break;
            case 5: _nav.DebugView = a; break;
            case 6: _nav.Measuring = a != 0; break;
            case 7: _nav.StartSweep = a; _nav.StartMode = (ViewMode)(int)b; break;
            case 8: _nav.Look((float)b, (float)c); break;
        }
    }

    [JSExport]
    public static string Stats(double fps, double loadSeconds, int texturesLoaded) => ViewerText.Stats(_model, _nav, _panos, fps, loadSeconds, texturesLoaded);

    [JSExport] public static string KeyHint() => ViewerText.KeyHint;

    /// <summary>Walk mode's hint for the prop under the pointer or in the hands ("" for none).</summary>
    [JSExport] public static string PropHint() => ViewerText.PropHint(_nav) ?? "";

    /// <summary>Which piece each triangle is drawn with (geometry.bin's order): 0 the room, i + 1 prop i.</summary>
    [JSExport]
    public static int[] PropOwners()
    {
        var owners = new int[_model.Geometry?.TriangleCount ?? 0];
        for (int i = 0; i < _model.Props.Count; i++) foreach (int t in _model.Props[i].Triangles) owners[t] = i + 1;
        return owners;
    }

    // ------------------------------------------------------------------ measure mode (Core MeasureTool)

    [JSExport]
    public static void MeasureReset(int units) => _tool = new MeasureTool(_model.Geometry, _features) { Units = (MeasureUnits)units };

    /// <summary>[origin xyz, direction xyz, pixel angle, shift, alt, pointer blocked]; view and clip come from the navigator.</summary>
    [JSExport]
    public static void MeasureUpdate(double[] input) => _tool?.Update(new MeasureInput
    {
        RayOrigin = V(input, 0),
        RayDirection = V(input, 3).Normalized,
        PixelAngle = (float)input[6],
        AxisLock = input[7] != 0,
        NoSnap = input[8] != 0,
        PointerBlocked = input[9] != 0,
        View = _nav.Mode,
        ClipHeight = _nav.ViewClipHeight,
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
    /// The measure overlay and panel as JSON, for a width × height view of the navigator's camera: items [kind (0 line,
    /// 1 dot, 2 ring, 3 label), x, y, x2, y2, size, CSS colour, text, selected], lines [total text, segments, floor
    /// plan], sel, draft, snapping, continuous, units.
    /// </summary>
    [JSExport]
    public static string MeasureFrame(double width, double height)
    {
        if (_tool == null) return "null";
        _tool.BuildOverlay(_nav.CameraPosition, _nav.CameraRotation, _nav.Fov, (float)width, (float)height, Overlay);
        var sb = new StringBuilder(1024);
        void Join<T>(string name, List<T> items, Action<T> write)
        {
            sb.Append('"').Append(name).Append("\":[");
            for (int k = 0; k < items.Count; k++)
            {
                if (k > 0) sb.Append(',');
                sb.Append('[');
                write(items[k]);
                sb.Append(']');
            }
            sb.Append("],");
        }
        string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        sb.Append('{');
        Join("items", Overlay.Items, i =>
        {
            sb.Append((int)i.Kind).Append(',').Append(F(i.X)).Append(',').Append(F(i.Y)).Append(',').Append(F(i.X2)).Append(',').Append(F(i.Y2)).Append(',').Append(F(i.Size));
            sb.Append(",\"rgba(").Append((int)(i.Color.R * 255)).Append(',').Append((int)(i.Color.G * 255)).Append(',').Append((int)(i.Color.B * 255)).Append(',').Append(F(i.Color.A)).Append(")\",");
            Str(sb, i.Text ?? "");
            sb.Append(',').Append(i.Selected ? 1 : 0);
        });
        Join("lines", _tool.Lines, l => { Str(sb, _tool.Format(l.TotalLength)); sb.Append(',').Append(l.SegmentCount).Append(',').Append(l.FloorPlan ? "true" : "false"); });
        sb.Append("\"sel\":").Append(_tool.Selected);
        sb.Append(",\"draft\":").Append(_tool.Draft != null ? "true" : "false");
        sb.Append(",\"snapping\":").Append(_tool.Snapping ? "true" : "false");
        sb.Append(",\"continuous\":").Append(_tool.Continuous ? "true" : "false");
        return sb.Append(",\"units\":").Append((int)_tool.Units).Append('}').ToString();
    }

    static Vec3 V(double[] a, int i) => new Vec3((float)a[i], (float)a[i + 1], (float)a[i + 2]);

    static void Str(StringBuilder sb, string s) => sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
}
