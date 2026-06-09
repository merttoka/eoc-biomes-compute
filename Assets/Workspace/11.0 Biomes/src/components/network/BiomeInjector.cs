using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Routes external installation drivers (plants, robotics, neuron firing, …) into
    /// biome channels as soft spatial "stamps." Each <see cref="Source"/> maps a physical
    /// location to a biome UV + channel; its live value (constant in the inspector, or
    /// pushed via <see cref="SetValue"/> from an OSC/sensor callback) becomes a Gaussian
    /// deposit. SimulationManager calls <see cref="Inject"/> once per step — after sim
    /// write-back and before Biome.Step() — so deposits ride the field ping-pong with no
    /// clobber risk (see Biome.InjectSources).
    ///
    /// Examples: a plant → Oxygen source at its mapped floor location; a robot arm →
    /// Temperature source driven by a proximity sensor; neuron firing → Pheromone/alarm.
    /// </summary>
    public class BiomeInjector : MonoBehaviour
    {
        public enum BlendMode { Additive = 0, MaxToward = 1, SetToward = 2 }

        [Serializable]
        public class Source
        {
            public string name = "source";
            public bool enabled = true;

            [Tooltip("Biome-space center, 0..1. Manually map the physical location here.")]
            public Vector2 fieldUV = new Vector2(0.5f, 0.5f);
            [Range(0.001f, 0.5f)] public float radius = 0.06f;
            [Range(0.25f, 6f)]    public float falloff = 1.5f;

            [Tooltip("Target biome channel index. 0=Nutrient 1-3=Pheromone 4=Oxygen 5=Temperature 6=Waste 7=Permeability.")]
            [Range(0, BiomeChannel.Count - 1)] public int channel = BiomeChannel.Oxygen;

            [Tooltip("Scales the live value into a deposit amount per step.")]
            public float gain = 0.01f;

            [Tooltip("Persistent sources: prefer MaxToward (builds a stable gradient) over Additive (saturates to a flat blob).")]
            public BlendMode mode = BlendMode.MaxToward;

            [Tooltip("Live value (0..1 typical). Edit live, or push via SetValue(name, v) from OSC/sensor.")]
            [Range(0f, 1f)] public float value = 1f;

            [Tooltip("Seconds before an un-refreshed value decays to 0 (sensor-dropout guard). 0 = never.")]
            public float valueTimeout = 0f;

            [NonSerialized] public float lastSetTime = -1f;
        }

        public List<Source> sources = new();

        [Header("Gizmo")]
        public bool drawGizmos = true;
        public Color gizmoColor = new Color(0.3f, 0.9f, 1f, 0.6f);

        // Must match HLSL `struct InjectStamp` in Biome.compute (32 bytes, tightly packed).
        [StructLayout(LayoutKind.Sequential)]
        private struct Stamp
        {
            public Vector2 uv;
            public float radius;
            public float falloff;
            public int channel;
            public float amount;
            public int mode;
            public float pad;
        }
        private const int StampStride = sizeof(float) * 8; // 32

        private ComputeBuffer _buffer;
        private Stamp[] _scratch;

        /// <summary>Push a live value to a named source (e.g. from an OSC or sensor callback).
        /// Cheap; safe to call every frame.</summary>
        public void SetValue(string sourceName, float v)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i] != null && sources[i].name == sourceName)
                {
                    sources[i].value = v;
                    sources[i].lastSetTime = Time.time;
                }
            }
        }

        /// <summary>Pack the active sources and dispatch injection into the biome. Call once
        /// per step, AFTER sim write-back and BEFORE biome.Step().</summary>
        public void Inject(Biome biome)
        {
            if (biome == null || sources == null) return;

            int n = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s != null && s.enabled && s.radius > 0f) n++;
            }
            if (n == 0) return;

            if (_scratch == null || _scratch.Length < n) _scratch = new Stamp[n];

            float now = Time.time;
            int k = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s == null || !s.enabled || s.radius <= 0f) continue;

                float val = s.value;
                if (s.valueTimeout > 0f && s.lastSetTime >= 0f && now - s.lastSetTime > s.valueTimeout)
                    val = 0f; // sensor-dropout guard: stale value decays to nothing

                _scratch[k++] = new Stamp
                {
                    uv = s.fieldUV,
                    radius = s.radius,
                    falloff = s.falloff,
                    channel = Mathf.Clamp(s.channel, 0, BiomeChannel.Count - 1),
                    amount = s.gain * val,
                    mode = (int)s.mode,
                    pad = 0f,
                };
            }

            if (_buffer == null || _buffer.count < k)
            {
                _buffer?.Release();
                _buffer = new ComputeBuffer(Mathf.Max(k, 4), StampStride);
            }
            _buffer.SetData(_scratch, 0, 0, k);
            biome.InjectSources(_buffer, k);
        }

        void OnDestroy()
        {
            _buffer?.Release();
            _buffer = null;
        }

        void OnDrawGizmosSelected()
        {
            if (!drawGizmos || sources == null) return;
            // Visualize each source on this object's local XY plane (1 unit = full biome).
            // fieldUV (0..1) is the authoritative map; this is just a placement aid.
            foreach (var s in sources)
            {
                if (s == null) continue;
                Gizmos.color = s.enabled ? gizmoColor : new Color(0.5f, 0.5f, 0.5f, 0.4f);
                Vector3 c = transform.TransformPoint(new Vector3(s.fieldUV.x - 0.5f, s.fieldUV.y - 0.5f, 0f));
                Gizmos.DrawWireSphere(c, s.radius * 0.5f);
#if UNITY_EDITOR
                UnityEditor.Handles.color = Gizmos.color;
                UnityEditor.Handles.Label(c, $"{s.name} → ch{s.channel}");
#endif
            }
        }
    }
}
