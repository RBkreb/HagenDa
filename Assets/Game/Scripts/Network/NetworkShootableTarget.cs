using System.Collections;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// A static networked target the server can damage, for verifying
    /// server-authoritative hitscan shooting.
    /// </summary>
    public class NetworkShootableTarget : NetworkBehaviour, IDamageable
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

        // Static targets have no capsule hitbox; bullets apply a flat 1x multiplier.
        [Server]
        public void TakeDamage(float damage, Vector3 hitPoint)
        {
            TakeDamage(damage);
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
