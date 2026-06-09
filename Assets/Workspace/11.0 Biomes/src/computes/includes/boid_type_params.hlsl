struct BoidTypeParams {
    float separateRange;    // pre-squared on CPU
    float alignRange;
    float attractRange;
    float maxSpeed;
    float maxForce;
    float depositAmount;
    float eatAmount;
    float foodSensorDistance;
    float sensorAngleRad;
    float foodSeekingStrength;
    float diffuseRate;
    float hue;
    float saturation;
    float firingSpeedMul;
    float firingDepositAmount;
};  // 60 bytes (15 floats)
StructuredBuffer<BoidTypeParams> typeParams;
uint typeCount;
