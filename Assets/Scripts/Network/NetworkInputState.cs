using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Serialized input snapshot sent from the owning client to the server each tick.
    /// This mirrors the FPS Engine's InputManager public fields (X, Y, Mousex, Mousey,
    /// Jumping, Sprinting, Crouching, Shooting) but as a compact networked struct.
    /// </summary>
    [System.Serializable]
    public struct NetworkInputState
    {
        public Vector2 move;       // WASD (x: strafe, y: forward)
        public float yaw;          // absolute look yaw in degrees (client-authoritative)
        public float pitch;        // absolute look pitch in degrees (client-authoritative)
        public bool jump;
        public bool sprint;
        public bool crouch;
        public bool fire;
        public bool aim;
    }
}
