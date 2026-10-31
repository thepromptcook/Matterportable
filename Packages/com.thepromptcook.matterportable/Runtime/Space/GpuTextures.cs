using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace Matterportable
{
    /// <summary>
    /// Image files → mipmapped GPU textures without main-thread stalls: UnityWebRequestTexture decodes on a worker
    /// thread, then a GPU blit copies into a render target and mips are generated on the GPU.
    /// Used for panorama faces (6-slice arrays) and mesh textures.
    /// </summary>
    internal static class GpuTextures
    {
        public static UnityWebRequest Load(string path)
        {
            var request = UnityWebRequestTexture.GetTexture(MpUtil.FileUri(path), true);
            request.SendWebRequest();
            return request;
        }

        public static bool Succeeded(UnityWebRequest request)
        {
#if UNITY_2020_2_OR_NEWER
            return request.result == UnityWebRequest.Result.Success;
#else
            return !request.isNetworkError && !request.isHttpError;
#endif
        }

        /// <summary>Mipmapped colour target (sRGB in linear projects); <paramref name="slices"/> &gt; 1 makes a 2D array.</summary>
        public static RenderTexture Create(int width, int height, int slices, TextureWrapMode wrap, string name)
        {
            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0)
            {
                dimension = slices > 1 ? TextureDimension.Tex2DArray : TextureDimension.Tex2D,
                volumeDepth = Mathf.Max(1, slices),
                useMipMap = true,
                autoGenerateMips = false,
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
                msaaSamples = 1,
            };
            var rt = new RenderTexture(desc)
            {
                name = name,
                wrapMode = wrap,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        /// <summary>Takes the decoded texture out of a finished request (the caller destroys it after copying).</summary>
        public static Texture2D Content(UnityWebRequest request) => DownloadHandlerTexture.GetContent(request);
    }
}
