using System.Collections.Generic;
using UnityEngine;

namespace Metaesthetica
{
    public class GPUResourceManager
    {
        private readonly List<ComputeBuffer> _buffers = new List<ComputeBuffer>();
        private readonly List<RenderTexture> _textures = new List<RenderTexture>();

        public ComputeBuffer CreateBuffer(int count, int stride)
        {
            var buffer = new ComputeBuffer(count, stride);
            _buffers.Add(buffer);
            return buffer;
        }

        public RenderTexture CreateTexture2D(int width, int height, FilterMode filterMode,
            RenderTextureFormat format = RenderTextureFormat.ARGBFloat, string name = "gpu_tex")
        {
            var texture = new RenderTexture(width, height, 1, format);
            texture.name = name;
            texture.enableRandomWrite = true;
            texture.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
            texture.volumeDepth = 1;
            texture.filterMode = filterMode;
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.autoGenerateMips = false;
            texture.useMipMap = false;
            texture.Create();
            _textures.Add(texture);
            return texture;
        }

        public void Track(ComputeBuffer buffer)
        {
            if (buffer != null && !_buffers.Contains(buffer))
                _buffers.Add(buffer);
        }

        public void Track(RenderTexture texture)
        {
            if (texture != null && !_textures.Contains(texture))
                _textures.Add(texture);
        }

        public void ReleaseAll()
        {
            foreach (var buffer in _buffers)
                if (buffer != null) buffer.Release();
            _buffers.Clear();

            foreach (var tex in _textures)
                if (tex != null) tex.Release();
            _textures.Clear();
        }
    }
}
