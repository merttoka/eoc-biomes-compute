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
};  // 36 bytes (9 floats)
StructuredBuffer<PhysarumTypeParams> typeParams;
uint typeCount;
