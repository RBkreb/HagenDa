using System.Collections.Generic;
using Mirror;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// FSM 战场调度器（PHASE9）。普通 MonoBehaviour（无 NetworkIdentity，场景加载即
    /// 存活，不受 Mirror 场景网络对象启停影响）：以固定累积步长跑 10Hz 决策 tick。
    /// 每 tick：
    ///   1. 共享战斗员快照（一次/tick——替代每 agent 遍历注册表的 O(n²) 模式）；
    ///   2. 感知批处理：目标发现由 AgentVisionSensor 前向方体 + 近距球 Overlap
    ///      在主线程完成，其 LOS 遮挡射线（预算 24 根/agent，59 agent = 1416 根）
    ///      打包成一个 RaycastCommand.ScheduleBatch，同帧 Complete 后分发解析
    ///      ——这是 Job System 分摊计算的主战场；
    ///   3. 寻路错峰：每 tick 最多 pathBudgetPerTick 条 NavMesh.CalculatePath
    ///      （主线程，分散到不同 tick 避免尖峰）；
    ///   4. 逐 agent 调 FSMAIController.DecisionTick。
    /// 移动/开火等执行由 60Hz intent 管线完成（NetworkAIController →
    /// NetworkPlayerController），与玩家/ML agent 完全同构。
    /// </summary>
    public class FSMBattleSystem : MonoBehaviour
    {
        public static FSMBattleSystem Instance { get; private set; }

        [Header("Tick")]
        [Tooltip("决策/感知频率（Hz），固定累积步长。")]
        public float decisionHz = 10f;

        [Tooltip("每 tick 最多计算的寻路条数（错峰：12/tick ≈ 120 条/s）。")]
        public int pathBudgetPerTick = 12;

        /// <summary>
        /// 战斗员快照（每 tick 重建一次，全体 FSM 大脑共享只读）。
        /// lastDamager 直读 NetworkPlayerHealth.lastAttacker（索敌优先级第 2 级：
        /// 最近伤害来源）。
        /// </summary>
        public struct CombatantView
        {
            public NetworkCombatant combatant;
            public Vector3 position;
            public int team;
            public int squad;
            public bool dead;
            public float health01;
            public bool marked;
            public int markedByTeam;
            public NetworkCombatant lastDamager;
        }

        private readonly List<FSMAIController> agents = new List<FSMAIController>();
        private readonly List<CombatantView> snapshot = new List<CombatantView>();
        private readonly List<NetworkCombatant> combatantBuf = new List<NetworkCombatant>();

        private struct PathRequest { public FSMAIController agent; public Vector3 target; }
        private readonly Queue<PathRequest> pathQueue = new Queue<PathRequest>();

        // 共享感知批处理数组（按 agent 数扩容；场景内实体数稳定）。
        private NativeArray<RaycastCommand> commands;
        private NativeArray<RaycastHit> rayHits;
        private int batchCapacityAgents;

        private float accum;

        /// <summary>性能统计（M0 验收 / M3 统计 HUD）：单 tick 总耗时（毫秒）。</summary>
        public float LastTickMs { get; private set; }
        public float MaxTickMs { get; private set; }
        public long TickCount { get; private set; }
        public int AgentCount => agents.Count;

        private readonly System.Diagnostics.Stopwatch tickWatch = new System.Diagnostics.Stopwatch();

        /// <summary>当前 tick 的共享快照（决策期间有效）。</summary>
        public IReadOnlyList<CombatantView> Snapshot => snapshot;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            DisposeBatch();
        }

        public void Register(FSMAIController agent)
        {
            if (agent != null && !agents.Contains(agent))
                agents.Add(agent);
        }

        public void Unregister(FSMAIController agent) => agents.Remove(agent);

        /// <summary>大脑发起寻路请求（系统错峰处理）。</summary>
        public void EnqueuePath(FSMAIController agent, Vector3 target)
        {
            pathQueue.Enqueue(new PathRequest { agent = agent, target = target });
        }

        private void Update()
        {
            if (!NetworkServer.active) return;
            // PHASE10 开局门控：部署阶段 FSM 大脑整体暂停（保持原位待命）。
            if (NetworkCommanderState.GateActive) return;

            float dt = 1f / Mathf.Max(1f, decisionHz);
            accum += Time.deltaTime;
            if (accum < dt) return;
            accum = Mathf.Min(accum - dt, dt);   // 丢弃积压，防止追帧螺旋

            Tick();
        }

        private void Tick()
        {
            PruneAgents();
            if (agents.Count == 0) return;

            // PHASE9: 清理过期服务器端烟雾体积。
            NetworkSmokeVolume.Cleanup();

            tickWatch.Restart();

            BuildSnapshot();
            RunPerceptionBatch();
            ProcessPathQueue();

            float dt = 1f / Mathf.Max(1f, decisionHz);
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a != null) a.DecisionTick(dt, snapshot);
            }

            tickWatch.Stop();
            LastTickMs = (float)tickWatch.Elapsed.TotalMilliseconds;
            if (LastTickMs > MaxTickMs) MaxTickMs = LastTickMs;
            TickCount++;
        }

        private void PruneAgents()
        {
            for (int i = agents.Count - 1; i >= 0; i--)
                if (agents[i] == null)
                    agents.RemoveAt(i);
        }

        private void BuildSnapshot()
        {
            snapshot.Clear();
            NetworkMatchManager.GetAllCombatants(combatantBuf);
            for (int i = 0; i < combatantBuf.Count; i++)
            {
                var c = combatantBuf[i];
                if (c == null) continue;

                var h = c.Health;
                snapshot.Add(new CombatantView
                {
                    combatant = c,
                    position = c.transform.position,
                    team = c.teamId,
                    squad = c.squadId,
                    dead = c.IsDead,
                    health01 = h != null ? Mathf.Clamp01(h.health / h.maxHealth) : 1f,
                    marked = c.IsMarked,
                    markedByTeam = c.markedByTeam,
                    lastDamager = h != null ? h.lastAttacker : null,
                });
            }
        }

        // ---------------------------------------------------------------
        // 感知批处理：全部射线一个 ScheduleBatch，同帧 Complete
        // ---------------------------------------------------------------

        private void RunPerceptionBatch()
        {
            EnsureBatchCapacity(agents.Count);
            int rays = AgentVisionSensor.MaxOcclusionRays;

            for (int i = 0; i < agents.Count; i++)
                agents[i].BuildPerception(commands, i * rays);

            JobHandle handle = RaycastCommand.ScheduleBatch(
                commands, rayHits, minCommandsPerJob: 64, dependsOn: default);
            handle.Complete();

            for (int i = 0; i < agents.Count; i++)
                agents[i].ParsePerception(rayHits, i * rays);
        }

        private void EnsureBatchCapacity(int agentCount)
        {
            if (commands.IsCreated && batchCapacityAgents >= agentCount) return;

            DisposeBatch();
            int rays = agentCount * AgentVisionSensor.MaxOcclusionRays;
            commands = new NativeArray<RaycastCommand>(
                rays, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            rayHits = new NativeArray<RaycastHit>(
                rays, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batchCapacityAgents = agentCount;
        }

        private void DisposeBatch()
        {
            if (commands.IsCreated) commands.Dispose();
            if (rayHits.IsCreated) rayHits.Dispose();
            commands = default;
            rayHits = default;
            batchCapacityAgents = 0;
        }

        private void OnApplicationQuit()
        {
            // 编辑器退出 Play 时先释放批处理数组，避免 Leak Detection 误报
            // Persistent 分配（OnDestroy 在域重载序列里时序不稳定）。
            DisposeBatch();
        }

        // ---------------------------------------------------------------
        // 寻路错峰
        // ---------------------------------------------------------------

        private void ProcessPathQueue()
        {
            int budget = pathBudgetPerTick;
            while (budget > 0 && pathQueue.Count > 0)
            {
                var req = pathQueue.Dequeue();
                if (req.agent == null) continue;
                req.agent.ComputePath(req.target);
                budget--;
            }
        }
    }
}
