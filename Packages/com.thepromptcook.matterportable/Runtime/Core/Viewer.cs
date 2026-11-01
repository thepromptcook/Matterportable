using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Matterportable.Core
{
    [Serializable]
    public struct Rgba
    {
        public float R, G, B, A;
        public Rgba(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
    }

    /// <summary>
    /// Tunables for navigation, rendering and streaming, shared by Unity, Godot and the web (one set of defaults).
    /// Read every frame, so edits apply live.
    /// </summary>
    [Serializable]
    public sealed class ViewerSettings
    {
        // Look. DragSensitivity 1 keeps the point under the cursor under it while dragging (like Matterport).
        public float DragSensitivity = 1f, KeyTurnSpeed = 100f, LookSmoothing = 18f, DefaultFov = 75f, MinFov = 25f, MaxFov = 95f, ZoomStep = 6f;
        public bool InvertDrag;
        // Moving between sweeps.
        public float TransitionBase = 0.5f, TransitionPerMeter = 0.12f, TransitionMax = 1.6f, ClickDragThreshold = 6f;
        public float StepMaxAngle = 50f, StepMaxDistance = 8f, ClickSnapRadius = 1.2f;
        // Panoramas: largest face size, neighbours preloaded, GPU memory budget, depth cube size (0 = off), filtering.
        public int PanoMaxSize = 2048, PrefetchNeighbors = 4, PanoMemoryBudgetMB = 1200, DepthMapSize = 256, Anisotropy = 4;
        // Markers and the move cursor.
        public bool ShowMarkers = true, ShowReticle = true;
        public float MarkerRadius = 0.2f, MarkerMaxDistance = 10f, ReticleRadius = 0.28f;
        public Rgba MarkerColor = new Rgba(1f, 1f, 1f, 0.9f);
        // Dollhouse and floor plan.
        public float DollhouseFov = 45f, FloorplanFov = 20f, OrbitSensitivity = 0.25f, ModeTransitionTime = 1.1f;
        public Rgba BackgroundColor = new Rgba(0.11f, 0.11f, 0.12f, 1f);
        // Diagnostics: also print progress to the console (always written to debug/load.log).
        public bool VerboseLogging;

        public float TransitionDuration(float meters) =>
            Math.Min(Math.Max(TransitionBase + TransitionPerMeter * meters, TransitionBase), Math.Max(TransitionBase, TransitionMax));
    }

    /// <summary>
    /// A loaded space as every front end sees it, in the Core frame: sweeps, floors, bounds and ray casts against the
    /// mesh. Engines add rendering on top.
    /// </summary>
    public sealed class SpaceModel
    {
        public SpaceManifest Manifest { get; }
        public SpaceCalibration Calibration { get; }
        public SpaceGeometry Geometry { get; }
        public IReadOnlyList<SweepInfo> Sweeps { get; }
        public Vec3 BoundsMin { get; }
        public Vec3 BoundsMax { get; }
        public int FloorCount => _floors.Count;
        readonly List<float> _floors = new List<float>();

        public SpaceModel(SpaceManifest manifest, SpaceCalibration calibration, SpaceGeometry geometry, IReadOnlyList<SweepInfo> sweeps)
        {
            Manifest = manifest;
            Calibration = calibration;
            Geometry = geometry;
            Sweeps = sweeps;
            // Floor heights: the median marker height of each floor's sweeps.
            int floors = 0;
            foreach (var s in sweeps) floors = Math.Max(floors, s.Floor + 1);
            for (int f = 0; f < floors; f++)
            {
                var ys = new List<float>();
                foreach (var s in sweeps) if (s.Floor == f) ys.Add(s.FloorPoint.Y);
                ys.Sort();
                _floors.Add(ys.Count > 0 ? ys[ys.Count / 2] : (f > 0 ? _floors[f - 1] + 3f : 0f));
            }
            if (geometry != null)
            {
                BoundsMin = geometry.BoundsMin;
                BoundsMax = geometry.BoundsMax;
            }
            else if (sweeps.Count > 0)
            {
                BoundsMin = BoundsMax = sweeps[0].Position;
                foreach (var s in sweeps) { BoundsMin = Vec3.Min(BoundsMin, s.Position); BoundsMax = Vec3.Max(BoundsMax, s.Position); }
            }
        }

        public float FloorHeight(int floor) => floor >= 0 && floor < _floors.Count ? _floors[floor] : 0f;

        /// <summary>Two-sided ray cast against the scanned mesh.</summary>
        public bool Raycast(Vec3 origin, Vec3 dir, float maxDistance, out RayHit hit)
        {
            hit = default;
            return Geometry != null && Geometry.Bvh.Raycast(origin, dir.Normalized, maxDistance, out hit);
        }

        /// <summary>First surface the user sees (see <see cref="Bvh.RaycastVisible"/>).</summary>
        public bool RaycastVisible(Vec3 origin, Vec3 dir, float maxDistance, bool cullBack, float clipHeight, out RayHit hit)
        {
            hit = default;
            return Geometry != null && Geometry.Bvh.RaycastVisible(origin, dir.Normalized, maxDistance, cullBack, clipHeight, out hit);
        }

        public bool LineOfSight(Vec3 a, Vec3 b) => Geometry == null || !Geometry.Bvh.Occluded(a, b, 0.1f);

        static readonly object CacheLock = new object();
        static (string key, SpaceModel model) _cache;

        /// <summary>
        /// Decodes the mesh, builds the BVH and the sweeps (call on a worker thread). The last model stays in memory:
        /// reopening a space (or Unity's next Play with fast Enter Play Mode) skips this unless the mesh or the
        /// calibration changed.
        /// </summary>
        public static SpaceModel Load(string dir, SpaceManifest manifest, SpaceCalibration cal, ILogSink log)
        {
            var meshPath = manifest.Mesh != null ? CacheLayout.Resolve(dir, manifest.Mesh.File) : null;
            var key = string.Join("|", dir, meshPath != null ? File.GetLastWriteTimeUtc(meshPath).Ticks : 0, manifest.Sweeps.Count, cal.ToJson().ToJson(false));
            lock (CacheLock)
                if (_cache.key == key) return _cache.model;
            SpaceGeometry geo = null;
            if (meshPath != null)
            {
                var bytes = File.ReadAllBytes(meshPath);
                var mesh = DamDecoder.Decode(bytes, log);
                if (!mesh.HasUvs) DamDecoder.WriteDiagnostics(dir, bytes);
                geo = SpaceGeometry.Build(mesh, cal);
            }
            var model = new SpaceModel(manifest, cal, geo, SweepBuilder.Build(manifest, cal, geo, log));
            lock (CacheLock) _cache = (key, model);
            return model;
        }
    }

    /// <summary>Text every front end shows.</summary>
    public static class ViewerText
    {
        public const string KeyHint = "Drag to look · Click to move · W A S D step · ←/→ turn · Wheel zoom · 1/2/3 Inside/Dollhouse/Floor plan · " +
                                      "M measure · [ ] prev/next · P tour · F1 stats · F2 render view · H hide · Esc menu";

        /// <summary>A floor's button label: its name in Matterport, else "Floor N".</summary>
        public static string FloorLabel(SpaceModel space, int floor)
        {
            var floors = space.Manifest.Floors;
            return floor < floors.Count && !string.IsNullOrEmpty(floors[floor].Label) ? floors[floor].Label : "Floor " + (floor + 1);
        }

        /// <summary>Render debug views (F2), in shader order.</summary>
        public static readonly string[] DebugViews = { "Final", "Mesh texture", "Panorama only", "Visibility A/B", "Blend weights" };

        /// <summary>The F1 statistics panel.</summary>
        public static string Stats(SpaceModel space, Navigator nav, PanoramaScheduler panos, double fps, double loadSeconds, int texturesLoaded)
        {
            var sb = new StringBuilder();
            sb.Append(space.Manifest.Name).Append("  (").Append(space.Manifest.ModelId).Append(")\n");
            sb.AppendFormat("{0:0} fps\n", fps);
            int cur = nav.CurrentSweep;
            if (cur >= 0 && cur < space.Sweeps.Count)
                sb.AppendFormat("Sweep {0}/{1}  id {2}  floor {3}\n", cur, space.Sweeps.Count - 1, space.Sweeps[cur].Id, space.Sweeps[cur].Floor);
            sb.AppendFormat("Mode {0}{1}{2}  FOV {3:0}°\n", nav.Mode, nav.IsMoving ? "  (moving)" : "", nav.IsChangingMode ? "  (changing view)" : "", nav.Fov);
            sb.AppendFormat("Panoramas: {0} cached ({1}), {2} depth maps, loading {3}, queued {4}\n",
                panos.LoadedCount, IoUtil.FormatBytes(panos.BytesUsed), panos.DepthCount, panos.ActiveLoads, panos.Queued);
            if (cur >= 0 && panos.Size(cur) > 0) sb.AppendFormat("Current panorama: {0}px, loaded in {1:0.00}s\n", panos.Size(cur), panos.LoadSeconds(cur));
            sb.Append(space.Geometry != null
                ? $"Mesh: {space.Geometry.TriangleCount:N0} triangles, textures {texturesLoaded}/{space.Manifest.Textures.Count}\n"
                : "Mesh: none (panorama-only)\n");
            var cal = space.Calibration;
            sb.AppendFormat("Alignment: {0}{1}\n", cal.Method, double.IsNaN(cal.Score) ? "" : $" (score {cal.Score:0.00})");
            sb.AppendFormat("Render view (F2): {0}\n", DebugViews[nav.DebugView]);
            sb.AppendFormat("Loaded in {0:0.0}s", loadSeconds);
            return sb.ToString();
        }
    }
}
