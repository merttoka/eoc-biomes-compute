//
// Transformation Utilities
//

float2 RotateVectorBy(const float2 vec, const float a)  {
    float x = vec.x * cos(a) - vec.y * sin(a);
    float y = sin(a) * vec.x + cos(a) * vec.y;
    return float2(x, y);
}