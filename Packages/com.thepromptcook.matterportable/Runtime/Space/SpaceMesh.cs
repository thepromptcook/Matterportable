using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Matterportable.Core;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace Matterportable
{
    /// <summary>
    /// The scanned mesh as Unity objects: the room, and a child per prop (moved by its pose in Walk mode), each with a
    /// submesh per Matterport texture. One material per texture is shared by all of them.
    /// </summary>
    internal sealed class SpaceMesh : IDisposable
    {
        static readonly int IdCull = Shader.PropertyToID("_Cull");
        static readonly int IdMainTex = Shader.PropertyToID("_MainTex");
        static readonly int IdPropTint = Shader.PropertyToID("_MP_PropTint");

        readonly List<Mesh> _meshes = new List<Mesh>();
        readonly List<Material> _materials = new List<Material>();
        readonly List<Texture> _textures = new List<Texture>();
        readonly Dictionary<string, int> _materialIndex = new Dictionary<string, int>();
        readonly List<Renderer> _props = new List<Renderer>(); // per prop (null: nothing to draw)
        readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        Vector4[] _tints;

        public GameObject Object { get; }
        public int TexturesLoaded { get; private set; }

        SpaceMesh(SpaceModel model, Transform parent, string name, bool runtime, bool addCollider, Shader shader)
        {
            var geo = model.Geometry;
            var mats = new Material[geo.Parts.Count];
            for (int i = 0; i < mats.Length; i++)
            {
                mats[i] = new Material(shader) { name = geo.Parts[i].Material, hideFlags = HideFlags.DontSave };
                mats[i].SetTexture(IdMainTex, Texture2D.grayTexture);
                mats[i].SetFloat(IdCull, (float)CullMode.Off);
                _materials.Add(mats[i]);
                _materialIndex[geo.Parts[i].Material ?? ""] = i;
            }
            var flags = runtime ? HideFlags.DontSave : HideFlags.HideAndDontSave;
            var pieces = model.MeshPieces();
            Object = new GameObject("Matterport Mesh") { hideFlags = flags };
            Object.transform.SetParent(parent, false);
            for (int p = 0; p < pieces.Count; p++)
            {
                var go = p == 0 ? Object : new GameObject("Prop " + (p - 1)) { hideFlags = flags };
                if (p > 0) go.transform.SetParent(Object.transform, false);
                var r = pieces[p].Count > 0 ? Build(geo, pieces[p], mats, go, p == 0 ? name : name + " prop " + (p - 1), addCollider) : null;
                if (p > 0) _props.Add(r);
            }
            _tints = new Vector4[_props.Count];
        }

        /// <summary>One piece as a compact mesh (only the vertices it uses) on <paramref name="go"/>.</summary>
        Renderer Build(SpaceGeometry geo, List<(int part, int[] indices)> piece, Material[] mats, GameObject go, string meshName, bool addCollider)
        {
            var map = new Dictionary<int, int>();
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var submeshes = new List<int[]>();
            var materials = new Material[piece.Count];
            for (int s = 0; s < piece.Count; s++)
            {
                var (part, indices) = piece[s];
                var idx = new int[indices.Length];
                for (int i = 0; i < idx.Length; i++)
                {
                    int v = indices[i];
                    if (!map.TryGetValue(v, out int k))
                    {
                        map[v] = k = verts.Count;
                        verts.Add(geo.Positions[v].ToUnity());
                        if (geo.HasUvs) uvs.Add(new Vector2(geo.Uvs[v].X, geo.Uvs[v].Y));
                    }
                    idx[i] = k;
                }
                submeshes.Add(idx);
                materials[s] = mats[part];
            }
            var mesh = new Mesh { name = meshName, indexFormat = verts.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16, hideFlags = HideFlags.DontSave };
            mesh.SetVertices(verts);
            if (geo.HasUvs) mesh.SetUVs(0, uvs);
            mesh.subMeshCount = submeshes.Count;
            for (int s = 0; s < submeshes.Count; s++) mesh.SetTriangles(submeshes[s], s, false);
            mesh.RecalculateBounds();
            _meshes.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.sharedMaterials = materials;
            if (addCollider) go.AddComponent<MeshCollider>().sharedMesh = mesh;
            return mr;
        }

        /// <param name="runtime">Play mode (visible in the hierarchy) vs. edit-mode preview (hidden).</param>
        public static SpaceMesh Create(SpaceModel model, Transform parent, string name, bool runtime, bool addCollider)
        {
            var shader = Shader.Find("Matterportable/Mesh");
            if (shader == null) throw new InvalidOperationException("Shader Matterportable/Mesh not found (is the package installed correctly?).");
            return new SpaceMesh(model, parent, name, runtime, addCollider, shader);
        }

        /// <summary>Walk mode: each prop where it is now, tinted as the Core says (<see cref="PropWorld.Tint"/>).</summary>
        public void ApplyProps(PropWorld props, int highlight, int debugView)
        {
            if (props == null) return;
            for (int i = 0; i < _props.Count && i < props.Poses.Length; i++)
            {
                var r = _props[i];
                if (r == null) continue;
                var pose = props.Poses[i];
                r.transform.SetPositionAndRotation(Object.transform.TransformPoint(pose.Translation.ToUnity()), Object.transform.rotation * pose.Rotation.ToUnity());
                var t = props.Tint(i, highlight, debugView);
                var tint = new Vector4(t.R, t.G, t.B, t.A);
                if (tint == _tints[i]) continue;
                _tints[i] = tint;
                _block.SetVector(IdPropTint, tint); // a vector, not a colour: no gamma conversion
                r.SetPropertyBlock(_block);
            }
        }

        public void SetCullMode(CullMode mode)
        {
            foreach (var m in _materials) m.SetFloat(IdCull, (float)mode);
        }

        /// <summary>Edit mode: loads all textures synchronously (no coroutines outside Play mode).</summary>
        public void LoadTexturesNow(SpaceManifest manifest, string spaceDir)
        {
            foreach (var entry in manifest.Textures)
            {
                var path = CacheLayout.Resolve(spaceDir, entry.File);
                if (!_materialIndex.ContainsKey(entry.Material ?? "") || !File.Exists(path)) continue;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = entry.Material, hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Repeat };
                if (tex.LoadImage(File.ReadAllBytes(path), true)) Assign(entry.Material, tex);
                else MpUtil.SafeDestroy(tex);
            }
        }

        /// <summary>Play mode: all textures decode in parallel off the main thread, then copy to mipmapped GPU targets.</summary>
        public IEnumerator LoadTexturesRoutine(SpaceManifest manifest, string spaceDir)
        {
            var pending = new List<(string material, UnityWebRequest request)>();
            foreach (var entry in manifest.Textures)
            {
                var path = CacheLayout.Resolve(spaceDir, entry.File);
                if (_materialIndex.ContainsKey(entry.Material ?? "") && File.Exists(path)) pending.Add((entry.Material, GpuTextures.Load(path)));
            }
            foreach (var (material, request) in pending)
            {
                while (!request.isDone) yield return null;
                if (GpuTextures.Succeeded(request) && Object != null)
                {
                    var decoded = GpuTextures.Content(request);
                    var rt = GpuTextures.Create(decoded.width, decoded.height, 1, TextureWrapMode.Repeat, material);
                    Graphics.Blit(decoded, rt);
                    rt.GenerateMips();
                    MpUtil.SafeDestroy(decoded);
                    Assign(material, rt);
                }
                request.Dispose();
            }
        }

        void Assign(string material, Texture tex)
        {
            _textures.Add(tex);
            _materials[_materialIndex[material ?? ""]].SetTexture(IdMainTex, tex);
            TexturesLoaded++;
        }

        public void Dispose()
        {
            MpUtil.SafeDestroy(Object);
            foreach (var m in _meshes) MpUtil.SafeDestroy(m);
            _meshes.Clear();
            foreach (var m in _materials) MpUtil.SafeDestroy(m);
            foreach (var t in _textures)
            {
                if (t is RenderTexture rt) rt.Release();
                MpUtil.SafeDestroy(t);
            }
            _materials.Clear();
            _textures.Clear();
        }
    }
}
