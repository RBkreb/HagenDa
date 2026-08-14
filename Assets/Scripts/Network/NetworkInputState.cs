using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Serialized input snapshot sent from the owning client to the server each tick.
    ///
    /// The client only sends *intent* (move / look / jump / fire); the server is
    /// authoritative for all physics, movement and shooting. This keeps the uplink
    /// minimal.
    /// </summary>
    [System.Serializable]
    public struct NetworkInputState
    {
        public Vector2 move;   // WASD (x: strafe, y: forward), already normalized
        public float yaw;      // absolute look yaw in degrees (client-authoritative)
        public float pitch;    // absolute look pitch in degrees (client-authoritative)
        public bool jump;      // jump intent (edge-triggered)
        public bool fire;      // fire intent (server-authoritative hitscan)
    }
}
