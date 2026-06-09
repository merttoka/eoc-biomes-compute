// Shared neuron-firing signal. Produced once per step by NeuronFiringSource
// (one float per neuron, already scaled by the global decay envelope) and bound
// per-sim by SimulationBase.BindNeuronFiring(). seedNeuronCount = the same neuron
// count used to seed agent positions, so a firing neuron excites the agents on it.
#ifndef NEURON_FIRING_INCLUDED
#define NEURON_FIRING_INCLUDED

StructuredBuffer<float> neuronFiring;   // length = neuronFiringCount
int   neuronFiringCount;                // 0 when no source wired => no firing
float firingThreshold;                  // per-sim, default 0.1

float NeuronFireValue(uint agentId, uint seedNeuronCount)
{
    if (neuronFiringCount <= 0) return 0.0;
    uint nIdx = (seedNeuronCount > 0) ? (agentId % seedNeuronCount) : agentId;
    return neuronFiring[nIdx % (uint)neuronFiringCount];
}

bool IsFiring(uint agentId, uint seedNeuronCount)
{
    return NeuronFireValue(agentId, seedNeuronCount) >= firingThreshold;
}

#endif
