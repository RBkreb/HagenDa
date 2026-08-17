using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Serialized input snapshot sent from the owning client to the server each tick.
    ///
    /// The client only sends *intent*; the server is authoritative for all physics,
    /// posture, movement and shooting. This keeps the uplink minimal.
    ///
    /// Edge-triggered inputs (jump / crouchToggle / proneToggle) are latched client-side
    /// and reset after being sent, so the server sees each press exactly once.
    /// </summary>
    [System.Serializable]
    public struct NetworkInputState
    {
        public Vector2 move;        // WASD (x: strafe, y: forward), already normalized
        public float yaw;           // absolute look yaw in degrees (client-authoritative)
        public float pitch;         // absolute look pitch in degrees (client-authoritative)
        public bool jump;           // space (edge-triggered)
        public bool sprint;         // left shift (held)
        public bool crouchToggle;   // x (edge-triggered)
        public bool proneToggle;    // c (edge-triggered)
        public bool crouchHold;     // left ctrl (held)
        public bool fire;           // left mouse (held)
        public bool throwGrenade;   // g (edge-triggered)
    }
}
