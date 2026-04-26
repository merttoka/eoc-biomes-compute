namespace Metaesthetica
{
    public interface IControllableSim
    {
        string SimName { get; }
        void SetParameter(string paramName, int index, float value);
        void SetParameterDelta(string paramName, int index, float delta);
        float GetParameter(string paramName, int index);
    }
}
