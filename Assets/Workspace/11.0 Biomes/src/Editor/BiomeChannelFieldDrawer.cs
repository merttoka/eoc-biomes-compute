using UnityEditor;
using UnityEngine;

namespace Biomes
{
    /// <summary>Renders an int field tagged with [BiomeChannelField] as a named dropdown
    /// of biome channels (from BiomeChannel.Names). The stored value remains the channel
    /// index, so it stays GPU- and serialization-compatible with a plain int.</summary>
    [CustomPropertyDrawer(typeof(BiomeChannelFieldAttribute))]
    public class BiomeChannelFieldDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Only applies to ints; fall back to the default drawer otherwise.
            if (property.propertyType != SerializedPropertyType.Integer)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            var names = BiomeChannel.Names;
            EditorGUI.BeginProperty(position, label, property);
            int idx = Mathf.Clamp(property.intValue, 0, names.Length - 1);
            int newIdx = EditorGUI.Popup(position, label.text, idx, names);
            if (newIdx != property.intValue)
                property.intValue = newIdx;
            EditorGUI.EndProperty();
        }
    }
}
