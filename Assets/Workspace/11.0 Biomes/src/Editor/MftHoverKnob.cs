using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Biomes
{
    /// <summary>Lets the MFT hover knob move the inspector field under the mouse. Runs only while the MFT is on the hover page.</summary>
    [InitializeOnLoad]
    internal sealed class MftHoverKnob : MidiFighterTwister.IHoverDriver
    {
        private const double RefreshSeconds = 0.1;
        private const double GestureIdleSeconds = 0.3;
        private const double LeaseSeconds = 1.0;

        private static readonly MftHoverKnob Instance = new();

        static MftHoverKnob()
        {
            MidiFighterTwister.HoverDriver = Instance;
            EditorApplication.update += Instance.Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Instance.Deactivate;
            EditorApplication.quitting += Instance.Deactivate;
        }

        private EditorWindow _window;
        private VisualElement _root;
        private bool _windowWantedMouseMove;
        private Vector2? _pointer;
        private double _nextRefresh;
        private InspectorFieldTarget _target;
        private double _lastNudge;

        public bool HasTarget => _target != null && _target.IsAlive;
        public bool InGesture => _target != null && _target.InGesture;
        public float Normalized => _target.Normalized;

        public void Nudge(float delta, bool allElements)
        {
            _lastNudge = EditorApplication.timeSinceStartup;
            _target.Nudge(delta, allElements);
        }

        private void Tick()
        {
            var mft = MidiFighterTwister.HoverPageOwner;
            if (mft == null || !mft.hoverKnobEnabled || !ImguiPropertyRects.Available)
            {
                Deactivate();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (InGesture && now - _lastNudge > GestureIdleSeconds) _target.EndGesture();
            if (now < _nextRefresh) return;
            _nextRefresh = now + RefreshSeconds;

            if (InGesture) return;
            Retarget();
            mft.RefreshHoverKnob();
        }

        private void Deactivate()
        {
            SetTarget(null);
            Unwatch();
        }

        private void Retarget()
        {
            // A closed window compares equal to a null mouseOverWindow, which would keep it watched and read with no window.
            if (_root != null && _window == null) Unwatch();

            var win = EditorWindow.mouseOverWindow;
            // A window can gain its inspector later, when the selection changes under a resting mouse.
            if (win != _window || (_root == null && InspectorFieldTarget.HasInspector(win))) Watch(win);
            if (_root != null) ImguiPropertyRects.Lease(win, LeaseSeconds);

            var next = _root != null && _pointer.HasValue ? InspectorFieldTarget.At(win, _pointer.Value, _target) : null;
            if (next != _target) SetTarget(next);
        }

        private void SetTarget(InspectorFieldTarget next)
        {
            _target?.Dispose();
            _target = next;
        }

        // Inspector windows only: the debugger records every property a watched window draws, on each repaint.
        private void Watch(EditorWindow win)
        {
            Unwatch();
            _window = win;
            if (!InspectorFieldTarget.HasInspector(win)) return;

            _root = win.rootVisualElement;
            _windowWantedMouseMove = win.wantsMouseMove;
            win.wantsMouseMove = true;
            _root.RegisterCallback<PointerMoveEvent>(OnPointerMove, TrickleDown.TrickleDown);
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
            _nextRefresh = 0;
        }
    }
}
