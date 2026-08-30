using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mirror;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE10 多模态 LLM 指挥官（每阵营一个实例，纯服务端）。
    ///
    /// 单飞轮次循环：触发原因入队 → 取最新快照（相机协程桥接为 Task）→ 组装
    /// 多模态消息（历史=文本压缩滚动窗口 20 轮；本轮=user 文本头+PNG 图）→
    /// 原生 tool_calls 循环（≤8 次，get_snapshot 特殊处理回图）→ 记录/记忆/清理。
    ///
    /// 退化语义（Q6/Q13）：传输错误/超时 → 休眠（SquadCommander 桩接管）+
    /// 每 probeInterval 探活（GET /v1/models）；成功立即拍快照重启指挥并禁用桩。
    ///桩为场景共享 → 由 Coordinator 统一重算（任一方休眠即启用）。
    /// </summary>
    public class CommanderOrchestrator : MonoBehaviour
    {
        public enum Phase { Idle, Opening, Active, Dormant }

        // ---------------------------------------------------------------
        // 注入引用 —— 必须 SerializeField 才能从编辑器装配存活到 Play（首轮实测教训）
        // ---------------------------------------------------------------

        [SerializeField] private int team = -1;
        [SerializeField] private CommanderConfig config;
        [SerializeField] private CommanderMapOverlay overlay;
        [SerializeField] private CommanderMapCamera cam;
        [SerializeField] private CommanderWeaponSystem weapons;
        [SerializeField] private NetworkCommanderState state;

        public int Team => team;
        public CommanderConfig Config => config;
        public CommanderMapOverlay Overlay => overlay;
        public CommanderMapCamera Cam => cam;
        public CommanderWeaponSystem Weapons => weapons;
        public NetworkCommanderState State => state;

        public Phase CurrentPhase { get; private set; } = Phase.Idle;
        public bool IsHealthy => CurrentPhase is Phase.Active or Phase.Opening;

        /// <summary>本方开局部署落定（完成或退化）。参数=是否退化。</summary>
        public event Action<bool> OpeningSettled;

        private LlmRestClient llm;
        private CommanderToolContext tools;
        private CommanderRoundLogger logger;

        // 轮次状态
        private bool busy;
        // 开局为 -∞：EnsureRuntime/开局轮完成时会重置为"门控解除瞬间"，
        // 保证空闲计时从解禁后才开始（实测：默认 0 会导致解禁瞬间立即触发空闲轮）。
        private float lastOutputAt = float.NegativeInfinity;
        private float minGapUntil;
        private float? wakeOverrideSec;            // wait 工具写入，轮次结束消费
        private bool manualTriggerPending;
        private string manualTriggerReason;
        private readonly List<string> infoBuffer = new List<string>();   // ≤100 行

        // 记忆滚动窗口
        private sealed class RoundRec { public string Input, Output; }
        private readonly Queue<RoundRec> memory = new Queue<RoundRec>();

        // 监视缓存
        private class HqCache
        {
            public CapturePoint cp;
            public int owner = -1;
            public float contention;
            public bool enemyContesting;
            public string letter = "?";
        }
        private readonly List<HqCache> hqCaches = new List<HqCache>();
        private readonly Dictionary<(int, int), bool> wipeArmed = new Dictionary<(int, int), bool>();

        private static string TeamLabelCn(int t) => t == (int)MatchTeam.Red ? "红" : "蓝";

        private string Label => $"指挥官{TeamLabelCn(Team)}";

        // ================================================================
        // 装配与启动
        // ================================================================

        /// <summary>编辑期装配：只写序列化字段（能在域重载后存活）。</summary>
        public void Init(int team, CommanderConfig config,
                         CommanderMapOverlay overlay, CommanderMapCamera cam,
                         CommanderWeaponSystem weapons, NetworkCommanderState state)
        {
            this.team = team;
            this.config = config;
            this.overlay = overlay;
            this.cam = cam;
            this.weapons = weapons;
            this.state = state;

            if (Application.isPlaying) EnsureRuntime();
        }

        /// <summary>
        /// 运行时部件惰性构建。Start 时服务器未必已激活（TrainingAutoHost 时序不定，
        /// 首轮实测踩坑）→ 由 Update 反复调用直至成功。
        /// </summary>
        private bool runtimeReady;

        private void Start() => EnsureRuntime();

        private void EnsureRuntime()
        {
            if (runtimeReady) return;
            if (!NetworkServer.active) return;

            if (team < 0 || config == null || overlay == null || cam == null ||
                weapons == null)
            {
                Debug.LogError($"[{name}] 注入不完整（team={team}），指挥官禁用");
                enabled = false;
                runtimeReady = true;
                return;
            }

            llm = new LlmRestClient(config);
            tools = new CommanderToolContext(this);
            logger = CommanderRoundLogger.Create(TeamLabelCn(team));

            weapons.Init(config, team, overlay);
            weapons.Deployed += (num, txt) => PushEvent(txt);
            weapons.RadarFinalScan += OnRadarFinalScan;

            CacheHqs();
            CacheWipes(initial: true);

            runtimeReady = true;
            Debug.Log($"[{Label}] 运行时资源就绪");
        }
        /// <summary>由 GateController 在服务端就绪后调用：进入开局部署阶段。</summary>
        public void BeginOpening()
        {
            if (CurrentPhase != Phase.Idle) return;
            CurrentPhase = Phase.Opening;
            Debug.Log($"[{Label}] 进入开局部署阶段");
        }

        // ================================================================
        // 主泵（Update 轮询 + 单飞异步轮次）
        // ================================================================

        private void Update()
        {
            if (!NetworkServer.active) return;
            EnsureRuntime();
            if (!runtimeReady) return;
            if (CurrentPhase is not (Phase.Opening or Phase.Active)) return;
            if (busy) return;
            if (!IsDue()) return;
            if (Time.time < minGapUntil) return;

            string reason = CurrentPhase == Phase.Opening
                ? "开局部署"
                : (manualTriggerPending ? manualTriggerReason : "空闲观察");

            _ = ExecuteRoundAsync(reason);
        }

        private bool IsDue()
        {
            if (CurrentPhase == Phase.Opening) return true;

            if (manualTriggerPending) return true;

            float interval = Config.idleInterval;
            if (wakeOverrideSec.HasValue)
            {
                interval = wakeOverrideSec.Value;
                wakeOverrideSec = null;          // 一次性
            }
            return Time.time - lastOutputAt >= interval;
        }

        /// <summary>wait 工具回调。</summary>
        public void RequestWakeAfter(float seconds)
        {
            wakeOverrideSec = Mathf.Clamp(seconds, Config.waitMin, Config.waitMax);
        }

        // ================================================================
        // 一轮完整执行
        // ================================================================

        private async Task ExecuteRoundAsync(string reason)
        {
            busy = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await RunRoundInnerAsync(reason, sw);
            }
            catch (Exception e)
            {
                Debug.LogError($"[{Label}] 轮次异常: {e}");
                EnterDormant($"轮次异常 {e.Message}");
                SettleOpeningIfPossible(degraded: true);
            }
            finally
            {
                lastOutputAt = Time.time;
                minGapUntil = Time.time + Config.minRoundGap;
                busy = false;
            }
        }

        // ================================================================
        // 开局部署（单发制，用户定案）：每方仅一次对话请求；该响应中包含的
        // squad_order 全部执行后即落定解除门控。缺失小队交由 Reassert /
        // FSM 自主选点兜底，不再有催促轮/重复部署请求。
        // ================================================================

        private async Task RunRoundInnerAsync(string reason,
                                              System.Diagnostics.Stopwatch sw)
        {
            toolResultLog.Clear();
            squadsOrderedThisRound.Clear();

            bool isOpening = CurrentPhase == Phase.Opening;

            // ---- 快照 ----
            byte[] png = await CapturePngAsync();

            // ---- 本轮输入文本头 ----
            string header = BuildHeaderText(reason);
            if (isOpening)
                header += "\n【要求】请在本次回复中一次性给出全部小队的指令，只调用工具、无需确认。";

            // ---- 组装消息：历史(压缩文本窗口) + 本轮 ----
            var messages = new List<LlmMessage>
            {
                LlmMessage.System(CommanderPrompts.SystemPrompt(Config, Team, openingPhase: isOpening)),
            };
            foreach (var rec in memory)
            {
                messages.Add(LlmMessage.User(rec.Input));
                messages.Add(LlmMessage.Assistant(rec.Output));
            }
            var currentInputForLog = header + "\n[附全局地图快照]";
            messages.Add(LlmMessage.UserImage(png, header));

            // ---- 请求 ----
            float timeout = isOpening ? Config.openingTimeout : Config.roundTimeout;
            var req = new LlmChatRequest { messages = messages, timeoutSeconds = timeout };
            // 双套工具集：门控阶段仅 squad_order，对局阶段全量。
            var schemas = CommanderToolContext.BuildSchemas(openingPhase: isOpening);
            req.tools = schemas;

            var result = await llm.ChatAsync(req);
            if (!result.ok)
            {
                LogRound(reason, currentInputForLog, null, null, result, sw.Elapsed.TotalSeconds);
                Debug.LogError($"[{Label}] 请求失败: {result.error} → 退化休眠");
                EnterDormant(result.error);
                SettleOpeningIfPossible(degraded: true);
                return;
            }

            var collectedAnalysis = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.content))
                collectedAnalysis.Add(result.content.Trim());

            int iteration = 0;
            var executedToolLines = new List<string>();

            if (isOpening)
            {
                // 单发制：仅执行本次响应携带的工具调用（通常一次含全部小队），
                // 不再回传结果续问；执行完即视为本方部署落定。
                iteration = 1;

                if (result.toolCalls != null && result.toolCalls.Count > 0)
                {
                    foreach (var call in result.toolCalls)
                    {
                        string cname = call["name"]?.ToString() ?? "";
                        JToken cargs = call["arguments"];
                        string text = ExecuteToolTracked(cname, cargs);
                        executedToolLines.Add($"{cname}({call["arguments_raw"]}) → {CmdTrunc(text.Replace("\n", " "), 60)}");
                    }
                }

                LogRound(reason, currentInputForLog,
                         collectedAnalysis.Count > 0 ? string.Join("\n", collectedAnalysis) : "(无文本)",
                         executedToolLines, result, sw.Elapsed.TotalSeconds);

                memory.Enqueue(new RoundRec
                {
                    Input = "[开局部署] 已由指挥官完成初始布防",
                    Output = BuildOutputSummary(
                        collectedAnalysis.Count > 0 ? string.Join("\n", collectedAnalysis) : "(无文本)",
                        executedToolLines),
                });
                while (memory.Count > Config.memoryRounds) memory.Dequeue();

                ConsumeTriggers(reason);

                int coveredSquads = squadsOrderedThisRound.Count;
                Debug.Log($"[{Label}] 开局部署完成 覆盖={coveredSquads}/{Config.squadsPerTeam} " +
                          $"耗时={sw.Elapsed.TotalSeconds:F1}s " +
                          $"tokens={result.promptTokens}/{result.completionTokens} " +
                          $"analysis=\"{CmdTrunc(collectedAnalysis.Count > 0 ? string.Join("\n", collectedAnalysis) : "(无文本)", 100)}\"");

                SettleOpening(degraded: false);
                return;
            }

            // ---- Active 阶段：完整多轮工具循环 ----
            while (result.toolCalls != null && result.toolCalls.Count > 0
                   && iteration < Config.maxToolIterations)
            {
                iteration++;

                var rawCalls = new JArray();
                foreach (var c in result.toolCalls)
                {
                    rawCalls.Add(new JObject
                    {
                        ["type"] = "function",
                        ["id"] = c["id"]?.ToString(),
                        ["function"] = new JObject
                        {
                            ["name"] = c["name"]?.ToString(),
                            ["arguments"] = c["arguments_raw"]?.ToString() ?? "{}",
                        }
                    });
                }
                messages.Add(LlmMessage.AssistantToolCalls(rawCalls, result.content ?? ""));

                foreach (var call in result.toolCalls)
                {
                    string cname = call["name"]?.ToString() ?? "";
                    JToken cargs = call["arguments"];
                    string cid = call["id"]?.ToString() ?? "";

                    executedToolLines.Add($"{cname}({call["arguments_raw"]})");

                    if (cname == "get_snapshot")
                    {
                        messages.Add(LlmMessage.ToolResult(cid,
                            "{\"result\":\"新快照已附加\"}"));
                        byte[] png2 = await CapturePngAsync();
                        messages.Add(LlmMessage.UserImage(png2, "（主动获取的新快照）"));
                        continue;
                    }

                    string text = ExecuteToolTracked(cname, cargs);
                    messages.Add(LlmMessage.ToolResult(cid, text));
                }

                result = await llm.ChatAsync(new LlmChatRequest
                {
                    messages = messages,
                    timeoutSeconds = Config.roundTimeout,
                    tools = schemas,
                });

                if (!result.ok)
                {
                    LogRound(reason, currentInputForLog, string.Join("\n", collectedAnalysis),
                             executedToolLines, result, sw.Elapsed.TotalSeconds);
                    Debug.LogError($"[{Label}] 工具循环请求失败: {result.error} → 退化休眠");
                    EnterDormant(result.error);
                    SettleOpeningIfPossible(degraded: true);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(result.content))
                    collectedAnalysis.Add(result.content.Trim());
            }

            // ---- 记录 & 记忆 & 清理（Active 轮）----
            string analysisFinal = collectedAnalysis.Count > 0
                ? string.Join("\n", collectedAnalysis)
                : "(无文本)";
            LogRound(reason, currentInputForLog, analysisFinal, executedToolLines,
                     result, sw.Elapsed.TotalSeconds);

            memory.Enqueue(new RoundRec
            {
                Input = currentInputForLog,
                Output = BuildOutputSummary(analysisFinal, executedToolLines),
            });
            while (memory.Count > Config.memoryRounds) memory.Dequeue();   // 丢弃的轮次仍在 JSONL

            ConsumeTriggers(reason);

            Debug.Log($"[{Label}] 轮次完成 触发={reason} 迭代={iteration} " +
                      $"耗时={sw.Elapsed.TotalSeconds:F1}s " +
                      $"tokens={result.promptTokens}/{result.completionTokens} " +
                      $"analysis=\"{CmdTrunc(analysisFinal, 120)}\"");
        }

        private void SettleOpening(bool degraded)
        {
            if (CurrentPhase == Phase.Idle) return;
            bool wasOpening = CurrentPhase == Phase.Opening;
            if (degraded) CurrentPhase = Phase.Dormant;
            else CurrentPhase = Phase.Active;
            // 空闲计时起点由 GateController 在"双方全部落定、门控正式解除"时
            // 统一重置（NotifyMatchStarted）——单侧重置会先于解禁空转一轮（实测踩坑）。

            if (wasOpening || degraded)
            {
                Debug.Log($"[{Label}] 开局部署落定 degraded={degraded}");
                OpeningSettled?.Invoke(degraded);
                Commanders.RecomputeStub(this);
            }
        }

        private void SettleOpeningIfPossible(bool degraded)
        {
            // 失败路径的收尾：只在还处于开局或活跃退化时需要通知门控一次。
            if (CurrentPhase == Phase.Opening)
                SettleOpening(degraded);
        }

        /// <summary>门控 watchdog 兜底：强制本方按退化落定（仅在仍处开局时有效）。</summary>
        public void ForceDegradeForGate()
        {
            if (CurrentPhase == Phase.Opening)
                SettleOpening(degraded: true);
        }

        // ================================================================
        // 休眠 / 探活 / 恢复
        // ================================================================

        private void EnterDormant(string why)
        {
            if (CurrentPhase == Phase.Dormant) return;
            CurrentPhase = Phase.Dormant;
            Debug.LogWarning($"[{Label}] 进入休眠（{why}），SquadCommander 接管，" +
                             $"{Config.probeInterval}s 后探活");
            Commanders.RecomputeStub(this);
            StartCoroutine(ProbeRoutine());
        }

        private IEnumerator ProbeRoutine()
        {
            yield return new WaitForSeconds(Mathf.Min(Config.probeInterval, 10f));

            while (CurrentPhase == Phase.Dormant && enabled)
            {
                bool ok = false;
                yield return ProbeOnce(v => ok = v);
                if (ok)
                {
                    Debug.Log($"[{Label}] 探活成功，立即恢复指挥并重新快照");
                    CurrentPhase = Phase.Active;
                    lastOutputAt = -999f;             // 立即可跑
                    manualTriggerPending = true;
                    manualTriggerReason = "探活恢复";
                    Commanders.RecomputeStub(this);
                    yield break;
                }
                yield return new WaitForSeconds(Config.probeInterval);
            }
        }

        /// <summary>单次探活（UnityWebRequest 主线程 await 桥接回协程）。</summary>
        private IEnumerator ProbeOnce(Action<bool> done)
        {
            var tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = llm.ProbeAsync(5).ContinueWith(t => tcs.TrySetResult(t.Result),
                TaskScheduler.Default);
            while (!tcs.Task.IsCompleted) yield return null;
            done(tcs.Task.Result);
        }

        // ================================================================
        // 触发源监视（Update 低频调用走此处的旗标）
        // ================================================================

        private float hqPollAccum;
        private const float HqPollPeriod = 1f;

        private void FixedUpdate()
        {
            if (!NetworkServer.active) return;
            if (CurrentPhase != Phase.Active) return;

            hqPollAccum += Time.fixedDeltaTime;
            if (hqPollAccum < HqPollPeriod) return;
            hqPollAccum -= HqPollPeriod;

            PollHqTransitions();
            PollSquadWipes();
            ReassertObjectives();
        }

        private void PollHqTransitions()
        {
            // 编辑期 Init 时 MatchManager 尚未启动 → 运行时就绪后惰性重建缓存。
            if (hqCaches.Count == 0 && NetworkMatchManager.Instance != null)
                CacheHqs();

            var mm = NetworkMatchManager.Instance;
            if (mm == null) return;

            // PHASE10 调优（用户定案）：据点类触发仅保留【己方据点失去点位保护
            // （中立化）】。完全争夺/占领预警只记事件流、不触发轮次——此前三类
            // 全开会高频打断 LLM 导致响应积压。
            foreach (var cache in hqCaches)
            {
                var cp = cache.cp;
                if (cp == null) continue;

                int prevOwner = cache.owner;       // 上一轮询的归属
                int newOwner = cp.ownerTeam;

                cp.GetTeamCounts(out int redN, out int blueN);
                bool myEnemyIsRed = Team == (int)MatchTeam.Blue;
                bool enemyPresent = myEnemyIsRed ? redN > 0 : blueN > 0;

                // 事件流记录（随下轮附带，不触发）：
                if (prevOwner >= 0 && newOwner >= 0 && prevOwner != newOwner)
                    PushEvent($"HQ-{cache.letter} 完全争夺（{(newOwner == (int)MatchTeam.Red ? "红方" : "蓝方")}占领）");
                if (newOwner >= 0 && enemyPresent && !cache.enemyContesting)
                    PushEvent($"HQ-{cache.letter} 被敌方争夺（占领预警）");

                // 触发：己方据点失去点位保护（我方有主 → 非我方归属）。
                if (prevOwner == Team && newOwner != Team)
                {
                    PushEvent($"HQ-{cache.letter} 中立化");
                    manualTriggerPending = true;
                    manualTriggerReason = "我方据点失去保护";
                }

                cache.owner = newOwner;
                cache.enemyContesting = newOwner >= 0 && enemyPresent;
            }
        }

        private void PollSquadWipes()
        {
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);

            var alive = new Dictionary<(int, int), int>();
            foreach (var c in buf)
            {
                if (c == null || c.teamId < 0 || c.squadId < 0) continue;
                if (c.teamId != Team) continue;               // 只监视己方全歼
                var k = (c.teamId, c.squadId);
                if (!c.IsDead) alive[k] = (alive.TryGetValue(k, out int n) ? n : 0) + 1;
            }

            // 全部己方小队键（含当前为 0 的）
            EnsureWipeKeys(buf);
            foreach (var kv in new List<KeyValuePair<(int, int), bool>>(wipeArmed))
            {
                int nowAlive = alive.TryGetValue(kv.Key, out int n) ? n : 0;
                if (kv.Value && nowAlive == 0)
                {
                    int num = kv.Key.Item2 + 1;
                    var g = SquadAvgGrid(buf, kv.Key.Item2);
                    PushEvent($"小队{num} 全歼警报 @({g.x:F0},{g.y:F0})");
                    wipeArmed[kv.Key] = false;
                    manualTriggerPending = true;
                    manualTriggerReason = "小队全歼";
                }
                else if (nowAlive > 0)
                {
                    wipeArmed[kv.Key] = true;                 // 重臂
                }
            }
        }

        private void EnsureWipeKeys(List<NetworkCombatant> buf)
        {
            foreach (var c in buf)
            {
                if (c == null || c.teamId != Team || c.squadId < 0) continue;
                var k = (c.teamId, c.squadId);
                if (!wipeArmed.ContainsKey(k)) wipeArmed[k] = true;
            }
        }

        private Vector2 SquadAvgGrid(List<NetworkCombatant> buf, int squadId)
        {
            Vector3 sum = Vector3.zero; int n = 0;
            foreach (var c in buf)
            {
                if (c == null || c.teamId != Team || c.squadId != squadId) continue;
                sum += c.transform.position; n++;
            }
            return n > 0 ? Overlay.WorldToGrid(sum / n) : Vector2.zero;
        }

        private void OnRadarFinalScan()
        {
            // 侦测结果报文（含瞬时报点坐标——Q23:b 的唯一空间例外）
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);
            var marks = new List<string>();
            foreach (var c in buf)
            {
                if (c == null || c.IsDead || c.teamId == Team) continue;
                if (!c.IsMarked || c.markedByTeam != Team) continue;
                var g = Overlay.WorldToGrid(c.transform.position);
                marks.Add($"({g.x:F0},{g.y:F0})");
            }
            PushEvent(marks.Count > 0
                ? $"侦测完成：标记敌军{marks.Count}名 {string.Join(" ", marks)}"
                : "侦测完成：范围内无敌军");

            manualTriggerPending = true;
            manualTriggerReason = "广域侦测结束";
        }

        private void CacheHqs()
        {
            hqCaches.Clear();
            var mm = NetworkMatchManager.Instance;
            if (mm == null) return;
            foreach (var cp in mm.capturePoints)
            {
                if (cp == null) continue;
                hqCaches.Add(new HqCache
                {
                    cp = cp,
                    owner = cp.ownerTeam,
                    contention = cp.contention,
                    letter = string.IsNullOrEmpty(cp.letter) ? "?" : cp.letter,
                });
            }
        }

        private void CacheWipes(bool initial)
        {
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);
            EnsureWipeKeys(buf);
        }

        // ================================================================
        // 工具执行包装（开局覆盖度统计 + 结果可观测性）
        // ================================================================

        private readonly List<string> toolResultLog = new List<string>();

        // 本回合已成功下令的小队（防同回合反复对同一队打转，PHASE10 v3）。
        private readonly HashSet<int> squadsOrderedThisRound = new HashSet<int>();

        /// <summary>同一轮内二次下令返回 true（首次登记 false 并放行）。由 ToolContext 调用。</summary>
        public bool TryMarkSquadOrderedThisRound(int squadId)
        {
            if (squadsOrderedThisRound.Contains(squadId)) return true;
            squadsOrderedThisRound.Add(squadId);
            return false;
        }

        private string ExecuteToolTracked(string name, JToken args)
        {
            string raw = args is JObject jo ? jo.ToString(Newtonsoft.Json.Formatting.None) : args?.ToString();
            string result = tools.Execute(name, args);

            toolResultLog.Add($"{name}({raw}) → {CmdTrunc(result.Replace("\n", " "), 80)}");
            Debug.Log($"[{Label}] 工具 {name}({CmdTrunc(raw, 50)}) → {CmdTrunc(result, 70)}");

            if (name == "squad_order")
            {
                // 仅成功结果入库（再主张缓存 + 客户端同步）。
                bool ok = result.Contains("\"result\":\"完成");
                if (ok)
                {
                    try
                    {
                        int sn = -1; float gx = 0f, gz = 0f;

                        var cellTok = args?["cell"];
                        var sNum = args?["squadNumber"]?.ToString();
                        if (!int.TryParse(sNum, out sn)) sn = -1;

                        if (!string.IsNullOrEmpty(cellTxtOf(args)))
                        {
                            // cell 形态：由 Overlay 解析格中心。
                            string c = cellTxtOf(args);
                            if (overlay.TryCellToGrid(c, out gx, out gz)) { /* ok */ }
                            else { gx = gz = 0f; }
                        }
                        else
                        {
                            var v = CommanderToolContext.ResolveArgs(args, "x", "z");
                            gx = Mathf.Clamp(v[0], 0f, overlay.MapWidth);
                            gz = Mathf.Clamp(v[1], 0f, overlay.MapLength);
                        }

                        StoreLastOrder(sn, gx, gz);
                        State?.SetSquadObjective(Team, sn - 1, gx, gz);
                    }
                    catch { /* 参数异常时由 ToolContext 已回传错误，本轮跳过缓存 */ }
                }
            }
            return result;
        }

        private static string cellTxtOf(JToken args) =>
            args?["cell"]?.ToString()?.Trim();

        // ================================================================
        // 信息缓冲 / 文本构建
        // ================================================================

        private void PushEvent(string line)
        {
            double dur = NetworkCommanderState.Instance != null &&
                         NetworkCommanderState.Instance.matchStarted
                ? NetworkTime.time - NetworkCommanderState.MatchStartedAt
                : 0.0;
            infoBuffer.Add($"[+{FmtDur(dur)}] {line}");
            while (infoBuffer.Count > 100) infoBuffer.RemoveAt(0);
        }

        private void InfoBufferToNothing() { /* 保留缓冲 */ }

        private void ConsumeTriggers(string reason)
        {
            infoBuffer.Clear();
            manualTriggerPending = false;
            manualTriggerReason = null;
        }

        private string BuildHeaderText(string reason)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"【触发】{reason}");

            double dur = 0;
            if (NetworkCommanderState.Instance != null &&
                NetworkCommanderState.Instance.matchStarted)
                dur = NetworkTime.time - NetworkCommanderState.MatchStartedAt;
            sb.AppendLine($"【对局时长】{FmtDur(dur)}");

            // 小队名册（非空间兜底 Q23:b）
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);
            var aliveCnt = new Dictionary<int, int>();
            foreach (var c in buf)
            {
                if (c == null || c.teamId != Team || c.squadId < 0) continue;
                if (!c.IsDead) aliveCnt[c.squadId] =
                    (aliveCnt.TryGetValue(c.squadId, out int n) ? n : 0) + 1;
            }
            var roster = new List<string>();
            for (int s = 0; s < Config.squadsPerTeam; s++)
            {
                int n = aliveCnt.TryGetValue(s, out int c) ? c : 0;
                roster.Add($"小队{s + 1}:{n}/5人");
            }
            sb.AppendLine($"【小队】{string.Join("; ", roster)}");

            // HQ 归属与争夺值
            var mm = NetworkMatchManager.Instance;
            if (mm != null)
            {
                var hqs = new List<string>();
                foreach (var cp in mm.capturePoints)
                {
                    if (cp == null) continue;
                    string owner = cp.ownerTeam == (int)MatchTeam.Red ? "红方"
                                 : cp.ownerTeam == (int)MatchTeam.Blue ? "蓝方" : "中立";
                    hqs.Add($"HQ-{cp.letter}{owner}{cp.contention:+0;-0;0}");
                }
                sb.AppendLine($"【据点】{string.Join("; ", hqs)}");
            }

            // 事件流
            sb.AppendLine(infoBuffer.Count > 0
                ? "【上轮以来事件】\n" + string.Join("\n", infoBuffer)
                : "【上轮以来事件】(无)");

            if (reason == "开局部署")
                sb.AppendLine("【任务】请在本次回复中一次性给出全部小队的部署指令。");

            return sb.ToString().TrimEnd();
        }

        private static string BuildOutputSummary(string analysis, List<string> toolLines)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("analysis: ").Append(analysis);
            if (toolLines != null && toolLines.Count > 0)
                sb.Append("\n动作: ").Append(string.Join("; ", toolLines));
            return sb.ToString();
        }

        private static string FmtDur(double sec)
        {
            int total = (int)Math.Max(0, sec);
            return $"{total / 60:D2}:{total % 60:D2}";
        }

        private static string CmdTrunc(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

        // ================================================================
        // 目标再主张：重部署会清除 commanderObjective（FSM 契约），由服务器周期性
        // 把该小队最近一次指令重新下发，避免空窗期被自动选点吞没（实测踩坑）。
        // ================================================================

        private readonly Dictionary<int, (float gx, float gz)> lastOrders = new();
        private float nextReassert;
        private bool importedStateObjectives;

        private void ReassertObjectives()
        {
            if (!importedStateObjectives)
            {
                importedStateObjectives = true;
                if (State != null)
                    foreach (var o in State.objectives)
                        if (o.team == Team) lastOrders[o.squad] = (o.gx, o.gz);
            }

            if (Time.time < nextReassert) return;
            nextReassert = Time.time + 2f;

            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);
            foreach (var c in buf)
            {
                if (c == null || c.teamId != Team || c.IsDead) continue;
                var fsm = c.GetComponent<FSMAIController>();
                if (fsm == null || fsm.commanderObjective) continue;
                if (!lastOrders.TryGetValue(c.squadId, out var g)) continue;

                // 单源换算（西南原点笛卡尔式，与快照标尺/HUD 一致）。
                var world = overlay.GridToWorld(g.gx, g.gz);
                world.y = c.transform.position.y;
                fsm.AssignObjective(world);   // 恢复 commanderObjective 权威标记
            }
        }

        private void StoreLastOrder(int squadNumber, float gx, float gz)
        {
            if (squadNumber >= 1 && squadNumber <= Config.squadsPerTeam)
                lastOrders[squadNumber - 1] = (gx, gz);
        }

        /// <summary>ToolContext 成功 squad_order 时回调（公共入口）。</summary>
        public void StoreLastOrderPublic(int squadNumber, float gx, float gz) =>
            StoreLastOrder(squadNumber, gx, gz);

        // ================================================================
        // 日志
        // ================================================================

        private void LogRound(string reason, string inputRepr, string analysis,
                              List<string> tools_, LlmChatResult result, double elapsed)
        {
            logger.Write(new
            {
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                team = Team.ToString(),
                phase = CurrentPhase.ToString(),
                trigger = reason,
                input = inputRepr,
                bufferedEvents = infoBuffer.ToArray(),
                analysis,
                toolsExecuted = tools_?.ToArray(),
                toolResults = toolResultLog.ToArray(),
                response = new
                {
                    finish = result.finishReason,
                    content = result.content,
                    reasoning = result.reasoningContent,
                    toolCalls = result.toolCalls,
                    promptTokens = result.promptTokens,
                    completionTokens = result.completionTokens,
                },
                elapsedSeconds = Math.Round(elapsed, 2),
                error = result.ok ? null : result.error,
            });
        }

        // ================================================================
        // 快照桥接
        // ================================================================

        private Task<byte[]> CapturePngAsync()
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            StartCoroutine(Cam.CaptureRoutine(Team, bytes =>
            {
                if (bytes != null && Config.saveSnapshotPng && logger != null)
                    logger.WriteSnapshot(bytes, $"{DateTime.Now:HHmmss}");
                tcs.TrySetResult(bytes);
            }));
            return tcs.Task;
        }

        // ================================================================
        // 多实例协调（stub 共享）— 静态注册表
        // ================================================================

        private static readonly List<CommanderOrchestrator> registry = new List<CommanderOrchestrator>();

        private void OnEnable() => registry.Add(this);
        private void OnDisable() => registry.Remove(this);

        /// <summary>
        /// 门控正式解除（双方部署完成/超时退化）时由 GateController 调用：
        /// 全体指挥官的空闲计时从这一刻起算。
        /// </summary>
        public static void NotifyMatchStarted()
        {
            foreach (var o in registry)
            {
                if (o == null) continue;
                o.lastOutputAt = Time.time;
                o.minGapUntil = Time.time + o.Config.minRoundGap;
            }
        }

        /// <summary>全局协调器：任一指挥官休眠 → 启用共享 SquadCommander 桩。</summary>
        public static class Commanders
        {
            public static SquadCommander Stub;

            public static void RecomputeStub(CommanderOrchestrator changed)
            {
                if (Stub == null)
                    Stub = FindObjectOfType<SquadCommander>();

                bool anyDormant = false;
                foreach (var o in registry)
                {
                    if (o == null) continue;
                    if (o.CurrentPhase == Phase.Dormant) anyDormant = true;
                }

                if (Stub != null)
                {
                    bool shouldRun = anyDormant;
                    if (Stub.enabled != shouldRun)
                    {
                        Stub.enabled = shouldRun;
                        if (shouldRun) Stub.ImmediateAssign();
                        Debug.Log($"[Commanders] SquadCommander 桩 {(shouldRun ? "启用" : "禁用")}");
                    }
                }
            }
        }
    }
}
