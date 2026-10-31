using Godot;

namespace Matterportable
{
    /// <summary>
    /// Tunables for navigation, rendering and streaming (mirrors the Unity MatterportViewerSettings; keep the
    /// defaults in sync). Read every frame, so edits in the remote inspector apply live.
    /// </summary>
    [GlobalClass]
    public partial class ViewerSettings : Resource
    {
        [ExportGroup("Look")]
        [Export(PropertyHint.Range, "0.1,4")] public float DragSensitivity = 1f;
        [Export] public float KeyTurnSpeed = 100f;
        [Export(PropertyHint.Range, "1,40")] public float LookSmoothing = 18f;
        [Export(PropertyHint.Range, "20,110")] public float DefaultFov = 75f;
        [Export] public float MinFov = 25f;
        [Export] public float MaxFov = 95f;
        [Export] public float ZoomStep = 6f;

        [ExportGroup("Moving between sweeps")]
        [Export] public float TransitionBase = 0.5f;
        [Export] public float TransitionPerMeter = 0.12f;
        [Export] public float TransitionMax = 1.6f;
        [Export] public float ClickDragThreshold = 6f;
        [Export(PropertyHint.Range, "10,90")] public float StepMaxAngle = 50f;
        [Export] public float StepMaxDistance = 8f;
        [Export] public float ClickSnapRadius = 1.2f;

        [ExportGroup("Panoramas")]
        [Export] public int PanoMaxSize = 2048;
        [Export(PropertyHint.Range, "0,12")] public int PrefetchNeighbors = 4;
        [Export] public int PanoMemoryBudgetMB = 1200;
        [Export] public int DepthMapSize = 256;

        [ExportGroup("Markers and cursor")]
        [Export] public bool ShowMarkers = true;
        [Export] public float MarkerRadius = 0.2f;
        [Export] public Color MarkerColor = new Color(1f, 1f, 1f, 0.9f);
        [Export] public float MarkerMaxDistance = 10f;
        [Export] public bool ShowReticle = true;
        [Export] public float ReticleRadius = 0.28f;

        [ExportGroup("Dollhouse and floor plan")]
        [Export] public float DollhouseFov = 45f;
        [Export] public float FloorplanFov = 20f;
        [Export] public float OrbitSensitivity = 0.25f;
        [Export] public float ModeTransitionTime = 1.1f;
        [Export] public Color BackgroundColor = new Color(0.11f, 0.11f, 0.12f);

        [ExportGroup("Diagnostics")]
        [Export] public bool VerboseLogging;

        public float TransitionDuration(float meters) =>
            Mathf.Clamp(TransitionBase + TransitionPerMeter * meters, TransitionBase, Mathf.Max(TransitionBase, TransitionMax));
    }
}
