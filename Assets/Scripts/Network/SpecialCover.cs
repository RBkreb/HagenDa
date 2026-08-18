using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Marker for special cover (PHASE6 特殊掩体): explosions' line-of-sight ray
    /// passes THROUGH this geometry (entities behind it still take explosion
    /// damage), while bullets still stop against it. Also used by the blast shield
    /// so its body counts as explosion-penetrable cover that still blocks bullets.
    /// </summary>
    public class SpecialCover : MonoBehaviour
    {
    }
}
