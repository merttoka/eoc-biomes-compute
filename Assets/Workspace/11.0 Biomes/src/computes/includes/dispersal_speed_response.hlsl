// Shared dispersal speed response for all sims. Uniforms are set from <Sim>.cs via
// SimulationBase.BindDispersalSpeedParams(). The dispersal magnitude comes from
// perception.a (UmweltEffect.SpeedBoost). Pass whichever speed scalar the sim uses
// (effectiveSpeed for trail sims, effectiveMaxSpeed for boids).
#ifndef DISPERSAL_SPEED_RESPONSE_INCLUDED
#define DISPERSAL_SPEED_RESPONSE_INCLUDED

int   dispersalSpeedMode;       // 0 = multiplier, 1 = constant flee speed
float dispersalSpeedMult;       // multiplier-mode gain
float dispersalConstantSpeed;   // constant-mode target flee speed

// Mode 1 = constant: snap toward a fixed flee speed (fast even when base speed is tiny).
// Mode 0 = multiplier: scale the current speed up with local dispersal magnitude.
void ApplyDispersalSpeedResponse(inout float speed, float disp) {
    if (dispersalSpeedMode == 1) {
        speed = lerp(speed, dispersalConstantSpeed, saturate(disp));
    } else {
        speed *= (1.0 + disp * dispersalSpeedMult);
    }
}

#endif
