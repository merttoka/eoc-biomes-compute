using System;
using System.Globalization;

namespace Biomes
{
    /// <summary>
    /// Flat string keys addressing every interpolatable field of an UmweltMapping, so a
    /// mapping can be snapshotted to a name→float dictionary and crossfaded key-by-key.
    /// Pure (no Unity types) so it is unit-testable from Biomes.Core.
    /// </summary>
    public static class UmweltKeys
    {
        public const string PermMin           = "permMin";
        public const string PermMax           = "permMax";
        public const string MetabolicHeat     = "metabolicHeat";
        public const string OxygenConsumption = "oxygenConsumption";
        public const string DeathO2           = "deathO2";
        public const string DeathPerm         = "deathPerm";
        public const string CorpseWaste       = "corpseWaste";
        public const string CorpseDecay       = "corpseDecay";

        public static readonly string[] Scalars =
        {
            PermMin, PermMax, MetabolicHeat, OxygenConsumption,
            DeathO2, DeathPerm, CorpseWaste, CorpseDecay,
        };

        private const string ReadPrefix   = "read:";
        private const string ReadSuffix   = ".weight";
        private const string WritePrefix  = "write:";
        private const string WriteSuffix  = ".amount";

        public static string Read(int channel, int effect) =>
            ReadPrefix + channel.ToString(CultureInfo.InvariantCulture) + ":" +
            effect.ToString(CultureInfo.InvariantCulture) + ReadSuffix;

        public static string Write(int channel) =>
            WritePrefix + channel.ToString(CultureInfo.InvariantCulture) + WriteSuffix;

        public static bool IsRead(string key)  => key != null && key.StartsWith(ReadPrefix, StringComparison.Ordinal);
        public static bool IsWrite(string key) => key != null && key.StartsWith(WritePrefix, StringComparison.Ordinal);

        public static bool IsScalar(string key)
        {
            if (key == null) return false;
            foreach (var s in Scalars) if (s == key) return true;
            return false;
        }

        public static bool TryParseRead(string key, out int channel, out int effect)
        {
            channel = 0; effect = 0;
            if (!IsRead(key) || !key.EndsWith(ReadSuffix, StringComparison.Ordinal)) return false;
            string body = key.Substring(ReadPrefix.Length, key.Length - ReadPrefix.Length - ReadSuffix.Length);
            int colon = body.IndexOf(':');
            if (colon <= 0 || colon == body.Length - 1) return false;
            return int.TryParse(body.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out channel)
                && int.TryParse(body.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out effect);
        }

        public static bool TryParseWrite(string key, out int channel)
        {
            channel = 0;
            if (!IsWrite(key) || !key.EndsWith(WriteSuffix, StringComparison.Ordinal)) return false;
            string body = key.Substring(WritePrefix.Length, key.Length - WritePrefix.Length - WriteSuffix.Length);
            return int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out channel);
        }
    }
}
