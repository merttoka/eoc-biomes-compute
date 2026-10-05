// Toroidal wrap of grid coordinates (field, trail and agent space are all a torus). Exact for
// coordinates at most one grid size off the grid — p in [-rez, 2rez) per axis — which every
// caller is (3x3 taps, the 5x5 tensor window, rounded positions); cheaper than the integer
// modulo ((p + rez) % rez) it stands for, which costs a divide per component.
#ifndef TORUS_INCLUDED
#define TORUS_INCLUDED

int2 WrapTorus(int2 p, int2 rez)
{
    return p + rez * ((int2)(p < 0) - (int2)(p >= rez));
}

uint2 WrapTorus(uint2 p, uint2 rez)
{
    return p - rez * (uint2)(p >= rez);
}

#endif
