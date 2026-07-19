using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Biomes
{
    /// <summary>Thumbnails for param snapshot assets: a PNG named "&lt;asset&gt;_thumb.png"
    /// sitting next to the asset. Captured from the live composer output on demand.</summary>
    public static class SnapshotThumbnailCache
    {
        private static readonly Dictionary<string, Texture2D> s_Cache = new();

        /// <summary>Path the thumbnail for <paramref name="asset"/> would live at, or null if the asset isn't on disk.</summary>
        public static string ThumbPath(Object asset)
        {
            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath)) return null;
            return Path.Combine(Path.GetDirectoryName(assetPath),
                Path.GetFileNameWithoutExtension(assetPath) + "_thumb.png");
        }

        /// <summary>Loads (and caches) the thumbnail texture for <paramref name="asset"/>, or null if none has been captured yet.</summary>
        public static Texture2D Get(Object asset)
        {
            string path = ThumbPath(asset);
            if (path == null) return null;
            if (s_Cache.TryGetValue(path, out var tex) && tex != null) return tex;
            tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            s_Cache[path] = tex;
            return tex;
        }

        /// <summary>Downscale + save the current composer output as the asset's thumb.</summary>
        public static void Capture(Object asset, RenderTexture composer, int thumbHeight = 128)
        {
            string path = ThumbPath(asset);
            if (path == null || composer == null) return;

            int w = Mathf.Max(32, thumbHeight * composer.width / composer.height);
            var scaled = RenderTexture.GetTemporary(w, thumbHeight, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(composer, scaled);

            var prev = RenderTexture.active;
            RenderTexture.active = scaled;
            var tex = new Texture2D(w, thumbHeight, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, thumbHeight), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(scaled);

            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            s_Cache.Remove(path);
        }
    }
}
