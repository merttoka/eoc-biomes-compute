using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Biomes
{
    [CustomEditor(typeof(MIDIMapping))]
    public class MIDIMappingEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var simsProp = serializedObject.FindProperty("m_Simulations");
            EditorGUILayout.PropertyField(simsProp, true);

            var mappingsProp = serializedObject.FindProperty("m_MIDIMappings");
            EditorGUILayout.LabelField("MIDI Mappings", EditorStyles.boldLabel);

            var mapping = target as MIDIMapping;
            var sims = GetSimulations(simsProp);

            for (int i = 0; i < mappingsProp.arraySize; i++)
            {
                var elem = mappingsProp.GetArrayElementAtIndex(i);
                EditorGUILayout.BeginVertical("box");

                EditorGUILayout.PropertyField(elem.FindPropertyRelative("m_MIDIChannel"));
                EditorGUILayout.PropertyField(elem.FindPropertyRelative("m_ControlNumber"));
                EditorGUILayout.PropertyField(elem.FindPropertyRelative("m_SimIndex"));
                EditorGUILayout.PropertyField(elem.FindPropertyRelative("m_UseDelta"));

                // Param name dropdown
                var paramNameProp = elem.FindPropertyRelative("m_ParamName");
                int simIdx = elem.FindPropertyRelative("m_SimIndex").intValue;

                if (simIdx >= 0 && simIdx < sims.Count && sims[simIdx] != null)
                {
                    var paramList = sims[simIdx].ModulatableParams;
                    if (paramList != null && paramList.Count > 0)
                    {
                        string[] options = new string[paramList.Count];
                        for (int p = 0; p < paramList.Count; p++)
                            options[p] = paramList[p];

                        int current = System.Array.IndexOf(options, paramNameProp.stringValue);
                        if (current < 0) current = 0;

                        current = EditorGUILayout.Popup("Param Name", current, options);
                        paramNameProp.stringValue = options[current];
                    }
                    else
                    {
                        EditorGUILayout.PropertyField(paramNameProp);
                    }
                }
                else
                {
                    EditorGUILayout.PropertyField(paramNameProp);
                }

                EditorGUILayout.PropertyField(elem.FindPropertyRelative("m_ParamIndex"));

                if (GUILayout.Button("Remove"))
                {
                    mappingsProp.DeleteArrayElementAtIndex(i);
                    i--;
                }

                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("Add Mapping"))
                mappingsProp.InsertArrayElementAtIndex(mappingsProp.arraySize);

            serializedObject.ApplyModifiedProperties();
        }

        private List<SimulationBase> GetSimulations(SerializedProperty simsProp)
        {
            var list = new List<SimulationBase>();
            for (int i = 0; i < simsProp.arraySize; i++)
            {
                var obj = simsProp.GetArrayElementAtIndex(i).objectReferenceValue;
                list.Add(obj as SimulationBase);
            }
            return list;
        }
    }
}
