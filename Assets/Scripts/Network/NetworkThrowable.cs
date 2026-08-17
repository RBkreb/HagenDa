using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative throwable projectile: a rigidbody sphere following a
    /// physics parabola. Subclass and override <see cref="OnImpact"/> for grenade /
    /// smoke variants. Reusable by both the player and the AI (via NetworkCombat).
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(Rigidbody))]
    public class NetworkThrowable : NetworkBehaviour
    {
        [Header("Throw")]
        [Tooltip("Seconds before detonation. >0 = fuse; <=0 = impact only.")]
        public float fuseTime = 2f;

        protected Rigidbody rb;
        private bool detonated;
        private float detonateTime;

        protected void Awake()
        {
            rb = GetComponent<Rigidbody>();
        }

        public override void OnStartServer()
        {
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            if (fuseTime > 0f)
                detonateTime = Time.time + fuseTime;
        }

        public override void OnStartClient()
        {
            if (!isServer)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
            }
        }

        /// <summary>Set the initial launch velocity (server).</summary>
        public void Launch(Vector3 velocity)
        {
            if (rb != null)
                rb.velocity = velocity;
        }

        private void Update()
        {
            if (!isServer || detonated) return;
            if (fuseTime > 0f && Time.time >= detonateTime)
                Detonate();
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!isServer || detonated) return;
            if (fuseTime <= 0f)
                Detonate();
        }

        private void Detonate()
        {
            detonated = true;
            OnImpact();

            // Hide the body and freeze physics immediately, but DELAY the actual
            // network destroy: destroying in the same call can race the ClientRpc
            // sent inside OnImpact (clients drop RPCs for dead objects), and the
            // body vanishing with zero linger time looks abrupt.
            var col = GetComponentInChildren<Collider>();
            if (col != null) col.enabled = false;

            var rend = GetComponentInChildren<Renderer>();
            if (rend != null) rend.enabled = false;

            if (rb != null) rb.isKinematic = true;

            Invoke(nameof(DestroyOnServer), 0.5f);
        }

        [Server]
        private void DestroyOnServer()
        {
            NetworkServer.Destroy(gameObject);
        }

        /// <summary>Called on the server when the throwable detonates.</summary>
        protected virtual void OnImpact()
        {
        }
    }
}
