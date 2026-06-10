using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using EasyButtons;

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

            [Tooltip("Optional OSC address override. Blank = listen on /inject/<name>. Set this " +
                     "to decouple the wire protocol from the display name (rename freely without " +
                     "breaking the sensor mapping).")]
            public string oscAddress = "";

            [Tooltip("NORMALIZED biome coordinate, 0..1 (NOT world space). (0,0)=one corner, " +
                     "(0.5,0.5)=center, (1,1)=opposite corner. This is the manual physical→biome map.")]
            public Vector2 fieldUV = new Vector2(0.5f, 0.5f);
            [Range(0.001f, 0.5f)] public float radius = 0.06f;
            [Range(0.25f, 6f)]    public float falloff = 1.5f;

            [Tooltip("Target biome channel.")]
            [BiomeChannelField] public int channel = BiomeChannel.Oxygen;

            [Tooltip("Multiplies the (calibrated, smoothed) value. Additive mode: small per-step " +
                     "increment (~0.005-0.05). Max/Set modes: target-level scale (~1).")]
            public float gain = 1f;

            [Tooltip("Persistent sources: prefer MaxToward (builds a stable gradient) over Additive (saturates to a flat blob).")]
            public BlendMode mode = BlendMode.MaxToward;

            [Header("Raw input → 0..1 calibration")]
            [Tooltip("Raw sensor range that maps to 0..1. Most sensors don't send 0..1 — set the " +
                     "min/max you actually see (e.g. a CO₂ ppm or distance range) and the injector " +
                     "remaps + clamps for you. Leave 0..1 for an already-normalized feed.")]
            public float inputMin = 0f;
            public float inputMax = 1f;
            [Tooltip("Temporal smoothing (EMA) of the calibrated value: 0 = none (snappy), " +
                     "0.9 = heavy (slow, denoised). Tames jittery sensors without TD-side work.")]
            [Range(0f, 0.99f)] public float smoothing = 0f;

            [Tooltip("Live RAW value (pre-calibration). Edit here to test, or push via SetValue(name, v) from OSC/sensor.")]
            public float value = 1f;

            [Tooltip("Seconds before an un-refreshed value decays to 0 (sensor-dropout guard). 0 = never.")]
            public float valueTimeout = 0f;

            [NonSerialized] public float lastSetTime = -1f;
            [NonSerialized] public volatile bool valueDirty; // set off-thread by SetValue, consumed in Inject
            // Monitoring (main-thread, for the editor readout): post-calibration smoothed value
            // actually stamped, and wall-clock of the last received message.
            [NonSerialized] public float monCalibrated;
            [NonSerialized] public float monLastMsgTime = -1f;
            [NonSerialized] public bool  monStale;
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

        /// <summary>Push a live value (0..1) to a named source — e.g. from an OSC/sensor
        /// callback. Thread-safe: only writes plain fields (no Unity API), so it is safe to
        /// call from an OSC receive thread. The valueTimeout clock is stamped on the main
        /// thread in Inject.</summary>
        public void SetValue(string sourceName, float v)
        {
            var list = sources;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s != null && s.name == sourceName) { s.value = v; s.valueDirty = true; }
            }
        }

        /// <summary>Remap raw input to 0..1 by [inputMin,inputMax], clamped. Identity for the
        /// default 0..1 range. Degenerate range (min≈max) → 0.</summary>
        private static float Calibrate(float raw, float lo, float hi)
        {
            float span = hi - lo;
            if (Mathf.Abs(span) < 1e-6f) return 0f;
            return Mathf.Clamp01((raw - lo) / span);
        }

        /// <summary>Move a named source to a new normalized biome UV (0..1) — e.g. a robot
        /// pose driving the stamp location. Thread-safe; clamped to [0,1].</summary>
        public void SetPosition(string sourceName, float u, float v)
        {
            var list = sources;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s != null && s.name == sourceName)
                    s.fieldUV = new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
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

                if (s.valueDirty) { s.lastSetTime = now; s.monLastMsgTime = now; s.valueDirty = false; } // stamp set-time on main thread

                // Calibrate raw → 0..1, guard sensor dropout, then EMA-smooth.
                float cal = Calibrate(s.value, s.inputMin, s.inputMax);
                s.monStale = (s.valueTimeout > 0f && s.lastSetTime >= 0f && now - s.lastSetTime > s.valueTimeout);
                if (s.monStale) cal = 0f; // stale value decays to nothing
                s.monCalibrated = Mathf.Lerp(cal, s.monCalibrated, Mathf.Clamp(s.smoothing, 0f, 0.99f));

                _scratch[k++] = new Stamp
                {
                    uv = new Vector2(Mathf.Clamp01(s.fieldUV.x), Mathf.Clamp01(s.fieldUV.y)),
                    radius = s.radius,
                    falloff = s.falloff,
                    channel = Mathf.Clamp(s.channel, 0, BiomeChannel.Count - 1),
                    amount = s.gain * s.monCalibrated,
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

        /// <summary>The OSC address a source listens on: its explicit override, or
        /// /inject/&lt;name&gt; by default. Used by OSCMapping to register callbacks.</summary>
        public static string OscAddressFor(Source s)
        {
            if (s == null) return null;
            if (!string.IsNullOrEmpty(s.oscAddress)) return s.oscAddress;
            if (string.IsNullOrEmpty(s.name)) return null;
            return $"/inject/{s.name}";
        }

        /// <summary>Bring-up aid: dump each source's wiring + live state to the Console so you
        /// can see at a glance which sensors are actually arriving. Run in Play mode.</summary>
        [Button("Log Live Source Values")]
        public void LogLiveValues()
        {
            if (sources == null || sources.Count == 0) { Debug.Log("[BiomeInjector] no sources"); return; }
            float now = Application.isPlaying ? Time.time : -1f;
            var sb = new StringBuilder("[BiomeInjector] live sources:\n");
            foreach (var s in sources)
            {
                if (s == null) continue;
                string age = (now >= 0f && s.monLastMsgTime >= 0f)
                    ? $"{now - s.monLastMsgTime:0.0}s ago" : "never";
                sb.Append($"  • {s.name} → {BiomeChannel.Names[Mathf.Clamp(s.channel, 0, BiomeChannel.Count - 1)]}")
                  .Append($" @uv({s.fieldUV.x:0.00},{s.fieldUV.y:0.00})")
                  .Append($"  raw={s.value:0.000} → cal={s.monCalibrated:0.000}")
                  .Append($"  osc={OscAddressFor(s)}  lastMsg={age}{(s.monStale ? " [STALE]" : "")}\n");
            }
            Debug.Log(sb.ToString());
        }

        void OnDestroy()
        {
            _buffer?.Release();
            _buffer = null;
        }

        // Keep authored values in range so a stamp can never land off the field.
        void OnValidate()
        {
            if (sources == null) return;
            foreach (var s in sources)
            {
                if (s == null) continue;
                s.fieldUV = new Vector2(Mathf.Clamp01(s.fieldUV.x), Mathf.Clamp01(s.fieldUV.y));
                s.channel = Mathf.Clamp(s.channel, 0, BiomeChannel.Count - 1);
            }
        }

        void OnDrawGizmosSelected()
        {
            if (!drawGizmos || sources == null) return;
            // The biome is a normalized [0,1] field. Draw it as a unit square on this
            // object's local XY plane — purely a placement aid; fieldUV is the real map.
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 1f, 1f, 0.25f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(1f, 1f, 0f)); // [0,1]² centered at origin
            foreach (var s in sources)
            {
                if (s == null) continue;
                Gizmos.color = s.enabled ? gizmoColor : new Color(0.5f, 0.5f, 0.5f, 0.4f);
                Gizmos.DrawWireSphere(new Vector3(s.fieldUV.x - 0.5f, s.fieldUV.y - 0.5f, 0f), s.radius);
            }
            Gizmos.matrix = Matrix4x4.identity;
#if UNITY_EDITOR
            foreach (var s in sources)
            {
                if (s == null) continue;
                Vector3 wc = transform.TransformPoint(new Vector3(s.fieldUV.x - 0.5f, s.fieldUV.y - 0.5f, 0f));
                UnityEditor.Handles.Label(wc, $"{s.name} → ch{s.channel}");
            }
#endif
        }
    }
}
