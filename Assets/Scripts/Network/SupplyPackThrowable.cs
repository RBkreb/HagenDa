using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Small supply pack (小型补给包). Thrown like a grenade, passes through living
    /// bodies (physics ignores them) and only rests against world rigidbodies
    /// (ground / walls). Each server tick it overlaps for a living entity; the first
    /// one touched instantly gains <see cref="supplyAmount"/> supply (to its selected
    /// equipment) and enters a temporary health-regen state, then the pack is spent.
    /// </summary>
    public class SupplyPackThrowable : NetworkThrowable
    {
        [Header("Supply")]
        public int supplyAmount = 80;
        public int gunReserveAmount = 30;  // 主武器备弹补充量
        public float detectRadius = 0.4f;
        public float regenHpPerSecond = 10f;

        private float activateTime;   // 投出后延迟，1s 后才可被触碰使用

        public override void OnStartServer()
        {
            base.OnStartServer();

            DeployableUtil.IgnoreLivingCollision(gameObject);

            activateTime = Time.time + 1f;
        }

        protected override void OnCollisionEnter(Collision collision)
        {
            FreezePhysics();
        }

        protected override void Update()
        {
            base.Update();
            if (!isServer) return;
            if (Time.time < activateTime) return;   // 1s 延迟

            var colliders = Physics.OverlapSphere(transform.position, detectRadius);
            foreach (var c in colliders)
            {
                var health = c.GetComponentInParent<NetworkPlayerHealth>();
                if (health == null || health.IsDead) continue;

                var equipment = health.GetComponent<NetworkEquipment>();
                if (equipment != null)
                    equipment.GrantSupply(supplyAmount);

                // 主武器备弹
                var gun = health.GetComponent<NetworkGun>();
                if (gun != null)
                    gun.AddReserveAmmo(gunReserveAmount);

                health.StartBuffRegen(regenHpPerSecond);

                NetworkServer.Destroy(gameObject);
                return;
            }
        }
    }
}
