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
    float brightness;          // HSB value at full trail (appended last: no older field moves)
};  // 52 bytes (13 floats)
StructuredBuffer<TermiteTypeParams> typeParams;
uint typeCount;
