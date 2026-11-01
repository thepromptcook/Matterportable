using System;
using System.Collections.Generic;
using Matterportable.Core;
using NUnit.Framework;

namespace Matterportable.Tests
{
    public class MeasurementTests
    {
        static readonly Vec3 Eye = new Vec3(2, 1.5f, 2.5f);

        /// <summary>Points the cursor at <paramref name="target"/> from <paramref name="eye"/>.</summary>
        static void Hover(MeasureTool tool, Vec3 target, Vec3? eye = null, bool shift = false, bool alt = false, ViewMode view = ViewMode.Inside)
        {
            var o = eye ?? Eye;
            tool.Update(new MeasureInput
            {
                RayOrigin = o, RayDirection = (target - o).Normalized, PixelAngle = 0.0015f,
                AxisLock = shift, NoSnap = alt, View = view, ClipHeight = float.PositiveInfinity,
            });
        }

        static MeasureTool ReadyTool(SpaceGeometry geo)
        {
            var tool = new MeasureTool(geo);
            for (int i = 0; i < 500 && !tool.FeaturesReady; i++) System.Threading.Thread.Sleep(10);
            Assert.IsTrue(tool.FeaturesReady);
            return tool;
        }

        [TestCase(0.3048f, MeasureUnits.Imperial, "1' 0\"")]
        [TestCase(2.54f, MeasureUnits.Imperial, "8' 4\"")]
        [TestCase(0.1524f, MeasureUnits.Imperial, "6\"")]
        [TestCase(0.3f, MeasureUnits.Imperial, "1' 0\"")]   // 11.8" rounds up to a whole foot
        [TestCase(2.5f, MeasureUnits.Metric, "2.50 m")]
        public void FormatsLikeMatterport(float meters, MeasureUnits units, string expected)
        {
            Assert.AreEqual(expected, MeasureTool.Format(meters, units));
        }

        [Test]
        public void FindsRoomCornersAndEdges()
        {
            var f = MeshFeatures.Build(TestSpaces.Room());
            Assert.AreEqual(8, f.Corners.Count);
            Assert.AreEqual(12, f.EdgeStart.Count, "only the 12 box edges are creases (face diagonals are flat)");
            Assert.IsTrue(f.NearestCorner(new Vec3(0.03f, 0.02f, 0.01f), 0.1f, out var corner));
            Assert.AreEqual(0f, (corner - new Vec3(0, 0, 0)).Length, 1e-4f);
            Assert.IsTrue(f.NearestEdgePoint(new Vec3(1.5f, 0.04f, 0.03f), 0.1f, out var onEdge));
            Assert.AreEqual(0f, (onEdge - new Vec3(1.5f, 0, 0)).Length, 1e-4f);
        }

        [Test]
        public void HierarchyAndFeaturesRoundTrip()
        {
            var geo = TestSpaces.Room();
            var idx = geo.Parts[0].Indices;
            var restored = Bvh.FromHierarchy(geo.Positions, idx, geo.Bvh.SerializeHierarchy());
            var rng = new Random(5);
            for (int i = 0; i < 200; i++)
            {
                var o = new Vec3((float)rng.NextDouble() * 4, (float)rng.NextDouble() * 2.5f, (float)rng.NextDouble() * 3);
                var d = new Vec3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f).Normalized;
                Assert.AreEqual(geo.Bvh.Raycast(o, d, 50, out var a), restored.Raycast(o, d, 50, out var b));
                Assert.AreEqual(a.Distance, b.Distance, 1e-5f);
                Assert.AreEqual(a.Triangle, b.Triangle);
            }
            var features = MeshFeatures.Build(geo);
            var back = MeshFeatures.Deserialize(features.Serialize());
            Assert.AreEqual(features.Corners.Count, back.Corners.Count);
            Assert.AreEqual(features.EdgeStart.Count, back.EdgeStart.Count);
            Assert.IsTrue(back.NearestCorner(new Vec3(3.98f, 2.48f, 0.02f), 0.1f, out var corner));
            Assert.AreEqual(0f, (corner - new Vec3(4, 2.5f, 0)).Length, 1e-4f);
            Assert.IsTrue(new MeasureTool(geo, back).FeaturesReady, "precomputed features are ready at once");
        }

        [TestCase(0)]
        [TestCase(20)]
        [TestCase(-30)]
        public void WallAxesFollowTheRoom(double degrees)
        {
            var tool = new MeasureTool(TestSpaces.Room(degrees));
            double r = degrees * Math.PI / 180;
            Assert.AreEqual(1f, Math.Abs(Vec3.Dot(tool.AxisX, new Vec3((float)Math.Cos(r), 0, (float)Math.Sin(r)))), 1e-3f);
            Assert.AreEqual(0f, Vec3.Dot(tool.AxisX, tool.AxisZ), 1e-5f);
        }

        [Test]
        public void ContinuousLinesUntilEscape()
        {
            var tool = ReadyTool(TestSpaces.Room());
            Hover(tool, new Vec3(1, 0, 1)); tool.Click();
            Hover(tool, new Vec3(2, 0, 1)); tool.Click();
            Hover(tool, new Vec3(2, 0, 2)); tool.Click();
            Assert.AreEqual(3, tool.Draft.Points.Count);
            Assert.IsTrue(tool.Escape(), "Escape ends the line");
            Assert.IsNull(tool.Draft);
            Assert.AreEqual(1, tool.Lines.Count);
            Assert.AreEqual(2, tool.Lines[0].SegmentCount);
            Assert.AreEqual(2f, tool.Lines[0].TotalLength, 1e-3f);
            Assert.IsFalse(tool.Escape(), "a second Escape leaves measure mode");
        }

        [Test]
        public void SingleSegmentsWhenContinuousIsOff()
        {
            var tool = ReadyTool(TestSpaces.Room());
            tool.Continuous = false;
            Hover(tool, new Vec3(1, 0, 1)); tool.Click();
            Hover(tool, new Vec3(3, 0, 1)); tool.Click();
            Assert.IsNull(tool.Draft);
            Assert.AreEqual(1, tool.Lines.Count);
            Assert.AreEqual("2.00 m", MeasureTool.Format(tool.Lines[0].TotalLength, MeasureUnits.Metric));
        }

        [Test]
        public void SnapsToPointsCornersAndNotWithAlt()
        {
            var tool = ReadyTool(TestSpaces.Room());
            tool.AddLine(new[] { new Vec3(1, 0, 1), new Vec3(2, 0, 1) });
            Hover(tool, new Vec3(2.01f, 0, 1.01f));
            Assert.AreEqual(MeasureSnap.Point, tool.Cursor.Snap);
            Assert.AreEqual(0f, (tool.Cursor.Point - new Vec3(2, 0, 1)).Length, 1e-4f);

            Hover(tool, new Vec3(3.97f, 0, 2.97f));
            Assert.AreEqual(MeasureSnap.Corner, tool.Cursor.Snap);
            Assert.AreEqual(0f, (tool.Cursor.Point - new Vec3(4, 0, 3)).Length, 1e-4f);

            Hover(tool, new Vec3(3.97f, 0, 2.97f), alt: true);
            Assert.AreEqual(MeasureSnap.Surface, tool.Cursor.Snap);
        }

        [Test]
        public void ShiftLocksToVertical()
        {
            var tool = ReadyTool(TestSpaces.Room());
            Hover(tool, new Vec3(1, 0, 1)); tool.Click();
            // Aim at the wall behind the start point, slightly to the side, at 1.2 m: locks to the vertical line.
            var eye = new Vec3(1, 1.2f, 2.5f);
            Hover(tool, new Vec3(1.1f, 1.2f, 0), eye, shift: true);
            Assert.AreEqual(MeasureAxis.Vertical, tool.Cursor.Axis);
            Assert.AreEqual(1f, tool.Cursor.Point.X, 1e-3f);
            Assert.AreEqual(1f, tool.Cursor.Point.Z, 1e-3f);
            Assert.AreEqual(1.2f, tool.Cursor.Point.Y, 0.02f);
            tool.Click();
            Assert.AreEqual(MeasureAxis.Vertical, tool.Draft.Axes[0]);
        }

        [Test]
        public void FloorPlanLinesAreHorizontalAndOnlyShownInFloorPlan()
        {
            var tool = ReadyTool(TestSpaces.Room());
            var above = new Vec3(2, 20, 1.5f);
            Hover(tool, new Vec3(1, 0, 1), above, view: ViewMode.Floorplan); tool.Click();
            Hover(tool, new Vec3(3, 0, 2), above, view: ViewMode.Floorplan); tool.Click();
            tool.Finish();
            Assert.IsTrue(tool.Lines[0].FloorPlan);
            Assert.AreEqual(Math.Sqrt(5), tool.Lines[0].TotalLength, 1e-3);

            var draw = new MeasureDrawList();
            tool.BuildDrawList(above, draw);
            Assert.AreEqual(1, draw.Labels.Count);
            Hover(tool, new Vec3(2, 0, 1.5f)); // back inside
            tool.BuildDrawList(Eye, draw);
            Assert.AreEqual(0, draw.Labels.Count);
        }

        [Test]
        public void ClickSelectsAndDeleteRemoves()
        {
            var tool = ReadyTool(TestSpaces.Room());
            tool.AddLine(new[] { new Vec3(1, 0, 1), new Vec3(3, 0, 1) });
            Hover(tool, new Vec3(2, 0, 1.01f));
            Assert.AreEqual(0, tool.Hovered);
            tool.Click();
            Assert.AreEqual(0, tool.Selected);
            Assert.IsNull(tool.Draft, "clicking a measurement selects it instead of starting a new one");
            tool.DeleteSelected();
            Assert.AreEqual(0, tool.Lines.Count);
            Assert.AreEqual(-1, tool.Selected);
        }

        [Test]
        public void HiddenPartsAreMarkedOccluded()
        {
            var tool = ReadyTool(TestSpaces.Room());
            tool.AddLine(new[] { new Vec3(1, 0, 1), new Vec3(3, 0, 1) });
            var draw = new MeasureDrawList();
            var outside = new Vec3(2, 1, -2); // behind the z = 0 wall
            Hover(tool, new Vec3(2, 1, 0), outside);
            tool.BuildDrawList(outside, draw);
            Assert.IsTrue(draw.Segments.TrueForAll(s => s.Occluded), "the wall hides the line inside");

            Hover(tool, new Vec3(2, 1, 0), outside, view: ViewMode.Dollhouse);
            tool.BuildDrawList(outside, draw);
            Assert.IsTrue(draw.Segments.TrueForAll(s => !s.Occluded), "in dollhouse you see through walls from behind");
        }

        [Test]
        public void OverlayProjectsLikeTheEngineCameras()
        {
            var tool = new MeasureTool(TestSpaces.Room());
            tool.AddLine(new[] { new Vec3(1, 1, 2), new Vec3(3, 1, 2) });
            tool.Selected = 0;
            var overlay = new MeasureOverlay();
            var eye = new Vec3(2, 1, 0.5f);
            tool.BuildOverlay(eye, Quat.Identity, 90, 200, 100, overlay); // looking along +Z, 90° vertical FOV, 200 x 100 px
            var label = overlay.Items.Find(i => i.Kind == MeasureOverlay.Kind.Label);
            Assert.AreEqual(100, label.X, 0.01f, "the midpoint is straight ahead");
            Assert.AreEqual(50, label.Y, 0.01f);
            Assert.IsTrue(label.Selected);
            var points = overlay.Items.FindAll(i => i.Kind == MeasureOverlay.Kind.Dot && i.Size == 4f);
            Assert.AreEqual(100 - 50 / 1.5f, points[0].X, 0.01f, "1 m left at 1.5 m depth, focal length 50 px");
            Assert.AreEqual(100 + 50 / 1.5f, points[1].X, 0.01f);
            tool.BuildOverlay(eye, Quat.Euler(0, 180), 90, 200, 100, overlay);
            CollectionAssert.IsEmpty(overlay.Items, "nothing behind the camera");
        }
    }
}
