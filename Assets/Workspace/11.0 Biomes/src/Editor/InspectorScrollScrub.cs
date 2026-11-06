using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Biomes
{
    /// <summary>
    /// Scrolling sideways over a numeric inspector field moves it, with no click or focus. Shift moves it a tenth as far,
    /// and Ctrl moves that field on every element of its array. The inspector's own vertical scroll is left alone.
    /// Works in any scene and needs no MFT.
    /// </summary>
    [InitializeOnLoad]
    internal static class InspectorScrollScrub
    {
        private const string EnabledPref = "Biomes.InspectorScrollScrub";
        private const string MenuPath = "Biomes/Scroll Sideways To Edit Inspector Fields";

        // Unity reports trackpad travel in steps of 0.05, and a comfortable swipe measures about 50 to 350 units.
        private const float WheelUnitsPerFullTravel = 200f;
        private const float WheelSlop = 0.6f;
        // Wheel events don't say when a finger lifts, so a short gap ends a gesture.
        private const double GestureIdleSeconds = 0.12;
        private const double LeaseSeconds = 5.0;
        private const double ResolveTimeoutSeconds = 0.25;
        private const double AttachCheckSeconds = 0.25;

        private static bool _enabled;
        private static EditorWindow _window;
        private static VisualElement _root;
        private static double _nextAttachCheck;

        private static ScrollGesture _gesture;
        private static double _lastWheel;
        private static InspectorFieldTarget _target;
        private static bool _resolving;
        private static bool _noTarget;
        private static double _resolveUntil;
        private static Vector2 _point;
        private static float _pendingDelta;
        private static bool _pendingAll;

        static InspectorScrollScrub()
        {
            _enabled = EditorPrefs.GetBool(EnabledPref, true);
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        [MenuItem(MenuPath)]
        private static void ToggleEnabled()
        {
            _enabled = !_enabled;
            EditorPrefs.SetBool(EnabledPref, _enabled);
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(MenuPath, _enabled);
            return true;
        }

        private static void Shutdown()
        {
            EndGesture();
            Detach();
        }

        private static void Tick()
        {
            if (!_enabled || !ImguiPropertyRects.Available)
            {
                Shutdown();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            // A resolve still waiting on the window's repaint holds the gesture open, or a short flick would be dropped.
            if (_gesture != null && !_resolving && now - _lastWheel > GestureIdleSeconds) EndGesture();
            if (_resolving) TryResolve(now);
            if (_gesture != null) return;

            var win = EditorWindow.mouseOverWindow;
            // A window can gain its inspector later, when the selection changes under a resting mouse.
            if (win == _window && (_root != null || now < _nextAttachCheck)) return;
            _nextAttachCheck = now + AttachCheckSeconds;
            Attach(win);
        }

        private static void Attach(EditorWindow win)
        {
            Detach();
            _window = win;
            if (!InspectorFieldTarget.HasInspector(win)) return;
            _root = win.rootVisualElement;
            _root.RegisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
        }

        private static void Detach()
        {
            _root?.UnregisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
            _root = null;
            _window = null;
        }

        private static void OnWheel(WheelEvent e)
        {
            if (e.commandKey) return;

            double now = EditorApplication.timeSinceStartup;
            if (_gesture == null || now - _lastWheel > GestureIdleSeconds)
            {
                EndGesture();
                _gesture = new ScrollGesture(WheelSlop);
            }
            _lastWheel = now;

            float? travel = _gesture.Feed(e.delta.x, e.delta.y);
            if (travel == null) return;

            // Sideways, or too small to tell yet: either way it must not reach the inspector's scroll view, where the
            // swipe's vertical drift would move the rows under the mouse.
            e.StopImmediatePropagation();
            if (travel.Value == 0f || _noTarget) return;

            ImguiPropertyRects.Lease(_window, LeaseSeconds);
            float delta = ToKnobDelta(travel.Value, e.shiftKey);
            if (_target != null)
            {
                _target.Nudge(delta, e.ctrlKey);
                return;
            }

            _pendingDelta += delta;
            _pendingAll = e.ctrlKey;
            if (_resolving) return;

            _point = e.mousePosition;
            _resolving = true;
            _resolveUntil = now + ResolveTimeoutSeconds;
            TryResolve(now);
        }

        // A natural swipe right reports a negative delta, and should read as dragging the field up.
        private static float ToKnobDelta(float travel, bool fine) =>
            -travel / WheelUnitsPerFullTravel * (fine ? HoverKnobMath.FineGain : 1f);

        private static void TryResolve(double now)
        {
            // Null at first when the lease has only just started: the window's rects exist only after its next repaint.
            var found = InspectorFieldTarget.At(_window, _point);
            if (found == null && now < _resolveUntil) return;

            _resolving = false;
            _target = found;
            _noTarget = found == null;
            if (found != null && _pendingDelta != 0f) found.Nudge(_pendingDelta, _pendingAll);
            _pendingDelta = 0f;
        }

        private static void EndGesture()
        {
            _target?.Dispose();
            _target = null;
            _gesture = null;
            _resolving = false;
            _noTarget = false;
            _pendingDelta = 0f;
        }
    }
}
