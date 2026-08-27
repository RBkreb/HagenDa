using System.Collections;
using UnityEngine;
using Mirror;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE10 开局部署门控（Q6/Q16 定案）。服务端时序：
    ///
    ///  1. 服务端就绪后等待一小段（战斗员注册/阵营分配）；
    ///  2. 红 → 蓝依次执行开局部署轮（各 300s 内部超时已在 Orchestrator 请求层兜底，
    ///     此处再加整体 watchdog：单方自 BeginOpening 起 330s 未落定即强制退化落定）;
    ///  3. 每方内部最多两轮（初始 + 一次催促），覆盖不足按现状接受（Orchestrator 处理）；
    ///  4. 双方落定后 NetworkCommanderState.SetMatchStarted() 解冻全场；
    ///  5. 若某方失败退化 → 该方休眠 + SquadCommander 桩接管（Coordinator 已处理）。
    ///
    /// 无本组件的场景完全不受影响（GateActive 恒 false，matchStarted 默认 true）。
    /// </summary>
    public class CommanderGateController : MonoBehaviour
    {
        [Tooltip("服务端启动后的等待秒数（让 59 个战斗员完成 OnStartServer 注册）。")]
        public float settleDelay = 2f;

        [Tooltip("单方开局 watchdog（秒）。")]
        public float perSideWatchdog = 330f;

        private bool gateDone;

        public bool GateDone => gateDone;   // 调试可查

        private void Start()
        {
            // Start 时 NetworkServer.active 几乎必然为 false（TrainingAutoHost 的
            // Start 时序不确定）→ 必须等待激活而非直接放弃（首轮实测踩坑）。
            StartCoroutine(WaitServerThenRun());
        }

        private IEnumerator WaitServerThenRun()
        {
            float t0 = Time.time;
            while (!NetworkServer.active)
            {
                if (Time.time - t0 > 30f)
                {
                    Debug.LogError("[Gate] 等待 30s 后服务器仍未激活，门控中止");
                    yield break;
                }
                yield return null;
            }
            Debug.Log("[Gate] 服务器已激活，开始门控");
            yield return RunGate();
        }

        private IEnumerator RunGate()
        {
            yield return new WaitForSeconds(settleDelay);

            var state = NetworkCommanderState.Instance;
            if (state == null)
            {
                Debug.LogWarning("[Gate] 缺少 NetworkCommanderState，门控跳过");
                yield break;
            }

            var red = FindTeam((int)MatchTeam.Red);
            var blue = FindTeam((int)MatchTeam.Blue);
            if (red == null && blue == null)
            {
                Debug.LogWarning("[Gate] 场上没有任何指挥官实例，门控跳过");
                state.SetMatchStarted();
                yield break;
            }

            Debug.Log("[Gate] 开局门控开始：全体冻结，指挥官开始部署");
            float gateStart = Time.time;

            if (red != null) yield return RunSide(red, gateStart);
            if (blue != null) yield return RunSide(blue, gateStart);

            state.SetMatchStarted();
            gateDone = true;
            Debug.Log($"[Gate] 门控结束，对局正式开始（耗时 {Time.time - gateStart:F0}s）");
        }

        private IEnumerator RunSide(CommanderOrchestrator orch, float gateStart)
        {
            bool settled = false;
            bool degraded = false;

            void Handler(bool d) { settled = true; degraded = d; }
            orch.OpeningSettled += Handler;
            orch.BeginOpening();

            float deadline = Time.time + perSideWatchdog;
            while (!settled && Time.time < deadline)
                yield return null;
            orch.OpeningSettled -= Handler;

            if (!settled)
            {
                // Orchestrator 卡死（不该发生——请求层有超时）：强制作退化收尾。
                Debug.LogError($"[Gate] {orch.name} watchdog 到期仍未落定，按退化处理");
                orch.ForceDegradeForGate();
                CommandersRecompute(orch);
            }
        }

        private static void CommandersRecompute(CommanderOrchestrator orch)
        {
            // ForceDegradeForGate 内部已触发 RecomputeStub；此空壳仅为语义占位。
        }

        private static CommanderOrchestrator FindTeam(int team)
        {
            foreach (var o in FindObjectsOfType<CommanderOrchestrator>())
                if (o.Team == team) return o;
            return null;
        }
    }
}
