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
        public float detectRadius = 0.4f;
        public float regenHpPerSecond = 10f;

        private float activateTime;   // 投出后短暂宽限期，避免出生即被投出者消耗

        public override void OnStartServer()
        {
            base.OnStartServer();

            // 与所有活体无碰撞，只与普通刚体（地面/墙体）碰撞。
            DeployableUtil.IgnoreLivingCollision(gameObject);

            activateTime = Time.time + 0.15f;
        }

        protected override void OnCollisionEnter(Collision collision)
        {
            // 不引爆：补给包停留在地面/墙边，等待活体进入检测半径。
        }

        protected override void Update()
        {
            base.Update();
            if (!isServer) return;
            if (Time.time < activateTime) return;   // 宽限期：先真正投出

            var colliders = Physics.OverlapSphere(transform.position, detectRadius);
            foreach (var c in colliders)
            {
                var health = c.GetComponentInParent<NetworkPlayerHealth>();
                if (health == null || health.IsDead) continue;

                // 忽略投出者自身。
                if (owner != null && health.GetComponent<NetworkIdentity>() == owner)
                    continue;

                var equipment = health.GetComponent<NetworkEquipment>();
                if (equipment != null)
                    equipment.GrantSupply(supplyAmount);

                health.StartBuffRegen(regenHpPerSecond);

                NetworkServer.Destroy(gameObject);
                return;
            }
        }
    }
}
