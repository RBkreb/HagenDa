using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Throwable that detonates into a smoke cloud.
    /// </summary>
    public class SmokeThrowable : NetworkThrowable
    {
        [Header("Smoke")]
        public float concentration = 0.6f;  // 浓度 = 初始 alpha
        public float radius = 5f;            // 半径
        public float decayTime = 5f;         // 衰减时间

        protected override void OnImpact()
        {
            // PHASE9: 服务器端注册烟雾体积（AI 视野遮挡用）。
            NetworkSmokeVolume.Register(transform.position, radius, decayTime, concentration);
            RpcSmokeVisual(transform.position, concentration, radius, decayTime);
        }

        [ClientRpc]
        private void RpcSmokeVisual(Vector3 pos, float concentration, float radius, float decayTime)
        {
            NetworkSmoke.Spawn(pos, concentration, radius, decayTime);
        }
    }
}
