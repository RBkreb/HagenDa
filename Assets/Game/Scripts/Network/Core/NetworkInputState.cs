using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Serialized input snapshot sent from the owning client to the server each tick.
    ///
    /// The client only sends *intent*; the server is authoritative for all physics,
    /// posture, movement and shooting. This keeps the uplink minimal.
    ///
    /// Edge-triggered inputs (jump / crouchToggle / proneToggle / reload /
    /// switchFireMode / slot keys / mark / deploy) are latched client-side and reset
    /// after being sent, so the server sees each press exactly once.
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
        public bool aim;            // right mouse (held) - aim down sights
        public bool reload;         // r (edge-triggered)
        public bool switchFireMode; // v (edge-triggered)
        public bool mark;           // q key: mark enemy (edge-triggered, PHASE7)

        // PHASE8: 配装槽位键（edge-triggered）
        public bool slotPrimary;    // 1 主武器
        public bool slotOpt1;       // 3 可选配备1
        public bool slotOpt2;       // 4 可选配备2
        public bool slotSpecial;    // g 特有配备
        public bool slotThrowable;  // z 通用投掷物

        public int deployChoice;    // 1=GR / 2=HQ / 3=squad (edge-triggered, PHASE7)
    }
}
