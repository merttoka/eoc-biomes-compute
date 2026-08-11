// Shared plumbing for field-native (agent-less) cellular rules driven by
// FieldSimulationBase. Both CA computes declare their rule params themselves and get
// everything below for free: the double-buffered state pair, toroidal neighbour reads,
// the CA-CA coupling gate, and neuron ignition.
//
// SINGLE DEFINITION of the state binding. The C# base owns stateRead/stateWrite and
// mandates the ping-pong precisely because the ported CA2D bound ONE texture as both read
// and write — an in-place update where threads read neighbours other threads had already
// overwritten. Declaring the pair here, once, means a rule cannot express that race even
// by accident: stateRead is read-only to the shader and stateWrite is write-only.
#ifndef CELLULAR_COMMON_INCLUDED
#define CELLULAR_COMMON_INCLUDED

// --- State (double-buffered; swapped CPU-side by FieldSimulationBase.SwapState) ---
Texture2D<float>   stateRead;    // previous step, read-only
RWTexture2D<float> stateWrite;   // this step, write-only

// --- Grid ---
uint cellRezX;
uint cellRezY;
uint nstates;     // discrete states the rule cycles through
uint time;        // WRAPPED SIM STEP, never Time.frameCount — consecutive catch-up steps
                  // inside one rendered frame must get distinct seeds or a multi-step frame
                  // is not deterministic under Recorder's driven clock.

// --- CA-CA coupling: one automaton gates the other -------------------------------
// The compelling configuration from the design: the cyclic CA only advances where the
// lookup CA is alive, so excitation waves propagate THROUGH the standing lattice and
// structure/flow become one coupled system rather than two stacked layers.
Texture2D<float> couplingState;
SamplerState sampler_point_clamp;   // point: an "alive" test wants the cell, not a blend
int   couplingEnabled;
float couplingStrength;    // 0 = ignore the gate entirely, 1 = fully gated
float couplingThreshold;   // normalized state at/above which the source counts as alive
int   couplingNStates;     // the SOURCE's state count (it may differ from ours)
int   couplingInvert;      // advance where the source is DEAD instead

// --- Neuron ignition --------------------------------------------------------------
StructuredBuffer<float2> neuronPositions;   // pre-multiplied by the SIM resolution
uint   neuronCount;
float2 neuronScale;
float2 neuronSrcRez;       // the resolution neuronPositions were built against
float  ignitionStrength;
float  ignitionRadius;     // cells

// --- Centre keep-out (physical screen cutout) -------------------------------------
float centreKeepOut;        // normalized width of the biased band; 0 = off
float centreKeepOutDepth;   // how much is removed at dead centre

#include "neuron_layout.hlsl"
#include "neuron_firing.hlsl"

// Toroidal neighbour read. The rule wraps rather than clamping so patterns do not pin to
// the frame edge — on an 11.84:1 canvas a clamped edge reads as a seam across the whole
// width. Matches the Repeat wrap mode the state textures are created with.
float ReadStateWrapped(int2 c)
{
    int w = (int)cellRezX;
    int h = (int)cellRezY;

    // Single-step wrap rather than the textbook ((c % n) + n) % n. Neighbour offsets are
    // bounded by the rule's radius, so a coordinate can be at most one grid-width out and
    // one conditional add or subtract always lands it back in range. Metal flagged the
    // modulus form as slow, and this is the hottest line in the project: a Moore
    // neighbourhood calls it (2r+1)^2 times per cell per step.
    c.x = c.x < 0 ? c.x + w : (c.x >= w ? c.x - w : c.x);
    c.y = c.y < 0 ? c.y + h : (c.y >= h ? c.y - h : c.y);

    // Guard the degenerate case where the radius exceeds the grid (tiny grid, large range),
    // which is the only way one wrap step is not enough. Clamping there beats reading
    // out of bounds.
    c = clamp(c, int2(0, 0), int2(w - 1, h - 1));
    return stateRead[uint2(c)];
}

// Integer state, rounded on read. The authoritative state is kept in the CA's own RFloat
// texture and rounded here, so accumulated float error can never drift a cell into the
// wrong rule branch.
uint ReadCellState(int2 c)
{
    return (uint)round(ReadStateWrapped(c));
}

/// 1 where the rule may advance, 0 where the coupling CA freezes it.
float CouplingGate(uint2 id)
{
    if (couplingEnabled == 0) return 1.0;

    // Sampled by UV: the coupling source is a separate sim, sized by its own cellRezHeight,
    // so the two grids are not guaranteed to share dimensions.
    float2 uv = (float2(id) + 0.5) / float2((float)cellRezX, (float)cellRezY);
    float raw = couplingState.SampleLevel(sampler_point_clamp, uv, 0);
    float norm = raw / max(1.0, (float)couplingNStates);

    float alive = step(couplingThreshold, norm);
    if (couplingInvert != 0) alive = 1.0 - alive;

    // Lerp rather than branch so the coupling can be faded in over a show arc.
    return lerp(1.0, alive, saturate(couplingStrength));
}

/// Attenuation biasing the CA's visible contribution out of the middle of the frame, so the
/// composition survives a central cutout in the physical display.
///
/// Applied to the RENDER, not the rule: the automaton keeps evolving across the whole grid
/// (a gap in the rule would leave a seam the waves never cross), only its contribution to the
/// composite is faded. Smoothstepped and floored rather than cut, because the same master may
/// also play on a screen with no cutout, which would show a hard-edged band as a defect.
float CentreKeepOutWeight(uint2 id)
{
    if (centreKeepOut <= 0.0) return 1.0;
    float u = (float(id.x) + 0.5) / max(1.0, (float)cellRezX);
    float d = abs(u - 0.5) * 2.0;                       // 0 at centre, 1 at either edge
    float w = smoothstep(0.0, 1.0, saturate(d / max(1e-4, centreKeepOut)));
    return lerp(1.0 - saturate(centreKeepOutDepth), 1.0, w);
}

/// 0..1 ignition energy at this cell from currently-firing neurons. A firing organoid
/// neuron plants excitation at its canvas position, tying the CA to the same playback
/// clock as the diurnal sun and the dispersal pulses.
///
/// COST: this loops every neuron per cell. It is off by default (ignitionStrength 0) and
/// when on it is amortised by the two levers field sims already have — cellRezHeight being
/// low relative to master (quadratic) and stepEvery (linear). Both cut cost: lower height or
/// higher stepEvery. Do not enable it at full cell resolution on the ultra-wide master.
float NeuronIgnition(uint2 id)
{
    if (ignitionStrength <= 0.0 || neuronFiringCount <= 0 || neuronCount == 0) return 0.0;

    float2 px = float2(id) + 0.5;
    float2 cellRez = float2((float)cellRezX, (float)cellRezY);
    float2 srcRez = max(neuronSrcRez, float2(1.0, 1.0));
    float best = 0.0;

    for (uint i = 0; i < neuronCount; i++)
    {
        float f = neuronFiring[i % (uint)neuronFiringCount];
        if (f < firingThreshold) continue;

        // Same shared mapping every sim and the ring overlay use, then rescaled from sim
        // space into cell space — the CA grid is a fraction of the sim resolution.
        float2 pSim = NeuronPxToFieldPx(neuronPositions[i], neuronScale, srcRez);
        float2 pCell = pSim * (cellRez / srcRez);

        float d = length(px - pCell);
        if (d > ignitionRadius) continue;
        best = max(best, f * saturate(1.0 - d / max(ignitionRadius, 1e-3)));
    }
    return saturate(best * ignitionStrength);
}

#endif
