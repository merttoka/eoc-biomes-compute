using UnityEngine;

namespace Metaesthetica
{
    [CreateAssetMenu(fileName = "PhysarumParams", menuName = "Metaesthetica/PhysarumParams")]
    public class PhysarumParams : ScriptableObject
    {
        [Header("Primary")]
        public Vector4 m_SenseAngles = new Vector4(22.5f, 22.5f, 22.5f, 22.5f);
        public Vector4 m_SenseDistances = new Vector4(9f, 9f, 9f, 9f);
        public Vector4 m_TurnAngles = new Vector4(45f, 45f, 45f, 45f);
        public Vector4 m_MoveSpeeds = new Vector4(0.4f, 0.4f, 0.4f, 0.4f);

        [Header("Behavior")]
        public Vector4 m_DepositAmounts = new Vector4(0.01f, 0.01f, 0.01f, 0.01f);
        public Vector4 m_EatAmounts = new Vector4(0.05f, 0.05f, 0.05f, 0.05f);
        public Vector4 m_ExternalInfluenceStrengths = Vector4.one;

        [Header("Render")]
        public Vector4 m_Hues = Vector4.zero;
        public Vector4 m_Saturations = new Vector4(0.5f, 0.5f, 0.5f, 0.5f);
        public Vector4 m_DiffuseRates = new Vector4(0.95f, 0.95f, 0.95f, 0.95f);

        public void ResetToDefaults()
        {
            m_SenseAngles = new Vector4(22.5f, 22.5f, 22.5f, 22.5f);
            m_SenseDistances = new Vector4(9f, 9f, 9f, 9f);
            m_TurnAngles = new Vector4(45f, 45f, 45f, 45f);
            m_MoveSpeeds = new Vector4(0.4f, 0.4f, 0.4f, 0.4f);
            m_DepositAmounts = new Vector4(0.01f, 0.01f, 0.01f, 0.01f);
            m_EatAmounts = new Vector4(0.05f, 0.05f, 0.05f, 0.05f);
            m_ExternalInfluenceStrengths = Vector4.one;
            m_Hues = Vector4.zero;
            m_Saturations = new Vector4(0.5f, 0.5f, 0.5f, 0.5f);
            m_DiffuseRates = new Vector4(0.95f, 0.95f, 0.95f, 0.95f);
        }
    }
}
