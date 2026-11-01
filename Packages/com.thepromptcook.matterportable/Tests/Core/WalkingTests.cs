using Matterportable.Core;
using NUnit.Framework;

namespace Matterportable.Tests
{
    /// <summary>The Walk-mode body against hand-made geometry: the scanned mesh is the collider.</summary>
    public class WalkingTests
    {
        const float Radius = 0.25f, Step = 0.3f, EyeHeight = 1.5f;

        static Walker At(SpaceGeometry geo, float x, float z, float floor = 0) =>
            new Walker(geo.Bvh, new Vec3(x, floor + EyeHeight, z), Radius, Step);

        /// <summary>Walks a distance in a direction, 5 cm per 1/60 s frame.</summary>
        static void Walk(Walker w, Vec3 direction, float meters)
        {
            var d = direction.Normalized * 0.05f;
            for (int i = 0, n = (int)System.Math.Round(meters / 0.05f); i < n; i++) w.Move(d, 1f / 60f);
        }

        [Test]
        public void WallsStopTheBodyAndItSlidesAlongThem()
        {
            var w = At(TestSpaces.Room(), 2, 1.5f);
            Assert.AreEqual(0f, w.Feet.Y, 1e-4f, "the feet start on the floor below the eyes");
            Walk(w, Vec3.Right, 5);
            Assert.AreEqual(4 - Radius, w.Feet.X, 0.02f, "stops one body radius from the wall");
            Walk(w, new Vec3(1, 0, 1), 5);
            Assert.AreEqual(4 - Radius, w.Feet.X, 0.02f);
            Assert.AreEqual(3 - Radius, w.Feet.Z, 0.02f, "slides along the wall into the corner");
        }

        [Test]
        public void FurnitureBlocksButALowPlatformIsSteppedOnto()
        {
            var table = TestSpaces.Room(0, (new Vec3(2.5f, 0, 1), new Vec3(3.5f, 0.75f, 2)));
            var w = At(table, 1, 1.5f);
            Walk(w, Vec3.Right, 3);
            Assert.AreEqual(2.5f - Radius, w.Feet.X, 0.03f, "the table blocks");
            Assert.AreEqual(0f, w.Feet.Y, 1e-4f);

            var platform = TestSpaces.Room(0, (new Vec3(2.5f, 0, 0.5f), new Vec3(3.5f, 0.2f, 2.5f)));
            w = At(platform, 1, 1.5f);
            Walk(w, Vec3.Right, 2);
            Assert.AreEqual(3f, w.Feet.X, 0.03f);
            Assert.AreEqual(0.2f, w.Feet.Y, 1e-3f, "a 20 cm platform is a step");
        }

        [Test]
        public void ClimbsStairsAndComesBackDown()
        {
            var stairs = TestSpaces.Room(0,
                (new Vec3(1.5f, 0, 1.0f), new Vec3(2.5f, 0.18f, 1.3f)),
                (new Vec3(1.5f, 0, 1.3f), new Vec3(2.5f, 0.36f, 1.6f)),
                (new Vec3(1.5f, 0, 1.6f), new Vec3(2.5f, 0.54f, 1.9f)),
                (new Vec3(1.5f, 0, 1.9f), new Vec3(2.5f, 0.72f, 3.0f))); // landing
            var w = At(stairs, 2, 0.4f);
            Walk(w, Vec3.Forward, 2.5f);
            Assert.AreEqual(0.72f, w.Feet.Y, 1e-3f, "up on the landing");
            Assert.AreEqual(3 - Radius, w.Feet.Z, 0.02f);
            Walk(w, -Vec3.Forward, 2.5f);
            Assert.AreEqual(0f, w.Feet.Y, 1e-3f, "back down on the floor");
            Assert.AreEqual(Radius, w.Feet.Z, 0.02f);
        }

        [Test]
        public void LowCeilingsBlockAndHighOnesDoNot()
        {
            var low = TestSpaces.Room(0, (new Vec3(1.8f, 1.4f, 0), new Vec3(2.2f, 1.6f, 3)));
            var w = At(low, 1, 1.5f);
            Walk(w, Vec3.Right, 2);
            Assert.Less(w.Feet.X, 1.8f, "a beam at head height blocks");

            var high = TestSpaces.Room(0, (new Vec3(1.8f, 1.75f, 0), new Vec3(2.2f, 1.9f, 3)));
            w = At(high, 1, 1.5f);
            Walk(w, Vec3.Right, 2);
            Assert.AreEqual(3f, w.Feet.X, 0.03f, "the body passes under a beam above its head");
        }

        [Test]
        public void StartsOnTheFloorUnderACameraAboveATable()
        {
            var room = TestSpaces.Room(0, (new Vec3(1.5f, 0, 1), new Vec3(2.5f, 0.75f, 2)));
            var w = new Walker(room.Bvh, new Vec3(2.45f, 1.5f, 1.5f), Radius, Step); // the camera is over the table's edge
            Assert.IsTrue(w.Standing);
            Assert.AreEqual(0f, w.Feet.Y, 1e-4f, "the table top is skipped: the feet are on the floor");
            Assert.AreEqual(1.5f, w.EyeHeight, 1e-4f);
            Assert.GreaterOrEqual(w.Feet.X, 2.5f + Radius - 0.02f, "stepped out from under the table");
            Assert.Less(w.Feet.X, 2.45f + 0.5f);
        }

        [Test]
        public void NeverWalksOffTheScanButFallsOffLedges()
        {
            var island = TestSpaces.Boxes((new Vec3(0, -0.1f, 0), new Vec3(2, 0, 2)));
            Assert.IsFalse(At(island, 4, 4).Standing, "nothing to stand on out there");
            var w = At(island, 1, 1);
            Assert.IsTrue(w.Standing);
            Walk(w, Vec3.Right, 3);
            Assert.That(w.Feet.X, Is.InRange(1.8f, 2f + Radius), "stops at the edge of the scanned floor");
            Assert.AreEqual(0f, w.Feet.Y, 1e-4f);

            var ledge = TestSpaces.Boxes((new Vec3(0, 0, 0), new Vec3(2, 1, 2)), (new Vec3(0, -0.1f, 0), new Vec3(5, 0, 2)));
            w = At(ledge, 1, 1, floor: 1);
            Assert.AreEqual(1f, w.Feet.Y, 1e-4f);
            Walk(w, Vec3.Right, 1.6f);
            for (int i = 0; i < 60; i++) w.Move(Vec3.Zero, 1f / 60f);
            Assert.AreEqual(0f, w.Feet.Y, 1e-4f, "fell a metre to the floor below");
            Assert.IsTrue(w.Grounded);
        }
    }
}
