// Rectangular screen cutouts (physical holes in an install's canvas).
// Authored on SimulationManager as normalized canvas rects, pushed to every
// consumer: perception build (steering via the avoidance channel), agent spawn
// (exclusion), and the composite + biome channel renders (mask to black).
// Up to KEEPOUT_MAX rects — raise the cap here and in SimulationManager's
// scratch array if an install ever needs more.
#ifndef KEEPOUT_INCLUDED
#define KEEPOUT_INCLUDED

#define KEEPOUT_MAX 4
int keepOutCount;                    // 0 = feature off (KeepOutField returns 0)
float4 keepOutRects[KEEPOUT_MAX];    // xMin, yMin, xMax, yMax — normalized canvas coords
float keepOutFeather;                // falloff width OUTSIDE each rect (normalized)

// 0 in the open -> 1 inside a rect, ramping across the feather band outside the
// edge. The ramp is the load-bearing part for steering: agent sensors sample the
// avoidance built from this and turn away BEFORE reaching the hole; a hard step
// would give them no gradient to react to.
float KeepOutField(float2 uv) {
    float m = 0.0;
    for (int i = 0; i < keepOutCount; i++) {
        float4 r = keepOutRects[i];
        float2 d = max(float2(r.x, r.y) - uv, uv - float2(r.z, r.w));
        float outside = length(max(d, 0.0));   // 0 inside, distance-to-rect outside
        m = max(m, 1.0 - saturate(outside / max(keepOutFeather, 1e-4)));
    }
    return m;
}


// Push a point out of any rect it falls inside: to the nearest edge plus the
// feather width, so evicted spawns are born below the avoidance ramp. A rect
// flush to a canvas edge can make the nearest-edge eviction land off-canvas;
// fall back to the nearer SIDE, which a physical cutout always has.
float2 EvictFromKeepOut(float2 uv) {
    for (int i = 0; i < keepOutCount; i++) {
        float4 r = keepOutRects[i];
        if (uv.x <= r.x || uv.x >= r.z || uv.y <= r.y || uv.y >= r.w) continue;
        float push = keepOutFeather + 1e-3;
        float dl = uv.x - r.x, dr = r.z - uv.x;
        float db = uv.y - r.y, dt = r.w - uv.y;
        float m = min(min(dl, dr), min(db, dt));
        float2 cand = uv;
        if (m == dl)      cand.x = r.x - push;
        else if (m == dr) cand.x = r.z + push;
        else if (m == db) cand.y = r.y - push;
        else              cand.y = r.w + push;
        if (cand.x < 0.0 || cand.x > 1.0 || cand.y < 0.0 || cand.y > 1.0)
            cand = float2(dl < dr ? r.x - push : r.z + push, uv.y);
        uv = saturate(cand);
    }
    return uv;
}

#endif
