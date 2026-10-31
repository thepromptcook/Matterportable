using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Matterportable.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Matterportable
{
    /// <summary>The scanned mesh as a Unity object: one mesh with a submesh + material per Matterport texture.</summary>
    internal sealed class SpaceMesh : IDisposable
    {
        static readonly int IdCull = Shader.PropertyToID("_Cull");
        static readonly int IdMainTex = Shader.PropertyToID("_MainTex");

        readonly Mesh _mesh;
        readonly List<Material> _materials = new List<Material>();
        readonly List<Texture2D> _textures = new List<Texture2D>();
        readonly Dictionary<string, int> _materialIndex = new Dictionary<string, int>();

        public GameObject Object { get; }
        public int TexturesLoaded { get; private set; }

        SpaceMesh(SpaceGeometry geo, Transform parent, string name, bool runtime, bool addCollider, Shader shader)
        {
            _mesh = new Mesh
            {
                name = name,
                indexFormat = geo.Positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                hideFlags = HideFlags.DontSave,
            };
            var verts = new Vector3[geo.Positions.Length];
            for (int i = 0; i < verts.Length; i++) verts[i] = geo.Positions[i].ToUnity();
            _mesh.vertices = verts;
            if (geo.HasUvs)
            {
                var uvs = new Vector2[geo.Uvs.Length];
                for (int i = 0; i < uvs.Length; i++) uvs[i] = new Vector2(geo.Uvs[i].X, geo.Uvs[i].Y);
                _mesh.uv = uvs;
            }
            _mesh.subMeshCount = geo.Parts.Count;
            for (int i = 0; i < geo.Parts.Count; i++) _mesh.SetTriangles(geo.Parts[i].Indices, i, false);
            _mesh.RecalculateBounds();

            Object = new GameObject("Matterport Mesh") { hideFlags = runtime ? HideFlags.DontSave : HideFlags.HideAndDontSave };
            Object.transform.SetParent(parent, false);
            Object.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = Object.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var mats = new Material[geo.Parts.Count];
            for (int i = 0; i < mats.Length; i++)
            {
                mats[i] = new Material(shader) { name = geo.Parts[i].Material, hideFlags = HideFlags.DontSave };
                mats[i].SetTexture(IdMainTex, Texture2D.grayTexture);
                mats[i].SetFloat(IdCull, (float)CullMode.Off);
                _materials.Add(mats[i]);
                _materialIndex[geo.Parts[i].Material ?? ""] = i;
            }
            mr.sharedMaterials = mats;
            if (addCollider) Object.AddComponent<MeshCollider>().sharedMesh = _mesh;
        }

        /// <param name="runtime">Play mode (visible in the hierarchy) vs. edit-mode preview (hidden).</param>
        public static SpaceMesh Create(SpaceGeometry geo, Transform parent, string name, bool runtime, bool addCollider)
        {
            var shader = Shader.Find("Matterportable/Mesh");
            if (shader == null) throw new InvalidOperationException("Shader Matterportable/Mesh not found (is the package installed correctly?).");
            return new SpaceMesh(geo, parent, name, runtime, addCollider, shader);
        }

        public void SetCullMode(CullMode mode)
        {
            foreach (var m in _materials) m.SetFloat(IdCull, (float)mode);
        }

        /// <summary>Edit mode: loads all textures synchronously (uncompressed, quick to rebuild).</summary>
        public void LoadTexturesNow(SpaceManifest manifest, string spaceDir)
        {
            foreach (var entry in manifest.Textures)
            {
                var path = CacheLayout.Resolve(spaceDir, entry.File);
                if (!_materialIndex.ContainsKey(entry.Material ?? "") || !File.Exists(path)) continue;
                Assign(entry.Material, DecodeTexture(File.ReadAllBytes(path), false, entry.Material));
            }
        }

        /// <summary>Play mode: reads files on a worker thread and decodes one texture per frame.</summary>
        public IEnumerator LoadTexturesRoutine(SpaceManifest manifest, string spaceDir, bool compress)
        {
            foreach (var entry in manifest.Textures)
            {
                if (!_materialIndex.ContainsKey(entry.Material ?? "")) continue;
                var path = CacheLayout.Resolve(spaceDir, entry.File);
                var read = Task.Run(() => File.Exists(path) ? File.ReadAllBytes(path) : null);
                while (!read.IsCompleted) yield return null;
                if (read.IsFaulted || read.Result == null) continue;
                Assign(entry.Material, DecodeTexture(read.Result, compress, entry.Material));
                yield return null;
            }
        }

        void Assign(string material, Texture2D tex)
        {
            if (tex == null || Object == null) return;
            _textures.Add(tex);
            _materials[_materialIndex[material ?? ""]].SetTexture(IdMainTex, tex);
            TexturesLoaded++;
        }

        static Texture2D DecodeTexture(byte[] bytes, bool compress, string name)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = name, hideFlags = HideFlags.DontSave };
            if (!tex.LoadImage(bytes, false))
            {
                MpUtil.SafeDestroy(tex);
                return null;
            }
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            if (compress && SystemInfo.SupportsTextureFormat(TextureFormat.DXT1)) tex.Compress(false);
            tex.Apply(false, true);
            return tex;
        }

        public void Dispose()
        {
            MpUtil.SafeDestroy(Object);
            MpUtil.SafeDestroy(_mesh);
            foreach (var m in _materials) MpUtil.SafeDestroy(m);
            foreach (var t in _textures) MpUtil.SafeDestroy(t);
            _materials.Clear();
            _textures.Clear();
        }
    }
}
