struct PrimordialTypeParams {
    float alpha;               // rad
    float beta;                // rad
    float radiusSq;            // px²
    float speed;               // px/step
    float sizePx;              // splat radius of one particle's deposit (< 0.75 = a single pixel)
    float noise;               // rad σ
    float otherTypeWeight;
    float fieldDrift;          // px/step per unit field difference
    float agitationNoise;      // rad σ per unit perception.a
    float depositAmount;
    float diffuseRate;
    float densityFloor;        // N at which the density ramp starts (the soup sits below it)
    float densityFull;         // N at which it saturates
    float densityHueShift;
    float densityGlow;
    float sparseDim;           // brightness cut at density 0 (1 = the soup goes dark)
    float firingSpeedMul;
    float firingDepositAmount;
    float hue;
    float saturation;
    float brightness;          // HSB value at full trail (last, like every agent sim)
};  // 84 bytes (21 floats)
StructuredBuffer<PrimordialTypeParams> typeParams;
uint typeCount;
