using UnityEngine;

namespace Echo.AI.Tracker
{
    /// How long the Tracker's body takes to cover a graph route, fitted to the real mover
    /// (ECHO > Tracker > Calibrate Mover) instead of the ideal route / runSpeed:
    ///   ETA = max(0, route - etaArrive) / v  +  etaStartup * (1 - v0 / v)
    /// v  = speed of the gait the Tracker will actually use (walk while confidence is in the Walk band),
    /// v0 = its current speed (a standing start pays the turn-in-place + acceleration once),
    /// etaArrive = how far short of the node the mover reports arrival (its arrive radius).
    /// Turns are not a term: the calibration measured <= 0 s per turn (the waypoint radius cuts corners).
    public static class TrackerMotion
    {
        public static float Eta(float route, float speed, float currentSpeed, TrackerSettings s)
        {
            if (float.IsPositiveInfinity(route)) return route;
            float v = Mathf.Max(0.1f, speed);
            float standing = 1f - Mathf.Clamp01(currentSpeed / v);
            return Mathf.Max(0f, route - s.etaArrive) / v + s.etaStartup * standing;
        }

        /// Speed of the gait a confidence band asks for (the gait the next MoveTo will use).
        public static float GaitSpeed(ConfidenceBand band, TrackerSettings s) =>
            band == ConfidenceBand.Run ? s.runSpeed : s.walkSpeed;
    }
}
