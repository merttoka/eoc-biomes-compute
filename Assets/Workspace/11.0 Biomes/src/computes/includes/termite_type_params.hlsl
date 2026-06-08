struct TermiteTypeParams {
    float senseAngle;
    float senseDistance;
    float turnAngle;
    float moveSpeed;
    float firingSpeedMul;
    float depositAmount;
    float firingDepositAmount;
    float depositProbability;
    float firingDepositProbability;
    float diffuseRate;
    float hue;
    float saturation;
};  // 48 bytes (12 floats)
StructuredBuffer<TermiteTypeParams> typeParams;
uint typeCount;
