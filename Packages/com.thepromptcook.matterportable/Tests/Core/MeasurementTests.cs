using System;
using System.Collections.Generic;
using Matterportable.Core;
using NUnit.Framework;

namespace Matterportable.Tests
{
    public class MeasurementTests
    {
        /// <summary>A closed room, 4 m (x) by 3 m (z) by 2.5 m high, with faces pointing inwards, optionally rotated.</summary>
        static SpaceGeometry Room(double rotationDegrees = 0)
        {
            double r = rotationDegrees * Math.PI / 180;
            Vec3 P(float x, float y, float z) => new Vec3((float)(x * Math.Cos(r) - z * Math.Sin(r)), y, (float)(x * Math.Sin(r) + z * Math.Cos(r)));
            var positions = new List<Vec3>();
            var indices = new List<int>();
            void Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 inside)
            {
                int i = positions.Count;
                positions.AddRange(new[] { a, b, c, d });
                // Orient both triangles so their normal points into the room.
                var n = Vec3.Cross(b - a, c - a);
                bool flip = Vec3.Dot(n, inside - a) < 0;
                indices.AddRange(flip ? new[] { i, i + 2, i + 1, i, i + 3, i + 2 } : new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            var centre = P(2, 1.25f, 1.5f);
            Quad(P(0, 0, 0), P(4, 0, 0), P(4, 0, 3), P(0, 0, 3), centre);             // floor
            Quad(P(0, 2.5f, 0), P(4, 2.5f, 0), P(4, 2.5f, 3), P(0, 2.5f, 3), centre); // ceiling
            Quad(P(0, 0, 0), P(4, 0, 0), P(4, 2.5f, 0), P(0, 2.5f, 0), centre);       // wall z = 0
            Quad(P(0, 0, 3), P(4, 0, 3), P(4, 2.5f, 3), P(0, 2.5f, 3), centre);       // wall z = 3
            Quad(P(0, 0, 0), P(0, 0, 3), P(0, 2.5f, 3), P(0, 2.5f, 0), centre);       // wall x = 0
            Quad(P(4, 0, 0), P(4, 0, 3), P(4, 2.5f, 3), P(4, 2.5f, 0), centre);       // wall x = 4
            var pos = positions.ToArray();
            var idx = indices.ToArray();
            var geo = new SpaceGeometry { Positions = pos, Bvh = new Bvh(pos, idx), BoundsMin = new Vec3(0, 0, 0), BoundsMax = new Vec3(4, 2.5f, 3) };
            geo.Parts.Add(new GeometryPart { Material = "m", Indices = idx });
            return geo;
        }

        static readonly Vec3 Eye = new Vec3(2, 1.5f, 2.5f);

        /// <summary>Points the cursor at <paramref name="target"/> from <paramref name="eye"/>.</summary>
        static void Hover(MeasureTool tool, Vec3 target, Vec3? eye = null, bool shift = false, bool alt = false, MeasureView view = MeasureView.Inside)
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
            var f = MeshFeatures.Build(Room());
            Assert.AreEqual(8, f.Corners.Count);
            Assert.AreEqual(12, f.EdgeStart.Count, "only the 12 box edges are creases (face diagonals are flat)");
            Assert.IsTrue(f.NearestCorner(new Vec3(0.03f, 0.02f, 0.01f), 0.1f, out var corner));
            Assert.AreEqual(0f, (corner - new Vec3(0, 0, 0)).Length, 1e-4f);
            Assert.IsTrue(f.NearestEdgePoint(new Vec3(1.5f, 0.04f, 0.03f), 0.1f, out var onEdge));
            Assert.AreEqual(0f, (onEdge - new Vec3(1.5f, 0, 0)).Length, 1e-4f);
        }

        [TestCase(0)]
        [TestCase(20)]
        [TestCase(-30)]
        public void WallAxesFollowTheRoom(double degrees)
        {
            var tool = new MeasureTool(Room(degrees));
            double r = degrees * Math.PI / 180;
            Assert.AreEqual(1f, Math.Abs(Vec3.Dot(tool.AxisX, new Vec3((float)Math.Cos(r), 0, (float)Math.Sin(r)))), 1e-3f);
            Assert.AreEqual(0f, Vec3.Dot(tool.AxisX, tool.AxisZ), 1e-5f);
        }

        [Test]
        public void ContinuousLinesUntilEscape()
        {
            var tool = ReadyTool(Room());
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
            var tool = ReadyTool(Room());
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
            var tool = ReadyTool(Room());
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
            var tool = ReadyTool(Room());
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
            var tool = ReadyTool(Room());
            var above = new Vec3(2, 20, 1.5f);
            Hover(tool, new Vec3(1, 0, 1), above, view: MeasureView.FloorPlan); tool.Click();
            Hover(tool, new Vec3(3, 0, 2), above, view: MeasureView.FloorPlan); tool.Click();
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
            var tool = ReadyTool(Room());
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
            var tool = ReadyTool(Room());
            tool.AddLine(new[] { new Vec3(1, 0, 1), new Vec3(3, 0, 1) });
            var draw = new MeasureDrawList();
            var outside = new Vec3(2, 1, -2); // behind the z = 0 wall
            Hover(tool, new Vec3(2, 1, 0), outside);
            tool.BuildDrawList(outside, draw);
            Assert.IsTrue(draw.Segments.TrueForAll(s => s.Occluded), "the wall hides the line inside");

            Hover(tool, new Vec3(2, 1, 0), outside, view: MeasureView.Dollhouse);
            tool.BuildDrawList(outside, draw);
            Assert.IsTrue(draw.Segments.TrueForAll(s => !s.Occluded), "in dollhouse you see through walls from behind");
        }
    }
}
