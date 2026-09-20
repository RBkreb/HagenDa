using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// 房间网络管理器(PHASE15 局间系统)。
    ///
    /// 为什么需要子类:真人在**对局中**加入时要"挤占 AI 而非新建身体",这取决于
    /// 连接时房间相位 —— 默认 <see cref="NetworkManager.OnServerAddPlayer"/> 只会
    /// 生成新玩家预制体。子类在 <c>OnServerConnect</c> 处先处理 host 登记,再让
    /// <c>NetworkRoomController.TryPossessAiForHuman</c> 尝试接管一个 AI 身体;
    /// 未接管成功(非对局中 / 无 AI)才回退到基类的常规生成流程。
    ///
    /// 不覆写 <c>OnServerDisconnect</c> 的销毁逻辑 —— 仍由基类销毁玩家对象,
    /// 房间控制器只负责注销登记并让 AI 补位。
    /// </summary>
    public class NetworkRoomManager : Mirror.NetworkManager
    {
        [Tooltip("对局中真人加入时挤占 AI(关闭则一律新建身体)。")]
        public bool possessAiOnLateJoin = true;

        public override void OnStartServer()
        {
            base.OnStartServer();
            NetworkServer.OnDisconnectedEvent += HandleServerDisconnected;
        }

        public override void OnStopServer()
        {
            NetworkServer.OnDisconnectedEvent -= HandleServerDisconnected;
            base.OnStopServer();
        }

        public override void OnServerConnect(NetworkConnectionToClient conn)
        {
            base.OnServerConnect(conn);

            var room = NetworkRoomController.Instance;
            if (room != null)
                room.RegisterHostConnection(conn);   // 首个连接成为 host
        }

        public override void OnServerAddPlayer(NetworkConnectionToClient conn)
        {
            var room = NetworkRoomController.Instance;
            if (room != null && possessAiOnLateJoin && room.TryPossessAiForHuman(conn))
                return;   // 已接管 AI 身体,不再生成新玩家

            base.OnServerAddPlayer(conn);
        }

        private void HandleServerDisconnected(NetworkConnectionToClient conn)
        {
            if (conn == null) return;
            NetworkRoomController.Instance?.RemoveHuman(conn.connectionId);
        }
    }
}
