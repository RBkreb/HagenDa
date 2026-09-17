using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative throwing component shared by the player and the AI
    /// (PHASE3). Shooting moved to <see cref="NetworkGun"/> in PHASE5; this component
    /// now owns only projectile throwing (grenade -> smoke -> rescue cycling).
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkCombat : NetworkBehaviour
    {
        [Header("Throwing")]
        public Transform throwPoint;                       // optional spawn point (falls back to origin)
        public NetworkThrowable grenadeThrowablePrefab;     // explosion throwable
        public NetworkThrowable smokeThrowablePrefab;       // smoke throwable
        public NetworkThrowable rescueThrowablePrefab;      // rescue throwable
        public float throwSpeed = 10f;
        public float throwCooldown = 1f;

        private float nextThrowTime;
        private int throwIndex;    // cycles grenade -> smoke -> rescue

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
    }
}
