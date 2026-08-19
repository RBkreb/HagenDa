using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Large supply crate (大型补给箱, PHASE6). Placed on the ground (falls and
    /// rests), it heals +25 HP and grants +25 supply every <see cref="interval"/>
    /// seconds to every living entity within <see cref="radius"/>. It does not
    /// collide with living bodies.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class LargeSupplyCrate : NetworkBehaviour
    {
        [Header("Supply")]
        public float radius = 5f;
        public float interval = 5f;
        public int supplyPerTick = 25;
        public float healthPerTick = 25f;
        public int gunReservePerTick = 60;  // 主武器备弹补充量

        private float acc;

        public override void OnStartServer()
        {
            DeployableUtil.IgnoreLivingCollision(gameObject);
        }

        private void OnCollisionEnter(Collision collision)
        {
            // 接触地面后立刻静止，去除物理防止漂移。
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
        }

        private void Update()
        {
            if (!isServer) return;

            acc += Time.deltaTime;
            if (acc < interval) return;
            acc = 0f;

            foreach (var c in Physics.OverlapSphere(transform.position, radius))
            {
                var health = c.GetComponentInParent<NetworkPlayerHealth>();
                if (health == null || health.IsDead) continue;

                health.Heal(healthPerTick);

                var eq = health.GetComponent<NetworkEquipment>();
                if (eq != null)
                    eq.GrantSupply(supplyPerTick);

                // 主武器备弹
                var gun = health.GetComponent<NetworkGun>();
                if (gun != null)
                    gun.AddReserveAmmo(gunReservePerTick);
            }
        }
    }
}
