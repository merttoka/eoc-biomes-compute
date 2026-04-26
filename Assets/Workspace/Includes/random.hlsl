//
// Randomness Utilities
//

// map a number from min1-max1 to min2-max2
float Map(const float v, 
        const float min1, const float max1, 
        const float min2, const float max2) {
    return (v-min1)/(max1-min1) * (max2-min2) + min2;
}


// Simplex noise 
// from: https://www.shadertoy.com/view/Msf3WH
float2 Hash( float2 p ) // replace this by something better
{
	p = float2( dot(p,float2(127.1,311.7)), dot(p,float2(269.5,183.3)) );
	return -1.0 + 2.0*frac(sin(p)*43758.5453123);
}
float SimplexNoise(const float2 p)
{
    const float K1 = 0.366025404; // (sqrt(3)-1)/2;
    const float K2 = 0.211324865; // (3-sqrt(3))/6;

	float2 i = floor( p + (p.x+p.y)*K1 );
    float2 a = p - i + (i.x+i.y)*K2;
    float m = step(a.y,a.x); 
    float2 o = float2(m, 1.0-m);
    float2 b = a - o + K2;
	float2 c = a - 1.0 + 2.0*K2;
    float3 h = max( 0.5-float3(dot(a,a), dot(b,b), dot(c,c) ), 0.0 );
	float3 n = h*h*h*h*float3( dot(a,Hash(i+0.0)), dot(b,Hash(i+o)), dot(c,Hash(i+1.0)));
    return dot( n, float3(70.0, 70, 70) );
}
// 5 octaves
float FractalSimplexNoise(float2 uv) {
    uv *= 5;
    float2x2 m = float2x2( 1.6,  1.2, -1.2,  1.6 );
    float f = 0.5000*SimplexNoise( uv ); uv = mul(m,uv);
    f += 0.2500*SimplexNoise( uv ); uv = mul(m,uv);
    f += 0.1250*SimplexNoise( uv ); uv = mul(m,uv);
    f += 0.0625*SimplexNoise( uv ); uv = mul(m,uv);
    f += 0.03125*SimplexNoise( uv ); uv = mul(m,uv);
    
    return 0.5+0.5*f;
}


// Random number generation utilities for compute shaders
// Based on hash functions from https://www.shadertoy.com/view/4djSRW

// float
float Random1(const float2 p) {
    float3 a = frac(p.yxy * float3(123.34, 234.34, 345.65));
    a += dot(a, a+34.45);
    return frac(a.x*a.y*a.z);
}
float Random1(const float3 p) {
    return frac(sin(dot(p, float3(12.9898,78.233,45.164))) * 43758.5453123);
}
float Random1Bounds(const float2 p, const float2 u) {
    float rand = Random1(p);
    return Map(rand, 0.0, 1.0, u.x, u.y);
}
int Random1IntBounds(const float2 p, const float2 u) {
    return (int)(Random1Bounds(p, u));
}


// float2
float2 Random2(const float2 p) {
    float3 a = frac(p.xyx * float3(123.34, 234.34, 345.65));
    a += dot(a, a+34.45);
    return frac(float2(a.x*a.y, a.y*a.z));
}
float2 RandomDirection2(const float2 p) {
    return (normalize(2.0 * (Random2(p) - 0.5)));
}
float2 Random2Bounds(const float2 p, const float2 u, const float2 v) {
    float2 rand = Random2(p);
    rand.x = Map(rand.x, 0.0, 1.0, u.x, u.y);
    rand.y = Map(rand.y, 0.0, 1.0, v.x, v.y);
    return rand;
}

// float3
float3 Random3(const float2 p) {
    float3 a = frac(p.yxy * float3(123.34, 234.34, 345.65));
    a += dot(a, a+34.45);
    return frac(float3(a.x*a.y, a.y*a.z, a.z*a.x));
}
float3 Random3(const float3 p) {
    float3 a = frac(p.xyz * float3(123.34, 234.34, 345.65));
    a += dot(a, a+34.45);
    return frac(float3(a.x*a.y, a.y*a.z, a.z*a.x));
}
float3 Random3Negative(const float3 p) {
    return 2.0 * (Random3(p) - 0.5);
}
float3 RandomDirection3(const float3 p) {
    return (normalize(2.0 * (Random3(p) - 0.5)));
}
float3 Random3Bounds(const float3 p, 
        const float2 u, const float2 v, const float2 w) {
    float3 rand = Random3(p);
    rand.x = Map(rand.x, 0.0, 1.0, u.x, u.y);
    rand.y = Map(rand.y, 0.0, 1.0, v.x, v.y);
    rand.z = Map(rand.z, 0.0, 1.0, w.x, w.y);
    return rand;
}

// float4
float4 Random4(const float2 p) {
    float4 a = frac(p.xyxy * float4(123.34, 234.34, 345.65, 456.78));
    a += dot(a, a+34.45);
    return frac(float4(a.x*a.y, a.y*a.z, a.z*a.x, a.x*a.w));
}

float4 Random4(const float3 p) {
    float4 a = frac(p.xyzx * float4(127.1, 311.7, 183.3, 456.78));
    a += dot(a, a+34.45);
    return frac(float4(a.x*a.y, a.y*a.z, a.z*a.x, a.x*a.w));
}