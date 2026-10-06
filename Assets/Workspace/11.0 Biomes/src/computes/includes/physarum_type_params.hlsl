struct PhysarumTypeParams {
    float senseAngle;
    float senseDistance;
    float turnAngle;
    float moveSpeed;
    float depositAmount;
    float eatAmount;
    float diffuseRate;
    float hue;
    float saturation;
    float firingSpeedMul;
    float firingDepositAmount;
    float brightness;          // HSB value at full trail (appended last: no older field moves)
};  // 48 bytes (12 floats)
StructuredBuffer<PhysarumTypeParams> typeParams;
uint typeCount;
