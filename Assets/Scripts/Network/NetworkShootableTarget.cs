using System.Collections;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// A static networked target the server can damage, for verifying
    /// server-authoritative hitscan shooting.
    /// </summary>
    public class NetworkShootableTarget : NetworkBehaviour
    {
        public float maxHealth = 100f;
        public float respawnDelay = 3f;

        [SyncVar(hook = nameof(OnHealthChanged))]
        public float health;

        public override void OnStartServer()
        {
            health = maxHealth;
        }

        [Server]
        public void TakeDamage(float damage)
        {
            if (health <= 0f) return;

            health = Mathf.Max(0f, health - Mathf.Abs(damage));
            if (health <= 0f) StartCoroutine(RespawnAfterDelay());
        }

        private IEnumerator RespawnAfterDelay()
        {
            yield return new WaitForSeconds(respawnDelay);
            health = maxHealth;
        }

        private void OnHealthChanged(float oldValue, float newValue)
        {
            // Optional: tint the target based on remaining health.
            var renderer = GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                float t = newValue / maxHealth;
                renderer.material.color = Color.Lerp(Color.red, Color.white, t);
            }
        }
    }
}
