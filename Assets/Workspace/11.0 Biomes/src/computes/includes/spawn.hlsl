// Alternative agent spawn modes (SimulationBase.SpawnMode). Mode 0 keeps each
// sim's legacy inline logic (neuron positions, scatter fallback); modes 1/2 are
// shared here. Requires random.hlsl (Random2, Hash1u), keepout.hlsl
// (KeepOutField), and rezX/rezY — include after all three.
#ifndef SPAWN_INCLUDED
#define SPAWN_INCLUDED

int spawnMode;             // 0 = neuron positions (legacy), 1 = bottom edge, 2 = scatter, 3 = top edge
float spawnBandFraction;   // mode 1: band height as a fraction of canvas height

// Spawn position for modes 1/2: uniform x, band-limited y (mode 1), resampled a
// few times to land clear of any keep-out rect (a bottom hole splits the floor
// into the side segments, as on cutout installs).
float2 AltSpawnPosition(uint id, uint time) {
    float2 uv = float2(0.5, 0.5);
    for (uint t = 0; t < 8; t++) {
        float2 c = Random2(id * .0001 + time * .001 + t * .1337);
        uv = (spawnMode == 1) ? float2(c.x, c.y * spawnBandFraction)
           : (spawnMode == 3) ? float2(c.x, 1.0 - c.y * spawnBandFraction)
           : c;
        if (KeepOutField(uv) < 0.5) break;
    }
    return uv * float2((float)rezX, (float)rezY);
}

// Heading angle from a 0..1 seed: bottom-edge spawns launch into an upward cone
// (+/- ~35 deg) so growth rises off the floor line; other modes keep the full circle.
float SpawnAngle(float seed01) {
    if (spawnMode == 1) return  1.5707963 + (seed01 - 0.5) * 1.2;   // up cone
    if (spawnMode == 3) return -1.5707963 + (seed01 - 0.5) * 1.2;   // down cone
    return seed01 * 6.2831853;
}

#endif
