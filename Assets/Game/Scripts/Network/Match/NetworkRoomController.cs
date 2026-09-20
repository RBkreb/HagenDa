using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// 局间系统(PHASE15):房间相位的服务器权威宿主 + 队伍/小队分配 + AI 补位/被挤占。
    ///
    /// 三态由 <see cref="RoomPhaseRules"/> 校验,以 SyncVar 下发,客户端据此决定显示
    /// 大厅 / 准备 / 对局界面:
    ///
    ///  空闲(Idle)   —— 刚进房间或上一局结束;地图与实体均不在场,host 可开局。
    ///  准备(Ready)  —— 正在加载地图并分配小队;实体仍未部署。
    ///  对局(Match)  —— 地图 + 小队就绪;真人选点后入场,AI 即时入场,未选者暂不部署。
    ///
    /// 真人挤占 AI:真人中途加入时,按 <see cref="AiSeatPolicy"/> 选一个 AI 席位
    /// (优先死席,否则强制击杀一个活席),再把该身体的控制权交给真人连接 ——
    /// 因为 AI 与真人共用同一条服务端输入通路,转交上层输入即可完成补位。
    ///
    /// 设计说明:所有可测语义都走 public 方法,不依赖场景加载或真实连接;
    /// 场景切换(<see cref="mapSceneName"/>)仅在配置了不同场景时触发。
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkRoomController : NetworkBehaviour
    {
        public static NetworkRoomController Instance { get; private set; }

        // ---------------------------------------------------------------
        // PHASE (sync)
        // ---------------------------------------------------------------

        [Tooltip("房间相位(空闲/准备/对局)。SyncVar → 客户端 UI 直接读取。")]
        [SyncVar] public RoomPhase phase = RoomPhase.Idle;

        [Tooltip("开局的 host 连接 id;由首个连接填充,其余连接视为客户端。")]
        [SyncVar] public int hostConnectionId = -1;

        [Tooltip("当前对局的地图场景名;留空 = 就地管理相位(单场景战场)。")]
        public string mapSceneName = "";

        // ---------------------------------------------------------------
        // Team / squad / AI fill
        // ---------------------------------------------------------------

        [Header("Team / Squad")]
        [Tooltip("每队编制上限(真人 + AI)。")]
        public int teamCapacity = 30;

        [Tooltip("单个小队容量(决定真人聚拢到同队的哪个小队)。")]
        public int squadSize = 5;

        [Tooltip("每队 AI 补位上限(0 = 不补 AI,纯真人)。")]
        public int aiPerTeam = 30;

        [Tooltip("AI 实体预制体(为空时使用场景内既有 AI 作为席位)。")]
        public GameObject aiPrefab;

        [Tooltip("两队 AI 生成中心(按 teamId 索引:0=红 1=蓝)。")]
        public Transform[] aiSpawnCenters = new Transform[2];

        // ---------------------------------------------------------------
        // Server-side state
        // ---------------------------------------------------------------

        private readonly List<NetworkAIController> aiSeats = new List<NetworkAIController>();
        private readonly DeployRoster roster = new DeployRoster();
        private readonly Dictionary<int, SquadAssignment> humanAssignments = new Dictionary<int, SquadAssignment>();
        private int humansJoined;       // 单调递增:保证均衡分配可复现,离队不会造成索引碰撞
        private bool loadingMatch;

        /// <summary>部署名册(局中系统)。</summary>
        public DeployRoster Roster => roster;

        /// <summary>已登记的 AI 席位数(服务器)。</summary>
        public int AiSeatCount => aiSeats.Count;

        /// <summary>已分配的真人连接数。</summary>
        public int AssignedHumanCount => humanAssignments.Count;

        // ---------------------------------------------------------------
        // LIFECYCLE
        // ---------------------------------------------------------------

        public override void OnStartServer()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            // 跨场景常驻:开局切换地图场景后,房间相位/分队/AI 补位仍需存活。
            DontDestroyOnLoad(gameObject);

            phase = RoomPhase.Idle;
            roster.BeginMatch();

            // 场景内既有 AI(单场景战场)登记为席位;预制体模式下按需生成。
            foreach (var ai in FindObjectsOfType<NetworkAIController>())
                RegisterAiSeat(ai);
        }

        public override void OnStartClient()
        {
            if (Instance == null) Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------
        // HOST / PHASE TRANSITIONS (server-authoritative)
        // ---------------------------------------------------------------

        /// <summary>该连接是否有 host 权限。服务器自身(无连接)恒为 true。</summary>
        public bool IsHostConnection(NetworkConnectionToClient conn)
        {
            if (conn == null) return true;                 // 服务器/离线
            if (hostConnectionId < 0) return true;         // 未登记 → 首个连接即 host
            return conn.connectionId == hostConnectionId;
        }

        /// <summary>登记 host 连接(首个连接)。</summary>
        [Server]
        public void RegisterHostConnection(NetworkConnectionToClient conn)
        {
            if (conn == null) return;
            if (hostConnectionId < 0) hostConnectionId = conn.connectionId;
        }

        /// <summary>host 决定开始对局:空闲 → 准备(开始加载地图 + 分配小队)。</summary>
        [Server]
        public bool StartMatchLoad()
        {
            if (!RoomPhaseRules.CanTransition(phase, RoomPhase.Ready)) return false;
            if (loadingMatch) return false;

            loadingMatch = true;
            roster.BeginMatch();          // 新一局:所有实体暂不部署
            phase = RoomPhase.Ready;
            ApplyPhaseHoldToAllCombatants(true);

            if (!string.IsNullOrEmpty(mapSceneName) &&
                mapSceneName != UnityEngine.SceneManagement.SceneManager.GetActiveScene().name &&
                NetworkManager.singleton != null)
            {
                // 真实场景切换:NetworkManager 与 room controller 需 DontDestroyOnLoad。
                NetworkManager.singleton.ServerChangeScene(mapSceneName);
            }
            return true;
        }

        /// <summary>
        /// 地图加载 + 小队分配完成:准备 → 对局。AI 即时选择部署点入场,真人等待选点。
        /// </summary>
        [Server]
        public bool CompleteMatchLoad()
        {
            if (!RoomPhaseRules.CanTransition(phase, RoomPhase.Match)) return false;

            loadingMatch = false;
            phase = RoomPhase.Match;

            // 解冻真人:他们仍需在部署界面选点(未选则暂不部署)。
            // AI 由 DeployAiAtMatchStart 直接落地。
            ApplyPhaseHoldToAllCombatants(false);
            DeployAiAtMatchStart();
            return true;
        }

        /// <summary>对局结束(Match → Idle):对局正常结束,或真人全部退出而强制结束。</summary>
        [Server]
        public bool EndMatch()
        {
            if (!RoomPhaseRules.CanTransition(phase, RoomPhase.Idle)) return false;
            phase = RoomPhase.Idle;
            loadingMatch = false;
            roster.BeginMatch();
            humanAssignments.Clear();
            return true;
        }

        /// <summary>取消开局(Ready → Idle)。</summary>
        [Server]
        public bool CancelMatchLoad()
        {
            if (!RoomPhaseRules.CanTransition(phase, RoomPhase.Idle)) return false;
            phase = RoomPhase.Idle;
            loadingMatch = false;
            return true;
        }

        // ---------------------------------------------------------------
        // HUMAN JOIN / LEAVE (team + squad + AI displacement)
        // ---------------------------------------------------------------

        /// <summary>
        /// 分配一名真人:均摊红蓝 + 同队聚拢小队(<see cref="TeamBalancer"/>)。
        /// 仅做队伍/小队登记,不牵动 AI —— 挤占由 <see cref="TryPossessAiForHuman"/>
        /// 单独负责(否则会被调用两次,第二次已无席位可用)。
        /// </summary>
        /// <param name="connectionKey">链接稳定标识(connectionId;测试可用任意键)。</param>
        /// <param name="preferredTeam">-1 = 由均衡器决定。</param>
        /// <returns>该真人的队伍/小队分配。</returns>
        [Server]
        public SquadAssignment AssignHuman(int connectionKey, int preferredTeam = -1)
        {
            // 用单调计数而非当前人数:有人退出后重连不会复用同一个槽位,
            // 从而保持"第 N 个真人落在哪个小队"可复现。
            int humanIndex = humansJoined++;
            var assignments = TeamBalancer.AssignHumans(humanIndex + 1, squadSize);
            var mine = assignments[humanIndex];

            if (preferredTeam >= 0)
                mine = new SquadAssignment(preferredTeam, mine.Squad);

            humanAssignments[connectionKey] = mine;
            return mine;
        }

        /// <summary>真人退出:记录清除,其席位由 AI 补位(对局中)。</summary>
        [Server]
        public void RemoveHuman(int connectionKey)
        {
            if (!humanAssignments.ContainsKey(connectionKey)) return;
            humanAssignments.Remove(connectionKey);

            // 对局中真人全退 → 强制结束。
            if (phase == RoomPhase.Match && humanAssignments.Count == 0)
            {
                EndMatch();
                return;
            }

            if (phase == RoomPhase.Match)
                SpawnAiToFill();
        }

        /// <summary>
        /// 为一名新真人在指定队伍选择一个 AI 席位并交出其身体。
        /// 优先死席;无死席则强制击杀一个活席(规格)。
        /// </summary>
        /// <returns>被接收的 AI(无可用席位时 null)。</returns>
        [Server]
        public NetworkAIController DisplaceAiForHuman(int preferredTeam)
        {
            var seats = BuildSeatSnapshot();
            var pick = AiSeatPolicy.Choose(seats, preferredTeam);
            if (!pick.Found) return null;

            NetworkAIController victim = null;
            foreach (var ai in aiSeats)
            {
                if (ai != null && ai.GetInstanceID() == pick.SeatId) { victim = ai; break; }
            }
            if (victim == null) return null;

            if (!pick.AlreadyDead)
            {
                // 规格:无死亡 AI 则强制击杀 1 个 AI。
                var health = victim.GetComponent<NetworkPlayerHealth>();
                health?.ForceKill();
            }

            aiSeats.Remove(victim);
            return victim;
        }

        /// <summary>
        /// 真人加入(对局中)的完整接线:分配队伍/小队 → 挤占 AI → 把 AI 身体交给该连接。
        /// 因为 AI 与真人共用同一条服务端输入通路,转交身体即完成补位(规格)。
        /// </summary>
        /// <returns>true = 已接管一个 AI 身体(调用方不应再生成新身体)。</returns>
        [Server]
        public bool TryPossessAiForHuman(NetworkConnectionToClient conn, int preferredTeam = -1)
        {
            if (phase != RoomPhase.Match) return false;   // 非对局中:走常规新身体流程

            int key = conn != null ? conn.connectionId : -1;
            var assignment = AssignHuman(key, preferredTeam);

            var victim = DisplaceAiForHuman(assignment.Team);
            if (victim == null) return false;             // 无 AI 可挤占:回退新身体

            if (conn != null)
            {
                var c = victim.GetComponent<NetworkCombatant>();
                if (c != null)
                {
                    c.teamId = assignment.Team;
                    c.squadId = assignment.Squad;
                }

                // 身体已是生成态:直接把所有权交给该连接(不销毁、不重生成)。
                // 新加入连接尚无 player → AddPlayerForConnection;已有 player(换人)
                // → ReplacePlayerForConnection 并销毁旧身体。
                if (conn.identity == null)
                    NetworkServer.AddPlayerForConnection(conn, victim.gameObject);
                else
                    NetworkServer.ReplacePlayerForConnection(
                        conn, victim.gameObject, ReplacePlayerOptions.Destroy);
            }
            return true;
        }

        // ---------------------------------------------------------------
        // AI SEAT REGISTRY
        // ---------------------------------------------------------------

        /// <summary>AI 生成时的登记入口(由 <see cref="NetworkAIController.OnStartServer"/> 调用)。</summary>
        internal static void NotifyAiSpawned(NetworkAIController ai)
        {
            if (Instance != null) Instance.RegisterAiSeat(ai);
        }

        /// <summary>AI 销毁时的注销入口。</summary>
        internal static void NotifyAiDespawned(NetworkAIController ai)
        {
            if (Instance != null) Instance.UnregisterAiSeat(ai);
        }

        [Server]
        public void RegisterAiSeat(NetworkAIController ai)
        {
            if (ai == null || aiSeats.Contains(ai)) return;
            aiSeats.Add(ai);
        }

        [Server]
        public void UnregisterAiSeat(NetworkAIController ai)
        {
            if (ai == null) return;
            aiSeats.Remove(ai);
        }

        /// <summary>当前 AI 席位快照(供挤占策略选择;顺序稳定)。</summary>
        public List<AiSeat> BuildSeatSnapshot()
        {
            var list = new List<AiSeat>(aiSeats.Count);
            foreach (var ai in aiSeats)
            {
                if (ai == null) continue;
                var c = ai.GetComponent<NetworkCombatant>();
                var h = ai.GetComponent<NetworkPlayerHealth>();
                int team = c != null ? c.teamId : -1;
                bool dead = h != null && h.IsDead;
                list.Add(new AiSeat(ai.GetInstanceID(), team, dead));
            }
            return list;
        }

        // ---------------------------------------------------------------
        // AI FILL / DEPLOY
        // ---------------------------------------------------------------

        /// <summary>
        /// 对场内全部作战单位统一施加/解除相位冻结。规格:"进入正式对局,所有实体
        /// 暂不部署,模型不进入地图"。必须在开局前对**所有**实体生效,而非只对
        /// 新加入者 —— 否则场景里预先摆好的 AI 会在加载期就站在地图上。
        /// </summary>
        [Server]
        public void ApplyPhaseHoldToAllCombatants(bool hold)
        {
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);

            foreach (var c in buf)
            {
                if (c == null) continue;
                var health = c.Health;
                if (health == null) continue;

                if (hold) health.SetPhaseHold(true);
                else health.ReleasePhaseHold();
            }
        }

        /// <summary>对局开始:每个 AI 立即选择一个部署点并落地(AI 无部署界面)。</summary>
        [Server]
        private void DeployAiAtMatchStart()
        {
            foreach (var ai in aiSeats)
            {
                if (ai == null) continue;
                var health = ai.GetComponent<NetworkPlayerHealth>();
                if (health == null) continue;

                int choice = AiDeployChoice(ai);
                roster.SetChoice(ai.GetInstanceID(), choice);
                health.ServerPlaceAtDeployPoint(choice);
                roster.DeploySingle(ai.GetInstanceID());
            }
        }

        /// <summary>
        /// AI 的部署选择。开局时统一走 GR(choice=1)—— 这是唯一保证存在的位置
        /// (信标要有人放、据点要先占领、小队点要有队友活着),任何图都能落地。
        /// 死亡后的重部署会走 <see cref="NetworkPlayerHealth"/> 里更丰富的优先级。
        /// </summary>
        private static int AiDeployChoice(NetworkAIController ai) => 1;

        /// <summary>真人退出后补一个 AI(对局中),保持队伍人数。</summary>
        [Server]
        private void SpawnAiToFill()
        {
            if (aiPrefab == null) return;   // 无预制体 → 依赖场景内既有席位
            SpawnOneAi(TeamBalancer.Red, aiSpawnCenters != null && aiSpawnCenters.Length > 0 ? aiSpawnCenters[0] : null);
        }

        /// <summary>生成一个 AI 并登记为席位(供 AI 补位)。</summary>
        [Server]
        public NetworkAIController SpawnOneAi(int team, Transform center)
        {
            if (aiPrefab == null) return null;

            Vector3 pos = center != null ? center.position : Vector3.zero;
            var go = Instantiate(aiPrefab, pos, Quaternion.identity);
            go.name = $"AI_fill_{team}_{aiSeats.Count}";
            NetworkServer.Spawn(go);

            var ai = go.GetComponent<NetworkAIController>();
            RegisterAiSeat(ai);

            var c = go.GetComponent<NetworkCombatant>();
            if (c != null) { c.teamId = team; }

            return ai;
        }

        // ---------------------------------------------------------------
        // DEPLOY ROSTER (局中系统)
        // ---------------------------------------------------------------

        /// <summary>记录一个实体的部署点选择(正式开局前不落地)。</summary>
        [Server]
        public void RecordDeployChoice(int entityId, int choice) => roster.SetChoice(entityId, choice);

        /// <summary>正式开局:落地所有已选择的实体;未选者暂不部署。</summary>
        [Server]
        public List<int> CommitDeployment() => roster.CommitDeployment();

        /// <summary>对局中补选 → 单独落地。</summary>
        [Server]
        public int DeploySingle(int entityId) => roster.DeploySingle(entityId);
    }
}
