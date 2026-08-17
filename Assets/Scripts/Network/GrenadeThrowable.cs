using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Throwable that detonates into an explosion (damage + light + particles).
    /// </summary>
    public class GrenadeThrowable : NetworkThrowable
    {
        [Header("Explosion")]
        public float explosionYield = 100f;   // 当量 = 中心伤害
        public float explosionRadius = 5f;     // 衰减半径

        protected override void OnImpact()
        {
            ExplosionUtility.ApplyDamage(transform.position, explosionYield, explosionRadius);
            RpcExplosionVisual(transform.position, explosionRadius);
        }

        [ClientRpc]
        private void RpcExplosionVisual(Vector3 pos, float radius)
        {
            ExplosionUtility.SpawnVisual(pos, radius);
        }
    }
}
