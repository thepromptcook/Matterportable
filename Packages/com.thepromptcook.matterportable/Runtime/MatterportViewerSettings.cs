using UnityEngine;

namespace Matterportable
{
    /// <summary>
    /// Tunables for navigation, rendering and streaming. Every value is read each frame, so editing the asset
    /// during Play mode takes effect immediately.
    /// </summary>
    [CreateAssetMenu(menuName = "Matterportable/Viewer Settings", fileName = "MatterportViewerSettings")]
    public sealed class MatterportViewerSettings : ScriptableObject
    {
        [Header("Look")]
        [Tooltip("1 = the point under the cursor stays under the cursor while dragging (like Matterport).")]
        [Range(0.1f, 4f)] public float dragSensitivity = 1f;
        [Tooltip("Off: drag the panorama (Matterport style). On: drag the camera (first-person style).")]
        public bool invertDrag;
        [Tooltip("Degrees per second when turning with the arrow keys or Q/E.")]
        public float keyTurnSpeed = 100f;
        [Tooltip("Higher = snappier camera response.")]
        [Range(1f, 40f)] public float lookSmoothing = 18f;
        [Range(20f, 110f)] public float defaultFov = 75f;
        [Range(10f, 60f)] public float minFov = 25f;
        [Range(60f, 120f)] public float maxFov = 95f;
        [Tooltip("Field-of-view change per mouse-wheel notch, in degrees.")]
        public float zoomStep = 6f;

        [Header("Moving between sweeps")]
        public float transitionBase = 0.5f;
        public float transitionPerMeter = 0.12f;
        public float transitionMax = 1.6f;
        public AnimationCurve transitionEase = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [Tooltip("Mouse movement (pixels) that turns a click into a drag.")]
        public float clickDragThreshold = 6f;
        [Tooltip("W/A/S/D only step to sweeps within this angle of the chosen direction.")]
        [Range(10f, 90f)] public float stepMaxAngle = 50f;
        public float stepMaxDistance = 8f;
        [Tooltip("Clicks within this distance of a sweep marker go straight to that sweep.")]
        public float clickSnapRadius = 1.2f;

        [Header("Panoramas")]
        [Tooltip("Largest panorama face size to stream (limited by what was downloaded).")]
        public int panoMaxSize = 2048;
        [Tooltip("Neighbouring sweeps to preload so clicks start instantly.")]
        [Range(0, 12)] public int prefetchNeighbors = 4;
        [Tooltip("GPU memory budget for cached panoramas, in MB.")]
        public int panoMemoryBudgetMB = 1200;
        [Tooltip("Per-face resolution of the occlusion depth maps used during transitions (0 disables).")]
        public int depthMapSize = 256;

        [Header("Markers & cursor")]
        public bool showMarkers = true;
        public float markerRadius = 0.2f;
        public Color markerColor = new Color(1f, 1f, 1f, 0.9f);
        public float markerMaxDistance = 10f;
        public bool showReticle = true;
        public float reticleRadius = 0.28f;

        [Header("Dollhouse & floor plan")]
        [Range(20f, 80f)] public float dollhouseFov = 45f;
        [Range(5f, 40f)] public float floorplanFov = 20f;
        public float orbitSensitivity = 0.25f;
        public float modeTransitionTime = 1.1f;
        public Color backgroundColor = new Color(0.11f, 0.11f, 0.12f, 1f);

        [Header("Mesh")]
        [Tooltip("Compress mesh textures after loading (much less GPU memory, slightly slower first load).")]
        public bool compressMeshTextures = true;
        [Tooltip("Adds a MeshCollider so your own gameplay code can raycast the space with Physics.")]
        public bool addMeshCollider;

        public float TransitionDuration(float meters) =>
            Mathf.Clamp(transitionBase + transitionPerMeter * meters, transitionBase, Mathf.Max(transitionBase, transitionMax));

        static MatterportViewerSettings _defaults;

        /// <summary>Shared defaults used when no settings asset is assigned.</summary>
        public static MatterportViewerSettings Defaults
        {
            get
            {
                if (_defaults == null)
                {
                    _defaults = CreateInstance<MatterportViewerSettings>();
                    _defaults.hideFlags = HideFlags.HideAndDontSave;
                    _defaults.name = "Default Matterport Viewer Settings";
                }
                return _defaults;
            }
        }
    }
}
