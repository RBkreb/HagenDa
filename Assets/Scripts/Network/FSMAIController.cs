using System.Collections.Generic;
using Mirror;
using Unity.Collections;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking
{
    /// <summary>FSM 状态（PHASE9-FSM.md）。优先级：生存 &gt; 支援 &gt; 战斗；常态为中间态。</summary>
    public enum FsmState { Normal, Combat, Survival, Support }

    /// <summary>FSM 兵种（PHASE9 配备专题：突击/支援/侦察，固定配装）。</summary>
    public enum FsmClass { Assault, Support, Recon }

    /// <summary>
    /// PHASE9 FSM 大脑（服务端权威）。四状态机：常态/战斗/生存/支援。
    ///
    /// 大脑不直接驱动身体：每 10Hz 决策 tick 只写 <see cref="NetworkInputState"/>
    /// （经 <see cref="NetworkAIController.SetIntent"/>），60Hz 执行全部走与
    /// 玩家/ML agent 完全同构的 intent 管线（NetworkPlayerController.SimulateServer
    /// → 3C 力/姿态/开火/装备/标记）。动作空间与 MLAgentBridge 对齐——未来 BC
    /// 示范零转换。决策允许读特权信息（FSMBattleSystem 共享快照 + 射线）。
    ///
    /// M0 骨架：常态（推进要地→巡逻+扫视）+ 死亡/重部署钩子 + 感知批处理入口。
    /// 战斗/生存/支援状态与状态迁移在 M1/M2 接入。
    /// </summary>
    [RequireComponent(typeof(NetworkAIController))]
    [RequireComponent(typeof(AgentRaySensor))]
    public class FSMAIController : NetworkBehaviour
    {
        [Header("Identity")]
        [Tooltip("兵种（场景生成时赋值：决定配装与行为分支）。")]
        public FsmClass aiClass = FsmClass.Assault;

        [Header("Look")]
        [Tooltip("视线最大转速（度/秒，PHASE9 规格 180°/s）。")]
        public float turnRate = 180f;
        [Tooltip("常态扫视转速（度/秒）。")]
        public float scanTurnRate = 75f;

        [Header("Normal state")]
        public float patrolRadius = 10f;
        public float arrivalDistance = 2f;
        public float scanIntervalMin = 1.5f;
        public float scanIntervalMax = 2.5f;
        [Tooltip("寻路请求冷却（秒）——59 人自然错峰的相位间隔。")]
        public float repathInterval = 1.5f;

        // ---- refs ----
        private NetworkAIController ai;
        private NetworkPlayerController body;
        private NetworkCombatant combatant;
        private AgentRaySensor sensor;
        private FSMBattleSystem system;

        // ---- brain state ----
        private FsmState state = FsmState.Normal;
        private bool dead;
        private float yaw;      // 指令绝对朝向（度）
        private float pitch;

        // 连续保持的意图（行为代码每 tick 写入，随 intent 下发）
        private Vector2 moveInput;
        private bool sprintHeld, fireHeld, aimHeld, crouchHoldHeld;

        // 单 tick 边沿意图（下发一次即清零）
        private bool edgeJump, edgeCrouch, edgeProne, edgeReload, edgeSwitchFireMode,
                     edgeMark, edgeSlotPrimary, edgeSlotOpt1, edgeSlotOpt2,
                     edgeSlotSpecial, edgeSlotThrowable;

        // ---- pathing ----
        // NavMeshPath 不能在字段初始化器里 new（构造期禁止调 InitializeNavMeshPath，
        // 编辑器实例化 prefab/保存场景都会抛 UnityException）——首次使用时惰性创建。
        private NavMeshPath navPath;
        private Vector3[] corners = new Vector3[0];
        private int cornerIndex;
        private float repathAt;
        private Vector3 pathGoal;

        // ---- normal sub-behavior ----
        private readonly List<StrategicZoneState> zoneBuf = new List<StrategicZoneState>();
        private Vector3 objective;
        private bool hasObjective;
        private float objectiveRefreshAt;
        private bool patrolling;
        private Vector3 patrolPoint;
        private float nextPatrolAt;
        private float nextScanAt;
        private float scanYaw;

        public FsmState State => state;
        public AgentRaySensor Sensor => sensor;

        public override void OnStartServer()
        {
            ai = GetComponent<NetworkAIController>();
            body = GetComponent<NetworkPlayerController>();
            combatant = GetComponent<NetworkCombatant>();
            sensor = GetComponent<AgentRaySensor>();
            system = FSMBattleSystem.Instance;

            yaw = transform.rotation.eulerAngles.y;
            pitch = 0f;
            scanYaw = yaw;
            state = FsmState.Normal;

            if (system != null) system.Register(this);
        }

        private void OnDestroy()
        {
            if (FSMBattleSystem.Instance != null)
                FSMBattleSystem.Instance.Unregister(this);
        }

        // ---------------------------------------------------------------
        // 感知批处理入口（FSMBattleSystem 每 tick 调用）
        // ---------------------------------------------------------------

        /// <summary>感知与指令朝向一致（大脑"正在看"的方向）。</summary>
        public void BuildPerception(NativeArray<RaycastCommand> commands, int offset)
        {
            if (sensor == null) return;
            sensor.BuildBatchCommands(transform.position, yaw,
                combatant != null ? combatant.teamId : -1, commands, offset);
        }

        public void ParsePerception(NativeArray<RaycastHit> results, int offset)
        {
            if (sensor != null) sensor.ParseBatchHits(results, offset);
        }

        /// <summary>由 FSMBattleSystem 错峰调用（无调度器时直接调用）。</summary>
        public void ComputePath(Vector3 target)
        {
            if (navPath == null) navPath = new NavMeshPath();

            if (!NavMesh.SamplePosition(target, out var hit, 5f, NavMesh.AllAreas))
                return;
            if (!NavMesh.CalculatePath(transform.position, hit.position,
                    NavMesh.AllAreas, navPath))
                return;

            var cs = navPath.corners;
            if (cs == null || cs.Length < 2) return;

            corners = cs;
            cornerIndex = 1;   // corners[0] = 自身当前位置
        }

        // ---------------------------------------------------------------
        // 决策 tick（10Hz 固定步长）
        // ---------------------------------------------------------------

        public void DecisionTick(float dt, List<FSMBattleSystem.CombatantView> snapshot)
        {
            // 死亡：NetworkAIController 自身有 dead 零输入门，无需重复下发。
            if (dead) return;

            MaybeApplyClassLoadout();

            // M0：仅常态。战斗/生存/支援状态与迁移 M1/M2 接入。
            UpdateNormal(dt, snapshot);

            PushIntent();
        }

        // ---------------------------------------------------------------
        // 兵种配装（PHASE9 配备专题：突击/支援/侦察固定配装）
        // 延迟到首个决策 tick 应用——所有组件的 OnStartServer 已跑完。
        // ---------------------------------------------------------------

        private bool loadoutApplied;

        private void MaybeApplyClassLoadout()
        {
            if (loadoutApplied) return;
            loadoutApplied = true;

            var eq = GetComponent<NetworkEquipment>();
            if (eq == null) return;

            var lo = new LoadoutDefinition();
            switch (aiClass)
            {
                case FsmClass.Support:
                    lo.optional1 = eq.IndexOfType(EquipmentType.LargeSupplyCrate);
                    lo.optional2 = eq.IndexOfType(EquipmentType.Interceptor);
                    lo.special = eq.IndexOfType(EquipmentType.Defibrillator);
                    lo.throwable = eq.IndexOfType(EquipmentType.SmokeGrenade);
                    break;
                case FsmClass.Recon:
                    lo.optional1 = eq.IndexOfType(EquipmentType.Jammer);
                    lo.optional2 = eq.IndexOfType(EquipmentType.DeployBeacon);
                    lo.special = eq.IndexOfType(EquipmentType.Sensor);
                    lo.throwable = eq.IndexOfType(EquipmentType.EmpGrenade);
                    break;
                default:    // Assault
                    lo.optional1 = eq.IndexOfType(EquipmentType.GrenadeLauncher);
                    lo.optional2 = eq.IndexOfType(EquipmentType.QuickDash);
                    lo.special = eq.IndexOfType(EquipmentType.HealingSyringe);
                    lo.throwable = eq.IndexOfType(EquipmentType.Grenade);
                    break;
            }

            if (eq.ValidateLoadout(lo, out _))
                eq.ApplyLoadout(lo);
        }

        // ---------------------------------------------------------------
        // 常态：推进要地（冲刺）→ 到位后绕点巡逻 + 周期扫视
        // ---------------------------------------------------------------

        private void UpdateNormal(float dt, List<FSMBattleSystem.CombatantView> snapshot)
        {
            RefreshObjective();

            bool atObjective = hasObjective &&
                FlatDistance(transform.position, objective) <= patrolRadius;

            if (!atObjective)
            {
                patrolling = false;
                RequestPath(objective);
                FollowPath(dt, sprint: true);
            }
            else
            {
                Patrol(dt);
            }
        }

        /// <summary>M0：就近的未占领/敌方要地（M1 换成按小队规则分配）。</summary>
        private void RefreshObjective()
        {
            if (hasObjective && Time.time < objectiveRefreshAt) return;
            objectiveRefreshAt = Time.time + 5f;

            var registry = StrategicZoneRegistry.Instance;
            if (registry == null || registry.Count == 0) return;

            zoneBuf.Clear();
            registry.GetZones(zoneBuf);
            if (zoneBuf.Count == 0) return;

            int myTeam = combatant != null ? combatant.teamId : -1;

            StrategicZoneState best = default, fallback = default;
            float bestD = float.MaxValue, fallbackD = float.MaxValue;
            bool found = false, foundAny = false;

            for (int i = 0; i < zoneBuf.Count; i++)
            {
                var z = zoneBuf[i];
                float d = FlatDistance(transform.position, z.position);
                if (d < fallbackD) { fallbackD = d; fallback = z; foundAny = true; }
                if (z.ownerTeam != myTeam && d < bestD) { bestD = d; best = z; found = true; }
            }

            if (!foundAny) return;
            var chosen = found ? best : fallback;

            if (!hasObjective || FlatDistance(objective, chosen.position) > 1f)
            {
                objective = chosen.position;
                hasObjective = true;
                RequestPath(objective, force: true);
            }
        }

        private void Patrol(float dt)
        {
            if (!patrolling ||
                FlatDistance(transform.position, patrolPoint) <= arrivalDistance ||
                Time.time >= nextPatrolAt)
            {
                patrolling = true;
                nextPatrolAt = Time.time + 6f;
                Vector2 o = Random.insideUnitCircle * patrolRadius;
                patrolPoint = objective + new Vector3(o.x, 0f, o.y);
                RequestPath(patrolPoint, force: true);
            }

            if (FlatDistance(transform.position, patrolPoint) > arrivalDistance + 0.5f)
                FollowPath(dt, sprint: false);   // 巡逻不冲刺
            else
                ScanAround(dt);
        }

        private void FollowPath(float dt, bool sprint)
        {
            if (cornerIndex >= corners.Length || corners.Length == 0)
            {
                moveInput = Vector2.zero;
                sprintHeld = false;
                ScanAround(dt);
                return;
            }

            Vector3 corner = corners[cornerIndex];
            Vector3 flat = corner - transform.position;
            flat.y = 0f;

            if (flat.magnitude <= arrivalDistance)
            {
                cornerIndex++;
                return;
            }

            // 朝路径点转向（全速），沿世界方向移动（移动向量按当前 yaw 本地系
            // 分解，转身完成前呈斜向/倒退步，与玩家手感一致）。
            float dirYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            yaw = Mathf.MoveTowardsAngle(yaw, dirYaw, turnRate * dt);
            pitch = Mathf.MoveTowards(pitch, 0f, turnRate * dt);
            MoveInWorldDir(flat, sprint);
        }

        /// <summary>常态扫视：周期性随机换朝向（逐步覆盖 360° 盲区）。</summary>
        private void ScanAround(float dt)
        {
            moveInput = Vector2.zero;
            sprintHeld = false;

            if (Time.time >= nextScanAt)
            {
                nextScanAt = Time.time + Random.Range(scanIntervalMin, scanIntervalMax);
                scanYaw = yaw + Random.Range(0f, 360f);
            }
            yaw = Mathf.MoveTowardsAngle(yaw, scanYaw, scanTurnRate * dt);
            pitch = Mathf.MoveTowards(pitch, 0f, scanTurnRate * dt);
        }

        // ---------------------------------------------------------------
        // 寻路请求（冷却错峰；force 用于目标/巡逻点变更时立即重算）
        // ---------------------------------------------------------------

        private void RequestPath(Vector3 target, bool force = false)
        {
            if (!force && Time.time < repathAt) return;

            bool exhausted = cornerIndex >= corners.Length;
            bool goalMoved = corners.Length > 0 && FlatDistance(pathGoal, target) > 3f;
            if (!force && corners.Length > 0 && !exhausted && !goalMoved) return;

            repathAt = Time.time + repathInterval;
            pathGoal = target;
            corners = new Vector3[0];
            cornerIndex = 0;

            if (system != null) system.EnqueuePath(this, target);
            else ComputePath(target);
        }

        // ---------------------------------------------------------------
        // 意图输出
        // ---------------------------------------------------------------

        private void MoveInWorldDir(Vector3 worldDir, bool sprint)
        {
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 0.0001f)
            {
                moveInput = Vector2.zero;
                sprintHeld = false;
                return;
            }
            worldDir.Normalize();

            float rad = yaw * Mathf.Deg2Rad;
            Vector3 fwd = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);

            moveInput = new Vector2(Vector3.Dot(worldDir, right), Vector3.Dot(worldDir, fwd));
            sprintHeld = sprint;
        }

        private void PushIntent()
        {
            var s = default(NetworkInputState);
            s.move = moveInput;
            s.yaw = yaw;
            s.pitch = pitch;
            s.sprint = sprintHeld;
            s.fire = fireHeld;
            s.aim = aimHeld;
            s.crouchHold = crouchHoldHeld;

            s.jump = edgeJump;
            s.crouchToggle = edgeCrouch;
            s.proneToggle = edgeProne;
            s.reload = edgeReload;
            s.switchFireMode = edgeSwitchFireMode;
            s.mark = edgeMark;
            s.slotPrimary = edgeSlotPrimary;
            s.slotOpt1 = edgeSlotOpt1;
            s.slotOpt2 = edgeSlotOpt2;
            s.slotSpecial = edgeSlotSpecial;
            s.slotThrowable = edgeSlotThrowable;

            if (ai != null) ai.SetIntent(s);

            // 边沿意图一次性：NetworkAIController 下发后会自行清零自己的副本，
            // 本地副本同样清零，避免下一 tick 重复触发。
            edgeJump = edgeCrouch = edgeProne = edgeReload = edgeSwitchFireMode = false;
            edgeMark = edgeSlotPrimary = edgeSlotOpt1 = edgeSlotOpt2 = false;
            edgeSlotSpecial = edgeSlotThrowable = false;
        }

        // ---------------------------------------------------------------
        // 生命周期钩子（NetworkPlayerHealth 调用）
        // ---------------------------------------------------------------

        /// <summary>死亡钩子：冻结大脑，清空运动/路径。</summary>
        public void SetDead(bool value)
        {
            dead = value;
            if (value)
            {
                moveInput = Vector2.zero;
                sprintHeld = fireHeld = aimHeld = crouchHoldHeld = false;
                ClearEdges();
                ClearPath();
            }
        }

        /// <summary>重部署钩子：从新位置重新开始常态。</summary>
        public void OnRedeploy(Vector3 pos)
        {
            dead = false;
            ClearPath();
            hasObjective = false;
            patrolling = false;
            state = FsmState.Normal;
            yaw = transform.rotation.eulerAngles.y;
            pitch = 0f;
        }

        private void ClearPath()
        {
            corners = new Vector3[0];
            cornerIndex = 0;
        }

        private void ClearEdges()
        {
            edgeJump = edgeCrouch = edgeProne = edgeReload = edgeSwitchFireMode = false;
            edgeMark = edgeSlotPrimary = edgeSlotOpt1 = edgeSlotOpt2 = false;
            edgeSlotSpecial = edgeSlotThrowable = false;
        }

        // ---------------------------------------------------------------
        // 工具
        // ---------------------------------------------------------------

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            d.y = 0f;
            return d.magnitude;
        }

        private Vector3 EyePos()
        {
            return transform.position + Vector3.up * 1.5f;
        }
    }
}
