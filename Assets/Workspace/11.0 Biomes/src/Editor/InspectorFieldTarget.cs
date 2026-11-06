using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Biomes
{
    /// <summary>
    /// A numeric inspector field an input device is moving. Writes go through its own SerializedObject, so they behave
    /// like a mouse edit, and each gesture is one Undo step. It holds no reference to the Editor it came from, which
    /// Unity destroys on a selection change.
    /// </summary>
    internal sealed class InspectorFieldTarget : IDisposable
    {
        private const float FreeRingPosition = 0.5f;

        private static readonly List<ImguiPropertyRects.Entry> Rects = new();
        private static readonly PropertyInfo EditorProperty =
            typeof(InspectorElement).GetProperty("editor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private readonly SerializedObject _so;
        private readonly EditorWindow _window;
        private readonly string _path;
        private readonly Dictionary<string, float> _raw = new();
        private bool _bounded;
        private float _min, _max;
        private float _floor = float.NegativeInfinity;
        private int _undoGroup;

        public bool InGesture { get; private set; }
        public bool IsAlive => _so.targetObject != null;

        private InspectorFieldTarget(SerializedObject so, EditorWindow window, string path)
        {
            _so = so;
            _window = window;
            _path = path;
        }

        public static bool HasInspector(EditorWindow win) =>
            win != null && win.rootVisualElement.Q<InspectorElement>() != null;

        /// <summary>
        /// The field at a panel-space point in a window being recorded, or null when there is none to move yet. Returns
        /// <paramref name="current"/> itself when it is already that field.
        /// </summary>
        public static InspectorFieldTarget At(EditorWindow win, Vector2 point, InspectorFieldTarget current = null)
        {
            if (!ImguiPropertyRects.TryRead(win, Rects)) return null;

            ImguiPropertyRects.Entry? hit = null;
            float hitArea = float.MaxValue;
            foreach (var r in Rects)
            {
                if (!r.Rect.Contains(point)) continue;
                float area = r.Rect.width * r.Rect.height;
                if (area < hitArea) { hitArea = area; hit = r; }
            }
            if (hit == null) return null;

            var editor = InspectedEditorAt(win, point);
            if (editor == null || editor.target == null) return null;
            if (editor.target.GetType().AssemblyQualifiedName != hit.Value.TargetTypeName) return null;
            if (current != null && current._path == hit.Value.Path && current._so.targetObject == editor.target) return current;

            return Create(editor, hit.Value.Path, win);
        }

        // Only an InspectorElement counts: a section's header and footer overlap rows that have scrolled out of view.
        private static Editor InspectedEditorAt(EditorWindow win, Vector2 point)
        {
            for (var e = win.rootVisualElement.panel?.Pick(point); e != null; e = e.parent)
                if (e is InspectorElement) return EditorProperty?.GetValue(e) as Editor;
            return null;
        }

        private static InspectorFieldTarget Create(Editor editor, string path, EditorWindow win)
        {
            var target = new InspectorFieldTarget(new SerializedObject(editor.targets), win, path);
            if (target.ConfigureRange(editor.target)) return target;
            target._so.Dispose();
            return null;
        }

        private bool ConfigureRange(UnityEngine.Object inspected)
        {
            var property = _so.FindProperty(_path);
            if (property == null || !property.editable) return false;

            bool isFloat = property.propertyType == SerializedPropertyType.Float;
            bool isInt = property.propertyType == SerializedPropertyType.Integer;
            if (!isFloat && !isInt) return false;

            HoverKnobMath.TryFindField(inspected.GetType(), _path, out FieldInfo field);
            if (field != null && field.IsDefined(typeof(KnobIgnoreAttribute), false)) return false;

            var range = field?.GetCustomAttribute<RangeAttribute>();
            if (range != null)
            {
                _bounded = true;
                _min = range.min;
                _max = range.max;
            }
            else
            {
                string paramName = field?.GetCustomAttribute<KnobParamAttribute>()?.Name ?? property.name;
                _bounded = TryParamRange(inspected, paramName, out _min, out _max);
                // A mouse edit honors [Min], and a value it guards can be a divisor.
                var floor = field?.GetCustomAttribute<MinAttribute>();
                if (floor != null) _floor = floor.min;
            }
            // A plain int could be a seed, count or channel index, so it needs a declared range.
            return _bounded || isFloat;
        }

        // Per-type params declare their travel in the set's "ranges" list. GetRange falls back to (0, 1) for an unlisted
        // name, so the name has to be checked against the list first.
        private bool TryParamRange(UnityEngine.Object inspected, string paramName, out float min, out float max)
        {
            min = max = 0f;
            if (inspected is not IParamSet set || HoverKnobMath.ArrayPathOf(_path) != "types") return false;

            var ranges = _so.FindProperty("ranges");
            if (ranges == null) return false;

            for (int i = 0; i < ranges.arraySize; i++)
            {
                if (ranges.GetArrayElementAtIndex(i).FindPropertyRelative("name")?.stringValue != paramName) continue;
                (min, max) = set.GetRange(paramName);
                return true;
            }
            return false;
        }

        /// <summary>The field's position in its range, 0-1, or the middle for a field without one.</summary>
        public float Normalized
        {
            get
            {
                if (!_bounded) return FreeRingPosition;
                _so.Update();
                var p = _so.FindProperty(_path);
                return p == null ? FreeRingPosition : Mathf.InverseLerp(_min, _max, ReadValue(p));
            }
        }

        /// <summary>Moves the field by a delta in knob units, on every element of its array when allElements is set.</summary>
        public void Nudge(float delta, bool allElements)
        {
            // A sim Reset destroys its live params, which can be the target mid-gesture.
            if (!IsAlive) return;

            if (!InGesture)
            {
                InGesture = true;
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Move Inspector Field");
                _undoGroup = Undo.GetCurrentGroup();
            }

            _so.Update();
            foreach (string path in PathsToMove(allElements))
            {
                var p = _so.FindProperty(path);
                if (p == null) continue;
                if (!_raw.TryGetValue(path, out float raw)) raw = ReadValue(p);
                raw = _bounded ? HoverKnobMath.Step(raw, delta, _min, _max) : Math.Max(HoverKnobMath.StepFree(raw, delta), _floor);
                _raw[path] = raw;
                if (p.propertyType == SerializedPropertyType.Float) p.floatValue = raw;
                else p.intValue = Mathf.RoundToInt(raw);
            }
            _so.ApplyModifiedProperties();

            if (_window != null) _window.Repaint();
        }

        public void EndGesture()
        {
            if (!InGesture) return;
            InGesture = false;
            _raw.Clear();
            Undo.CollapseUndoOperations(_undoGroup);
        }

        public void Dispose()
        {
            EndGesture();
            _so.Dispose();
        }

        private string[] PathsToMove(bool allElements)
        {
            string arrayPath = allElements ? HoverKnobMath.ArrayPathOf(_path) : null;
            if (arrayPath == null) return new[] { _path };
            var array = _so.FindProperty(arrayPath);
            return array == null ? new[] { _path } : HoverKnobMath.SiblingPaths(_path, array.arraySize);
        }

        private static float ReadValue(SerializedProperty p) =>
            p.propertyType == SerializedPropertyType.Float ? p.floatValue : p.intValue;
    }
}
