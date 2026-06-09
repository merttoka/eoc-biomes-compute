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
};  // 44 bytes (11 floats)
StructuredBuffer<PhysarumTypeParams> typeParams;
uint typeCount;
