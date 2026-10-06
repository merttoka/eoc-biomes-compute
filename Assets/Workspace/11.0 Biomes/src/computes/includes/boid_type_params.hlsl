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
    float brightness;          // HSB value at full trail (appended last: no older field moves)
};  // 64 bytes (16 floats)
StructuredBuffer<BoidTypeParams> typeParams;
uint typeCount;
