using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative shared combat component used by BOTH the player and the
    /// AI: hitscan shooting + throwing projectiles. Keeps a single code path for
    /// every attack so player and AI behave identically.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkCombat : NetworkBehaviour
    {
        [Header("Shooting")]
        public float shootRange = 200f;
        public float shootDamage = 20f;
        public float fireRate = 0.15f;

        [Header("Throwing")]
        public Transform throwPoint;                       // optional spawn point (falls back to origin)
        public NetworkThrowable grenadeThrowablePrefab;     // explosion throwable
        public NetworkThrowable smokeThrowablePrefab;       // smoke throwable
        public float throwSpeed = 10f;
        public float throwCooldown = 1f;

        private float nextFireTime;
        private float nextThrowTime;
        private bool throwSmokeNext;   // alternates each throw: false=grenade, true=smoke

        /// <summary>Fire a hitscan shot from origin along forward (server).</summary>
        [Server]
        public void TryFire(Vector3 origin, Vector3 forward)
        {
            if (Time.time < nextFireTime) return;
            nextFireTime = Time.time + fireRate;
            FireOnServer(origin, forward);
        }

        private void FireOnServer(Vector3 origin, Vector3 forward)
        {
            if (Physics.Raycast(origin, forward, out RaycastHit hit, shootRange,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                var damageable = hit.collider.GetComponentInParent<IDamageable>();
                if (damageable != null)
                    damageable.TakeDamage(shootDamage);
            }
        }

        /// <summary>Spawn and launch a throwable from origin along forward (server).
        /// Alternates between grenade and smoke each throw.</summary>
        [Server]
        public void TryThrow(Vector3 origin, Vector3 forward)
        {
            NetworkThrowable prefab = throwSmokeNext ? smokeThrowablePrefab : grenadeThrowablePrefab;
            throwSmokeNext = !throwSmokeNext;   // toggle for next throw
            if (prefab == null) return;
            if (Time.time < nextThrowTime) return;
            nextThrowTime = Time.time + throwCooldown;

            Vector3 spawnPos = throwPoint != null ? throwPoint.position : origin;
            Vector3 dir = forward;
            if (dir.sqrMagnitude < 0.0001f) dir = transform.forward;
            dir.Normalize();

            var go = Instantiate(prefab.gameObject, spawnPos, Quaternion.LookRotation(dir));
            var t = go.GetComponent<NetworkThrowable>();
            if (t != null)
                t.Launch(dir * throwSpeed);

            NetworkServer.Spawn(go);
        }
    }
}
