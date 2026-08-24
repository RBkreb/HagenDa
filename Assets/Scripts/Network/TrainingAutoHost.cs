using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Play 后自动启动 Host（训练场景专用）。普通 MonoBehaviour——
    /// 无 NetworkIdentity，不受 Mirror 的"服务器未启动时禁用场景网络对象"
    /// 机制影响（NetworkBehaviour.Start 在 GO inactive 时不会调用，
    /// 这是此前自动启动失败的根因）。
    /// 挂在 NetworkManager 同对象或场景任意非网络对象上。
    /// </summary>
    public class TrainingAutoHost : MonoBehaviour
    {
        private void Start()
        {
            if (NetworkManager.singleton == null) return;
            if (NetworkServer.active) return;

            NetworkManager.singleton.autoCreatePlayer = false;
            NetworkManager.singleton.StartHost();
            Debug.Log("[Training] Host auto-started on Play");
        }
    }
}
