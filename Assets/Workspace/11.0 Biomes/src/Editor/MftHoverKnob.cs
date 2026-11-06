using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Biomes
{
    /// <summary>
    /// Lets the MFT hover knob move whichever numeric inspector field is under the mouse. Runs only while the MFT is on
    /// the hover page. A write goes through a SerializedObject, so it behaves like a mouse edit, Undo included.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class MftHoverKnob : MidiFighterTwister.IHoverDriver
    {
        private const double RefreshSeconds = 0.1;
        private const double GestureIdleSeconds = 0.3;
        private const float FreeRingPosition = 0.5f;

        private static readonly MftHoverKnob Instance = new();

        static MftHoverKnob()
        {
            MidiFighterTwister.HoverDriver = Instance;
            EditorApplication.update += Instance.Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Instance.Deactivate;
            EditorApplication.quitting += Instance.Deactivate;
        }

        private sealed class Target
        {
            public SerializedObject So;
            public EditorWindow Window;
            public string Path;
            public bool Bounded;
            public float Min, Max;
        }

        private readonly List<ImguiPropertyRects.Entry> _rects = new();
        private readonly Dictionary<string, float> _raw = new();
        private readonly Dictionary<Type, PropertyInfo> _editorProperties = new();

        private bool _active;
        private EditorWindow _window;
        private VisualElement _root;
        private bool _windowWantedMouseMove;
        private Vector2? _pointer;
        private bool _pointerMoved;
        private double _nextRefresh;
        private Target _target;
        private bool _gesture;
        private double _lastNudge;
        private int _undoGroup;

        public bool HasTarget => _target != null && _target.So.targetObject != null;
        public bool InGesture => _gesture;

        public float Normalized
        {
            get
            {
                if (!HasTarget || !_target.Bounded) return FreeRingPosition;
                _target.So.Update();
                var p = _target.So.FindProperty(_target.Path);
                return p == null ? FreeRingPosition : Mathf.InverseLerp(_target.Min, _target.Max, ReadValue(p));
            }
        }

        public void Nudge(float delta, bool allElements)
        {
            if (!HasTarget) return;

            if (!_gesture)
            {
                _gesture = true;
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("MFT Hover Knob");
                _undoGroup = Undo.GetCurrentGroup();
            }
            _lastNudge = EditorApplication.timeSinceStartup;

            var so = _target.So;
            so.Update();
            foreach (string path in PathsToMove(allElements))
            {
                var p = so.FindProperty(path);
                if (p == null) continue;
                if (!_raw.TryGetValue(path, out float raw)) raw = ReadValue(p);
                raw = _target.Bounded ? HoverKnobMath.Step(raw, delta, _target.Min, _target.Max) : HoverKnobMath.StepFree(raw, delta);
                _raw[path] = raw;
                if (p.propertyType == SerializedPropertyType.Float) p.floatValue = raw;
                else p.intValue = Mathf.RoundToInt(raw);
            }
            so.ApplyModifiedProperties();

            if (_target.Window != null) _target.Window.Repaint();
        }

        private string[] PathsToMove(bool allElements)
        {
            string arrayPath = allElements ? HoverKnobMath.ArrayPathOf(_target.Path) : null;
            if (arrayPath == null) return new[] { _target.Path };
            var array = _target.So.FindProperty(arrayPath);
            return array == null ? new[] { _target.Path } : HoverKnobMath.SiblingPaths(_target.Path, array.arraySize);
        }

        private static float ReadValue(SerializedProperty p) =>
            p.propertyType == SerializedPropertyType.Float ? p.floatValue : p.intValue;

        private void Tick()
        {
            var mft = MidiFighterTwister.HoverPageOwner;
            if (mft == null || !mft.hoverKnobEnabled || !ImguiPropertyRects.Available)
            {
                Deactivate();
                return;
            }
            _active = true;

            double now = EditorApplication.timeSinceStartup;
            if (_gesture && now - _lastNudge > GestureIdleSeconds) EndGesture();
            if (now < _nextRefresh && !_pointerMoved) return;
            _nextRefresh = now + RefreshSeconds;
            _pointerMoved = false;

            if (_gesture) return;
            Retarget();
            mft.ServiceLeds();
            mft.RefreshHoverKnob();
        }

        private void EndGesture()
        {
            _gesture = false;
            _raw.Clear();
            Undo.CollapseUndoOperations(_undoGroup);
        }

        private void Deactivate()
        {
            if (!_active) return;
            _active = false;
            if (_gesture) EndGesture();
            SetTarget(null);
            Unwatch();
            ImguiPropertyRects.Stop();
        }

        private void Retarget()
        {
            var win = EditorWindow.mouseOverWindow;
            // A window can gain its inspector later, when the selection changes under a resting mouse.
            if (win != _window || (_root == null && HasInspector(win))) Watch(win);

            var next = _root != null && _pointer.HasValue ? Resolve(win, _pointer.Value) : null;
            if (next?.Path == _target?.Path && next?.So.targetObject == _target?.So.targetObject)
            {
                next?.So.Dispose();
                return;
            }
            SetTarget(next);
        }

        private void SetTarget(Target next)
        {
            _target?.So.Dispose();
            _target = next;
            _raw.Clear();
        }

        private static bool HasInspector(EditorWindow win) =>
            win != null && win.rootVisualElement.Q<InspectorElement>() != null;

        // Inspector windows only: the debugger records every property a watched window draws, on each repaint.
        private void Watch(EditorWindow win)
        {
            Unwatch();
            _window = win;
            if (!HasInspector(win)) return;

            _root = win.rootVisualElement;
            _windowWantedMouseMove = win.wantsMouseMove;
            win.wantsMouseMove = true;
            _root.RegisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
            ImguiPropertyRects.Watch(win);
            win.Repaint();
        }

        private void Unwatch()
        {
            if (_root != null)
            {
                _root.UnregisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
                if (_window != null) _window.wantsMouseMove = _windowWantedMouseMove;
            }
            _root = null;
            _window = null;
            _pointer = null;
        }

        // Panel space, the same space the IMGUI property rects are recorded in.
        private void OnPointerMove(PointerMoveEvent e)
        {
            _pointer = e.position;
            _pointerMoved = true;
        }

        private Target Resolve(EditorWindow win, Vector2 point)
        {
            ImguiPropertyRects.Read(_rects);

            ImguiPropertyRects.Entry? hit = null;
            float hitArea = float.MaxValue;
            foreach (var r in _rects)
            {
                if (!r.Rect.Contains(point)) continue;
                float area = r.Rect.width * r.Rect.height;
                if (area < hitArea) { hitArea = area; hit = r; }
            }
            if (hit == null) return null;

            var editor = InspectedEditorAt(win, point);
            if (editor == null || editor.target == null) return null;
            if (editor.target.GetType().AssemblyQualifiedName != hit.Value.TargetTypeName) return null;

            return CreateTarget(editor, hit.Value.Path, win);
        }

        // Only an InspectorElement counts: a section's header and footer overlap rows that have scrolled out of view.
        private Editor InspectedEditorAt(EditorWindow win, Vector2 point)
        {
            for (var e = win.rootVisualElement.panel?.Pick(point); e != null; e = e.parent)
            {
                if (e is not InspectorElement) continue;
                var type = e.GetType();
                if (!_editorProperties.TryGetValue(type, out var property))
                {
                    property = type.GetProperty("editor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _editorProperties[type] = property;
                }
                return property?.GetValue(e) as Editor;
            }
            return null;
        }

        private static Target CreateTarget(Editor editor, string path, EditorWindow win)
        {
            var so = new SerializedObject(editor.targets);
            var target = new Target { So = so, Window = win, Path = path };
            if (ConfigureRange(target, editor.target)) return target;
            so.Dispose();
            return null;
        }

        private static bool ConfigureRange(Target target, UnityEngine.Object inspected)
        {
            var property = target.So.FindProperty(target.Path);
            if (property == null || !property.editable) return false;

            bool isFloat = property.propertyType == SerializedPropertyType.Float;
            bool isInt = property.propertyType == SerializedPropertyType.Integer;
            if (!isFloat && !isInt) return false;

            HoverKnobMath.TryFindField(inspected.GetType(), target.Path, out FieldInfo field);
            if (field != null && field.IsDefined(typeof(KnobIgnoreAttribute), false)) return false;

            var range = field?.GetCustomAttribute<RangeAttribute>();
            if (range != null)
            {
                target.Bounded = true;
                target.Min = range.min;
                target.Max = range.max;
                return true;
            }
            if (TryParamRange(target, inspected, out float min, out float max))
            {
                target.Bounded = true;
                target.Min = min;
                target.Max = max;
                return true;
            }
            // A plain int could be a seed, count or channel index, so it needs a declared range.
            return isFloat;
        }

        // Per-type params declare their travel in the set's "ranges" list. GetRange falls back to (0, 1) for an unlisted
        // name, so the name has to be checked against the list first.
        private static bool TryParamRange(Target target, UnityEngine.Object inspected, out float min, out float max)
        {
            min = max = 0f;
            if (inspected is not IParamSet set || HoverKnobMath.ArrayPathOf(target.Path) != "types") return false;

            var ranges = target.So.FindProperty("ranges");
            if (ranges == null) return false;

            string leaf = HoverKnobMath.LeafName(target.Path);
            for (int i = 0; i < ranges.arraySize; i++)
            {
                if (ranges.GetArrayElementAtIndex(i).FindPropertyRelative("name")?.stringValue != leaf) continue;
                (min, max) = set.GetRange(leaf);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The rect and path of every SerializedProperty a window drew. IMGUI keeps no widgets to query, so this reads
        /// the internal data behind Window > Analysis > IMGUI Debugger. Everything is resolved by reflection and checked
        /// up front: on an editor without it, the knob stays idle after one warning.
        /// </summary>
        private static class ImguiPropertyRects
        {
            public struct Entry
            {
                public string TargetTypeName, Path;
                public Rect Rect;
            }

            private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            private static readonly Type HelperType = typeof(Editor).Assembly.GetType("UnityEditor.GUIViewDebuggerHelper");
            private static readonly Type InstructionType = typeof(Editor).Assembly.GetType("UnityEditor.IMGUIPropertyInstruction");
            private static readonly MethodInfo DebugWindow = HelperType?.GetMethod("DebugWindow", AnyStatic);
            private static readonly MethodInfo StopDebugging = HelperType?.GetMethod("StopDebugging", AnyStatic);
            private static readonly MethodInfo GetInstructions = HelperType?.GetMethod("GetPropertyInstructions", AnyStatic);
            private static readonly FieldInfo TypeNameField = InstructionType?.GetField("targetTypeName");
            private static readonly FieldInfo PathField = InstructionType?.GetField("path");
            private static readonly FieldInfo RectField = InstructionType?.GetField("rect");
            private static readonly FieldInfo HostViewField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);

            public static readonly bool Available = CheckAvailable();

            private static bool CheckAvailable()
            {
                bool ok = DebugWindow != null && StopDebugging != null && GetInstructions != null
                    && GetInstructions.GetParameters().Length == 2
                    && TypeNameField != null && PathField != null && RectField != null && HostViewField != null;
                if (!ok) Debug.LogWarning("[MFT] Hover knob unavailable: this Unity version changed the internal IMGUI debugger API it reads.");
                return ok;
            }

            public static void Watch(EditorWindow win)
            {
                var view = HostViewField.GetValue(win);
                if (view != null) DebugWindow.Invoke(null, new[] { view });
            }

            public static void Stop() => StopDebugging?.Invoke(null, null);

            public static void Read(List<Entry> into)
            {
                into.Clear();
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(InstructionType));
                GetInstructions.Invoke(null, new object[] { list, false });
                foreach (var item in list)
                {
                    into.Add(new Entry
                    {
                        TargetTypeName = (string)TypeNameField.GetValue(item),
                        Path = (string)PathField.GetValue(item),
                        Rect = (Rect)RectField.GetValue(item),
                    });
                }
            }
        }
    }
}
