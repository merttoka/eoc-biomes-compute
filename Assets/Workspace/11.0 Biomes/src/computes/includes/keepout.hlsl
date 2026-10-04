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


// Inclusive, matching KeepOutField (which scores a rect's edge as inside).
bool InsideKeepOutRect(float2 uv, float4 r) {
    return uv.x >= r.x && uv.x <= r.z && uv.y >= r.y && uv.y <= r.w;
}

bool InsideAnyKeepOutRect(float2 uv) {
    for (int i = 0; i < keepOutCount; i++)
        if (InsideKeepOutRect(uv, keepOutRects[i])) return true;
    return false;
}

// Push a point out of any rect it falls inside: past the nearest edge plus the
// feather width, so evicted spawns are born below the avoidance ramp. A side is a
// candidate only if its pushed point is on-canvas and outside every rect, so a rect
// flush to a canvas edge (or a corner / full-width band) evicts through a side it
// actually has. If every on-canvas side lands in another rect (abutting rects), take
// the nearest one that at least leaves this rect; that rect evicts it in turn, and
// the second sweep covers rects earlier in the list. If no pushed point is on-canvas
// (feather wider than every gap to the border), clamp to the border through the nearest
// side the rect doesn't touch: inside the feather ramp, but out of the hole.
// An evicted point stays below u/v = 1 so the caller's * rez never lands on pixel rez.
float2 EvictFromKeepOut(float2 uv) {
    float push = keepOutFeather + 1e-3;
    bool evicted = false;
    for (int sweep = 0; sweep < 2; sweep++) {
        for (int i = 0; i < keepOutCount; i++) {
            float4 r = keepOutRects[i];
            if (!InsideKeepOutRect(uv, r)) continue;
            // left, right, bottom, top — ties keep this order
            float2 cand[4] = { float2(r.x - push, uv.y), float2(r.z + push, uv.y),
                               float2(uv.x, r.y - push), float2(uv.x, r.w + push) };
            float  dist[4] = { uv.x - r.x, r.z - uv.x, uv.y - r.y, r.w - uv.y };
            float best = 1e9, bestHop = 1e9, bestClamp = 1e9;
            float2 pick = uv, hop = uv, clamped = uv;
            for (int k = 0; k < 4; k++) {
                bool onCanvas = cand[k].x >= 0.0 && cand[k].x <= 1.0
                             && cand[k].y >= 0.0 && cand[k].y <= 1.0;
                if (!onCanvas) {
                    float2 c = saturate(cand[k]);
                    if (dist[k] < bestClamp && !InsideKeepOutRect(c, r)) { bestClamp = dist[k]; clamped = c; }
                    continue;
                }
                if (dist[k] < best && !InsideAnyKeepOutRect(cand[k])) { best = dist[k]; pick = cand[k]; }
                if (dist[k] < bestHop) { bestHop = dist[k]; hop = cand[k]; }
            }
            if (best >= 1e9) pick = bestHop < 1e9 ? hop : clamped;   // no free side: hop into the neighbour (evicted next), else the border
            evicted = evicted || bestHop < 1e9 || bestClamp < 1e9;
            uv = pick;   // a rect covering the whole canvas has no exit; the point stays
        }
    }
    return evicted ? min(uv, 1.0 - 1e-4) : uv;
}

#endif
