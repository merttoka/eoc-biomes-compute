// Shared neuron-layout mapping. `neuronScale` is how much of the canvas the neuron
// layout fills (0-1); the layout is scaled about the canvas centre, so a scale of
// (0.5, 0.6) places the neuron cloud in the middle 50% x 60% of the field.
//
// SINGLE SOURCE OF TRUTH: the scale value is owned by NeuronFiringSource.spawnScale
// and pushed to every consumer. Do NOT re-declare it. These functions are the only
// definition of the mapping — mirrored on the CPU by NeuronFiringSource.NeuronToFieldUV
// / NeuronToFieldPixels, which must stay in agreement (asserted by NeuronLayoutTests).
//
// Two entry points because callers hold positions in different spaces: the composite
// ring overlay keeps normalized CSV positions, while the sims pre-multiply by rez when
// they upload (SimulationBase.BuildNeuronPositions).
#ifndef NEURON_LAYOUT_INCLUDED
#define NEURON_LAYOUT_INCLUDED

// Normalized neuron position (0..1) -> normalized field UV (0..1).
float2 NeuronToFieldUV(float2 npNorm, float2 neuronScale)
{
    return npNorm * neuronScale + (1.0 - neuronScale) * 0.5;
}

// Pixel-space neuron position -> pixel-space field position.
// Algebraically NeuronToFieldUV(npPx / rez, neuronScale) * rez, without the round trip.
float2 NeuronPxToFieldPx(float2 npPx, float2 neuronScale, float2 rez)
{
    return npPx * neuronScale + rez * (1.0 - neuronScale) * 0.5;
}

#endif
