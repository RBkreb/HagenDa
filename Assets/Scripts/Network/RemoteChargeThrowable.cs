using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Remote-detonated charge (信号炸药 / 线控炸药). On impact it sticks in place
    /// WITHOUT exploding; <see cref="NetworkEquipment"/> later calls
    /// <see cref="Trigger"/> to detonate all placed charges (left-click). EMP
    /// vulnerability is enforced at the equipment level, not here.
    /// </summary>
    public class RemoteChargeThrowable : NetworkThrowable
    {
        [Header("Explosion")]
        public float explosionYield = 200f;
        public float explosionRadius = 7f;

        private bool stuck;
        private bool exploded;

        // No fuse detonation: the charge is detonated remotely by NetworkEquipment.
        protected override void Update()
        {
        }

        protected override void OnCollisionEnter(Collision collision)
        {
            if (!isServer || stuck || exploded) return;
            Stick();
        }

        private void Stick()
        {
            stuck = true;
            FreezePhysics();
        }

        /// <summary>Detonate the charge (server). Called by NetworkEquipment.</summary>
        [Server]
        public void Trigger()
        {
            if (!isServer || exploded) return;

            if (!stuck)
                Stick();

            exploded = true;
            ExplosionUtility.ApplyDamage(transform.position, explosionYield, explosionRadius, owner);
            RpcExplosionVisual(transform.position, explosionRadius);

            // Hide + freeze immediately, but DELAY the network destroy: destroying
            // in the same call races the ClientRpc (clients drop RPCs for dead
            // objects), which would swallow the explosion visual.
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

        [ClientRpc]
        private void RpcExplosionVisual(Vector3 pos, float radius)
        {
            ExplosionUtility.SpawnVisual(pos, radius);
        }
    }
}
