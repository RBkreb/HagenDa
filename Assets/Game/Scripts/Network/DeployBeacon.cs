using Mirror;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking
{
    /// <summary>
    /// 部署信标 (可选配备). A yellow inverted cone placed on the ground; after it
    /// lands it freezes in place (physics disabled). It acts as a redeploy point for
    /// the OWNER'S SQUAD ONLY (deploy screen choice 4):
    ///
    ///  - carry 1, deploy cap 1 per entity, supply cost 150,
    ///  - 5 redeployments; the counter decrements per squad member spawning here,
    ///    and the beacon destroys itself when exhausted,
    ///  - destroyed outright by EMP (IEmpTarget),
    ///  - persists through the owner's redeploy (unlike crates/sensors) so the
    ///    squad can keep spawning on it while the owner is dead,
    ///  - does not collide with living bodies.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class DeployBeacon : NetworkBehaviour, IEmpTarget
    {
        [Header("Beacon")]
        [Tooltip("总重部署次数，用尽后信标自毁。")]
        public int maxUses = 5;

        [SyncVar] public int ownerTeam = -1;
        [SyncVar] public int ownerSquad = -1;
        [SyncVar] public int usesRemaining;

        public override void OnStartServer()
        {
            usesRemaining = maxUses;
            DeployableUtil.IgnoreLivingCollision(gameObject);
        }

        /// <summary>Server: set the team + squad that placed this beacon (from the deployer).</summary>
        public void SetOwner(int team, int squad)
        {
            ownerTeam = team;
            ownerSquad = squad;
        }

        /// <summary>ML 训练：记录部署者引用（信标重生效用奖励归属）。</summary>
        [System.NonSerialized] public NetworkCombatant ownerCombatant;

        private void OnCollisionEnter(Collision collision)
        {
            // 接触地面后立刻静止并去除物理，固定在落点（倒圆锥立在尖端）。
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
        }

        private void Start()
        {
            // 大地图/世界标记（与 GR"G"/HQ 字母一致的纯色圆盘标记）。
            var marker = gameObject.AddComponent<MapPointMarker>();
            marker.Init("B", 4f, new Color(0.95f, 0.8f, 0.15f));
        }

        /// <summary>Only the deployer's own squad may spawn here.</summary>
        public bool MatchesSquad(int team, int squad)
        {
            return team >= 0 && squad >= 0 && team == ownerTeam && squad == ownerSquad;
        }

        /// <summary>
        /// Server: consume one redeployment and resolve a clear spawn position near
        /// the beacon (2m 内无遮挡，最多尝试 10 次，失败则退回信标位置).
        /// Destroys the beacon when the last use is consumed.
        /// </summary>
        [Server]
        public Vector3? ConsumeDeployPoint()
        {
            if (usesRemaining <= 0) return null;

            for (int i = 0; i < 10; i++)
            {
                Vector2 o = Random.insideUnitCircle * 2f;
                Vector3 p = transform.position + new Vector3(o.x, 0f, o.y);

                if (NavMesh.SamplePosition(p, out NavMeshHit hit, 1f, NavMesh.AllAreas))
                    p = hit.position;

                if (!Physics.CheckSphere(p + Vector3.up * 0.9f, 0.5f,
                                         Physics.DefaultRaycastLayers,
                                         QueryTriggerInteraction.Ignore))
                {
                    usesRemaining--;
                    RewardBus.BeaconRespawn(ownerCombatant);
                    if (usesRemaining <= 0)
                        NetworkServer.Destroy(gameObject);
                    return p;
                }
            }

            // No clear spot found: spawn at the beacon itself (collisions with
            // living bodies are ignored anyway).
            usesRemaining--;
            RewardBus.BeaconRespawn(ownerCombatant);
            if (usesRemaining <= 0)
                NetworkServer.Destroy(gameObject);
            return transform.position;
        }

        /// <summary>EMP destroys the beacon outright.</summary>
        public void ApplyEmp(float duration)
        {
            if (isServer)
                NetworkServer.Destroy(gameObject);
        }
    }
}
