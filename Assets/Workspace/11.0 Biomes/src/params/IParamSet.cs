namespace Biomes
{
    /// <summary>
    /// By-name raw access to a parameter set, whether it is a live runtime clone
    /// (agentParams) or an on-disk preset asset (paramsSO). Values are raw (not
    /// 0-1 normalized). Used by ParameterInterpolator to read "from"/"to" states.
    /// </summary>
    public interface IParamSet
    {
        int TypeCount { get; }
        float GetValue(string name, int typeIndex);
        void SetValue(string name, int typeIndex, float raw);
        (float min, float max) GetRange(string name);

        // Edit-mode operations (used by the shared params inspector). All concrete
        // param ScriptableObjects already implement these.
        void SyncTypesList();
        void RandomizeParams();
        void RandomizeColors();
        void ResetToDefaults();

        // Resolution-independence: scale pixel-unit distance params (speed, ranges, sensor
        // distance) and trail-density params (deposit/eat) by k = rezY/referenceHeight.
        // Applied to the runtime clone on Reset — never the on-disk asset. See SimulationBase.
        void ScaleSpatial(float k);
        void ScaleDensity(float k);
    }
}
