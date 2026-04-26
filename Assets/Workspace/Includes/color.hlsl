//
// Color Utilities
// 

// Inigo Quiles: www.thebookofshaders.com/06
float4 hsb2rgb(float3 c, float a=1.0) {
    float3 rgb = clamp(abs(((c.x * 6.0 + float3(0.0, 4.0, 2.0)) % 6.0) - 3.0) - 1.0, 0.0, 1.0);
    rgb = rgb*rgb*(3.0-2.0*rgb);
    float3 o = c.z * lerp(float3(1.0, 1.0, 1.0), rgb, c.y);
    return float4(o.r, o.g, o.b, a);
}

// Inigo Quiles: www.thebookofshaders.com/06
float3 rgb2hsb( in float3 c ){
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(c.bg, K.wz),
                   float4(c.gb, K.xy),
                   step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r),
                   float4(c.r, p.yzx),
                   step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)),
                  d / (q.x + e),
                  q.x);
}


float3 hsv2rgb(float3 _hsv) {
    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(_hsv.xxx + K.xyz) * 6.0 - K.www);
    return _hsv.z * lerp(K.xxx, saturate(p - K.xxx), _hsv.y);
}

float3 rgb2hsv(float3 _rgb) {
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(_rgb.bg, K.wz), float4(_rgb.gb, K.xy), step(_rgb.b, _rgb.g));
    float4 q = lerp(float4(p.xyw, _rgb.r), float4(_rgb.r, p.yzx), step(p.x, _rgb.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 lerpColor(float3 _color1, float3 _color2, float _t) {
    return lerp(_color1, _color2, _t);
}

float3 blendAdd(float3 _base, float3 _blend) {
    return min(_base + _blend, float3(1.0, 1.0, 1.0));
}

float3 blendMultiply(float3 _base, float3 _blend) {
    return _base * _blend;
}

float3 blendScreen(float3 _base, float3 _blend) {
    return 1.0 - (1.0 - _base) * (1.0 - _blend);
}

float3 blendOverlay(float3 _base, float3 _blend) {
    return lerp(
        2.0 * _base * _blend,
        1.0 - 2.0 * (1.0 - _base) * (1.0 - _blend),
        step(0.5, _base)
    );
}