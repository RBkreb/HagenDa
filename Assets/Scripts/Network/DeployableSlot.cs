using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Marks a placed deployable (大型补给箱/拦截装置/感应器/信号炸药/线控炸药)
    /// with its owner + slot type, so <see cref="NetworkEquipment"/> can enforce the
    /// per-type deploy cap (PHASE8 配备互斥): when a deployable of the same type is
    /// placed and the cap is reached, the OLDEST one is destroyed first.
    /// </summary>
    public class DeployableSlot : NetworkBehaviour
    {
        [Tooltip("部署者（NetworkEquipment 所在实体）。")]
        public NetworkIdentity owner;

        [Tooltip("装备类型（同一实体同一类型共享部署上限）。")]
        public EquipmentType type;

        [Tooltip("部署时间戳（服务器），用于\"最先部署的被摧毁\"。")]
        public double deployTime;

        [Server]
        public void Init(NetworkIdentity owner, EquipmentType type)
        {
            this.owner = owner;
            this.type = type;
            deployTime = NetworkTime.time;
        }
    }
}
