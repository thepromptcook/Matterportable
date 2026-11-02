using System.Collections.Generic;
using Matterportable.Core;
using NUnit.Framework;

namespace Matterportable.Tests
{
    /// <summary>The shared viewer (Navigator, PanoramaScheduler) that Unity, Godot and the web all run.</summary>
    public class ViewerTests
    {
        sealed class FakeLoader : IPanoramaLoader
        {
            public PanoramaScheduler Scheduler;
            public readonly List<(int sweep, int size)> Pending = new List<(int, int)>();
            public readonly List<int> Unloaded = new List<int>(), Depth = new List<int>(), Clean = new List<int>();
            public void Load(int sweep, PanoTier tier) => Pending.Add((sweep, tier.Size));
            public void LoadClean(int sweep, IReadOnlyList<string> faces, IReadOnlyList<string> painted) => Clean.Add(sweep);
            public void ComputeDepth(int sweep) => Depth.Add(sweep);
            public void Unload(int sweep) => Unloaded.Add(sweep);

            public void CompleteAll()
            {
                var done = Pending.ToArray();
                Pending.Clear();
                foreach (var (sweep, size) in done) Scheduler.Loaded(sweep, size);
                foreach (var d in Depth) Scheduler.DepthReady(d);
                Depth.Clear();
                foreach (var c in Clean) Scheduler.CleanLoaded(c, 2048);
                Clean.Clear();
            }
        }

        sealed class Viewer
        {
            public readonly SpaceModel Space;
            public readonly ViewerSettings Settings = new ViewerSettings();
            public readonly FakeLoader Loader = new FakeLoader();
            public readonly PanoramaScheduler Panos;
            public readonly Navigator Nav;

            public Viewer(int start = 0, SpaceModel space = null)
            {
                Space = space ?? TestSpaces.RoomModel();
                Panos = new PanoramaScheduler(Space.Sweeps, Settings, Loader, hasMesh: true);
                Loader.Scheduler = Panos;
                Nav = new Navigator(Space, Settings, Panos) { StartSweep = start };
            }

            /// <summary>Runs frames of 1/60 s; loads complete at the end of each frame.</summary>
            public void Frames(int count, NavigatorInput input = default)
            {
                for (int i = 0; i < count; i++)
                {
                    input.DeltaTime = 1f / 60f;
                    input.PixelAngle = 0.0015f;
                    input.ViewportHeight = 900;
                    Nav.Update(input);
                    Panos.Update(input.DeltaTime);
                    Loader.CompleteAll();
                    input.Pressed = NavKeys.None;
                    input.ButtonsDown = input.ButtonsUp = 0;
                }
            }

            /// <summary>Presses and releases the left button without moving, with the pointer ray aimed at a point.</summary>
            public void ClickAt(Vec3 target)
            {
                var input = new NavigatorInput { PointerOver = true, PointerX = 500, PointerY = 400, RayOrigin = Nav.CameraPosition, RayDirection = (target - Nav.CameraPosition).Normalized };
                input.ButtonsDown = input.ButtonsHeld = 1;
                Frames(1, input);
                input.ButtonsHeld = 0;
                input.ButtonsUp = 1;
                Frames(1, input);
            }
        }

        [Test]
        public void SchedulerLoadsThePreviewFirstThenTheFullSize()
        {
            var v = new Viewer();
            v.Panos.RequestProgressive(0, 2048, 0);
            v.Panos.Update(0.016f);
            CollectionAssert.AreEqual(new[] { (0, 512) }, v.Loader.Pending, "one load per sweep at a time: the preview first");
            v.Loader.CompleteAll();
            Assert.AreEqual(512, v.Panos.Size(0));
            v.Panos.Update(0.016f);
            CollectionAssert.AreEqual(new[] { (0, 2048) }, v.Loader.Pending);
            v.Loader.CompleteAll();
            Assert.AreEqual(2048, v.Panos.Size(0));
            Assert.IsFalse(v.Panos.Loaded(0, 512), "a smaller tier arriving late is discarded");
        }

        [Test]
        public void SchedulerDropsTheLeastRecentlyUsedButNeverThePinned()
        {
            var v = new Viewer();
            v.Settings.PanoMemoryBudgetMB = 64; // less than one 2048 px panorama
            int pinned = 0;
            v.Panos.IsPinned = s => s == pinned;
            v.Panos.Request(0, 2048, 0);
            v.Panos.Update(0.016f);
            v.Loader.CompleteAll();
            v.Panos.Update(0.016f);
            CollectionAssert.IsEmpty(v.Loader.Unloaded, "the pinned panorama stays even over budget");
            pinned = 1;
            v.Panos.Request(1, 2048, 0);
            v.Panos.Update(0.016f);
            v.Loader.CompleteAll();
            v.Panos.Update(0.016f);
            CollectionAssert.AreEqual(new[] { 0 }, v.Loader.Unloaded);
            v.Settings.DepthMapSize = 4096; // a depth cube without photos (they were dropped first), over budget on its own
            v.Panos.RequestDepth(2);
            v.Loader.CompleteAll();
            v.Panos.Update(0.016f);
            CollectionAssert.AreEqual(new[] { 0, 2 }, v.Loader.Unloaded);
        }

        [Test]
        public void StartsAtTheStartSweepWithMarkersOnTheOthers()
        {
            var v = new Viewer(start: 1);
            v.Frames(5);
            Assert.AreEqual(1, v.Nav.CurrentSweep);
            Assert.Less((v.Nav.CameraPosition - v.Space.Sweeps[1].Position).Length, 1e-4f);
            Assert.AreEqual(2, v.Nav.Markers.Count, "both other sweeps are in view");
            Assert.AreEqual(1, v.Nav.View.PanoA, "the current panorama is bound once loaded");
        }

        [Test]
        public void StepKeysMoveToTheSweepInThatDirection()
        {
            var v = new Viewer();
            v.Frames(2);
            v.Frames(1, new NavigatorInput { Pressed = NavKeys.Right }); // facing +Z, so sweep 1 (+X) is to the right
            v.Frames(10);
            Assert.IsTrue(v.Nav.IsMoving);
            Assert.AreEqual(0, v.Nav.View.PanoA);
            Assert.AreEqual(1, v.Nav.View.PanoB);
            Assert.That(v.Nav.View.Blend, Is.InRange(0f, 1f));
            v.Frames(60);
            Assert.AreEqual(1, v.Nav.CurrentSweep);
            Assert.Less((v.Nav.CameraPosition - v.Space.Sweeps[1].Position).Length, 1e-3f);
        }

        [Test]
        public void AtASweepThePhotoShowsWithoutTheDepthTest()
        {
            var v = new Viewer();
            v.Frames(5);
            Assert.AreEqual(0, v.Nav.View.DepthA, "the depth cube is ready");
            Assert.IsFalse(v.Nav.View.UseDepth, "nothing the camera sees is hidden from the sweep it stands at");
            v.Frames(1, new NavigatorInput { Pressed = NavKeys.Right });
            v.Frames(10);
            Assert.IsTrue(v.Nav.IsMoving);
            Assert.IsTrue(v.Nav.View.UseDepth, "on the way, each panorama gives way where its sweep can't see");
        }

        [Test]
        public void ClickingTheFloorNearAMarkerMovesThere()
        {
            var v = new Viewer(start: 1);
            v.Frames(3);
            v.ClickAt(new Vec3(3, 0, 2.1f));
            v.Frames(90);
            Assert.AreEqual(2, v.Nav.CurrentSweep);
        }

        [Test]
        public void DraggingTurnsLikeGrabbingThePhoto()
        {
            var v = new Viewer();
            v.Frames(2);
            var input = new NavigatorInput { PointerOver = true, PointerX = 500, PointerY = 400, ButtonsDown = 1, ButtonsHeld = 1 };
            v.Frames(1, input);
            for (int i = 1; i <= 10; i++)
            {
                input.PointerX = 500 + 10 * i;
                v.Frames(1, input);
            }
            input.ButtonsHeld = 0;
            input.ButtonsUp = 1;
            v.Frames(1, input);
            v.Frames(60);
            var forward = v.Nav.CameraRotation * Vec3.Forward;
            Assert.Less(forward.X, -0.1f, "dragging right turns the view left, so the scene follows the cursor");
            Assert.IsFalse(v.Nav.Clicked);
            Assert.AreEqual(0, v.Nav.CurrentSweep, "a drag is not a click");
        }

        [Test]
        public void DollhouseAndBackInside()
        {
            var v = new Viewer();
            v.Frames(2);
            v.Frames(1, new NavigatorInput { Pressed = NavKeys.Dollhouse });
            v.Frames(80);
            Assert.AreEqual(ViewMode.Dollhouse, v.Nav.Mode);
            Assert.IsTrue(v.Nav.CullBack);
            Assert.Greater(v.Nav.CameraPosition.Y, 4f, "the dollhouse camera looks down from above");
            v.Frames(1, new NavigatorInput { Pressed = NavKeys.Inside });
            v.Frames(80);
            Assert.AreEqual(ViewMode.Inside, v.Nav.Mode);
            Assert.IsFalse(v.Nav.CullBack);
            Assert.Less((v.Nav.CameraPosition - v.Space.Sweeps[0].Position).Length, 1e-3f);
        }

        [Test]
        public void MeasureModeKeepsClicksForTheMeasureTool()
        {
            var v = new Viewer(start: 1);
            v.Frames(3);
            v.Nav.Measuring = true;
            var input = new NavigatorInput { PointerOver = true, RayOrigin = v.Nav.CameraPosition, RayDirection = (new Vec3(3, 0, 2.1f) - v.Nav.CameraPosition).Normalized, ButtonsDown = 1, ButtonsHeld = 1 };
            v.Frames(1, input);
            input.ButtonsHeld = 0;
            input.ButtonsUp = 1;
            v.Nav.Update(input);
            Assert.IsTrue(v.Nav.Clicked, "the engine routes this click to the measure tool");
            v.Frames(60);
            Assert.AreEqual(1, v.Nav.CurrentSweep);
            CollectionAssert.IsEmpty(v.Nav.Markers, "markers hide while measuring");
        }

        [Test]
        public void WalkModeMovesFreelyAndTheWallsStopIt()
        {
            var v = new Viewer();
            v.Frames(2);
            v.Frames(1, new NavigatorInput { Pressed = NavKeys.Walk });
            Assert.AreEqual(ViewMode.Walk, v.Nav.Mode);
            v.Frames(180, new NavigatorInput { Held = NavKeys.Forward }); // facing +Z for 3 s
            Assert.AreEqual(3 - v.Settings.BodyRadius, v.Nav.CameraPosition.Z, 0.03f, "stopped by the wall");
            Assert.AreEqual(1f, v.Nav.CameraPosition.X, 0.01f);
            Assert.AreEqual(1.5f, v.Nav.CameraPosition.Y, 0.01f, "eyes at the capture camera's height above the floor");
            CollectionAssert.IsEmpty(v.Nav.Markers, "no sweep markers while walking");
        }

        [Test]
        public void WalkingCrossFadesTheNearestPanoramasWithoutJumps()
        {
            var v = new Viewer();
            v.Frames(2);
            v.Nav.Teleport(0, 90); // facing +X, towards sweep 1
            v.Nav.SetMode(ViewMode.Walk);
            float Weight(int sweep)
            {
                var view = v.Nav.View;
                return view.PanoA == sweep ? 1 - view.Blend : view.PanoB == sweep ? view.Blend : 0;
            }
            float last = Weight(1);
            Assert.Less(last, 0.05f, "at sweep 0 its own photo shows");
            for (int i = 0; i < 70; i++)
            {
                v.Frames(1, new NavigatorInput { Held = NavKeys.Forward });
                Assert.Less(System.Math.Abs(Weight(1) - last), 0.15f, "no pops");
                last = Weight(1);
            }
            Assert.Greater(last, 0.9f, "near sweep 1 its photo takes over");
            Assert.AreEqual(1, v.Nav.CurrentSweep);
        }

        [Test]
        public void WalkModePicksUpFurnitureCarriesItAndPutsItDown()
        {
            var table = (new Vec3(1.8f, 0, 1.6f), new Vec3(2.6f, 0.75f, 2.4f));
            var v = new Viewer(space: TestSpaces.RoomModel(table));
            Assert.AreEqual(1, v.Space.Props.Count, "the table is a prop");
            v.Frames(2);
            v.Nav.SetMode(ViewMode.Walk);
            v.Nav.Look(45, 35); // from sweep 0 at (1, 1.5, 1), down towards the table
            v.Frames(30);
            Assert.AreEqual(0, v.Nav.HighlightProp, "the table is under the view");
            StringAssert.Contains("F or click: pick it up", ViewerText.PropHint(v.Nav));
            v.Frames(1, new NavigatorInput { Pressed = NavKeys.Use });
            Assert.AreEqual(0, v.Nav.Props.Carried);
            v.Frames(30, new NavigatorInput { Held = NavKeys.TurnLeft }); // turn left: the table swings round with the view
            v.Frames(1, new NavigatorInput { Pressed = NavKeys.Use });
            Assert.AreEqual(-1, v.Nav.Props.Carried, "put down");
            var pose = v.Nav.Props.Poses[0];
            var middle = pose.Apply(new Vec3(2.2f, 0, 2f));
            Assert.Less(middle.X, 2.2f - 0.3f, "moved round to the left (towards -X) with the view");
            Assert.AreEqual(0f, middle.Y, 0.02f, "back on the floor");
            Assert.IsTrue(v.Nav.Props.AnyMoved);
            Assert.IsTrue(v.Nav.Props.Raycast(new Vec3(2.2f, 1.5f, 2f), Vec3.Down, 2f, out var hit, out int prop) && prop == -1 && hit.Point.Y < 0.05f,
                "where it stood, the floor shows");
        }

        [Test]
        public void MovedFurnitureBringsTheCleanPhotosAndEmptyRoomsTakesItAway()
        {
            var model = TestSpaces.RoomModel((new Vec3(1.8f, 0, 1.6f), new Vec3(2.6f, 0.75f, 2.4f)));
            foreach (var sweep in model.Sweeps) sweep.CleanFaces = new[] { "c0", "c1", "c2", "c3", "c4", "c5" };
            var v = new Viewer(space: model);
            v.Frames(2);
            v.Nav.SetMode(ViewMode.Walk);
            v.Frames(10);
            Assert.AreEqual(-1, v.Nav.View.CleanA, "nothing moved: the photos as they are");
            v.Nav.Props.Carry(0, v.Nav.CameraPosition, 45);
            v.Frames(5, new NavigatorInput { Held = NavKeys.TurnLeft });
            v.Nav.Props.Drop();
            v.Frames(5);
            Assert.AreEqual(v.Nav.View.PanoA, v.Nav.View.CleanA, "a moved prop brings the clean photos of the shown sweep");
            Assert.IsFalse(v.Nav.View.EmptyRooms);

            v.Frames(1, new NavigatorInput { Pressed = NavKeys.EmptyRooms });
            Assert.IsTrue(v.Nav.EmptyRooms);
            Assert.IsTrue(v.Nav.View.EmptyRooms);
            Assert.IsTrue(v.Nav.Props.Moved(0), "hidden props count as moved: their photo pixels are refused");
            var at = v.Nav.Props.Poses[0].Apply(new Vec3(2.2f, 0.5f, 2f));
            Assert.IsTrue(v.Nav.Props.Raycast(new Vec3(at.X, 1.5f, at.Z), Vec3.Down, 2f, out var hit, out int prop));
            Assert.AreEqual(-1, prop, "nothing in the way where the table is");
            Assert.Less(hit.Point.Y, 0.05f);
            v.Frames(3);
            Assert.AreEqual(-1, v.Nav.HighlightProp);
            Assert.IsNull(ViewerText.PropHint(v.Nav));
        }

        [Test]
        public void WalkModeFromDollhouseAndBackToInside()
        {
            var v = new Viewer(start: 1);
            v.Frames(2);
            v.Nav.SetMode(ViewMode.Dollhouse);
            v.Frames(80);
            v.Nav.SetMode(ViewMode.Walk);
            v.Frames(80);
            Assert.AreEqual(ViewMode.Walk, v.Nav.Mode, "flew in, then walks");
            Assert.Less((v.Nav.CameraPosition - v.Space.Sweeps[1].Position).Length, 0.02f);
            v.Frames(30, new NavigatorInput { Held = NavKeys.Left });
            v.Nav.SetMode(ViewMode.Inside);
            v.Frames(80);
            Assert.AreEqual(ViewMode.Inside, v.Nav.Mode);
            Assert.Less((v.Nav.CameraPosition - v.Space.Sweeps[v.Nav.CurrentSweep].Position).Length, 1e-3f, "moved to the nearest sweep");
        }
    }
}
