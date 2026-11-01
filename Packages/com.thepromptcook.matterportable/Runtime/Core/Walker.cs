using System;

namespace Matterportable.Core
{
    /// <summary>
    /// The body that walks the scan in Walk mode (free WASD movement). The scanned mesh is its collider, so walls,
    /// furniture, stairs, floors and ceilings all act on it: anything taller than a step blocks it (it slides along),
    /// it climbs stairs and follows floors down, falls off ledges, can't squeeze under low ceilings, and refuses moves
    /// with no floor below (holes in the scan, open windows, the edge of the scanned area). Core frame, metres.
    /// </summary>
    public sealed class Walker
    {
        const float MaxDrop = 4f, Gravity = 9.81f, HeadRoom = 0.12f, MaxSubstep = 0.1f;
        const float MinFloorNormal = 0.64f; // floors are flatter than 50°

        readonly Bvh _bvh;
        readonly float _radius, _step;
        readonly float[] _spheres; // body sphere heights above the feet, from just above a step to the head
        float _fallSpeed;

        /// <summary>The point between the feet, on the floor.</summary>
        public Vec3 Feet { get; private set; }
        public float EyeHeight { get; }
        public bool Grounded { get; private set; } = true;
        /// <summary>False when there was nowhere to stand at the start: no floor below (unplaced 360° photos, shots
        /// outside the scanned area) or the body would be stuck in the mesh.</summary>
        public bool Standing { get; }
        public Vec3 Eye => new Vec3(Feet.X, Feet.Y + EyeHeight, Feet.Z);

        /// <param name="eye">Where the eyes start (a sweep's camera position). The feet go to the floor under it, so the
        /// eye height is the camera's height above that floor and standing there shows exactly its photo.</param>
        public Walker(Bvh bvh, Vec3 eye, float radius, float stepHeight)
        {
            _bvh = bvh;
            _radius = radius;
            _step = stepHeight;
            Standing = FloorUnder(eye, out float floor);
            EyeHeight = Standing ? eye.Y - floor : 1.55f;
            float low = stepHeight + radius, high = Math.Max(low, EyeHeight + HeadRoom - radius);
            int count = 1 + (int)Math.Ceiling((high - low) / radius);
            _spheres = new float[count];
            for (int i = 0; i < count; i++) _spheres[i] = count == 1 ? low : low + (high - low) * i / (count - 1);
            var feet = new Vec3(eye.X, eye.Y - EyeHeight, eye.Z);
            Feet = feet;
            if (!Standing) return;
            // Right where the camera was, or the nearest spot within half a metre the body fits (next to a table).
            for (int ring = 0; ring <= 3; ring++)
                for (int k = 0; k < (ring == 0 ? 1 : 12); k++)
                {
                    double a = k * Math.PI / 6;
                    var p = feet + new Vec3((float)Math.Sin(a), 0, (float)Math.Cos(a)) * (0.15f * ring);
                    if (ring > 0)
                    {
                        if (!FindFloor(p, out float f) || Math.Abs(f - feet.Y) > _step) continue;
                        p = new Vec3(p.X, f, p.Z);
                    }
                    if (!Resolve(ref p)) continue;
                    Feet = p;
                    return;
                }
            Standing = false;
        }

        /// <summary>The floor under a standing camera: the first walkable surface 1 to 2.5 m below it (a table or counter
        /// under a tripod is less than a metre down, so it is skipped).</summary>
        bool FloorUnder(Vec3 eye, out float floor)
        {
            floor = 0;
            var origin = eye;
            for (int i = 0; i < 8; i++)
            {
                if (!_bvh.Raycast(origin, Vec3.Down, eye.Y - origin.Y < 2.5f ? 2.5f - (eye.Y - origin.Y) : 0f, out var hit)) return false;
                float below = eye.Y - hit.Point.Y;
                if (below >= 1f && Math.Abs(hit.Normal.Y) >= MinFloorNormal)
                {
                    floor = hit.Point.Y;
                    return true;
                }
                origin = hit.Point + Vec3.Down * 0.01f;
            }
            return false;
        }

        /// <summary>Moves by a horizontal displacement (in sub-steps short enough never to pass through a wall),
        /// then applies gravity.</summary>
        public void Move(Vec3 delta, float dt)
        {
            delta = new Vec3(delta.X, 0, delta.Z);
            int steps = Math.Max(1, (int)Math.Ceiling(delta.Length / MaxSubstep));
            for (int i = 0; i < steps; i++) Feet = Step(Feet, delta / steps);
            Fall(dt);
        }

        Vec3 Step(Vec3 from, Vec3 delta)
        {
            var p = from + delta;
            if (!FindFloor(p, out float floor)) return from; // nothing to stand on
            if (Grounded && floor > p.Y - _step) p = new Vec3(p.X, floor, p.Z); // stairs up and down
            if (!Resolve(ref p)) return from; // walls, furniture, low ceilings
            if (Grounded && FindFloor(p, out floor) && floor > p.Y - _step) p = new Vec3(p.X, floor, p.Z);
            return p;
        }

        void Fall(float dt)
        {
            if (!FindFloor(Feet, out float floor)) return;
            if ((Grounded && floor > Feet.Y - _step) || floor >= Feet.Y)
            {
                Feet = new Vec3(Feet.X, floor, Feet.Z);
                Grounded = true;
                _fallSpeed = 0;
                return;
            }
            Grounded = false; // off a ledge
            _fallSpeed += Gravity * dt;
            Feet = new Vec3(Feet.X, Math.Max(floor, Feet.Y - _fallSpeed * dt), Feet.Z);
            if (Feet.Y > floor) return;
            Grounded = true;
            _fallSpeed = 0;
        }

        /// <summary>Pushes the body out of the mesh sideways. False when it can't be: a surface inside the body that
        /// is flat (too tall to step onto, or too low to pass under) or a squeeze that doesn't resolve.</summary>
        bool Resolve(ref Vec3 feet)
        {
            for (int iteration = 0; iteration < 8; iteration++)
            {
                float worst = 0;
                Vec3 normal = default;
                foreach (float h in _spheres)
                    if (_bvh.DeepestContact(new Vec3(feet.X, feet.Y + h, feet.Z), _radius, out var n, out float depth) && depth > worst)
                    {
                        worst = depth;
                        normal = n;
                    }
                if (worst < 1e-4f) return true;
                var flat = new Vec3(normal.X, 0, normal.Z);
                float f = flat.Length;
                if (f < 0.3f) return false;
                feet += flat * (Math.Min(worst / f, _radius) / f); // far enough sideways to clear the surface
            }
            return false;
        }

        /// <summary>The highest floor under the body's footprint, from a step above the feet down to
        /// <see cref="MaxDrop"/> below.</summary>
        bool FindFloor(Vec3 feet, out float floor)
        {
            floor = float.NegativeInfinity;
            float o = _radius * 0.5f, top = feet.Y + _step + 0.01f;
            for (int i = 0; i < 5; i++)
            {
                float x = feet.X + (i == 1 ? o : i == 2 ? -o : 0), z = feet.Z + (i == 3 ? o : i == 4 ? -o : 0);
                if (_bvh.Raycast(new Vec3(x, top, z), Vec3.Down, _step + MaxDrop, out var hit) && Math.Abs(hit.Normal.Y) >= MinFloorNormal)
                    floor = Math.Max(floor, hit.Point.Y);
            }
            return floor > float.NegativeInfinity;
        }
    }
}
