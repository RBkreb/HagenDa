using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Throwable that detonates into a persistent EMP field (电磁手雷). Triggered on
    /// impact; spawns a <see cref="NetworkEmpField"/> with the configured radius,
    /// lifetime and interference duration.
    /// </summary>
    public class EmpGrenadeThrowable : NetworkThrowable
    {
        [Header("EMP")]
        public NetworkEmpField empFieldPrefab;
        public float radius = 6f;
        public float lifetime = 10f;
        public float interfereDuration = 2f;

        protected override void OnImpact()
        {
            if (empFieldPrefab == null) return;

            var go = Instantiate(empFieldPrefab.gameObject, transform.position, Quaternion.identity);
            var field = go.GetComponent<NetworkEmpField>();
            if (field != null)
            {
                field.radius = radius;
                field.lifetime = lifetime;
                field.interfereDuration = interfereDuration;
            }

            NetworkServer.Spawn(go);
        }
    }
}
