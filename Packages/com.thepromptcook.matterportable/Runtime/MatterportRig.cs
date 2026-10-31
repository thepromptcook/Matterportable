using UnityEngine;

namespace Matterportable
{
    /// <summary>
    /// A space plus the camera that explores it (navigator + HUD). The one place rigs are built: used by the in-game
    /// launcher, the editor scene builder and the PlayMode test.
    /// </summary>
    public sealed class MatterportRig
    {
        public MatterportSpace Space { get; }
        public MatterportNavigator Navigator { get; }
        public Camera Camera { get; }

        MatterportRig(MatterportSpace space, MatterportNavigator navigator, Camera camera)
        {
            Space = space;
            Navigator = navigator;
            Camera = camera;
        }

        /// <summary>Creates the rig. In Play mode the space starts loading next frame; in Edit mode it previews.</summary>
        public static MatterportRig Create(string link, string cacheFolder = "", MatterportViewerSettings settings = null)
        {
            var space = new GameObject("Matterport Space").AddComponent<MatterportSpace>();
            space.link = link ?? "";
            space.cacheFolder = cacheFolder ?? "";
            space.settings = settings;

            var camGo = new GameObject("Matterport Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.nearClipPlane = 0.03f;
            cam.farClipPlane = 1000f;
            cam.fieldOfView = space.Settings.defaultFov;
            camGo.AddComponent<AudioListener>();
            var nav = camGo.AddComponent<MatterportNavigator>();
            nav.space = space;
            camGo.AddComponent<MatterportHud>();

            if (!Application.isPlaying)
            {
                space.BuildEditorPreview();
                if (space.Sweeps.Count > 0)
                    camGo.transform.position = space.SweepWorldPosition(Mathf.Clamp(space.startSweep, 0, space.Sweeps.Count - 1));
            }
            return new MatterportRig(space, nav, cam);
        }

        public void Destroy()
        {
            if (Navigator != null) MpUtil.SafeDestroy(Navigator.gameObject);
            if (Space != null) MpUtil.SafeDestroy(Space.gameObject);
        }
    }
}
