using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative shared combat component used by BOTH the player and the
    /// AI: projectile shooting + throwing projectiles. Keeps a single code path for
    /// every attack so player and AI behave identically.
    ///
    /// Shooting (PHASE4): fires pooled 750 m/s projectiles with server-side segment
    /// hit detection and penetration. Throwing cycles grenade -> smoke -> rescue.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkCombat : NetworkBehaviour
    {
        [Header("Shooting")]
        public float shootDamage = 20f;
        public float fireRate = 0.15f;
        public float bulletSpeed = 750f;
        public float bulletLifetime = 5f;
        public int bulletPoolCapacity = 60;
        public GameObject bulletPrefab;

        [Header("Throwing")]
        public Transform throwPoint;                       // optional spawn point (falls back to origin)
        public NetworkThrowable grenadeThrowablePrefab;     // explosion throwable
        public NetworkThrowable smokeThrowablePrefab;       // smoke throwable
        public NetworkThrowable rescueThrowablePrefab;      // rescue throwable
        public float throwSpeed = 10f;
        public float throwCooldown = 1f;

        private float nextFireTime;
        private float nextThrowTime;
        private int throwIndex;    // cycles grenade -> smoke -> rescue
        private BulletPool bulletPool;

        public override void OnStartServer()
        {
            if (bulletPrefab != null)
                bulletPool = new BulletPool(bulletPrefab, bulletPoolCapacity, transform);
        }

        /// <summary>Fire a pooled projectile from origin along forward (server).</summary>
        [Server]
        public void TryFire(Vector3 origin, Vector3 forward)
        {
            if (Time.time < nextFireTime) return;
            nextFireTime = Time.time + fireRate;
            FireOnServer(origin, forward);
        }

        private void FireOnServer(Vector3 origin, Vector3 forward)
        {
            if (bulletPool == null) return;

            var bullet = bulletPool.Get();
            if (bullet == null) return;   // pool exhausted

            bullet.Fire(origin, forward, bulletSpeed, shootDamage, bulletLifetime, this);
        }

        /// <summary>Spawn and launch a throwable from origin along forward (server).
        /// Cycles grenade -> smoke -> rescue each throw.</summary>
        [Server]
        public void TryThrow(Vector3 origin, Vector3 forward)
        {
            NetworkThrowable prefab = NextThrowable();
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

            // The throwable spawns at eye level INSIDE the thrower's capsule. Any
            // contact with the thrower's own colliders would deflect it off the aim
            // direction, so disable collision between the projectile and the thrower.
            var projectileColliders = go.GetComponentsInChildren<Collider>(true);
            foreach (var ownCollider in GetComponentsInChildren<Collider>(true))
            {
                foreach (var projectileCollider in projectileColliders)
                    Physics.IgnoreCollision(projectileCollider, ownCollider, true);
            }

            NetworkServer.Spawn(go);
        }

        private NetworkThrowable NextThrowable()
        {
            NetworkThrowable[] all = { grenadeThrowablePrefab, smokeThrowablePrefab, rescueThrowablePrefab };
            for (int i = 0; i < all.Length; i++)
            {
                int idx = throwIndex % all.Length;
                throwIndex++;
                if (all[idx] != null) return all[idx];
            }
            return null;
        }

        /// <summary>Server callback from a bullet when it hits a living entity. Shows
        /// the shooter's hitmarker (player only; AI has no owner connection).</summary>
        [Server]
        public void NotifyHit()
        {
            if (connectionToClient == null) return;
            TargetRpcHitmarker();
        }

        [TargetRpc]
        private void TargetRpcHitmarker()
        {
            var hud = GetComponent<PlayerHud>();
            if (hud != null) hud.ShowHitmarker();
        }

        /// <summary>Server callback from a bullet on ANY hit (entity or cover).
        /// Broadcasts a small impact-smoke puff at the hit point to all clients.</summary>
        [Server]
        public void NotifyImpact(Vector3 pos)
        {
            RpcImpactSmoke(pos);
        }

        [ClientRpc]
        private void RpcImpactSmoke(Vector3 pos)
        {
            // Small, quick smoke puff as hit feedback. Reuses the smoke-cloud visual
            // (transparent double-sided sphere that fades out) with a tiny radius and
            // short decay, so cover hits are visible without a long tracer line.
            NetworkSmoke.Spawn(pos, 0.6f, 0.3f, 0.4f);
        }
    }
}
