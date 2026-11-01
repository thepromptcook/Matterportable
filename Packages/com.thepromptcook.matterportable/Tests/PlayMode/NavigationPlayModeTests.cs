using System;
using System.Collections;
using System.IO;
using Matterportable.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Matterportable.Tests
{
    /// <summary>
    /// End-to-end check in Play mode (Window ▸ General ▸ Test Runner ▸ PlayMode): loads a small generated space,
    /// moves between sweeps, switches view modes. Catches regressions that compile checks and Core tests can't.
    /// </summary>
    public class NavigationPlayModeTests
    {
        string _root;
        MatterportRig _rig;

        [SetUp]
        public void SetUp()
        {
            // The launcher bootstraps itself into empty scenes; keep the test scene to just our rig.
            foreach (var launcher in MpUtil.FindAll<MatterportLauncher>()) UnityEngine.Object.Destroy(launcher.gameObject);
            _root = Path.Combine(Path.GetTempPath(), "mpt-play-" + Guid.NewGuid().ToString("N"));
            var space = SyntheticSpace.Generate(new SyntheticOptions { PanoSizes = new[] { 32, 64 }, TextureSize = 64 });
            space.WriteTo(CacheLayout.SpaceDir(_root, space.Manifest.ModelId));
        }

        [TearDown]
        public void TearDown()
        {
            _rig?.Destroy();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        static IEnumerator WaitUntil(Func<bool> condition, float seconds, string what)
        {
            float start = Time.realtimeSinceStartup;
            while (!condition())
            {
                if (Time.realtimeSinceStartup - start > seconds) Assert.Fail("Timed out waiting for " + what);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator LoadsMovesAndSwitchesViews()
        {
            _rig = MatterportRig.Create(DemoSpace.ModelId, _root);
            var space = _rig.Space;

            yield return WaitUntil(() => space.IsLoaded || space.Error != null, 30f, "the space to load");
            Assert.IsNull(space.Error, space.Error);
            Assert.AreEqual(10, space.Model.Sweeps.Count);
            yield return WaitUntil(() => space.Scheduler.IsReady(0), 10f, "the first panorama");
            yield return null;
            var nav = _rig.Navigator.Core;
            Assert.AreEqual(0, nav.CurrentSweep);
            Assert.Less(Vector3.Distance(_rig.Camera.transform.position, space.SweepWorldPosition(0)), 0.01f);

            nav.GoTo(1);
            yield return WaitUntil(() => nav.CurrentSweep == 1 && !nav.IsMoving && !nav.IsWaitingForPanorama, 10f, "the move to sweep 1");
            yield return null;
            Assert.Less(Vector3.Distance(_rig.Camera.transform.position, space.SweepWorldPosition(1)), 0.01f);

            nav.SetMode(ViewMode.Dollhouse);
            yield return WaitUntil(() => nav.Mode == ViewMode.Dollhouse && !nav.IsChangingMode, 10f, "dollhouse view");
            nav.SetMode(ViewMode.Inside);
            yield return WaitUntil(() => nav.Mode == ViewMode.Inside && !nav.IsChangingMode, 10f, "inside view");
            Assert.AreEqual(1, nav.CurrentSweep);
        }
    }
}
