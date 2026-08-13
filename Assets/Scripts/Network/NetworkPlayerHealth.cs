using System.Collections;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative health for the networked player.
    /// The server owns <see cref="health"/> (a SyncVar); clients only observe it.
    /// </summary>
    public class NetworkPlayerHealth : NetworkBehaviour
    {
        [Header("Health")]
        public float maxHealth = 100f;
        public float respawnDelay = 3f;

        [SyncVar(hook = nameof(OnHealthChanged))]
        public float health;

        public bool IsDead => health <= 0f;

        public override void OnStartServer()
        {
            health = maxHealth;
        }

        /// <summary>
        /// Apply damage on the server. Called by server-authoritative hit detection.
        /// </summary>
        [Server]
        public void TakeDamage(float damage)
        {
            if (health <= 0f) return;

            health = Mathf.Max(0f, health - Mathf.Abs(damage));
            if (health <= 0f) Die();
        }

        [Server]
        public void Die()
        {
            RpcDie();
            StartCoroutine(RespawnAfterDelay());
        }

        private IEnumerator RespawnAfterDelay()
        {
            yield return new WaitForSeconds(respawnDelay);
            health = maxHealth;
            RpcRespawn();
        }

        [ClientRpc]
        private void RpcDie()
        {
            // Local death feedback hook (VFX/sound) goes here.
        }

        [ClientRpc]
        private void RpcRespawn()
        {
            // Local respawn feedback hook goes here.
        }

        private void OnHealthChanged(float oldValue, float newValue)
        {
            // Optional: update owner-side health UI here.
        }
    }
}
