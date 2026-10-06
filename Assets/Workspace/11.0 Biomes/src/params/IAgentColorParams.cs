namespace Biomes
{
    /// <summary>
    /// An agent sim's param set (Physarum / Boid / Termite): per-type hue, saturation and
    /// brightness, plus the family that palette swatches are tagged with.
    /// </summary>
    public interface IAgentColorParams : IParamSet
    {
        AgentFamily Family { get; }
    }

    public static class AgentColorParamsExtensions
    {
        public static (float h, float s, float b) GetHsb(this IParamSet p, int type) =>
            (p.GetValue("hue", type), p.GetValue("saturation", type), p.GetValue("brightness", type));

        public static void SetHsb(this IParamSet p, int type, (float h, float s, float b) c)
        {
            p.SetValue("hue", type, c.h);
            p.SetValue("saturation", type, c.s);
            p.SetValue("brightness", type, c.b);
        }
    }
}
