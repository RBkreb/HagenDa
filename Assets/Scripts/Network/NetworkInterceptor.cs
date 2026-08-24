using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Interceptor (拦截系统, PHASE6). A placed blue sphere; any damaging throwable
    /// (grenade / RPG / delayed bomb / signal / wired charge) that enters its radius
    /// is destroyed. It self-destructs after <see cref="maxIntercepts"/> successful
    /// interceptions, and is destroyed outright by an EMP (via IEmpTarget).
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkInterceptor : NetworkBehaviour, IEmpTarget
    {
        [Header("Interceptor")]
        public float radius = 3f;
        public int maxIntercepts = 3;

        /// <summary>ML 训练：部署者（拦截效用奖励归属）。服务器端引用。</summary>
        [System.NonSerialized] public NetworkCombatant ownerCombatant;

        private int intercepts;

        public override void OnStartServer()
        {
            DeployableUtil.IgnoreLivingCollision(gameObject);
        }

        private void OnCollisionEnter(Collision collision)
        {
            // 接触地面后立刻静止，去除物理防止漂移。
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
        }

        private void Update()
        {
            if (!isServer) return;

            foreach (var c in Physics.OverlapSphere(transform.position, radius))
            {
                var grenade = c.GetComponentInParent<GrenadeThrowable>();
                if (grenade != null)
                {
                    Intercept(grenade.gameObject);
                    continue;
                }

                var charge = c.GetComponentInParent<RemoteChargeThrowable>();
                if (charge != null)
                {
                    Intercept(charge.gameObject);
                }
            }
        }

        private void Intercept(GameObject target)
        {
            if (target == null) return;
            NetworkServer.Destroy(target);
            intercepts++;

            // ML 训练：拦截挡弹效用奖励。
            RewardBus.InterceptorBlock(ownerCombatant);

            if (intercepts >= maxIntercepts)
                NetworkServer.Destroy(gameObject);
        }

        /// <summary>EMP destroys the interceptor outright.</summary>
        public void ApplyEmp(float duration)
        {
            if (isServer)
                NetworkServer.Destroy(gameObject);
        }
    }
}
