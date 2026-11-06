using System;
using System.Reflection;

namespace Biomes
{
    /// <summary>Excludes a field from hover knob and sideways scroll edits, for a field a plain write would leave inconsistent.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class KnobIgnoreAttribute : Attribute { }

    /// <summary>Names a field's entry in its param set's "ranges" list, where that differs from the field's own name.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class KnobParamAttribute : Attribute
    {
        public readonly string Name;
        public KnobParamAttribute(string name) => Name = name;
    }

    /// <summary>
    /// Value stepping and SerializedProperty path handling for moving a value by knob or scroll. A delta is in knob
    /// units: 1 is the knob's full travel, one MIDI step is 1/<see cref="StepsPerUnit"/>.
    /// </summary>
    public static class HoverKnobMath
    {
        public const float StepsPerUnit = 127f;
        public const float FineGain = 0.1f;

        private const float FreeFractionPerStep = 0.01f;
        private const float FreeMagnitudeFloor = 0.1f;
        private const string ArrayToken = ".Array.data[";

        /// <summary>
        /// Moves a ranged value. Clamps only in the direction of travel: a value authored outside the range
        /// (eatAmount 0.94 against a max of 0.1) holds or moves inward, and never snaps to the bound.
        /// </summary>
        public static float Step(float raw, float delta, float min, float max)
        {
            float next = raw + delta * (max - min);
            if (delta > 0f) return raw > max ? raw : Math.Min(next, max);
            if (delta < 0f) return raw < min ? raw : Math.Max(next, min);
            return raw;
        }

        /// <summary>Moves an unranged value by a share of its own size, with a floor so it can leave zero.</summary>
        public static float StepFree(float raw, float delta) =>
            raw + delta * StepsPerUnit * FreeFractionPerStep * Math.Max(Math.Abs(raw), FreeMagnitudeFloor);

        /// <summary>
        /// The delta to use for a gesture's first tick, limited to one MIDI step. The knob's previous position may
        /// belong to a different target, so the first raw delta can be any size.
        /// </summary>
        public static float FirstTick(float delta) => LimitSteps(delta, 1f);

        /// <summary>Limits a delta to a number of MIDI steps, in either direction.</summary>
        public static float LimitSteps(float delta, float steps)
        {
            float limit = steps / StepsPerUnit;
            return Math.Max(-limit, Math.Min(delta, limit));
        }

        /// <summary>
        /// The knob position for a 0-1 value. Only the range's own ends sit on an end stop: a knob there reports nothing
        /// further, so a value that merely rounded to one could never be turned the rest of the way.
        /// </summary>
        public static int RingPosition(float norm)
        {
            int ring = (int)Math.Round(norm * StepsPerUnit);
            if (norm <= 0f || norm >= 1f) return ring;
            return Math.Max(1, Math.Min(ring, (int)StepsPerUnit - 1));
        }

        /// <summary>
        /// Whether a turning knob's position has to be sent back. A slowed or unranged value moves less than the knob, which
        /// would reach an end stop first. One step apart mid-range is only rounding: sending that back would pull a
        /// full-speed turn backwards. On an end stop the knob reports nothing further, so even one step matters there.
        /// </summary>
        public static bool NeedsResync(int position, float norm)
        {
            int ring = RingPosition(norm);
            if (ring == position) return false;
            bool onEndStop = position <= 0 || position >= (int)StepsPerUnit;
            return onEndStop || Math.Abs(ring - position) > 1;
        }

        /// <summary>The innermost array holding this property, or null when it isn't in an array.</summary>
        public static string ArrayPathOf(string propertyPath)
        {
            int i = propertyPath.LastIndexOf(ArrayToken, StringComparison.Ordinal);
            return i < 0 ? null : propertyPath.Substring(0, i);
        }

        /// <summary>The same field on every element of the innermost array, or just this path when it isn't in one.</summary>
        public static string[] SiblingPaths(string propertyPath, int arraySize)
        {
            int i = propertyPath.LastIndexOf(ArrayToken, StringComparison.Ordinal);
            if (i < 0) return new[] { propertyPath };

            int open = i + ArrayToken.Length;
            string head = propertyPath.Substring(0, open);
            string tail = propertyPath.Substring(propertyPath.IndexOf(']', open));
            var paths = new string[arraySize];
            for (int n = 0; n < arraySize; n++) paths[n] = head + n + tail;
            return paths;
        }

        /// <summary>Finds the field a SerializedProperty path ends on. An array element resolves to the array's field.</summary>
        public static bool TryFindField(Type rootType, string propertyPath, out FieldInfo field)
        {
            field = null;
            Type type = rootType;
            string[] segments = propertyPath.Split('.');
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == "Array" && i + 1 < segments.Length && segments[i + 1].StartsWith("data[", StringComparison.Ordinal))
                {
                    type = ElementTypeOf(type);
                    if (type == null) return false;
                    i++;
                    continue;
                }
                field = FindField(type, segments[i]);
                if (field == null) return false;
                type = field.FieldType;
            }
            return field != null;
        }

        private static Type ElementTypeOf(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            return type.IsGenericType ? type.GetGenericArguments()[0] : null;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, flags);
                if (f != null) return f;
            }
            return null;
        }
    }
}
