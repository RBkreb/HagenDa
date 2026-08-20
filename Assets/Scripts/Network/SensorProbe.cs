using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// 感应器 (PHASE8 特有配备). Like the large supply crate it is placed on the
    /// ground (falls and rests). Every <see cref="interval"/> seconds it marks every
    /// ENEMY (hostile team) within <see cref="radius"/> for <see cref="markDuration"/>
    /// seconds — the same 10s-style mark used by the Q key, so marked enemies appear
    /// on the minimap/big map and show a head marker.
    ///
    ///  - carry 1, regenerates 1 every 30s (ammoRegenInterval),
    ///  - can be destroyed outright by EMP (IEmpTarget),
    ///  - does not collide with living bodies.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class SensorProbe : NetworkBehaviour, IEmpTarget
    {
        [Header("Sensor")]
        public float radius = 20f;
        public float interval = 5f;
        public float markDuration = 3f;

        private float acc;
        private int ownerTeam = -1;

        public override void OnStartServer()
        {
            DeployableUtil.IgnoreLivingCollision(gameObject);
        }

        /// <summary>Server: set the team that placed this sensor (from the thrower).</summary>
        public void SetOwnerTeam(int team)
        {
            ownerTeam = team;
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

            var pos = transform.position;
            float r2 = radius * radius;

            foreach (var c in Object.FindObjectsOfType<NetworkCombatant>())
            {
                if (c == null || c.IsDead) continue;
                if (c.teamId < 0 || ownerTeam < 0) continue;
                if (c.teamId == ownerTeam) continue;   // 只标记敌方

                if ((c.transform.position - pos).sqrMagnitude <= r2)
                    c.SetMarked(NetworkTime.time + markDuration);
            }
        }

        /// <summary>EMP destroys the sensor outright.</summary>
        public void ApplyEmp(float duration)
        {
            if (isServer)
                NetworkServer.Destroy(gameObject);
        }
    }
}
