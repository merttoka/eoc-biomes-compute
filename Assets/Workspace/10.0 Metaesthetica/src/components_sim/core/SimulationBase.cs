using System;
using UnityEngine;
using EasyButtons;

namespace Metaesthetica
{
    public abstract class SimulationBase : MonoBehaviour, IControllableSim
    {
        [Header("Setup")]
        public ComputeShader cs;
        public Material outputMat;

        [HideInInspector] public int rezX = 2048;
        [HideInInspector] public int rezY = 2048;

        public Texture externalInfluenceTex;

        protected RenderTexture readTex;
        protected RenderTexture writeTex;
        protected RenderTexture outTex;

        protected GPUResourceManager gpu;

        protected static readonly int s_RezXID = Shader.PropertyToID("rezX");
        protected static readonly int s_RezYID = Shader.PropertyToID("rezY");
        protected static readonly int s_TimeID = Shader.PropertyToID("time");
        protected static readonly int s_ReadTexID = Shader.PropertyToID("readTex");
        protected static readonly int s_WriteTexID = Shader.PropertyToID("writeTex");
        protected static readonly int s_OutTexID = Shader.PropertyToID("outTex");
        protected static readonly int s_ExternalInfluenceTexID = Shader.PropertyToID("externalInfluenceTex");

        public abstract string SimName { get; }
        public abstract void SetParameter(string paramName, int index, float value);
        public abstract void SetParameterDelta(string paramName, int index, float delta);
        public abstract float GetParameter(string paramName, int index);

        protected abstract void InitKernels();
        protected abstract void InitTextures();
        protected abstract void InitBuffers();
        protected abstract void GPUReset();
        protected abstract void GPUStep();
        protected abstract void Render();

        public RenderTexture GetOutputTexture() => outTex;

        public void SetResolution(int x, int y)
        {
            rezX = x;
            rezY = y;
        }

        [Button]
        public virtual void Reset()
        {
            Release();
            gpu = new GPUResourceManager();

            readTex = gpu.CreateTexture2D(rezX, rezY, FilterMode.Point, name: SimName + "_read");
            writeTex = gpu.CreateTexture2D(rezX, rezY, FilterMode.Point, name: SimName + "_write");
            outTex = gpu.CreateTexture2D(rezX, rezY, FilterMode.Trilinear, name: SimName + "_out");

            InitKernels();
            InitTextures();
            InitBuffers();
            GPUReset();
            Render();
        }

        public virtual void Step()
        {
            cs.SetInt(s_TimeID, Time.frameCount);
            GPUStep();
            SwapTex();
            Render();
        }

        protected void Dispatch(int kernel, int x, int y, int z)
        {
            cs.GetKernelThreadGroupSizes(kernel, out uint wx, out uint wy, out uint wz);
            cs.Dispatch(kernel,
                Mathf.CeilToInt((float)x / wx),
                Mathf.CeilToInt((float)y / wy),
                Mathf.CeilToInt((float)z / wz));
        }

        protected void SwapTex()
        {
            (readTex, writeTex) = (writeTex, readTex);
        }

        protected void ResetTexture(int kernel)
        {
            cs.SetInt(s_RezXID, rezX);
            cs.SetInt(s_RezYID, rezY);
            cs.SetTexture(kernel, s_WriteTexID, writeTex);
            Dispatch(kernel, rezX, rezY, 1);
            cs.SetTexture(kernel, s_WriteTexID, readTex);
            Dispatch(kernel, rezX, rezY, 1);
            cs.SetTexture(kernel, s_WriteTexID, outTex);
            Dispatch(kernel, rezX, rezY, 1);
        }

        public virtual void Release()
        {
            gpu?.ReleaseAll();
            gpu = null;
        }

        [Button("Export as PNG")]
        public void ExportPNG()
        {
            if (outTex == null) return;
            var tex = new Texture2D(rezX, rezY, TextureFormat.RGBA32, false);
            RenderTexture.active = outTex;
            tex.ReadPixels(new Rect(0, 0, rezX, rezY), 0, 0);
            tex.Apply();
            byte[] bytes = tex.EncodeToPNG();
            System.IO.File.WriteAllBytes($"Recordings/Sim{SimName}-{DateTime.Now.ToFileTime()}.png", bytes);
            Destroy(tex);
        }

        void OnEnable() => Reset();
        void OnDisable() => Release();
        void OnDestroy() => Release();

        protected float MapAndClamp(float value, float minValue, float maxValue, float min = 0, float max = 1)
        {
            float mapped = (value - min) / (max - min) * (maxValue - minValue) + minValue;
            return Mathf.Clamp(mapped, minValue, maxValue);
        }

        protected float ClampDelta(float field, float delta, float minValue, float maxValue)
        {
            float value = field + delta * (maxValue - minValue);
            return Mathf.Clamp(value, minValue, maxValue);
        }
    }
}
