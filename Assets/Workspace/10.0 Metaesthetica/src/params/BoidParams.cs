using UnityEngine;

namespace Metaesthetica
{
    [CreateAssetMenu(fileName = "BoidParams", menuName = "Metaesthetica/BoidParams")]
    public class BoidParams : ScriptableObject
    {
        [Header("Primary")]
        [Range(1, 500)] public float seperationRange = 10;
        [Range(1, 500)] public float alignmentRange = 10;
        [Range(1, 500)] public float attractionRange = 10;
        [Range(0.1f, 5)] public float maxspeed = 1;
        [Range(0.1f, 5)] public float maxforce = 1;

        [Header("Behavior")]
        [Range(0, 1)] public float depositAmount = .01f;
        [Range(0, 1)] public float eatAmount = 0.05f;

        [Header("Food Seeking")]
        [Range(0.1f, 100f)] public float foodSensorDistance = 10f;
        [Range(0f, Mathf.PI)] public float sensorAngleRad = Mathf.PI / 6f;
        [Range(0f, 5f)] public float foodSeekingStrength = 0.5f;

        [Header("External Influence")]
        [Range(0f, 5f)] public float externalInfluenceStrength = 1.0f;

        [Header("Render")]
        [Range(0, 1)] public float hue;
        [Range(0, 1)] public float saturation = 0.5f;
        [Range(0, 1)] public float diffuseRate = .95f;

        public void ResetToDefaults()
        {
            seperationRange = 10;
            alignmentRange = 10;
            attractionRange = 10;
            maxspeed = 1;
            maxforce = 1;
            depositAmount = .01f;
            eatAmount = 0.05f;
            foodSensorDistance = 10f;
            sensorAngleRad = Mathf.PI / 6f;
            foodSeekingStrength = 0.5f;
            externalInfluenceStrength = 1.0f;
            hue = 0;
            saturation = 0.5f;
            diffuseRate = .95f;
        }
    }
}
