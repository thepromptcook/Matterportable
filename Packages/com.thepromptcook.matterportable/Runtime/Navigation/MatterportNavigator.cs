using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Matterportable
{
    /// <summary>
    /// Matterport-style camera: drag to look, click to move to a sweep (with the panorama cross-fade transition),
    /// W/A/S/D or arrow keys to step between sweeps, mouse wheel to zoom, and Dollhouse / Floor plan views.
    /// Put it on the camera; it finds the <see cref="MatterportSpace"/> automatically.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    [AddComponentMenu("Matterportable/Matterport Navigator")]
    public sealed class MatterportNavigator : MonoBehaviour
    {
        public enum ViewMode { Inside, Dollhouse, Floorplan }

        [Tooltip("Space to navigate. Found automatically when empty.")]
        public MatterportSpace space;
        public bool enableMouse = true;
        public bool enableKeyboard = true;

        public ViewMode Mode => _mode;
        public int CurrentSweep => _current;
        public bool IsMoving => _move.Active;
        public bool IsChangingMode => _anim.Active;
        public bool IsWaitingForPanorama => _pending >= 0;
        /// <summary>Selected floor in Dollhouse/Floor plan (-1 = all floors).</summary>
        public int SelectedFloor => _floor;
        public int HoverSweep { get; private set; } = -1;
        public bool AutoTour { get; set; }
        /// <summary>
        /// Screen rectangles (GUI coordinates, y down) where clicks belong to UI. Filled while UI draws
        /// (<see cref="MpGui.Block"/>), read by the next frame's Updates and cleared in LateUpdate.
        /// </summary>
        public readonly List<Rect> BlockedGuiRects = new List<Rect>();
        /// <summary>Takes over left clicks (measure mode): return true to consume the click. Also hides the sweep
        /// markers and the move cursor while set.</summary>
        public Func<Vector2, bool> ClickHandler;
        /// <summary>World height above which geometry is hidden in the current view (floor filter), else +infinity.</summary>
        public float ViewClipHeight => _mode != ViewMode.Inside && !(_anim.Active && _anim.Target == ViewMode.Inside) ? ClipHeight() : float.PositiveInfinity;

        public event Action<int> ArrivedAtSweep;
        public event Action<ViewMode> ModeChanged;

        Camera _cam;
        MatterportViewerSettings S => space != null ? space.Settings : MatterportViewerSettings.Defaults;
        ViewMode _mode = ViewMode.Inside;
        int _current = -1;
        float _yaw, _pitch, _targetYaw, _targetPitch, _fov = 75f, _targetFov = 75f;

        struct Move
        {
            public bool Active;
            public int From, To;
            public Vector3 FromPos, ToPos;
            public float Start, Duration;
        }

        struct ModeAnim
        {
            public bool Active;
            public ViewMode Target;
            public float Start, Duration;
            public Vector3 FromPos, ToPos;
            public Quaternion FromRot, ToRot;
            public float FromFov, ToFov, FromProj, ToProj;
            public int Sweep;
        }

        Move _move;
        ModeAnim _anim;
        int _queued = -1;
        int _pending = -1;
        float _pendingSince;
        bool _pendingIsFlyIn;
        float _projection = 1f;

        Vector3 _orbitTarget, _orbitTargetGoal;
        float _orbitYaw, _orbitPitch = 40f, _orbitDistance = 12f, _orbitDistanceGoal = 12f;
        int _floor = -1;

        bool _pointerDown;
        int _pointerButton;
        Vector2 _pressPos;
        bool _dragging;

        SweepMarkers _markers;
        readonly List<int> _visibleFromCurrent = new List<int>();
        bool[] _visible = Array.Empty<bool>();
        bool _reticle;
        Vector3 _reticlePos, _reticleNormal;
        float _arrivedAt;
        bool _initialised;

        // ------------------------------------------------------------------ setup

        void Awake()
        {
            _cam = GetComponent<Camera>();
        }

        void OnEnable()
        {
            if (space == null) space = FindSpace();
            if (space != null) space.Loaded += OnSpaceLoaded;
            if (space != null && space.IsLoaded) OnSpaceLoaded(space);
        }

        void OnDisable()
        {
            if (space != null) space.Loaded -= OnSpaceLoaded;
            _markers?.Dispose();
            _markers = null;
        }

        static MatterportSpace FindSpace() => MpUtil.FindFirst<MatterportSpace>();

        void OnSpaceLoaded(MatterportSpace s)
        {
            var settings = S;
            _cam.clearFlags = CameraClearFlags.Skybox;
            _cam.nearClipPlane = 0.03f;
            _cam.farClipPlane = Mathf.Max(_cam.farClipPlane, 1000f);
            _mode = ViewMode.Inside;
            _move = default;
            _anim = default;
            _pending = _queued = -1;
            _floor = -1;
            _projection = 1f;
            var euler = transform.rotation.eulerAngles;
            _yaw = _targetYaw = euler.y;
            _pitch = _targetPitch = 0f;
            _fov = _targetFov = settings.defaultFov;
            _markers?.Dispose();
            _markers = new SweepMarkers();
            space.SetCullMode(CullMode.Off);
            Arrive(Mathf.Clamp(space.startSweep, 0, space.Sweeps.Count - 1));
            _initialised = true;
        }

        // ------------------------------------------------------------------ public API

        /// <summary>Moves to a sweep with the standard transition (from Dollhouse/Floor plan it flies in).</summary>
        public void GoTo(int sweep)
        {
            if (!Ready || sweep < 0 || sweep >= space.Sweeps.Count) return;
            if (_mode != ViewMode.Inside) { WhenPanoramaReady(sweep, flyIn: true); return; }
            if (sweep == _current && !_move.Active) return;
            if (_move.Active || _anim.Active) _queued = sweep;
            else WhenPanoramaReady(sweep, flyIn: false);
        }

        /// <summary>Jumps instantly (no transition) - handy for tests and tools.</summary>
        public void Teleport(int sweep, float? yaw = null)
        {
            if (!Ready || sweep < 0 || sweep >= space.Sweeps.Count) return;
            _move = default;
            _anim = default;
            _mode = ViewMode.Inside;
            _projection = 1f;
            space.SetCullMode(CullMode.Off);
            if (yaw.HasValue) _yaw = _targetYaw = yaw.Value;
            Arrive(sweep);
            transform.position = space.SweepWorldPosition(sweep);
        }

        public void SetMode(ViewMode mode)
        {
            if (!Ready || _anim.Active || mode == _mode) return;
            if (mode == ViewMode.Inside) WhenPanoramaReady(Mathf.Max(0, _current), flyIn: true);
            else StartOrbitMode(mode);
        }

        public void SetFloor(int floor)
        {
            if (!Ready) return;
            _floor = Mathf.Clamp(floor, -1, space.FloorCount - 1);
            if (_mode != ViewMode.Inside) _orbitTargetGoal = OrbitCenter();
        }

        public void LookAt(float yaw, float pitch)
        {
            _targetYaw = yaw;
            _targetPitch = Mathf.Clamp(pitch, -85f, 85f);
        }

        bool Ready => space != null && space.IsLoaded && space.Sweeps.Count > 0 && _cam != null;
        float MoveT => Mathf.Clamp01((Time.unscaledTime - _move.Start) / Mathf.Max(0.01f, _move.Duration));

        // ------------------------------------------------------------------ frame

        void Update()
        {
            if (space == null && Time.frameCount % 30 == 0)
            {
                space = FindSpace();
                if (space != null) space.Loaded += OnSpaceLoaded;
            }
            if (!Ready) return;
            if (!_initialised) OnSpaceLoaded(space);
            NavInput.BeginFrame();
            float dt = Time.unscaledDeltaTime;

            if (enableKeyboard) HandleKeys(dt);
            if (enableMouse) HandlePointer();
            UpdatePending();
            UpdateMove();
            UpdateModeAnim();
            UpdateCamera(dt);
            UpdateHover();
            UpdateTour();
            PushView();
            DrawMarkers();
        }

        void LateUpdate() => BlockedGuiRects.Clear();

        void HandleKeys(float dt)
        {
            if (NavInput.KeyDown(KeyCode.Alpha1)) SetMode(ViewMode.Inside);
            if (NavInput.KeyDown(KeyCode.Alpha2)) SetMode(ViewMode.Dollhouse);
            if (NavInput.KeyDown(KeyCode.Alpha3)) SetMode(ViewMode.Floorplan);
            if (NavInput.KeyDown(KeyCode.F2)) space.DebugView++;
            if (NavInput.KeyDown(KeyCode.P)) AutoTour = !AutoTour;
            if (NavInput.KeyDown(KeyCode.Home)) GoTo(Mathf.Clamp(space.startSweep, 0, space.Sweeps.Count - 1));
            if (NavInput.KeyDown(KeyCode.RightBracket)) GoTo((_current + 1) % space.Sweeps.Count);
            if (NavInput.KeyDown(KeyCode.LeftBracket)) GoTo((_current - 1 + space.Sweeps.Count) % space.Sweeps.Count);
            if (NavInput.KeyDown(KeyCode.PageUp)) SetFloor(_floor < 0 ? 0 : Mathf.Min(_floor + 1, space.FloorCount - 1));
            if (NavInput.KeyDown(KeyCode.PageDown)) SetFloor(_floor <= 0 ? -1 : _floor - 1);

            float turn = 0;
            if (NavInput.Key(KeyCode.LeftArrow) || NavInput.Key(KeyCode.Q)) turn -= 1;
            if (NavInput.Key(KeyCode.RightArrow) || NavInput.Key(KeyCode.E)) turn += 1;

            if (_mode == ViewMode.Inside)
            {
                _targetYaw += turn * S.keyTurnSpeed * dt;
                if (NavInput.KeyDown(KeyCode.W) || NavInput.KeyDown(KeyCode.UpArrow)) Step(Vector3.forward);
                if (NavInput.KeyDown(KeyCode.S) || NavInput.KeyDown(KeyCode.DownArrow)) Step(Vector3.back);
                if (NavInput.KeyDown(KeyCode.A)) Step(Vector3.left);
                if (NavInput.KeyDown(KeyCode.D)) Step(Vector3.right);
            }
            else if (!_anim.Active)
            {
                if (NavInput.Key(KeyCode.A)) turn -= 1;
                if (NavInput.Key(KeyCode.D)) turn += 1;
                _orbitYaw += turn * S.keyTurnSpeed * dt;
                float zoom = 0;
                if (NavInput.Key(KeyCode.W) || NavInput.Key(KeyCode.UpArrow)) zoom -= 1;
                if (NavInput.Key(KeyCode.S) || NavInput.Key(KeyCode.DownArrow)) zoom += 1;
                _orbitDistanceGoal = Mathf.Clamp(_orbitDistanceGoal * Mathf.Exp(zoom * dt), 1.5f, 500f);
            }
        }

        void HandlePointer()
        {
            var mouse = NavInput.MousePosition;
            for (int b = 0; b < 3; b++)
            {
                if (_pointerDown || !NavInput.ButtonDown(b)) continue;
                if (IsOverUi(mouse)) continue;
                _pointerDown = true;
                _pointerButton = b;
                _pressPos = mouse;
                _dragging = false;
            }

            if (_pointerDown)
            {
                if (!_dragging && (mouse - _pressPos).magnitude > S.clickDragThreshold) _dragging = true;
                if (_dragging) Drag(NavInput.MouseDelta, _pointerButton);
                if (NavInput.ButtonUp(_pointerButton) || !NavInput.ButtonHeld(_pointerButton))
                {
                    if (!_dragging && _pointerButton == 0) Click(mouse);
                    _pointerDown = false;
                    _dragging = false;
                }
            }

            float scroll = NavInput.Scroll;
            if (Mathf.Abs(scroll) > 0.001f && !IsOverUi(mouse))
            {
                if (_mode == ViewMode.Inside) _targetFov = Mathf.Clamp(_targetFov - scroll * S.zoomStep, S.minFov, S.maxFov);
                else _orbitDistanceGoal = Mathf.Clamp(_orbitDistanceGoal * Mathf.Exp(-scroll * 0.15f), 1.5f, 500f);
            }
        }

        /// <summary>True when the mouse (screen coordinates, y up) is over UI that registered itself with MpGui.Block.</summary>
        public bool IsOverUi(Vector2 mouse)
        {
            var gui = new Vector2(mouse.x, Screen.height - mouse.y);
            foreach (var r in BlockedGuiRects) if (r.Contains(gui)) return true;
            return false;
        }

        void Drag(Vector2 delta, int button)
        {
            if (_anim.Active) return;
            var s = S;
            if (_mode == ViewMode.Inside)
            {
                // Grab-style: the point under the cursor follows the cursor.
                float degPerPixel = _fov / Mathf.Max(1, Screen.height) * s.dragSensitivity;
                float sign = s.invertDrag ? -1f : 1f;
                _targetYaw -= delta.x * degPerPixel * sign;
                _targetPitch = Mathf.Clamp(_targetPitch + delta.y * degPerPixel * sign, -85f, 85f);
                return;
            }

            bool rotate = _mode == ViewMode.Dollhouse ? button == 0 : button != 0;
            if (rotate)
            {
                _orbitYaw += delta.x * s.orbitSensitivity;
                if (_mode == ViewMode.Dollhouse) _orbitPitch = Mathf.Clamp(_orbitPitch - delta.y * s.orbitSensitivity, 8f, 89f);
            }
            else
            {
                // Pan in the ground plane, scaled so the scene follows the cursor.
                float worldPerPixel = 2f * _orbitDistance * Mathf.Tan(_fov * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
                var rot = Quaternion.Euler(0, _orbitYaw, 0);
                var pan = rot * new Vector3(-delta.x, 0, -delta.y) * worldPerPixel;
                _orbitTargetGoal += pan;
                _orbitTarget += pan;
            }
        }

        void Click(Vector2 mouse)
        {
            if (_anim.Active || (ClickHandler != null && ClickHandler(mouse))) return;
            var ray = _cam.ScreenPointToRay(mouse);
            int target = _mode == ViewMode.Inside ? PickInside(ray) : PickFromAbove(ray, mouse);
            if (target >= 0) GoTo(target);
        }

        // ------------------------------------------------------------------ choosing sweeps

        int PickInside(Ray ray)
        {
            if (HoverSweep >= 0) return HoverSweep;
            var s = S;
            var cam = space.SweepWorldPosition(_current);
            int best = -1;
            float bestScore = float.MaxValue;

            if (space.Raycast(ray, 200f, out var hit, out var normal, out _))
            {
                bool floorLike = normal.y > 0.6f;
                // Where would you stand to look at what you clicked? On floors: right there. Elsewhere: in front of it.
                var stand = floorLike ? hit : hit - ray.direction * 0.6f;
                for (int i = 0; i < space.Sweeps.Count; i++)
                {
                    if (i == _current) continue;
                    var m = space.MarkerWorldPosition(i);
                    var flat = new Vector2(m.x - stand.x, m.z - stand.z).magnitude;
                    float dy = Mathf.Abs(m.y - (floorLike ? hit.y : m.y));
                    float score = flat + 2f * dy;
                    var toSweep = space.SweepWorldPosition(i) - cam;
                    if (!floorLike && Vector3.Angle(ray.direction, toSweep) > 40f) continue;
                    if (!IsVisible(i)) score += 3f;
                    if (score < bestScore) { bestScore = score; best = i; }
                }
                if (best >= 0 && bestScore < (floorLike ? 2.5f : 4f) + s.clickSnapRadius) return best;
                if (best >= 0 && IsVisible(best)) return best;
            }

            // Nothing hit (window, sky) or no good candidate: nearest visible sweep in the click direction.
            best = -1;
            float bestAngle = 25f;
            for (int i = 0; i < space.Sweeps.Count; i++)
            {
                if (i == _current || !IsVisible(i)) continue;
                float angle = Vector3.Angle(ray.direction, space.SweepWorldPosition(i) - cam);
                if (angle < bestAngle) { bestAngle = angle; best = i; }
            }
            return best;
        }

        int PickFromAbove(Ray ray, Vector2 mouse)
        {
            if (HoverSweep >= 0) return HoverSweep;
            if (!space.RaycastBelow(ray, 1000f, ClipHeight(), out var hit, out _)) return -1;
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < space.Sweeps.Count; i++)
            {
                if (!FloorShown(space.Sweeps[i].Floor)) continue;
                var m = space.MarkerWorldPosition(i);
                float d = (m - hit).magnitude;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        void Step(Vector3 localDirection)
        {
            if (_current < 0) return;
            var s = S;
            var dir = Quaternion.Euler(0, _yaw, 0) * localDirection;
            var from = space.SweepWorldPosition(_current);
            int best = -1;
            float bestScore = float.MaxValue;
            void Consider(int i)
            {
                var v = space.SweepWorldPosition(i) - from;
                v.y = 0;
                float dist = v.magnitude, angle = Vector3.Angle(dir, v);
                if (i == _current || dist < 0.2f || dist > s.stepMaxDistance || angle > s.stepMaxAngle) return;
                float a = angle / s.stepMaxAngle;
                float score = dist * (1f + 1.5f * a * a); // prefer close sweeps straight ahead
                if (score < bestScore) { bestScore = score; best = i; }
            }
            foreach (int i in space.Sweeps[_current].Neighbors) Consider(i);
            if (best < 0)
                for (int i = 0; i < space.Sweeps.Count; i++) if (IsVisible(i)) Consider(i);
            if (best >= 0) GoTo(best);
        }

        bool IsVisible(int i) => i >= 0 && i < _visible.Length && _visible[i];

        void ComputeVisibility()
        {
            int n = space.Sweeps.Count;
            if (_visible.Length != n) _visible = new bool[n];
            _visibleFromCurrent.Clear();
            var from = space.SweepWorldPosition(_current);
            float maxDist = S.markerMaxDistance;
            for (int i = 0; i < n; i++)
            {
                _visible[i] = false;
                if (i == _current) continue;
                var marker = space.MarkerWorldPosition(i) + Vector3.up * 0.15f;
                bool near = (marker - from).magnitude <= maxDist * 1.6f;
                _visible[i] = near && space.LineOfSight(from, marker);
                if (_visible[i] && (marker - from).magnitude <= maxDist) _visibleFromCurrent.Add(i);
            }
        }

        // ------------------------------------------------------------------ inside transitions

        void StartMove(int to)
        {
            var s = S;
            var from = _current >= 0 ? space.SweepWorldPosition(_current) : transform.position;
            var target = space.SweepWorldPosition(to);
            _move = new Move
            {
                Active = true,
                From = _current,
                To = to,
                FromPos = from,
                ToPos = target,
                Start = Time.unscaledTime,
                Duration = s.TransitionDuration(Vector3.Distance(from, target)),
            };
            space.Panoramas.RequestDepth(to);
            space.Panoramas.Request(to, s.panoMaxSize, -5f);
        }

        /// <summary>Moves (or flies in) once the target's panorama is loaded - after at most 4 s, so a slow disk never blocks.</summary>
        void WhenPanoramaReady(int sweep, bool flyIn)
        {
            _pending = sweep;
            _pendingIsFlyIn = flyIn;
            _pendingSince = Time.unscaledTime;
            if (!space.Panoramas.IsReady(sweep)) space.Panoramas.RequestProgressive(sweep, S.panoMaxSize, -10f);
            UpdatePending();
        }

        void UpdatePending()
        {
            if (_pending < 0 || (!space.Panoramas.IsReady(_pending) && Time.unscaledTime - _pendingSince < 4f)) return;
            int target = _pending;
            _pending = -1;
            if (_pendingIsFlyIn) BeginFlyIn(target);
            else if (_mode == ViewMode.Inside && !_move.Active) StartMove(target);
        }

        void UpdateMove()
        {
            if (!_move.Active) return;
            if (MoveT >= 1f)
            {
                _move.Active = false;
                Arrive(_move.To);
                if (_queued >= 0)
                {
                    int q = _queued;
                    _queued = -1;
                    GoTo(q);
                }
            }
        }

        void Arrive(int sweep)
        {
            var s = S;
            _current = sweep;
            _arrivedAt = Time.unscaledTime;
            space.Panoramas.RequestProgressive(sweep, s.panoMaxSize, 0f);
            space.Panoramas.RequestDepth(sweep);
            ComputeVisibility();

            // Prefetch neighbours (nearest first) at the small tier, the closest few at full size.
            var neighbors = new List<int>(space.Sweeps[sweep].Neighbors);
            if (neighbors.Count == 0) neighbors.AddRange(_visibleFromCurrent);
            var here = space.SweepWorldPosition(sweep);
            neighbors.Sort((a, b) => (space.SweepWorldPosition(a) - here).sqrMagnitude.CompareTo((space.SweepWorldPosition(b) - here).sqrMagnitude));
            for (int k = 0; k < neighbors.Count && k < s.prefetchNeighbors; k++)
            {
                var entry = space.Manifest.Sweeps[neighbors[k]];
                int size = k < 2 ? s.panoMaxSize : (entry.SmallestTier?.Size ?? s.panoMaxSize);
                space.Panoramas.Request(neighbors[k], size, 10f + k);
            }
            ArrivedAtSweep?.Invoke(sweep);
        }

        void UpdateTour()
        {
            if (!AutoTour || _mode != ViewMode.Inside || _move.Active || _pending >= 0) return;
            if (Time.unscaledTime - _arrivedAt < 2.5f) return;
            _arrivedAt = Time.unscaledTime;
            GoTo((_current + 1) % space.Sweeps.Count);
        }

        // ------------------------------------------------------------------ dollhouse / floor plan

        Vector3 OrbitCenter()
        {
            var b = space.WorldBounds;
            var center = b.center;
            if (_floor >= 0)
            {
                var pts = new List<Vector3>();
                for (int i = 0; i < space.Sweeps.Count; i++)
                    if (space.Sweeps[i].Floor == _floor) pts.Add(space.MarkerWorldPosition(i));
                if (pts.Count > 0)
                {
                    var fb = new Bounds(pts[0], Vector3.zero);
                    foreach (var p in pts) fb.Encapsulate(p);
                    center = new Vector3(fb.center.x, space.FloorHeightWorld(_floor) + 1.2f, fb.center.z);
                }
            }
            return center;
        }

        float OrbitDistanceFor(ViewMode mode)
        {
            var b = space.WorldBounds;
            float radius = Mathf.Max(3f, new Vector2(b.size.x, b.size.z).magnitude * 0.5f);
            float fov = mode == ViewMode.Floorplan ? S.floorplanFov : S.dollhouseFov;
            return radius / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * 1.05f;
        }

        Pose OrbitPose(Vector3 target, float yaw, float pitch, float distance)
        {
            var rot = Quaternion.Euler(pitch, yaw, 0);
            return new Pose(target - rot * Vector3.forward * distance, rot);
        }

        void StartOrbitMode(ViewMode mode)
        {
            var s = S;
            _orbitYaw = _mode == ViewMode.Inside ? _yaw : _orbitYaw;
            _orbitPitch = mode == ViewMode.Floorplan ? 89.9f : 40f;
            _orbitTarget = _orbitTargetGoal = OrbitCenter();
            _orbitDistance = _orbitDistanceGoal = OrbitDistanceFor(mode);
            var to = OrbitPose(_orbitTarget, _orbitYaw, _orbitPitch, _orbitDistance);
            Animate(mode, to.position, to.rotation, mode == ViewMode.Floorplan ? s.floorplanFov : s.dollhouseFov, 0f, _current);
            space.SetCullMode(CullMode.Back);
            _move = default;
        }

        void BeginFlyIn(int sweep)
        {
            _yaw = _targetYaw = _mode == ViewMode.Inside ? _yaw : _orbitYaw;
            _pitch = _targetPitch = 0f;
            _fov = _targetFov = S.defaultFov;
            Animate(ViewMode.Inside, space.SweepWorldPosition(sweep), Quaternion.Euler(0, _yaw, 0), _fov, 1f, sweep);
            _current = sweep;
            space.Panoramas.RequestDepth(sweep);
        }

        void Animate(ViewMode target, Vector3 toPos, Quaternion toRot, float toFov, float toProjection, int sweep)
        {
            _anim = new ModeAnim
            {
                Active = true,
                Target = target,
                Start = Time.unscaledTime,
                Duration = S.modeTransitionTime,
                FromPos = transform.position,
                ToPos = toPos,
                FromRot = transform.rotation,
                ToRot = toRot,
                FromFov = _cam.fieldOfView,
                ToFov = toFov,
                FromProj = _projection,
                ToProj = toProjection,
                Sweep = sweep,
            };
        }

        void UpdateModeAnim()
        {
            if (!_anim.Active) return;
            float t = Mathf.Clamp01((Time.unscaledTime - _anim.Start) / Mathf.Max(0.01f, _anim.Duration));
            float e = S.transitionEase.Evaluate(t);
            transform.position = Vector3.LerpUnclamped(_anim.FromPos, _anim.ToPos, e);
            transform.rotation = Quaternion.SlerpUnclamped(_anim.FromRot, _anim.ToRot, e);
            _cam.fieldOfView = Mathf.LerpUnclamped(_anim.FromFov, _anim.ToFov, e);
            // Projection fades out early when leaving a sweep, and in late when arriving, like Matterport.
            float phase = _anim.ToProj > _anim.FromProj ? Mathf.InverseLerp(0.55f, 1f, t) : Mathf.InverseLerp(0f, 0.45f, t);
            _projection = Mathf.Lerp(_anim.FromProj, _anim.ToProj, phase);
            if (t < 1f) return;

            _anim.Active = false;
            var previous = _mode;
            _mode = _anim.Target;
            _projection = _anim.ToProj;
            if (_mode == ViewMode.Inside)
            {
                space.SetCullMode(CullMode.Off);
                Arrive(_anim.Sweep);
            }
            if (previous != _mode) ModeChanged?.Invoke(_mode);
        }

        // ------------------------------------------------------------------ camera

        void UpdateCamera(float dt)
        {
            if (_anim.Active) return;
            var s = S;
            float k = 1f - Mathf.Exp(-s.lookSmoothing * dt);
            if (_mode == ViewMode.Inside)
            {
                _yaw = Mathf.LerpAngle(_yaw, _targetYaw, k);
                _pitch = Mathf.Lerp(_pitch, _targetPitch, k);
                _fov = Mathf.Lerp(_fov, _targetFov, k);
                var pos = _move.Active ? Vector3.LerpUnclamped(_move.FromPos, _move.ToPos, s.transitionEase.Evaluate(MoveT)) : space.SweepWorldPosition(_current);
                transform.SetPositionAndRotation(pos, Quaternion.Euler(_pitch, _yaw, 0));
                _cam.fieldOfView = _fov;
            }
            else
            {
                _orbitTarget = Vector3.Lerp(_orbitTarget, _orbitTargetGoal, k);
                _orbitDistance = Mathf.Lerp(_orbitDistance, _orbitDistanceGoal, k);
                var pose = OrbitPose(_orbitTarget, _orbitYaw, _orbitPitch, _orbitDistance);
                transform.SetPositionAndRotation(pose.position, pose.rotation);
                _cam.fieldOfView = Mathf.Lerp(_cam.fieldOfView, _mode == ViewMode.Floorplan ? s.floorplanFov : s.dollhouseFov, k);
            }
        }

        float ClipHeight()
        {
            if (_mode == ViewMode.Inside || _floor < 0 || _floor + 1 >= space.FloorCount) return float.PositiveInfinity;
            return space.FloorHeightWorld(_floor + 1) - 0.25f;
        }

        bool FloorShown(int floor) => _floor < 0 || floor == _floor;

        void PushView()
        {
            int a = _current, b = -1;
            float blend = 0;
            if (_move.Active)
            {
                a = _move.From;
                b = _move.To;
                blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.1f, 0.9f, MoveT));
            }
            else if (_anim.Active)
            {
                a = _anim.Sweep;
            }
            space.SetView(a, b, blend, _projection, ViewClipHeight);
        }

        // ------------------------------------------------------------------ hover, markers, cursor

        void UpdateHover()
        {
            HoverSweep = -1;
            _reticle = false;
            if (!enableMouse || _anim.Active || _dragging || ClickHandler != null || IsOverUi(NavInput.MousePosition)) return;
            var mouse = NavInput.MousePosition;
            if (mouse.x < 0 || mouse.y < 0 || mouse.x > Screen.width || mouse.y > Screen.height) return;
            var ray = _cam.ScreenPointToRay(mouse);

            if (_mode == ViewMode.Inside)
            {
                if (_move.Active) return;
                if (space.Raycast(ray, 200f, out var hit, out var normal, out _))
                {
                    _reticle = true;
                    _reticlePos = hit;
                    _reticleNormal = normal;
                    float best = S.markerRadius * 2.2f;
                    foreach (int i in _visibleFromCurrent)
                    {
                        float d = (space.MarkerWorldPosition(i) - hit).magnitude;
                        if (d < best) { best = d; HoverSweep = i; }
                    }
                }
                return;
            }

            // From above: nearest marker on screen.
            float bestPx = 22f;
            for (int i = 0; i < space.Sweeps.Count; i++)
            {
                if (!FloorShown(space.Sweeps[i].Floor)) continue;
                var sp = _cam.WorldToScreenPoint(space.MarkerWorldPosition(i));
                if (sp.z <= 0) continue;
                float d = (new Vector2(sp.x, sp.y) - mouse).magnitude;
                if (d < bestPx) { bestPx = d; HoverSweep = i; }
            }
        }

        void DrawMarkers()
        {
            if (_markers == null) return;
            var s = S;
            if (s.showMarkers && !_anim.Active && ClickHandler == null)
            {
                if (_mode == ViewMode.Inside)
                {
                    var here = space.SweepWorldPosition(_current);
                    foreach (int i in _visibleFromCurrent)
                    {
                        if (_move.Active && i == _move.To) continue;
                        var m = space.MarkerWorldPosition(i);
                        float dist = (m - here).magnitude;
                        float fade = 1f - Mathf.InverseLerp(s.markerMaxDistance * 0.6f, s.markerMaxDistance, dist);
                        if (_move.Active) fade *= 0.5f;
                        var c = s.markerColor;
                        c.a *= fade * (i == HoverSweep ? 1f : 0.75f);
                        _markers.Draw(_cam, m, Vector3.up, s.markerRadius * (i == HoverSweep ? 1.2f : 1f), c);
                    }
                }
                else
                {
                    float scale = Mathf.Clamp(_orbitDistance / 25f, 0.6f, 3f);
                    for (int i = 0; i < space.Sweeps.Count; i++)
                    {
                        if (!FloorShown(space.Sweeps[i].Floor)) continue;
                        var c = s.markerColor;
                        if (i != HoverSweep) c.a *= 0.7f;
                        _markers.Draw(_cam, space.MarkerWorldPosition(i), Vector3.up, s.markerRadius * scale * (i == HoverSweep ? 1.3f : 1f), c, 0.55f, 0.5f);
                    }
                }
            }
            if (s.showReticle && _reticle && HoverSweep < 0)
                _markers.Draw(_cam, _reticlePos, _reticleNormal, s.reticleRadius, new Color(1f, 1f, 1f, 0.75f), 0.82f, 0.12f);
        }
    }
}
