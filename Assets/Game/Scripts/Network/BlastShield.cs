using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Blast shield (防爆盾, PHASE6). A special cover panel fixed in front of the
    /// player that follows the view like a gun. It blocks bullets (its collider is
    /// solid geometry) but, because it carries <see cref="SpecialCover"/>, explosions'
    /// line-of-sight rays pass through it. The 60% explosion reduction itself is
    /// applied via NetworkPlayerHealth.explosionDamageMultiplier by NetworkEquipment.
    /// </summary>
    public class BlastShield : MonoBehaviour
    {
        /// <summary>Create the shield panel as a child of <paramref name="parent"/>.</summary>
        public static BlastShield Attach(Transform parent)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "BlastShield";
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, 0.7f);
            go.transform.localScale = new Vector3(0.9f, 1.3f, 0.15f);

            // Replace the primitive's default collider with a clean box matching the
            // scaled cube (1x1x1 local) so the shield reliably blocks bullets.
            Object.DestroyImmediate(go.GetComponent<Collider>());
            var box = go.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = Vector3.one;

            // 特殊掩体: explosions pass through, bullets do not.
            go.AddComponent<SpecialCover>();

            return go.AddComponent<BlastShield>();
        }
    }
}
