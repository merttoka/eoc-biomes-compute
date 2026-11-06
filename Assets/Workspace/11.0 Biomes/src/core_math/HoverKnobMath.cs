using System;
using System.Reflection;

namespace Biomes
{
    /// <summary>Excludes a field from the MFT hover knob, for fields whose side effects only run in a custom inspector.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class KnobIgnoreAttribute : Attribute { }

    /// <summary>
    /// Value stepping and SerializedProperty path handling for the MFT hover knob. A delta is in knob units:
    /// 1 is the knob's full travel, one MIDI step is 1/<see cref="StepsPerUnit"/>.
    /// </summary>
    public static class HoverKnobMath
    {
        public const float StepsPerUnit = 127f;

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
        public static float FirstTick(float delta)
        {
            float oneStep = 1f / StepsPerUnit;
            return Math.Max(-oneStep, Math.Min(delta, oneStep));
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

        public static string LeafName(string propertyPath) =>
            propertyPath.Substring(propertyPath.LastIndexOf('.') + 1);

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
