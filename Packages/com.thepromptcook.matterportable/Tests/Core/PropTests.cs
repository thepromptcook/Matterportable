using System;
using Matterportable.Core;
using NUnit.Framework;

namespace Matterportable.Tests
{
    /// <summary>Finding movable furniture and doors in a scan, and moving them in Walk mode's world.</summary>
    public class PropTests
    {
        static readonly (Vec3, Vec3) Table = (new Vec3(2.5f, 0, 1f), new Vec3(3.5f, 0.75f, 2f));
        static readonly (Vec3, Vec3) Door = (new Vec3(0.02f, 0, 1f), new Vec3(0.82f, 2f, 1.04f));      // open, hinged at the wall x = 0
        static readonly (Vec3, Vec3) Bookcase = (new Vec3(3.6f, 0, 0.1f), new Vec3(4f, 2.5f, 0.9f));    // up to the ceiling
        static readonly (Vec3, Vec3) Counter = (new Vec3(0f, 0, 2.4f), new Vec3(3.5f, 0.9f, 3f));       // too long to carry
        static readonly float[] Floor = { 0f };

        [Test]
        public void FindsFurnitureAndDoorsButNotWallsOrCounters()
        {
            var room = TestSpaces.Room(0, Table, Door, Bookcase, Counter);
            var props = PropFinder.Find(room, Floor);
            Assert.AreEqual(2, props.Count, "the table and the door; the bookcase reaches the ceiling and the counter is too long");
            var table = props.Find(p => p.Kind == PropKind.Movable);
            var door = props.Find(p => p.Kind == PropKind.Door);
            Assert.IsNotNull(table);
            Assert.IsNotNull(door);
            Assert.GreaterOrEqual(table.Triangles.Length, 10, "the box's faces, all but its bottom on the floor");
            Assert.Less((table.Pivot - new Vec3(3f, 0, 1.5f)).Length, 0.03f, "the middle of its base");
            Assert.Less(door.Pivot.X, 0.1f, "hinged at the wall end");
            Assert.AreEqual(1.02f, door.Pivot.Z, 0.05f);
        }

        [Test]
        public void PropsRoundTripThroughObjectsJson()
        {
            var room = TestSpaces.Room(0, Table, Door);
            var props = PropFinder.Find(room, Floor);
            var back = PropFinder.FromJson(JsonValue.Parse(PropFinder.ToJson(props, room).ToJson(true)), room);
            Assert.AreEqual(props.Count, back.Count);
            for (int i = 0; i < props.Count; i++)
            {
                Assert.AreEqual(props[i].Kind, back[i].Kind);
                CollectionAssert.AreEqual(props[i].Triangles, back[i].Triangles);
                Assert.Less((props[i].Pivot - back[i].Pivot).Length, 1e-4f);
                Assert.Less((props[i].Max - back[i].Max).Length, 1e-5f);
            }
            var other = JsonValue.Parse(PropFinder.ToJson(props, room).ToJson(true)).Set("version", PropFinder.Version + 1);
            Assert.IsNull(PropFinder.FromJson(other, room), "another version is found again");
            Assert.IsNull(PropFinder.FromJson(JsonValue.Parse(PropFinder.ToJson(props, room).ToJson(true)), TestSpaces.Room(0, Table)), "and so is another mesh");
        }

        [Test]
        public void CarriedFurnitureFollowsTheViewAndLandsOnWhatIsBelow()
        {
            var room = TestSpaces.Room(0, Table, (new Vec3(0.5f, 0, 0.3f), new Vec3(1.5f, 0.75f, 0.9f))); // two tables
            var world = new PropWorld(room, PropFinder.Find(room, Floor));
            int table = world.Props[0].Pivot.X > 2f ? 0 : 1;
            var eye = new Vec3(2f, 1.5f, 1.5f);
            world.Carry(table, eye, 90); // facing +X, towards it
            world.Update(eye, 0, 1f / 60); // turn to face +Z
            var pivot = world.Poses[table].Apply(world.Props[table].Pivot);
            Assert.AreEqual(2f, pivot.X, 0.01f);
            Assert.Greater(pivot.Z, 2f, "in front of the eyes, short of the wall");
            Assert.Less(pivot.Z, 3f);
            Assert.IsFalse(world.Raycast(new Vec3(3f, 1.5f, 1.5f), Vec3.Down, 1.4f, out _), "nothing left where it stood");

            world.Update(eye, 228, 1f / 60); // towards the middle of the other table, at (1, 0.6)
            var feet = new Vec3(eye.X, 0, eye.Z);
            Assert.IsFalse(world.Drop(feet + new Vec3(-0.6f, 0, -0.6f), 0.25f, 1.5f), "not onto someone standing there");
            Assert.AreEqual(table, world.Carried, "still carried");
            Assert.IsTrue(world.DropRefused);
            Assert.IsTrue(world.Drop(feet, 0.25f, 1.5f));
            pivot = world.Poses[table].Apply(world.Props[table].Pivot);
            Assert.AreEqual(0.75f, pivot.Y, 0.02f, "put down on the other table");
            Assert.IsTrue(world.Raycast(new Vec3(pivot.X, 2f, pivot.Z), Vec3.Down, 1f, out _, out int hitProp) && hitProp == table, "and it collides there (its top at 1.5 m)");
        }

        [Test]
        public void PieceMasksShowWherePropsAreInThePhotos()
        {
            var model = TestSpaces.RoomModel((new Vec3(1.8f, 0, 1.6f), new Vec3(2.6f, 0.75f, 2.4f)));
            var layout = CubeFaces.DefaultMatterportLayout();
            int down = Array.FindIndex(layout, f => f.Forward == SignedAxis.NegY);
            var mask = PropMasks.PieceMask(model, 2, down, 32); // sweep 2 at (3, 1.5, 2.2) looks down; the table is at its -X side
            int table = 0, total = 0;
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    var d = layout[down].Direction((x + 0.5f) / 32, (y + 0.5f) / 32);
                    if (mask[y * 32 + x] == 0) continue;
                    total++;
                    if (d.X < 0) table++;
                }
            Assert.Greater(total, 20, "the table shows in the downward face");
            Assert.AreEqual(total, table, "and only towards it");
        }

        [Test]
        public void TheDemoHouseWritesExactCleanPhotos()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mpt-demo-clean-" + Guid.NewGuid().ToString("N"));
            try
            {
                var space = SyntheticSpace.Generate(new SyntheticOptions { PanoSizes = new[] { 16, 64 }, TextureSize = 64, CleanPhotos = true });
                space.WriteTo(dir);
                var model = SpaceModel.Load(dir, SpaceManifest.Load(dir), SpaceCalibration.LoadOrDefault(dir), null);
                Assert.Greater(model.Props.Count, 0);
                Assert.IsTrue(model.HasCleanPanoramas, "every sweep that sees furniture has clean photos");
                int changed = 0, unpainted = 0;
                for (int s = 0; s < model.Sweeps.Count; s++)
                {
                    var sweep = model.Sweeps[s];
                    if (sweep.CleanFaces == null) continue;
                    var painted = CleanPanoramas.LoadPainted(dir, sweep.PaintedProps, out int size);
                    for (int f = 0; f < 6; f++)
                    {
                        var photo = space.Panoramas[s][1][f];
                        var clean = space.CleanFaces[s][f];
                        for (int y = 0; y < 64; y++)
                            for (int x = 0; x < 64; x++)
                            {
                                int i = 3 * (y * 64 + x);
                                if (photo.Pixels[i] == clean.Pixels[i] && photo.Pixels[i + 1] == clean.Pixels[i + 1] && photo.Pixels[i + 2] == clean.Pixels[i + 2]) continue;
                                changed++;
                                if (painted[f * size * size + (y * size / 64) * size + x * size / 64] == 0) unpainted++;
                            }
                    }
                }
                Assert.Greater(changed, 100, "the furniture is gone from the clean photos");
                Assert.Less(unpainted, changed / 50, "and the painted maps say where");
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { } }
        }

        [Test]
        public void CleanPhotosReplaceOnlyThePaintedFaces()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mpt-clean-" + Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "recon", "clean"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "recon", "clean", "clean.json"),
                    "{\"faces\": [{\"sweep\": 1, \"face\": 3, \"file\": \"001/face3.jpg\", \"painted\": \"001/painted3.png\"}, {\"sweep\": 9, \"face\": 0, \"file\": \"x.jpg\"}]}");
                var model = TestSpaces.RoomModel();
                Assert.IsFalse(model.HasCleanPanoramas);
                Assert.AreEqual(1, CleanPanoramas.Apply(dir, model.Sweeps), "sweep 9 doesn't exist");
                Assert.IsNull(model.Sweeps[0].CleanFaces);
                var faces = model.Sweeps[1].CleanFaces;
                Assert.AreEqual("recon/clean/001/face3.jpg", faces[3]);
                Assert.AreEqual(model.Sweeps[1].Entry.Tiers[1].FacePath(0), faces[0], "the others are the photo's (largest tier)");
                Assert.AreEqual("recon/clean/001/painted3.png", model.Sweeps[1].PaintedProps[3], "which prop each pixel had painted out");
                Assert.IsNull(model.Sweeps[1].PaintedProps[0]);

                var map = new RgbImage(4, 4); // prop 6 (7) painted out in the bottom row
                for (int x = 0; x < 4; x++) map.Pixels[3 * x] = 7;
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "recon", "clean", "001"));
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "recon", "clean", "001", "painted3.png"), Png.Encode(map));
                var bytes = CleanPanoramas.LoadPainted(dir, model.Sweeps[1].PaintedProps, out int size);
                Assert.AreEqual(4, size);
                Assert.AreEqual(6 * 16, bytes.Length);
                Assert.AreEqual(7, bytes[3 * 16 + 2], "face 3, bottom row");
                Assert.AreEqual(0, bytes[3 * 16 + 4 * 3 + 2], "face 3, top row");
                Assert.AreEqual(0, bytes[2], "face 0 has none");
                Assert.IsTrue(model.HasCleanPanoramas);
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { } }
        }

        [Test]
        public void DoorsSwingAwayFromTheViewerAndBack()
        {
            var room = TestSpaces.Room(0, Door);
            var world = new PropWorld(room, PropFinder.Find(room, Floor));
            Assert.AreEqual(PropKind.Door, world.Props[0].Kind);
            var viewer = new Vec3(0.5f, 1.5f, 2f); // on the +Z side
            world.ToggleDoor(0, viewer);
            Assert.IsTrue(world.DoorSwung(0));
            for (int i = 0; i < 60; i++) world.Update(viewer, 0, 1f / 60);
            var end = world.Poses[0].Apply(new Vec3(0.8f, 1f, 1.02f));
            Assert.AreEqual(0.02f, end.X, 0.05f, "turned 90° about the hinge");
            Assert.Less(end.Z, 0.5f, "away from the viewer");
            world.ToggleDoor(0, viewer);
            for (int i = 0; i < 60; i++) world.Update(viewer, 0, 1f / 60);
            Assert.IsTrue(world.Poses[0].IsIdentity, "back where it was scanned");
        }
    }
}
