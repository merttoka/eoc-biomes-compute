using NUnit.Framework;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Archived snapshots predate the brightness field. Unity fills a field missing from YAML from
/// its C# initializer, which must equal the constant each render kernel used to hardcode
/// (Physarum/Boid 0.8, Termite 1.0) so every exhibited look renders unchanged.
/// </summary>
public class AgentColorAssetTests
{
    private const string Snapshots = "Assets/Workspace/11.1 CURRENTS Scene/assets/Snapshots/";

    [TestCase("PhysarumParams_20260607.asset", 0.8f)]
    [TestCase("Boid_20260610_150648.asset", 0.8f)]
    [TestCase("Termite_20260610_150648.asset", 1f)]
    public void SnapshotWithoutBrightness_LoadsLegacyDefault(string file, float legacy)
    {
        var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(Snapshots + file);
        Assert.That(asset, Is.Not.Null, file);
        var types = new SerializedObject(asset).FindProperty("types");
        Assert.That(types.arraySize, Is.GreaterThan(0), file);
        for (int i = 0; i < types.arraySize; i++)
        {
            var b = types.GetArrayElementAtIndex(i).FindPropertyRelative("brightness");
            Assert.That(b, Is.Not.Null, $"{file} type {i}: no brightness field");
            Assert.That(b.floatValue, Is.EqualTo(legacy), $"{file} type {i}");
        }
    }
}
