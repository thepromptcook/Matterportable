using System;
using System.Collections.Generic;

namespace Matterportable.Core
{
    public enum ViewMode { Inside, Dollhouse, Floorplan }

    /// <summary>Keys the navigator understands (engines map their input to these).</summary>
    [Flags]
    public enum NavKeys
    {
        None = 0, Inside = 1, Dollhouse = 1 << 1, Floorplan = 1 << 2, Forward = 1 << 3, Back = 1 << 4, Left = 1 << 5, Right = 1 << 6,
        TurnLeft = 1 << 7, TurnRight = 1 << 8, Next = 1 << 9, Previous = 1 << 10, Home = 1 << 11, FloorUp = 1 << 12, FloorDown = 1 << 13,
        Tour = 1 << 14, DebugView = 1 << 15,
    }

    /// <summary>One frame of input. Pixels have y pointing down; the ray is the pointer's, in the Core frame.</summary>
    public struct NavigatorInput
    {
        public float DeltaTime, PointerX, PointerY, PixelAngle, ViewportHeight, Scroll;
        /// <summary>The pointer is over the view (not over UI, inside the window).</summary>
        public bool PointerOver;
        /// <summary>Mouse buttons (bit 0 left, 1 right, 2 middle) pressed / held / released this frame.</summary>
        public int ButtonsDown, ButtonsHeld, ButtonsUp;
        public Vec3 RayOrigin, RayDirection;
        public NavKeys Pressed, Held;
    }

    /// <summary>A circle lying on a surface: sweep markers and the move cursor.</summary>
    public struct Marker
    {
        public Vec3 Position, Normal;
        public float Radius, Inner, Fill;
        public Rgba Color;
    }

    /// <summary>What the projection shaders show this frame: the sweeps whose photos and depth cubes go in slots A/B
    /// (-1: none), the sweeps whose poses drive them, the shader state and whether the mesh culls back faces.</summary>
    public struct ViewBinding
    {
        public int PanoA, PanoB, DepthA, DepthB, PoseA, PoseB, DebugView;
        public float Blend, Projection, ClipHeight;
        public bool UseDepth, ValidA, ValidB, CullBack;

        /// <summary>Before the navigator's first frame: mesh textures only, nothing clipped.</summary>
        public static readonly ViewBinding None = new ViewBinding { PanoA = -1, PanoB = -1, DepthA = -1, DepthB = -1, ClipHeight = 1e9f };
    }

    /// <summary>
    /// Matterport-style navigation, shared by Unity, Godot and the web: drag to look, click to move with the
    /// cross-fade, W/A/S/D to step between sweeps, wheel to zoom, Dollhouse and Floor plan. Works in the Core frame
    /// (Unity's: left-handed, Y up; yaw clockwise from above, positive pitch looks down). Engines feed
    /// <see cref="NavigatorInput"/> and apply <see cref="CameraPosition"/>, <see cref="CameraRotation"/>,
    /// <see cref="Fov"/>, <see cref="View"/> and <see cref="Markers"/>.
    /// </summary>
    public sealed class Navigator
    {
        public ViewMode Mode { get; private set; } = ViewMode.Inside;
        public int CurrentSweep { get; private set; } = -1;
        public int SelectedFloor { get; private set; } = -1;
        public int HoverSweep { get; private set; } = -1;
        public bool AutoTour;
        public int DebugView;
        public int StartSweep;
        public ViewMode StartMode = ViewMode.Inside;
        /// <summary>Measure mode: clicks are left to the measure tool (<see cref="Clicked"/>); markers and the move
        /// cursor hide.</summary>
        public bool Measuring;
        /// <summary>A left click (not a drag) ended this frame.</summary>
        public bool Clicked { get; private set; }
        public bool IsMoving => _move.Active;
        public bool IsChangingMode => _anim.Active;
        public bool IsWaitingForPanorama => _pending >= 0;
        public bool CullBack { get; private set; }
        public Vec3 CameraPosition { get; private set; }
        public Quat CameraRotation { get; private set; } = Quat.Identity;
        public float Fov { get; private set; }
        public ViewBinding View { get; private set; }
        public readonly List<Marker> Markers = new List<Marker>();

        /// <summary>Height above which geometry is hidden in the current view (floor filter), else +infinity.</summary>
        public float ViewClipHeight => Mode != ViewMode.Inside && !(_anim.Active && _anim.Target == ViewMode.Inside) ? ClipHeight() : float.PositiveInfinity;

        struct Move { public bool Active; public int From, To; public Vec3 FromPos, ToPos; public double Start; public float Duration; }

        struct ModeAnim
        {
            public bool Active;
            public ViewMode Target;
            public double Start;
            public float Duration, FromFov, ToFov, FromProj, ToProj;
            public Vec3 FromPos, ToPos;
            public Quat FromRot, ToRot;
            public int Sweep;
        }

        readonly SpaceModel _space;
        readonly ViewerSettings _s;
        readonly PanoramaScheduler _panos;
        double _time, _pendingSince, _arrivedAt;
        float _yaw, _pitch, _targetYaw, _targetPitch, _fov, _targetFov, _projection = 1f;
        Move _move;
        ModeAnim _anim;
        int _queued = -1, _pending = -1, _button, _shownA = -1, _shownB = -1;
        bool _pendingIsFlyIn, _initialised, _pointerDown, _dragging, _reticle;
        float _pressX, _pressY, _lastX, _lastY;
        Vec3 _orbitTarget, _orbitTargetGoal, _reticlePos, _reticleNormal;
        float _orbitYaw, _orbitPitch = 40f, _orbitDistance = 12f, _orbitDistanceGoal = 12f;
        bool[] _visible = Array.Empty<bool>();
        readonly List<int> _visibleFromCurrent = new List<int>();

        public Navigator(SpaceModel space, ViewerSettings settings, PanoramaScheduler panoramas)
        {
            _space = space;
            _s = settings;
            _panos = panoramas;
            _panos.IsPinned = s => s == _shownA || s == _shownB;
            Fov = _fov = _targetFov = settings.DefaultFov;
        }

        IReadOnlyList<SweepInfo> Sweeps => _space.Sweeps;
        Vec3 SweepPos(int i) => Sweeps[i].Position;
        Vec3 MarkerPos(int i) => Sweeps[i].FloorPoint;
        float MoveT => Clamp01((float)(_time - _move.Start) / Math.Max(0.01f, _move.Duration));
        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;
        static float InverseLerp(float a, float b, float v) => Clamp01((v - a) / (b - a));
        static float Ease(float t) => t * t * (3f - 2f * t);
        static float Rad(float degrees) => degrees * (float)Math.PI / 180f;

        // ------------------------------------------------------------------ public API

        public void GoTo(int sweep)
        {
            if (sweep < 0 || sweep >= Sweeps.Count) return;
            if (Mode != ViewMode.Inside) { WhenPanoramaReady(sweep, flyIn: true); return; }
            if (sweep == CurrentSweep && !_move.Active) return;
            if (_move.Active || _anim.Active) _queued = sweep;
            else WhenPanoramaReady(sweep, flyIn: false);
        }

        public void SetMode(ViewMode mode)
        {
            if (_anim.Active || mode == Mode || Sweeps.Count == 0) return;
            if (mode == ViewMode.Inside) WhenPanoramaReady(Math.Max(0, CurrentSweep), flyIn: true);
            else StartOrbitMode(mode);
        }

        public void SetFloor(int floor)
        {
            SelectedFloor = Math.Max(-1, Math.Min(floor, _space.FloorCount - 1));
            if (Mode != ViewMode.Inside) _orbitTargetGoal = OrbitCenter();
        }

        /// <summary>Jumps instantly (no transition), looking along yaw/pitch in degrees - for tests and tools.</summary>
        public void Teleport(int sweep, float yaw, float pitch = 0f)
        {
            if (sweep < 0 || sweep >= Sweeps.Count) return;
            if (!_initialised) Initialise();
            _move = default;
            _anim = default;
            _pending = _queued = -1;
            Mode = ViewMode.Inside;
            CullBack = false;
            _projection = 1f;
            _yaw = _targetYaw = yaw;
            _pitch = _targetPitch = pitch;
            _fov = _targetFov = _s.DefaultFov;
            Arrive(sweep);
        }

        void Initialise()
        {
            _initialised = true;
            Arrive(Math.Max(0, Math.Min(StartSweep, Sweeps.Count - 1)));
            CameraPosition = SweepPos(CurrentSweep);
            if (StartMode != ViewMode.Inside) StartOrbitMode(StartMode);
        }

        // ------------------------------------------------------------------ frame

        public void Update(in NavigatorInput input)
        {
            if (Sweeps.Count == 0) return;
            _time += input.DeltaTime;
            if (!_initialised) Initialise();
            HandleKeys(input);
            HandlePointer(input);
            UpdatePending();
            UpdateMove();
            UpdateModeAnim();
            UpdateCamera(input.DeltaTime);
            UpdateHover(input);
            UpdateTour();
            PushView();
            BuildMarkers();
        }

        void HandleKeys(in NavigatorInput input)
        {
            NavKeys k = input.Pressed, held = input.Held;
            bool Has(NavKeys key) => (k & key) != 0;
            bool Held(NavKeys key) => (held & key) != 0;
            int n = Sweeps.Count;
            if (Has(NavKeys.Inside)) SetMode(ViewMode.Inside);
            if (Has(NavKeys.Dollhouse)) SetMode(ViewMode.Dollhouse);
            if (Has(NavKeys.Floorplan)) SetMode(ViewMode.Floorplan);
            if (Has(NavKeys.DebugView)) DebugView = (DebugView + 1) % ViewerText.DebugViews.Length;
            if (Has(NavKeys.Tour)) AutoTour = !AutoTour;
            if (Has(NavKeys.Home)) GoTo(Math.Max(0, Math.Min(StartSweep, n - 1)));
            if (Has(NavKeys.Next)) GoTo((CurrentSweep + 1) % n);
            if (Has(NavKeys.Previous)) GoTo((CurrentSweep - 1 + n) % n);
            if (Has(NavKeys.FloorUp)) SetFloor(SelectedFloor < 0 ? 0 : Math.Min(SelectedFloor + 1, _space.FloorCount - 1));
            if (Has(NavKeys.FloorDown)) SetFloor(SelectedFloor <= 0 ? -1 : SelectedFloor - 1);

            float turn = (Held(NavKeys.TurnRight) ? 1 : 0) - (Held(NavKeys.TurnLeft) ? 1 : 0), dt = input.DeltaTime;
            if (Mode == ViewMode.Inside)
            {
                _targetYaw += turn * _s.KeyTurnSpeed * dt;
                if (Has(NavKeys.Forward)) Step(Vec3.Forward);
                if (Has(NavKeys.Back)) Step(-Vec3.Forward);
                if (Has(NavKeys.Left)) Step(-Vec3.Right);
                if (Has(NavKeys.Right)) Step(Vec3.Right);
            }
            else if (!_anim.Active)
            {
                turn += (Held(NavKeys.Right) ? 1 : 0) - (Held(NavKeys.Left) ? 1 : 0);
                _orbitYaw += turn * _s.KeyTurnSpeed * dt;
                float zoom = (Held(NavKeys.Back) ? 1 : 0) - (Held(NavKeys.Forward) ? 1 : 0);
                _orbitDistanceGoal = Clamp(_orbitDistanceGoal * (float)Math.Exp(zoom * dt), 1.5f, 500f);
            }
        }

        /// <summary>Click vs drag: a press that moves less than ClickDragThreshold before release is a click.</summary>
        void HandlePointer(in NavigatorInput input)
        {
            Clicked = false;
            for (int b = 0; b < 3 && !_pointerDown; b++)
            {
                if ((input.ButtonsDown >> b & 1) == 0 || !input.PointerOver) continue;
                _pointerDown = true;
                _dragging = false;
                _button = b;
                _pressX = _lastX = input.PointerX;
                _pressY = _lastY = input.PointerY;
            }
            if (_pointerDown)
            {
                float dx = input.PointerX - _lastX, dy = input.PointerY - _lastY;
                _lastX = input.PointerX;
                _lastY = input.PointerY;
                if (!_dragging && Math.Abs(input.PointerX - _pressX) + Math.Abs(input.PointerY - _pressY) > _s.ClickDragThreshold) _dragging = true;
                if (_dragging && (dx != 0 || dy != 0)) Drag(dx, dy, Math.Max(1f, input.ViewportHeight));
                if ((input.ButtonsUp >> _button & 1) != 0 || (input.ButtonsHeld >> _button & 1) == 0)
                {
                    Clicked = !_dragging && _button == 0;
                    _pointerDown = _dragging = false;
                }
            }
            if (Math.Abs(input.Scroll) > 0.001f && input.PointerOver)
            {
                if (Mode == ViewMode.Inside) _targetFov = Clamp(_targetFov - input.Scroll * _s.ZoomStep, _s.MinFov, _s.MaxFov);
                else _orbitDistanceGoal = Clamp(_orbitDistanceGoal * (float)Math.Exp(-input.Scroll * 0.15f), 1.5f, 500f);
            }
            if (Clicked && !Measuring && !_anim.Active)
            {
                int target = Mode == ViewMode.Inside ? PickInside(input.RayOrigin, input.RayDirection) : PickFromAbove(input.RayOrigin, input.RayDirection);
                if (target >= 0) GoTo(target);
            }
        }

        void Drag(float dx, float dy, float height)
        {
            if (_anim.Active) return;
            if (Mode == ViewMode.Inside)
            {
                // Grab-style: the point under the cursor follows the cursor.
                float degPerPixel = _fov / height * _s.DragSensitivity * (_s.InvertDrag ? -1f : 1f);
                _targetYaw -= dx * degPerPixel;
                _targetPitch = Clamp(_targetPitch - dy * degPerPixel, -85f, 85f);
                return;
            }
            if (Mode == ViewMode.Dollhouse ? _button == 0 : _button != 0)
            {
                _orbitYaw += dx * _s.OrbitSensitivity;
                if (Mode == ViewMode.Dollhouse) _orbitPitch = Clamp(_orbitPitch + dy * _s.OrbitSensitivity, 8f, 89f);
                return;
            }
            // Pan in the ground plane, scaled so the scene follows the cursor.
            float worldPerPixel = 2f * _orbitDistance * (float)Math.Tan(Rad(Fov * 0.5f)) / height;
            var pan = Quat.Euler(0, _orbitYaw) * new Vec3(-dx, 0, dy) * worldPerPixel;
            _orbitTargetGoal += pan;
            _orbitTarget += pan;
        }

        // ------------------------------------------------------------------ choosing sweeps

        int PickInside(Vec3 origin, Vec3 dir)
        {
            if (HoverSweep >= 0) return HoverSweep;
            var cam = SweepPos(CurrentSweep);
            int best = -1;
            float bestScore = float.MaxValue;
            if (_space.Raycast(origin, dir, 200f, out var hit))
            {
                bool floorLike = hit.Normal.Y > 0.6f;
                // Where would you stand to look at what you clicked? On floors: right there. Elsewhere: in front of it.
                var stand = floorLike ? hit.Point : hit.Point - dir * 0.6f;
                for (int i = 0; i < Sweeps.Count; i++)
                {
                    if (i == CurrentSweep) continue;
                    var m = MarkerPos(i);
                    float flat = (float)Math.Sqrt((m.X - stand.X) * (m.X - stand.X) + (m.Z - stand.Z) * (m.Z - stand.Z));
                    float score = flat + 2f * Math.Abs(m.Y - (floorLike ? hit.Point.Y : m.Y));
                    if (!floorLike && Vec3.Angle(dir, SweepPos(i) - cam) > 40f) continue;
                    if (!IsVisible(i)) score += 3f;
                    if (score < bestScore) { bestScore = score; best = i; }
                }
                if (best >= 0 && bestScore < (floorLike ? 2.5f : 4f) + _s.ClickSnapRadius) return best;
                if (best >= 0 && IsVisible(best)) return best;
            }
            // Nothing hit (window, sky) or no good candidate: nearest visible sweep in the click direction.
            best = -1;
            float bestAngle = 25f;
            for (int i = 0; i < Sweeps.Count; i++)
            {
                if (i == CurrentSweep || !IsVisible(i)) continue;
                float angle = Vec3.Angle(dir, SweepPos(i) - cam);
                if (angle < bestAngle) { bestAngle = angle; best = i; }
            }
            return best;
        }

        int PickFromAbove(Vec3 origin, Vec3 dir)
        {
            if (HoverSweep >= 0) return HoverSweep;
            if (!_space.RaycastVisible(origin, dir, 1000f, true, ClipHeight(), out var hit)) return -1;
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < Sweeps.Count; i++)
            {
                if (!FloorShown(Sweeps[i].Floor)) continue;
                float d = (MarkerPos(i) - hit.Point).Length;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        void Step(Vec3 localDirection)
        {
            if (CurrentSweep < 0) return;
            var dir = Quat.Euler(0, _yaw) * localDirection;
            var from = SweepPos(CurrentSweep);
            int best = -1;
            float bestScore = float.MaxValue;
            void Consider(int i)
            {
                var v = SweepPos(i) - from;
                v.Y = 0;
                float dist = v.Length, angle = Vec3.Angle(dir, v);
                if (i == CurrentSweep || dist < 0.2f || dist > _s.StepMaxDistance || angle > _s.StepMaxAngle) return;
                float a = angle / _s.StepMaxAngle, score = dist * (1f + 1.5f * a * a); // prefer close sweeps straight ahead
                if (score < bestScore) { bestScore = score; best = i; }
            }
            foreach (int i in Sweeps[CurrentSweep].Neighbors) Consider(i);
            if (best < 0)
                for (int i = 0; i < Sweeps.Count; i++) if (IsVisible(i)) Consider(i);
            if (best >= 0) GoTo(best);
        }

        bool IsVisible(int i) => i >= 0 && i < _visible.Length && _visible[i];

        void ComputeVisibility()
        {
            int n = Sweeps.Count;
            if (_visible.Length != n) _visible = new bool[n];
            _visibleFromCurrent.Clear();
            var from = SweepPos(CurrentSweep);
            for (int i = 0; i < n; i++)
            {
                var marker = MarkerPos(i) + Vec3.Up * 0.15f;
                float dist = (marker - from).Length;
                _visible[i] = i != CurrentSweep && dist <= _s.MarkerMaxDistance * 1.6f && _space.LineOfSight(from, marker);
                if (_visible[i] && dist <= _s.MarkerMaxDistance) _visibleFromCurrent.Add(i);
            }
        }

        // ------------------------------------------------------------------ inside transitions

        void StartMove(int to)
        {
            var from = CurrentSweep >= 0 ? SweepPos(CurrentSweep) : CameraPosition;
            var target = SweepPos(to);
            _move = new Move { Active = true, From = CurrentSweep, To = to, FromPos = from, ToPos = target, Start = _time, Duration = _s.TransitionDuration((target - from).Length) };
            _panos.RequestDepth(to);
            _panos.Request(to, _s.PanoMaxSize, -5f);
        }

        /// <summary>Moves (or flies in) once the target's panorama is loaded - after at most 4 s, so a slow disk never blocks.</summary>
        void WhenPanoramaReady(int sweep, bool flyIn)
        {
            _pending = sweep;
            _pendingIsFlyIn = flyIn;
            _pendingSince = _time;
            if (!_panos.IsReady(sweep)) _panos.RequestProgressive(sweep, _s.PanoMaxSize, -10f);
            UpdatePending();
        }

        void UpdatePending()
        {
            if (_pending < 0 || (!_panos.IsReady(_pending) && _time - _pendingSince < 4)) return;
            int target = _pending;
            _pending = -1;
            if (_pendingIsFlyIn) BeginFlyIn(target);
            else if (Mode == ViewMode.Inside && !_move.Active) StartMove(target);
        }

        void UpdateMove()
        {
            if (!_move.Active || MoveT < 1f) return;
            _move.Active = false;
            Arrive(_move.To);
            if (_queued < 0) return;
            int q = _queued;
            _queued = -1;
            GoTo(q);
        }

        void Arrive(int sweep)
        {
            CurrentSweep = sweep;
            _arrivedAt = _time;
            _panos.RequestProgressive(sweep, _s.PanoMaxSize, 0f);
            _panos.RequestDepth(sweep);
            ComputeVisibility();
            // Prefetch neighbours (nearest first): the closest two at full size, the rest at the small tier.
            var neighbors = new List<int>(Sweeps[sweep].Neighbors);
            if (neighbors.Count == 0) neighbors.AddRange(_visibleFromCurrent);
            var here = SweepPos(sweep);
            neighbors.Sort((a, b) => (SweepPos(a) - here).SqrLength.CompareTo((SweepPos(b) - here).SqrLength));
            for (int k = 0; k < neighbors.Count && k < _s.PrefetchNeighbors; k++)
                _panos.Request(neighbors[k], k < 2 ? _s.PanoMaxSize : (Sweeps[neighbors[k]].Entry.SmallestTier?.Size ?? _s.PanoMaxSize), 10f + k);
        }

        void UpdateTour()
        {
            if (!AutoTour || Mode != ViewMode.Inside || _move.Active || _pending >= 0 || _time - _arrivedAt < 2.5) return;
            _arrivedAt = _time;
            GoTo((CurrentSweep + 1) % Sweeps.Count);
        }

        // ------------------------------------------------------------------ dollhouse / floor plan

        Vec3 OrbitCenter()
        {
            var center = (_space.BoundsMin + _space.BoundsMax) * 0.5f;
            if (SelectedFloor < 0) return center;
            bool any = false;
            Vec3 mn = default, mx = default;
            for (int i = 0; i < Sweeps.Count; i++)
            {
                if (Sweeps[i].Floor != SelectedFloor) continue;
                var p = MarkerPos(i);
                mn = any ? Vec3.Min(mn, p) : p;
                mx = any ? Vec3.Max(mx, p) : p;
                any = true;
            }
            return any ? new Vec3((mn.X + mx.X) * 0.5f, _space.FloorHeight(SelectedFloor) + 1.2f, (mn.Z + mx.Z) * 0.5f) : center;
        }

        float OrbitDistanceFor(ViewMode mode)
        {
            var size = _space.BoundsMax - _space.BoundsMin;
            float radius = Math.Max(3f, (float)Math.Sqrt(size.X * size.X + size.Z * size.Z) * 0.5f);
            return radius / (float)Math.Tan(Rad((mode == ViewMode.Floorplan ? _s.FloorplanFov : _s.DollhouseFov) * 0.5f)) * 1.05f;
        }

        static (Vec3 position, Quat rotation) OrbitPose(Vec3 target, float yaw, float pitch, float distance)
        {
            var rot = Quat.Euler(pitch, yaw);
            return (target - rot * Vec3.Forward * distance, rot);
        }

        void StartOrbitMode(ViewMode mode)
        {
            _orbitYaw = Mode == ViewMode.Inside ? _yaw : _orbitYaw;
            _orbitPitch = mode == ViewMode.Floorplan ? 89.9f : 40f;
            _orbitTarget = _orbitTargetGoal = OrbitCenter();
            _orbitDistance = _orbitDistanceGoal = OrbitDistanceFor(mode);
            var to = OrbitPose(_orbitTarget, _orbitYaw, _orbitPitch, _orbitDistance);
            Animate(mode, to.position, to.rotation, mode == ViewMode.Floorplan ? _s.FloorplanFov : _s.DollhouseFov, 0f, CurrentSweep);
            CullBack = true;
            _move = default;
        }

        void BeginFlyIn(int sweep)
        {
            _yaw = _targetYaw = Mode == ViewMode.Inside ? _yaw : _orbitYaw;
            _pitch = _targetPitch = 0f;
            _fov = _targetFov = _s.DefaultFov;
            Animate(ViewMode.Inside, SweepPos(sweep), Quat.Euler(0, _yaw), _fov, 1f, sweep);
            CurrentSweep = sweep;
            _panos.RequestDepth(sweep);
        }

        void Animate(ViewMode target, Vec3 toPos, Quat toRot, float toFov, float toProjection, int sweep) => _anim = new ModeAnim
        {
            Active = true, Target = target, Start = _time, Duration = _s.ModeTransitionTime, FromPos = CameraPosition, ToPos = toPos,
            FromRot = CameraRotation, ToRot = toRot, FromFov = Fov, ToFov = toFov, FromProj = _projection, ToProj = toProjection, Sweep = sweep,
        };

        void UpdateModeAnim()
        {
            if (!_anim.Active) return;
            float t = Clamp01((float)(_time - _anim.Start) / Math.Max(0.01f, _anim.Duration)), e = Ease(t);
            CameraPosition = Vec3.Lerp(_anim.FromPos, _anim.ToPos, e);
            CameraRotation = Quat.Slerp(_anim.FromRot, _anim.ToRot, e);
            Fov = _anim.FromFov + (_anim.ToFov - _anim.FromFov) * e;
            // Projection fades out early when leaving a sweep, and in late when arriving, like Matterport.
            float phase = _anim.ToProj > _anim.FromProj ? InverseLerp(0.55f, 1f, t) : InverseLerp(0f, 0.45f, t);
            _projection = _anim.FromProj + (_anim.ToProj - _anim.FromProj) * phase;
            if (t < 1f) return;
            _anim.Active = false;
            Mode = _anim.Target;
            _projection = _anim.ToProj;
            if (Mode != ViewMode.Inside) return;
            CullBack = false;
            Arrive(_anim.Sweep);
        }

        void UpdateCamera(float dt)
        {
            if (_anim.Active) return;
            float k = 1f - (float)Math.Exp(-_s.LookSmoothing * dt);
            if (Mode == ViewMode.Inside)
            {
                float dyaw = ((_targetYaw - _yaw) % 360f + 540f) % 360f - 180f; // the short way round
                _yaw += dyaw * k;
                _pitch += (_targetPitch - _pitch) * k;
                _fov += (_targetFov - _fov) * k;
                CameraPosition = _move.Active ? Vec3.Lerp(_move.FromPos, _move.ToPos, Ease(MoveT)) : SweepPos(CurrentSweep);
                CameraRotation = Quat.Euler(_pitch, _yaw);
                Fov = _fov;
                return;
            }
            _orbitTarget = Vec3.Lerp(_orbitTarget, _orbitTargetGoal, k);
            _orbitDistance += (_orbitDistanceGoal - _orbitDistance) * k;
            (CameraPosition, CameraRotation) = OrbitPose(_orbitTarget, _orbitYaw, _orbitPitch, _orbitDistance);
            Fov += ((Mode == ViewMode.Floorplan ? _s.FloorplanFov : _s.DollhouseFov) - Fov) * k;
        }

        float ClipHeight() =>
            Mode == ViewMode.Inside || SelectedFloor < 0 || SelectedFloor + 1 >= _space.FloorCount ? float.PositiveInfinity : _space.FloorHeight(SelectedFloor + 1) - 0.25f;

        bool FloorShown(int floor) => SelectedFloor < 0 || floor == SelectedFloor;

        /// <summary>The shader binding (SpaceRenderer.Apply's rules): if only one panorama is ready, both slots show it.</summary>
        void PushView()
        {
            int a = CurrentSweep, b = -1;
            float blend = 0;
            if (_move.Active)
            {
                a = _move.From;
                b = _move.To;
                blend = Ease(InverseLerp(0.1f, 0.9f, MoveT));
            }
            else if (_anim.Active) a = _anim.Sweep;
            _shownA = a;
            _shownB = b;
            _panos.Touch(a);
            _panos.Touch(b);
            bool validA = a >= 0 && _panos.IsReady(a), validB = b >= 0 && _panos.IsReady(b);
            bool depthA = a >= 0 && _panos.HasDepth(a), depthB = b >= 0 && _panos.HasDepth(b);
            int ia = validA ? a : validB ? b : Math.Max(0, a), last = Sweeps.Count - 1;
            View = new ViewBinding
            {
                PanoA = validA ? a : validB ? b : -1,
                PanoB = validB ? b : validA ? a : -1,
                DepthA = depthA ? a : -1,
                DepthB = depthB ? b : depthA ? a : -1,
                PoseA = Math.Min(ia, last),
                PoseB = Math.Min(validB ? b : ia, last),
                Blend = validA && validB ? blend : validB ? 1f : 0f,
                Projection = _projection,
                UseDepth = _s.DepthMapSize > 0 && depthA && (b < 0 || depthB || blend <= 0),
                DebugView = DebugView,
                ValidA = validA || validB,
                ValidB = validA && validB,
                ClipHeight = Math.Min(ViewClipHeight, 1e9f),
                CullBack = CullBack,
            };
        }

        // ------------------------------------------------------------------ hover, markers, cursor

        void UpdateHover(in NavigatorInput input)
        {
            HoverSweep = -1;
            _reticle = false;
            if (Measuring || _anim.Active || _dragging || !input.PointerOver) return;
            if (Mode == ViewMode.Inside)
            {
                if (_move.Active || !_space.Raycast(input.RayOrigin, input.RayDirection, 200f, out var hit)) return;
                _reticle = true;
                _reticlePos = hit.Point;
                _reticleNormal = hit.Normal;
                float best = _s.MarkerRadius * 2.2f;
                foreach (int i in _visibleFromCurrent)
                {
                    float d = (MarkerPos(i) - hit.Point).Length;
                    if (d < best) { best = d; HoverSweep = i; }
                }
                return;
            }
            // From above: the marker nearest the pointer, within 22 pixels.
            float bestPixels = 22f;
            for (int i = 0; i < Sweeps.Count; i++)
            {
                if (!FloorShown(Sweeps[i].Floor)) continue;
                var v = MarkerPos(i) - input.RayOrigin;
                if (Vec3.Dot(v, input.RayDirection) <= 0) continue;
                float pixels = Rad(Vec3.Angle(input.RayDirection, v)) / Math.Max(1e-6f, input.PixelAngle);
                if (pixels < bestPixels) { bestPixels = pixels; HoverSweep = i; }
            }
        }

        void BuildMarkers()
        {
            Markers.Clear();
            void Add(Vec3 position, Vec3 normal, float radius, Rgba color, float inner, float fill)
            {
                if (radius > 0 && color.A > 0.001f) Markers.Add(new Marker { Position = position, Normal = normal, Radius = radius, Color = color, Inner = inner, Fill = fill });
            }
            if (_s.ShowMarkers && !_anim.Active && !Measuring)
            {
                if (Mode == ViewMode.Inside)
                {
                    var here = SweepPos(CurrentSweep);
                    foreach (int i in _visibleFromCurrent)
                    {
                        if (_move.Active && i == _move.To) continue;
                        float fade = (1f - InverseLerp(_s.MarkerMaxDistance * 0.6f, _s.MarkerMaxDistance, (MarkerPos(i) - here).Length)) * (_move.Active ? 0.5f : 1f);
                        var c = _s.MarkerColor;
                        c.A *= fade * (i == HoverSweep ? 1f : 0.75f);
                        Add(MarkerPos(i), Vec3.Up, _s.MarkerRadius * (i == HoverSweep ? 1.2f : 1f), c, 0.72f, 0.22f);
                    }
                }
                else
                {
                    float scale = Clamp(_orbitDistance / 25f, 0.6f, 3f);
                    for (int i = 0; i < Sweeps.Count; i++)
                    {
                        if (!FloorShown(Sweeps[i].Floor)) continue;
                        var c = _s.MarkerColor;
                        if (i != HoverSweep) c.A *= 0.7f;
                        Add(MarkerPos(i), Vec3.Up, _s.MarkerRadius * scale * (i == HoverSweep ? 1.3f : 1f), c, 0.55f, 0.5f);
                    }
                }
            }
            if (_s.ShowReticle && _reticle && HoverSweep < 0) Add(_reticlePos, _reticleNormal, _s.ReticleRadius, new Rgba(1f, 1f, 1f, 0.75f), 0.82f, 0.12f);
        }
    }
}
