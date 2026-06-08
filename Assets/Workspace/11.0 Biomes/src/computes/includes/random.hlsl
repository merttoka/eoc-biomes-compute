// Randomness Utilities (from 10.0 Metaesthetica)

float Map(const float v, const float min1, const float max1, const float min2, const float max2) {
    return (v - min1) / (max1 - min1) * (max2 - min2) + min2;
}

float2 Hash(float2 p) {
    p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
    return -1.0 + 2.0 * frac(sin(p) * 43758.5453123);
}

float SimplexNoise(const float2 p) {
    const float K1 = 0.366025404;
    const float K2 = 0.211324865;
    float2 i = floor(p + (p.x + p.y) * K1);
    float2 a = p - i + (i.x + i.y) * K2;
    float m = step(a.y, a.x);
    float2 o = float2(m, 1.0 - m);
    float2 b = a - o + K2;
    float2 c = a - 1.0 + 2.0 * K2;
    float3 h = max(0.5 - float3(dot(a, a), dot(b, b), dot(c, c)), 0.0);
    float3 n = h * h * h * h * float3(dot(a, Hash(i + 0.0)), dot(b, Hash(i + o)), dot(c, Hash(i + 1.0)));
    return dot(n, float3(70.0, 70.0, 70.0));
}

float FractalSimplexNoise(float2 uv) {
    uv *= 5;
    float2x2 m = float2x2(1.6, 1.2, -1.2, 1.6);
    float f = 0.5000 * SimplexNoise(uv); uv = mul(m, uv);
    f += 0.2500 * SimplexNoise(uv); uv = mul(m, uv);
    f += 0.1250 * SimplexNoise(uv); uv = mul(m, uv);
    f += 0.0625 * SimplexNoise(uv); uv = mul(m, uv);
    f += 0.03125 * SimplexNoise(uv);
    return 0.5 + 0.5 * f;
}

float Random1(const float2 p) {
    float3 a = frac(p.yxy * float3(123.34, 234.34, 345.65));
    a += dot(a, a + 34.45);
    return frac(a.x * a.y * a.z);
}

float2 Random2(const float2 p) {
    float3 a = frac(p.xyx * float3(123.34, 234.34, 345.65));
    a += dot(a, a + 34.45);
    return frac(float2(a.x * a.y, a.y * a.z));
}

float2 RandomDirection2(const float2 p) {
    return normalize(2.0 * (Random2(p) - 0.5));
}

// Robust integer hash → [0,1). Prefer this for per-agent randomness seeded by large
// indices: frac(id * const) loses float precision once id is large (visible banding).
float Hash1u(uint x) {
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x * (1.0 / 4294967296.0);
}
