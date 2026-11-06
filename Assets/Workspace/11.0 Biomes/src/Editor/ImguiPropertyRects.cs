using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// The rect and path of every SerializedProperty a window drew. IMGUI keeps no widgets to query, so this reads the
    /// internal data behind Window > Analysis > IMGUI Debugger. Everything is resolved by reflection and checked up
    /// front: on an editor without it, callers stay idle after one warning. Recording is held by a lease, so it stops by
    /// itself once nothing asks for it.
    /// </summary>
    internal static class ImguiPropertyRects
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

        private const double FirstRepaintSeconds = 0.03;

        private static EditorWindow _watched;
        private static double _watchedSince;
        private static double _leaseUntil;
        private static IList _instructions;

        private static bool CheckAvailable()
        {
            bool ok = DebugWindow != null && StopDebugging != null && GetInstructions != null
                && GetInstructions.GetParameters().Length == 2
                && TypeNameField != null && PathField != null && RectField != null && HostViewField != null;
            if (!ok) Debug.LogWarning("[MFT] Inspector field lookup unavailable: this Unity version changed the internal IMGUI debugger API it reads.");
            return ok;
        }

        /// <summary>Records this window for at least the given time.</summary>
        public static void Lease(EditorWindow win, double seconds)
        {
            _leaseUntil = Math.Max(_leaseUntil, EditorApplication.timeSinceStartup + seconds);
            if (win == _watched) return;

            var view = HostViewField.GetValue(win);
            if (view == null) return;
            DebugWindow.Invoke(null, new[] { view });
            _watched = win;
            _watchedSince = EditorApplication.timeSinceStartup;
            win.Repaint();

            EditorApplication.update -= ExpireLease;
            EditorApplication.update += ExpireLease;
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting -= Stop;
            EditorApplication.quitting += Stop;
        }

        private static void ExpireLease()
        {
            if (EditorApplication.timeSinceStartup >= _leaseUntil) Stop();
        }

        private static void Stop()
        {
            EditorApplication.update -= ExpireLease;
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            EditorApplication.quitting -= Stop;
            _watched = null;
            StopDebugging.Invoke(null, null);
        }

        /// <summary>Reads the rects a window last drew. False until it is being recorded and has had time to repaint.</summary>
        public static bool TryRead(EditorWindow win, List<Entry> into)
        {
            into.Clear();
            if (win != _watched || EditorApplication.timeSinceStartup < _watchedSince + FirstRepaintSeconds) return false;

            _instructions ??= (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(InstructionType));
            _instructions.Clear();
            GetInstructions.Invoke(null, new object[] { _instructions, false });
            foreach (var item in _instructions)
            {
                into.Add(new Entry
                {
                    TargetTypeName = (string)TypeNameField.GetValue(item),
                    Path = (string)PathField.GetValue(item),
                    Rect = (Rect)RectField.GetValue(item),
                });
            }
            return true;
        }
    }
}
