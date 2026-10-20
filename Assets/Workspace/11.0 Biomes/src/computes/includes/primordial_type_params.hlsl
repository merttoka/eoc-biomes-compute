struct PrimordialTypeParams {
    float alpha;               // rad
    float beta;                // rad
    float radiusSq;            // px²
    float speed;               // px/step
    float noise;               // rad σ
    float otherTypeWeight;
    float fieldDrift;          // px/step per unit field difference
    float agitationNoise;      // rad σ per unit perception.a
    float depositAmount;
    float diffuseRate;
    float densityFull;         // N at which the density ramp saturates
    float densityHueShift;
    float densityGlow;
    float firingSpeedMul;
    float firingDepositAmount;
    float hue;
    float saturation;
    float brightness;          // HSB value at full trail (last, like every agent sim)
};  // 72 bytes (18 floats)
StructuredBuffer<PrimordialTypeParams> typeParams;
uint typeCount;
