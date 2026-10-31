using System;
using System.Collections.Generic;
using Godot;

namespace Matterportable
{
    /// <summary>
    /// Matterport-style camera (mirrors the Unity MatterportNavigator; keep behaviour changes in sync): drag to look,
    /// click to move with the cross-fade, W/A/S/D to step between sweeps, wheel to zoom, Dollhouse and Floor plan.
    /// Godot conventions: the camera looks down -Z, yaw is counter-clockwise from above, positive pitch looks up.
    /// UI controls consume their own clicks, so pointer input arrives through _UnhandledInput.
    /// </summary>
    public partial class MatterportNavigator : Camera3D
    {
        public enum ViewMode { Inside, Dollhouse, Floorplan }

        public MatterportSpace Space;
        /// <summary>View to switch to once the space has loaded.</summary>
        public ViewMode StartMode = ViewMode.Inside;
        public ViewMode Mode { get; private set; } = ViewMode.Inside;
        public int CurrentSweep { get; private set; } = -1;
        public int SelectedFloor { get; private set; } = -1;
        public int HoverSweep { get; private set; } = -1;
        public bool IsMoving => _move.Active;
        public bool IsChangingMode => _anim.Active;
        public bool IsWaitingForPanorama => _pending >= 0;
        public bool AutoTour { get; set; }
        /// <summary>Takes over left clicks (measure mode): return true to consume the click. Also hides the sweep
        /// markers and the move cursor while set.</summary>
        public Func<Vector2, bool> ClickHandler;
        /// <summary>World height above which geometry is hidden in the current view (floor filter), else +infinity.</summary>
        public float ViewClipHeight => Mode != ViewMode.Inside && !(_anim.Active && _anim.Target == ViewMode.Inside) ? ClipHeight() : float.PositiveInfinity;
        public event Action<int> ArrivedAtSweep;

        ViewerSettings S => Space.Settings;
        float _yaw, _pitch, _targetYaw, _targetPitch, _fov = 75f, _targetFov = 75f, _projection = 1f;

        struct Move { public bool Active; public int From, To; public Vector3 FromPos, ToPos; public float Start, Duration; }
        struct ModeAnim
        {
            public bool Active;
            public ViewMode Target;
            public float Start, Duration, FromFov, ToFov, FromProj, ToProj;
            public Vector3 FromPos, ToPos;
            public Quaternion FromRot, ToRot;
            public int Sweep;
        }

        Move _move;
        ModeAnim _anim;
        int _queued = -1, _pending = -1;
        float _pendingSince, _arrivedAt;
        bool _pendingIsFlyIn, _initialised;
        Vector3 _orbitTarget, _orbitTargetGoal;
        float _orbitYaw, _orbitPitch = 40f, _orbitDistance = 12f, _orbitDistanceGoal = 12f;

        bool _pointerDown, _dragging;
        MouseButton _pointerButton;
        Vector2 _pressPos, _dragDelta;
        float _scroll;

        SweepMarkers _markers;
        readonly List<int> _visibleFromCurrent = new List<int>();
        bool[] _visible = Array.Empty<bool>();
        bool _reticle;
        Vector3 _reticlePos, _reticleNormal;

        static float Now => Time.GetTicksMsec() / 1000f;
        bool HasSpace => Space != null && Space.IsLoaded && Space.Sweeps.Count > 0;
        float MoveT => Mathf.Clamp((Now - _move.Start) / Mathf.Max(0.01f, _move.Duration), 0f, 1f);
        static float Ease(float t) => t * t * (3f - 2f * t); // = Unity AnimationCurve.EaseInOut(0,0,1,1)

        public override void _Ready()
        {
            Near = 0.03f;
            Far = 1000f;
            _markers = new SweepMarkers(this);
        }

        void Initialise()
        {
            Mode = ViewMode.Inside;
            _move = default;
            _anim = default;
            _pending = _queued = -1;
            SelectedFloor = -1;
            _projection = 1f;
            _yaw = _targetYaw = 0f;
            _pitch = _targetPitch = 0f;
            _fov = _targetFov = S.DefaultFov;
            Space.SetCullBack(false);
            Arrive(Mathf.Clamp(Space.StartSweep, 0, Space.Sweeps.Count - 1));
            _initialised = true;
            if (StartMode != ViewMode.Inside) StartOrbitMode(StartMode);
        }

        // ------------------------------------------------------------------ public API

        public void GoTo(int sweep)
        {
            if (!HasSpace || sweep < 0 || sweep >= Space.Sweeps.Count) return;
            if (Mode != ViewMode.Inside) { WhenPanoramaReady(sweep, flyIn: true); return; }
            if (sweep == CurrentSweep && !_move.Active) return;
            if (_move.Active || _anim.Active) _queued = sweep;
            else WhenPanoramaReady(sweep, flyIn: false);
        }

        public void SetMode(ViewMode mode)
        {
            if (!HasSpace || _anim.Active || mode == Mode) return;
            if (mode == ViewMode.Inside) WhenPanoramaReady(Math.Max(0, CurrentSweep), flyIn: true);
            else StartOrbitMode(mode);
        }

        /// <summary>Jumps instantly (no transition), looking along <paramref name="yaw"/> and <paramref name="pitch"/>
        /// (degrees, Godot convention) - for tests and tools.</summary>
        public void Teleport(int sweep, float yaw, float pitch = 0f)
        {
            if (!HasSpace || sweep < 0 || sweep >= Space.Sweeps.Count) return;
            if (!_initialised) Initialise();
            _move = default;
            _anim = default;
            _pending = _queued = -1;
            Mode = ViewMode.Inside;
            _projection = 1f;
            Space.SetCullBack(false);
            _yaw = _targetYaw = yaw;
            _pitch = _targetPitch = pitch;
            _fov = _targetFov = S.DefaultFov;
            Arrive(sweep);
        }

        public void SetFloor(int floor)
        {
            if (!HasSpace) return;
            SelectedFloor = Mathf.Clamp(floor, -1, Space.FloorCount - 1);
            if (Mode != ViewMode.Inside) _orbitTargetGoal = OrbitCenter();
        }

        // ------------------------------------------------------------------ input

        public override void _UnhandledInput(InputEvent e)
        {
            if (!HasSpace) return;
            if (e is InputEventMouseButton mb)
            {
                if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) _scroll += mb.Factor > 0 ? mb.Factor : 1f;
                else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) _scroll -= mb.Factor > 0 ? mb.Factor : 1f;
                else if (mb.Pressed && !_pointerDown && mb.ButtonIndex <= MouseButton.Middle)
                {
                    _pointerDown = true;
                    _pointerButton = mb.ButtonIndex;
                    _pressPos = mb.Position;
                    _dragging = false;
                }
                else if (!mb.Pressed && _pointerDown && mb.ButtonIndex == _pointerButton)
                {
                    if (!_dragging && _pointerButton == MouseButton.Left) Click(mb.Position);
                    _pointerDown = _dragging = false;
                }
            }
            else if (e is InputEventMouseMotion mm && _pointerDown)
            {
                if (!_dragging && (mm.Position - _pressPos).Length() > S.ClickDragThreshold) _dragging = true;
                if (_dragging) _dragDelta += mm.Relative;
            }
            else if (e is InputEventKey k && k.Pressed && !k.Echo)
            {
                switch (k.Keycode)
                {
                    case Key.Key1: SetMode(ViewMode.Inside); break;
                    case Key.Key2: SetMode(ViewMode.Dollhouse); break;
                    case Key.Key3: SetMode(ViewMode.Floorplan); break;
                    case Key.F2: Space.DebugView++; break;
                    case Key.P: AutoTour = !AutoTour; break;
                    case Key.Home: GoTo(Mathf.Clamp(Space.StartSweep, 0, Space.Sweeps.Count - 1)); break;
                    case Key.Bracketright: GoTo((CurrentSweep + 1) % Space.Sweeps.Count); break;
                    case Key.Bracketleft: GoTo((CurrentSweep - 1 + Space.Sweeps.Count) % Space.Sweeps.Count); break;
                    case Key.Pageup: SetFloor(SelectedFloor < 0 ? 0 : Math.Min(SelectedFloor + 1, Space.FloorCount - 1)); break;
                    case Key.Pagedown: SetFloor(SelectedFloor <= 0 ? -1 : SelectedFloor - 1); break;
                }
                if (Mode == ViewMode.Inside)
                {
                    if (k.Keycode == Key.W || k.Keycode == Key.Up) Step(new Vector3(0, 0, -1));
                    if (k.Keycode == Key.S || k.Keycode == Key.Down) Step(new Vector3(0, 0, 1));
                    if (k.Keycode == Key.A) Step(new Vector3(-1, 0, 0));
                    if (k.Keycode == Key.D) Step(new Vector3(1, 0, 0));
                }
            }
        }

        void HandleHeldKeys(float dt)
        {
            float turn = 0;
            if (Input.IsKeyPressed(Key.Left) || Input.IsKeyPressed(Key.Q)) turn += 1;
            if (Input.IsKeyPressed(Key.Right) || Input.IsKeyPressed(Key.E)) turn -= 1;
            if (Mode == ViewMode.Inside)
            {
                _targetYaw += turn * S.KeyTurnSpeed * dt;
                return;
            }
            if (_anim.Active) return;
            if (Input.IsKeyPressed(Key.A)) turn += 1;
            if (Input.IsKeyPressed(Key.D)) turn -= 1;
            _orbitYaw += turn * S.KeyTurnSpeed * dt;
            float zoom = 0;
            if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) zoom -= 1;
            if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) zoom += 1;
            _orbitDistanceGoal = Mathf.Clamp(_orbitDistanceGoal * Mathf.Exp(zoom * dt), 1.5f, 500f);
        }

        void ApplyPointer()
        {
            if (_dragDelta != Vector2.Zero) Drag(_dragDelta, _pointerButton);
            _dragDelta = Vector2.Zero;
            if (Mathf.Abs(_scroll) > 0.001f)
            {
                if (Mode == ViewMode.Inside) _targetFov = Mathf.Clamp(_targetFov - _scroll * S.ZoomStep, S.MinFov, S.MaxFov);
                else _orbitDistanceGoal = Mathf.Clamp(_orbitDistanceGoal * Mathf.Exp(-_scroll * 0.15f), 1.5f, 500f);
            }
            _scroll = 0;
        }

        void Drag(Vector2 delta, MouseButton button)
        {
            if (_anim.Active) return;
            float height = Mathf.Max(1, GetViewport().GetVisibleRect().Size.Y);
            if (Mode == ViewMode.Inside)
            {
                // Grab-style: the point under the cursor follows the cursor.
                float degPerPixel = _fov / height * S.DragSensitivity;
                _targetYaw += delta.X * degPerPixel;
                _targetPitch = Mathf.Clamp(_targetPitch + delta.Y * degPerPixel, -85f, 85f);
                return;
            }
            bool rotate = Mode == ViewMode.Dollhouse ? button == MouseButton.Left : button != MouseButton.Left;
            if (rotate)
            {
                _orbitYaw -= delta.X * S.OrbitSensitivity;
                if (Mode == ViewMode.Dollhouse) _orbitPitch = Mathf.Clamp(_orbitPitch + delta.Y * S.OrbitSensitivity, 8f, 89f);
                return;
            }
            // Pan in the ground plane so the scene follows the cursor.
            float worldPerPixel = 2f * _orbitDistance * Mathf.Tan(Mathf.DegToRad(Fov * 0.5f)) / height;
            var pan = new Basis(Vector3.Up, Mathf.DegToRad(_orbitYaw)) * new Vector3(-delta.X, 0, -delta.Y) * worldPerPixel;
            _orbitTargetGoal += pan;
            _orbitTarget += pan;
        }

        void Click(Vector2 mouse)
        {
            if (_anim.Active || (ClickHandler != null && ClickHandler(mouse))) return;
            var origin = ProjectRayOrigin(mouse);
            var dir = ProjectRayNormal(mouse);
            int target = Mode == ViewMode.Inside ? PickInside(origin, dir) : PickFromAbove(origin, dir);
            if (target >= 0) GoTo(target);
        }

        // ------------------------------------------------------------------ choosing sweeps

        int PickInside(Vector3 origin, Vector3 dir)
        {
            if (HoverSweep >= 0) return HoverSweep;
            var cam = Space.SweepWorldPosition(CurrentSweep);
            int best = -1;
            float bestScore = float.MaxValue;
            if (Space.Raycast(origin, dir, 200f, out var hit, out var normal, out _))
            {
                bool floorLike = normal.Y > 0.6f;
                // Where would you stand to look at what you clicked? On floors: right there. Elsewhere: in front of it.
                var stand = floorLike ? hit : hit - dir * 0.6f;
                for (int i = 0; i < Space.Sweeps.Count; i++)
                {
                    if (i == CurrentSweep) continue;
                    var m = Space.MarkerWorldPosition(i);
                    float flat = new Vector2(m.X - stand.X, m.Z - stand.Z).Length();
                    float dy = Mathf.Abs(m.Y - (floorLike ? hit.Y : m.Y));
                    float score = flat + 2f * dy;
                    if (!floorLike && Mathf.RadToDeg(dir.AngleTo(Space.SweepWorldPosition(i) - cam)) > 40f) continue;
                    if (!IsVisible(i)) score += 3f;
                    if (score < bestScore) { bestScore = score; best = i; }
                }
                if (best >= 0 && bestScore < (floorLike ? 2.5f : 4f) + S.ClickSnapRadius) return best;
                if (best >= 0 && IsVisible(best)) return best;
            }
            // Nothing hit (window, sky) or no good candidate: nearest visible sweep in the click direction.
            best = -1;
            float bestAngle = 25f;
            for (int i = 0; i < Space.Sweeps.Count; i++)
            {
                if (i == CurrentSweep || !IsVisible(i)) continue;
                float angle = Mathf.RadToDeg(dir.AngleTo(Space.SweepWorldPosition(i) - cam));
                if (angle < bestAngle) { bestAngle = angle; best = i; }
            }
            return best;
        }

        int PickFromAbove(Vector3 origin, Vector3 dir)
        {
            if (HoverSweep >= 0) return HoverSweep;
            if (!Space.RaycastBelow(origin, dir, 1000f, ClipHeight(), out var hit, out _)) return -1;
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < Space.Sweeps.Count; i++)
            {
                if (!FloorShown(Space.Sweeps[i].Floor)) continue;
                float d = (Space.MarkerWorldPosition(i) - hit).Length();
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        void Step(Vector3 localDirection)
        {
            if (CurrentSweep < 0) return;
            var dir = new Basis(Vector3.Up, Mathf.DegToRad(_yaw)) * localDirection;
            var from = Space.SweepWorldPosition(CurrentSweep);
            int best = -1;
            float bestScore = float.MaxValue;
            void Consider(int i)
            {
                var v = Space.SweepWorldPosition(i) - from;
                v.Y = 0;
                float dist = v.Length(), angle = Mathf.RadToDeg(dir.AngleTo(v));
                if (i == CurrentSweep || dist < 0.2f || dist > S.StepMaxDistance || angle > S.StepMaxAngle) return;
                float a = angle / S.StepMaxAngle;
                float score = dist * (1f + 1.5f * a * a); // prefer close sweeps straight ahead
                if (score < bestScore) { bestScore = score; best = i; }
            }
            foreach (int i in Space.Sweeps[CurrentSweep].Neighbors) Consider(i);
            if (best < 0)
                for (int i = 0; i < Space.Sweeps.Count; i++) if (IsVisible(i)) Consider(i);
            if (best >= 0) GoTo(best);
        }

        bool IsVisible(int i) => i >= 0 && i < _visible.Length && _visible[i];

        void ComputeVisibility()
        {
            int n = Space.Sweeps.Count;
            if (_visible.Length != n) _visible = new bool[n];
            _visibleFromCurrent.Clear();
            var from = Space.SweepWorldPosition(CurrentSweep);
            for (int i = 0; i < n; i++)
            {
                _visible[i] = false;
                if (i == CurrentSweep) continue;
                var marker = Space.MarkerWorldPosition(i) + Vector3.Up * 0.15f;
                float dist = (marker - from).Length();
                _visible[i] = dist <= S.MarkerMaxDistance * 1.6f && Space.LineOfSight(from, marker);
                if (_visible[i] && dist <= S.MarkerMaxDistance) _visibleFromCurrent.Add(i);
            }
        }

        // ------------------------------------------------------------------ inside transitions

        void StartMove(int to)
        {
            var from = CurrentSweep >= 0 ? Space.SweepWorldPosition(CurrentSweep) : Position;
            var target = Space.SweepWorldPosition(to);
            _move = new Move { Active = true, From = CurrentSweep, To = to, FromPos = from, ToPos = target, Start = Now, Duration = S.TransitionDuration(from.DistanceTo(target)) };
            Space.Panoramas.RequestDepth(to);
            Space.Panoramas.Request(to, S.PanoMaxSize, -5f);
        }

        /// <summary>Moves (or flies in) once the target's panorama is loaded - after at most 4 s.</summary>
        void WhenPanoramaReady(int sweep, bool flyIn)
        {
            _pending = sweep;
            _pendingIsFlyIn = flyIn;
            _pendingSince = Now;
            if (!Space.Panoramas.IsReady(sweep)) Space.Panoramas.RequestProgressive(sweep, S.PanoMaxSize, -10f);
            UpdatePending();
        }

        void UpdatePending()
        {
            if (_pending < 0 || (!Space.Panoramas.IsReady(_pending) && Now - _pendingSince < 4f)) return;
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
            if (_queued >= 0)
            {
                int q = _queued;
                _queued = -1;
                GoTo(q);
            }
        }

        void Arrive(int sweep)
        {
            CurrentSweep = sweep;
            _arrivedAt = Now;
            Space.Panoramas.RequestProgressive(sweep, S.PanoMaxSize, 0f);
            Space.Panoramas.RequestDepth(sweep);
            ComputeVisibility();
            // Prefetch neighbours (nearest first): the closest two at full size, the rest at the small tier.
            var neighbors = new List<int>(Space.Sweeps[sweep].Neighbors);
            if (neighbors.Count == 0) neighbors.AddRange(_visibleFromCurrent);
            var here = Space.SweepWorldPosition(sweep);
            neighbors.Sort((a, b) => Space.SweepWorldPosition(a).DistanceSquaredTo(here).CompareTo(Space.SweepWorldPosition(b).DistanceSquaredTo(here)));
            for (int k = 0; k < neighbors.Count && k < S.PrefetchNeighbors; k++)
            {
                var entry = Space.Manifest.Sweeps[neighbors[k]];
                Space.Panoramas.Request(neighbors[k], k < 2 ? S.PanoMaxSize : (entry.SmallestTier?.Size ?? S.PanoMaxSize), 10f + k);
            }
            ArrivedAtSweep?.Invoke(sweep);
        }

        void UpdateTour()
        {
            if (!AutoTour || Mode != ViewMode.Inside || _move.Active || _pending >= 0 || Now - _arrivedAt < 2.5f) return;
            _arrivedAt = Now;
            GoTo((CurrentSweep + 1) % Space.Sweeps.Count);
        }

        // ------------------------------------------------------------------ dollhouse / floor plan

        Vector3 OrbitCenter()
        {
            var center = Space.WorldBounds.GetCenter();
            if (SelectedFloor < 0) return center;
            Aabb? fb = null;
            for (int i = 0; i < Space.Sweeps.Count; i++)
                if (Space.Sweeps[i].Floor == SelectedFloor)
                    fb = fb?.Expand(Space.MarkerWorldPosition(i)) ?? new Aabb(Space.MarkerWorldPosition(i), Vector3.Zero);
            if (fb == null) return center;
            var c = fb.Value.GetCenter();
            return new Vector3(c.X, Space.FloorHeightWorld(SelectedFloor) + 1.2f, c.Z);
        }

        float OrbitDistanceFor(ViewMode mode)
        {
            var size = Space.WorldBounds.Size;
            float radius = Mathf.Max(3f, new Vector2(size.X, size.Z).Length() * 0.5f);
            float fov = mode == ViewMode.Floorplan ? S.FloorplanFov : S.DollhouseFov;
            return radius / Mathf.Tan(Mathf.DegToRad(fov * 0.5f)) * 1.05f;
        }

        /// <summary>Camera looking down at <paramref name="target"/> from <paramref name="pitch"/> degrees above.</summary>
        static Transform3D OrbitPose(Vector3 target, float yaw, float pitch, float distance)
        {
            var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(-pitch), Mathf.DegToRad(yaw), 0));
            return new Transform3D(basis, target + basis.Z * distance);
        }

        void StartOrbitMode(ViewMode mode)
        {
            _orbitYaw = Mode == ViewMode.Inside ? _yaw : _orbitYaw;
            _orbitPitch = mode == ViewMode.Floorplan ? 89.9f : 40f;
            _orbitTarget = _orbitTargetGoal = OrbitCenter();
            _orbitDistance = _orbitDistanceGoal = OrbitDistanceFor(mode);
            var to = OrbitPose(_orbitTarget, _orbitYaw, _orbitPitch, _orbitDistance);
            Animate(mode, to, mode == ViewMode.Floorplan ? S.FloorplanFov : S.DollhouseFov, 0f, CurrentSweep);
            Space.SetCullBack(true);
            _move = default;
        }

        void BeginFlyIn(int sweep)
        {
            _yaw = _targetYaw = Mode == ViewMode.Inside ? _yaw : _orbitYaw;
            _pitch = _targetPitch = 0f;
            _fov = _targetFov = S.DefaultFov;
            Animate(ViewMode.Inside, new Transform3D(new Basis(Vector3.Up, Mathf.DegToRad(_yaw)), Space.SweepWorldPosition(sweep)), _fov, 1f, sweep);
            CurrentSweep = sweep;
            Space.Panoramas.RequestDepth(sweep);
        }

        void Animate(ViewMode target, Transform3D to, float toFov, float toProjection, int sweep)
        {
            _anim = new ModeAnim
            {
                Active = true, Target = target, Start = Now, Duration = S.ModeTransitionTime,
                FromPos = Position, ToPos = to.Origin, FromRot = Basis.GetRotationQuaternion(), ToRot = to.Basis.GetRotationQuaternion(),
                FromFov = Fov, ToFov = toFov, FromProj = _projection, ToProj = toProjection, Sweep = sweep,
            };
        }

        void UpdateModeAnim()
        {
            if (!_anim.Active) return;
            float t = Mathf.Clamp((Now - _anim.Start) / Mathf.Max(0.01f, _anim.Duration), 0f, 1f);
            float e = Ease(t);
            Transform = new Transform3D(new Basis(_anim.FromRot.Slerp(_anim.ToRot, e)), _anim.FromPos.Lerp(_anim.ToPos, e));
            Fov = Mathf.Lerp(_anim.FromFov, _anim.ToFov, e);
            // Projection fades out early when leaving a sweep, and in late when arriving, like Matterport.
            float phase = _anim.ToProj > _anim.FromProj ? Mathf.InverseLerp(0.55f, 1f, t) : Mathf.InverseLerp(0f, 0.45f, t);
            _projection = Mathf.Lerp(_anim.FromProj, _anim.ToProj, Mathf.Clamp(phase, 0f, 1f));
            if (t < 1f) return;
            _anim.Active = false;
            Mode = _anim.Target;
            _projection = _anim.ToProj;
            if (Mode == ViewMode.Inside)
            {
                Space.SetCullBack(false);
                Arrive(_anim.Sweep);
            }
        }

        // ------------------------------------------------------------------ frame

        public override void _Process(double delta)
        {
            _markers.BeginFrame();
            if (!HasSpace) return;
            if (!_initialised) Initialise();
            float dt = (float)delta;
            HandleHeldKeys(dt);
            ApplyPointer();
            UpdatePending();
            UpdateMove();
            UpdateModeAnim();
            UpdateCamera(dt);
            UpdateHover();
            UpdateTour();
            PushView();
            DrawMarkers();
        }

        void UpdateCamera(float dt)
        {
            if (_anim.Active) return;
            float k = 1f - Mathf.Exp(-S.LookSmoothing * dt);
            if (Mode == ViewMode.Inside)
            {
                _yaw = Mathf.RadToDeg(Mathf.LerpAngle(Mathf.DegToRad(_yaw), Mathf.DegToRad(_targetYaw), k));
                _pitch = Mathf.Lerp(_pitch, _targetPitch, k);
                _fov = Mathf.Lerp(_fov, _targetFov, k);
                var pos = _move.Active ? _move.FromPos.Lerp(_move.ToPos, Ease(MoveT)) : Space.SweepWorldPosition(CurrentSweep);
                Transform = new Transform3D(Basis.FromEuler(new Vector3(Mathf.DegToRad(_pitch), Mathf.DegToRad(_yaw), 0)), pos);
                Fov = _fov;
                return;
            }
            _orbitTarget = _orbitTarget.Lerp(_orbitTargetGoal, k);
            _orbitDistance = Mathf.Lerp(_orbitDistance, _orbitDistanceGoal, k);
            Transform = OrbitPose(_orbitTarget, _orbitYaw, _orbitPitch, _orbitDistance);
            Fov = Mathf.Lerp(Fov, Mode == ViewMode.Floorplan ? S.FloorplanFov : S.DollhouseFov, k);
        }

        float ClipHeight()
        {
            if (Mode == ViewMode.Inside || SelectedFloor < 0 || SelectedFloor + 1 >= Space.FloorCount) return float.PositiveInfinity;
            return Space.FloorHeightWorld(SelectedFloor + 1) - 0.25f;
        }

        bool FloorShown(int floor) => SelectedFloor < 0 || floor == SelectedFloor;

        void PushView()
        {
            int a = CurrentSweep, b = -1;
            float blend = 0;
            if (_move.Active)
            {
                a = _move.From;
                b = _move.To;
                float t = Mathf.Clamp(Mathf.InverseLerp(0.1f, 0.9f, MoveT), 0f, 1f);
                blend = t * t * (3f - 2f * t);
            }
            else if (_anim.Active) a = _anim.Sweep;
            Space.SetView(a, b, blend, _projection, ViewClipHeight);
        }

        // ------------------------------------------------------------------ hover, markers, cursor

        void UpdateHover()
        {
            HoverSweep = -1;
            _reticle = false;
            if (_anim.Active || _dragging || ClickHandler != null) return;
            var vp = GetViewport();
            var mouse = vp.GetMousePosition();
            if (!vp.GetVisibleRect().HasPoint(mouse) || vp.GuiGetHoveredControl() != null) return;
            var origin = ProjectRayOrigin(mouse);
            var dir = ProjectRayNormal(mouse);
            if (Mode == ViewMode.Inside)
            {
                if (_move.Active || !Space.Raycast(origin, dir, 200f, out var hit, out var normal, out _)) return;
                _reticle = true;
                _reticlePos = hit;
                _reticleNormal = normal;
                float best = S.MarkerRadius * 2.2f;
                foreach (int i in _visibleFromCurrent)
                {
                    float d = (Space.MarkerWorldPosition(i) - hit).Length();
                    if (d < best) { best = d; HoverSweep = i; }
                }
                return;
            }
            float bestPx = 22f;
            for (int i = 0; i < Space.Sweeps.Count; i++)
            {
                var m = Space.MarkerWorldPosition(i);
                if (!FloorShown(Space.Sweeps[i].Floor) || IsPositionBehind(m)) continue;
                float d = (UnprojectPosition(m) - mouse).Length();
                if (d < bestPx) { bestPx = d; HoverSweep = i; }
            }
        }

        void DrawMarkers()
        {
            var s = S;
            if (s.ShowMarkers && !_anim.Active && ClickHandler == null)
            {
                if (Mode == ViewMode.Inside)
                {
                    var here = Space.SweepWorldPosition(CurrentSweep);
                    foreach (int i in _visibleFromCurrent)
                    {
                        if (_move.Active && i == _move.To) continue;
                        var m = Space.MarkerWorldPosition(i);
                        float fade = 1f - Mathf.Clamp(Mathf.InverseLerp(s.MarkerMaxDistance * 0.6f, s.MarkerMaxDistance, m.DistanceTo(here)), 0f, 1f);
                        if (_move.Active) fade *= 0.5f;
                        var c = s.MarkerColor;
                        c.A *= fade * (i == HoverSweep ? 1f : 0.75f);
                        _markers.Draw(m, Vector3.Up, s.MarkerRadius * (i == HoverSweep ? 1.2f : 1f), c);
                    }
                }
                else
                {
                    float scale = Mathf.Clamp(_orbitDistance / 25f, 0.6f, 3f);
                    for (int i = 0; i < Space.Sweeps.Count; i++)
                    {
                        if (!FloorShown(Space.Sweeps[i].Floor)) continue;
                        var c = s.MarkerColor;
                        if (i != HoverSweep) c.A *= 0.7f;
                        _markers.Draw(Space.MarkerWorldPosition(i), Vector3.Up, s.MarkerRadius * scale * (i == HoverSweep ? 1.3f : 1f), c, 0.55f, 0.5f);
                    }
                }
            }
            if (s.ShowReticle && _reticle && HoverSweep < 0)
                _markers.Draw(_reticlePos, _reticleNormal, s.ReticleRadius, new Color(1f, 1f, 1f, 0.75f), 0.82f, 0.12f);
        }
    }
}
