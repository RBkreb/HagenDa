using Mirror;
using System.Collections.Generic;
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

        /// <summary>ML 训练：部署者（补给效用奖励归属）。服务器端引用。</summary>
        [System.NonSerialized] public NetworkCombatant ownerCombatant;

        private float acc;
        private HashSet<NetworkCombatant> rewarded = new HashSet<NetworkCombatant>();

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

            bool anyBeneficiary = false;

            foreach (var c in Physics.OverlapSphere(transform.position, radius, MapLayers.EntityQueryMask))
            {
                var health = c.GetComponentInParent<NetworkPlayerHealth>();
                if (health == null || health.IsDead) continue;

                // 补给箱只服务部署者阵营（此前无阵营概念，对敌我同时生效）。
                var beneficiary = health.GetComponent<NetworkCombatant>();
                if (beneficiary != null && ownerCombatant != null &&
                    beneficiary.teamId != ownerCombatant.teamId)
                    continue;

                health.Heal(healthPerTick);
                anyBeneficiary = true;

                var eq = health.GetComponent<NetworkEquipment>();
                if (eq != null)
                    eq.GrantSupply(supplyPerTick);

                // 主武器备弹
                var gun = health.GetComponent<NetworkGun>();
                if (gun != null)
                    gun.AddReserveAmmo(gunReservePerTick);
            }

            // ML 训练：本 tick 有受益者 → 部署者获得一次补给效用奖励（每箱一次）。
            if (anyBeneficiary && ownerCombatant != null && !rewarded.Contains(ownerCombatant))
            {
                rewarded.Add(ownerCombatant);
                RewardBus.SupportUtility(ownerCombatant, RewardBus.SupportKind.Supply);
            }
        }
    }
}
