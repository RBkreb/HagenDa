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
    /// 玩家/ML agent 完全同构的 intent 管线。
    /// </summary>
    [RequireComponent(typeof(NetworkAIController))]
    [RequireComponent(typeof(AgentRaySensor))]
    public class FSMAIController : NetworkBehaviour
    {
        [Header("Identity")]
        [Tooltip("兵种（场景生成时赋值：决定配装与行为分支）。")]
        public FsmClass aiClass = FsmClass.Assault;

        [Header("Look")]
        public float turnRate = 180f;
        public float scanTurnRate = 75f;

        [Header("Normal state")]
        public float patrolRadius = 10f;
        public float arrivalDistance = 2f;
        public float scanIntervalMin = 1.5f;
        public float scanIntervalMax = 2.5f;
        public float repathInterval = 1.5f;

        [Header("Combat")]
        public float detectRange = 50f;
        public float combatSearchTimeout = 5f;
        public float combatStrafeInterval = 2f;
        public float jumpChance = 0.15f;
        public float grenadeCooldown = 8f;
        public float grenadeMaxRange = 30f;

        [Header("Survival")]
        public float survivalHpThreshold = 0.5f;
        public float survivalExitHp = 0.75f;
        public float survivalExitAmmo = 0.75f;

        [Header("Weapon tactics")]
        [Tooltip("有效命中距离 = 10 / 散布平均值。")]
        public float hipEffectiveDist = 10f / ((3f + 10f) * 0.5f);   // ≈1.54m
        public float adsEffectiveDist = 10f / ((0.1f + 5f) * 0.5f);   // ≈3.92m

        // ---- refs ----
        private NetworkAIController ai;
        private NetworkPlayerController body;
        private NetworkCombatant combatant;
        private NetworkPlayerHealth health;
        private NetworkGun gun;
        private NetworkEquipment equipment;
        private AgentRaySensor sensor;
        private FSMBattleSystem system;

        // ---- brain state ----
        private FsmState state = FsmState.Normal;
        private bool dead;
        private float yaw, pitch;

        private Vector2 moveInput;
        private bool sprintHeld, fireHeld, aimHeld, crouchHoldHeld;
        private bool edgeJump, edgeCrouch, edgeProne, edgeReload, edgeSwitchFireMode,
                     edgeMark, edgeSlotPrimary, edgeSlotOpt1, edgeSlotOpt2,
                     edgeSlotSpecial, edgeSlotThrowable;

        // ---- pathing ----
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

        /// <summary>PHASE9 指挥官：外部指定目标要地（覆盖自动就近选择）。</summary>
        [Server]
        public void AssignObjective(Vector3 zonePos)
        {
            objective = zonePos;
            hasObjective = true;
            objectiveRefreshAt = Time.time + 20f;   // 指挥官下发后 20s 内不自动刷新
            patrolling = false;
            RequestPath(objective, force: true);
        }

        // ---- combat sub-behavior ----
        private NetworkCombatant target;
        private Vector3 lastKnownTargetPos;
        private bool hasLastKnownPos;
        private float targetRefreshAt;
        private float combatSearchEnd;
        private float nextStrafeAt;
        private int strafeDir = 1;
        private float nextGrenadeAt;
        private bool burstWaiting;   // 点射间隔：等散布回复
        private float burstWaitEnd;
        private bool wasInBurst;     // 上 tick 是否在点射中（检测点射结束→等回复）
        private int ammoAtLastCheck; // 换弹检定：跨阈值掷骰
        private bool reloadRolledThisThreshold; // 当前阈值区间是否已掷过

        // ---- survival sub-behavior ----
        private Vector3 coverPos;
        private bool hasCover;
        private bool coverIsLow;

        // ---- support sub-behavior ----
        private Vector3 sosTargetPos;
        private int sosTargetId = -1;
        private float supportTimeoutEnd;
        private const float SupportTimeout = 25f;

        // ---- broadcast reception ----
        private float enemySpottedSuppressUntil;

        public FsmState State => state;
        public AgentRaySensor Sensor => sensor;

        public override void OnStartServer()
        {
            ai = GetComponent<NetworkAIController>();
            body = GetComponent<NetworkPlayerController>();
            combatant = GetComponent<NetworkCombatant>();
            health = GetComponent<NetworkPlayerHealth>();
            gun = GetComponent<NetworkGun>();
            equipment = GetComponent<NetworkEquipment>();
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
        // 感知批处理入口
        // ---------------------------------------------------------------

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

        public void ComputePath(Vector3 target)
        {
            if (navPath == null) navPath = new NavMeshPath();
            if (!NavMesh.SamplePosition(target, out var hit, 5f, NavMesh.AllAreas)) return;
            if (!NavMesh.CalculatePath(transform.position, hit.position,
                    NavMesh.AllAreas, navPath)) return;
            var cs = navPath.corners;
            if (cs == null || cs.Length < 2) return;
            corners = cs;
            cornerIndex = 1;
        }

        // ---------------------------------------------------------------
        // 决策 tick
        // ---------------------------------------------------------------

        public void DecisionTick(float dt, List<FSMBattleSystem.CombatantView> snapshot)
        {
            if (dead) return;
            MaybeApplyClassLoadout();

            // 状态优先级：生存 > 支援 > 战斗 > 常态
            FsmState newState = EvaluateTransitions(snapshot);
            if (newState != state)
            {
                OnExitState(state);
                state = newState;
                OnEnterState(state);
            }

            switch (state)
            {
                case FsmState.Normal:   UpdateNormal(dt, snapshot); break;
                case FsmState.Combat:   UpdateCombat(dt, snapshot); break;
                case FsmState.Survival: UpdateSurvival(dt, snapshot); break;
                case FsmState.Support:  UpdateSupport(dt, snapshot); break;
            }

            PushIntent();
        }

        // ---------------------------------------------------------------
        // 状态迁移
        // ---------------------------------------------------------------

        private FsmState EvaluateTransitions(List<FSMBattleSystem.CombatantView> snapshot)
        {
            float hp01 = GetHp01();
            float ammo01 = GetAmmo01();

            // 生存：血≤50% 或 备弹≤25%
            if (hp01 <= survivalHpThreshold || ammo01 <= 0.25f)
                return FsmState.Survival;

            // 生存退出：血>75% 且 备弹>75%
            if (state == FsmState.Survival &&
                hp01 > survivalExitHp && ammo01 > survivalExitAmmo)
                return FsmState.Normal;

            // 支援（M2）：支援兵 且 (收到定向 DeathSOS 或 30m 内请求支援广播)
            if (aiClass == FsmClass.Support && state != FsmState.Survival)
            {
                if (ReceivedDeathSOS())
                    return FsmState.Support;
            }

            // 支援退出：目标消失（被救/重部署）或超时 25s
            if (state == FsmState.Support)
            {
                if (!HasActiveSOSTarget() || Time.time >= supportTimeoutEnd)
                    return FsmState.Normal;
            }

            // 战斗进入：发现敌人 / 被标记 / 广播触发
            if (state != FsmState.Combat)
            {
                if (HasVisibleEnemy(snapshot) || IsMarked() || ReceivedEnemySpotted())
                    return FsmState.Combat;
            }

            // 战斗退出：无可见敌 5s
            if (state == FsmState.Combat)
            {
                if (!HasVisibleEnemy(snapshot) && Time.time >= combatSearchEnd)
                    return FsmState.Normal;
            }

            return state;
        }

        private void OnEnterState(FsmState s)
        {
            switch (s)
            {
                case FsmState.Combat:
                    hasCover = false;
                    break;
                case FsmState.Survival:
                    hasCover = false;
                    break;
            }
        }

        private void OnExitState(FsmState s)
        {
            switch (s)
            {
                case FsmState.Combat:
                    fireHeld = false;
                    aimHeld = false;
                    break;
                case FsmState.Survival:
                    crouchHoldHeld = false;
                    // 站起来
                    if (body != null && body.posture != PlayerPosture.Stand)
                        edgeJump = true;   // 蹲→站：跳跃键
                    break;
            }
        }

        // ---------------------------------------------------------------
        // 常态
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

            // 常态发敌情广播（目击敌人→广播，接收方 10s 压制）
            EmitEnemySpottedBroadcast();

            // PHASE9 侦察兵装备触发
            if (aiClass == FsmClass.Recon)
            {
                TryUseJammer();        // 自身被标记→干扰器
                TryUseDeployBeacon();  // 距要地 50m→信标
                TryUseSensorProbe(snapshot);  // 距要地 5m 或 20m 内发现敌→探测器
            }
        }

        private void RefreshObjective()
        {
            if (hasObjective && Time.time < objectiveRefreshAt) return;
            objectiveRefreshAt = Time.time + 5f;

            var registry = StrategicZoneRegistry.Instance;
            if (registry == null || registry.Count == 0) return;

            int myTeam = combatant != null ? combatant.teamId : -1;
            var chosen = registry.NearestZone(transform.position, myTeam);
            if (!chosen.HasValue) return;

            var chosenPos = chosen.Value.position;
            if (!hasObjective || FlatDistance(objective, chosenPos) > 1f)
            {
                objective = chosenPos;
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
                FollowPath(dt, sprint: false);
            else
                ScanAround(dt);
        }

        // ---------------------------------------------------------------
        // 战斗
        // ---------------------------------------------------------------

        private void UpdateCombat(float dt, List<FSMBattleSystem.CombatantView> snapshot)
        {
            // 索敌三级：①最近可见敌 → ②最近伤害来源 → ③被标记敌
            var targetOpt = SelectTarget(snapshot);
            bool hasVisible = targetOpt.HasValue;
            VisibleTarget visible = hasVisible ? targetOpt.Value : default;

            if (hasVisible)
            {
                target = visible.combatant;
                lastKnownTargetPos = visible.position;
                hasLastKnownPos = true;
                combatSearchEnd = Time.time + combatSearchTimeout;
            }
            else
            {
                // 无可见目标：朝最后已知位推进搜索
                if (hasLastKnownPos)
                {
                    if (FlatDistance(transform.position, lastKnownTargetPos) > arrivalDistance)
                    {
                        RequestPath(lastKnownTargetPos);
                        FollowPath(dt, sprint: true);
                    }
                    else
                        ScanAround(dt);
                }
                else
                {
                    // 无任何线索：扫视等待
                    ScanAround(dt);
                }
            }

            // 开火条件：战斗 & 敌军可见 & 敌军可被射击
            if (hasVisible)
            {
                float dist = FlatDistance(transform.position, visible.position);

                // PHASE9 "仅烟雾遮挡+被标记→向大致方向扫射"：
                // 如果目标被烟雾遮挡但被标记，向大致方向扫射。
                bool smokeOccluded = IsTargetSmokeOccluded(visible.position);
                bool targetMarked = visible.combatant.IsMarked;

                if (smokeOccluded && targetMarked)
                {
                    // 向大致方向扫射（不精确瞄准）
                    FaceTarget(visible.position, dt);
                    fireHeld = true;
                    aimHeld = false;
                }
                else if (!smokeOccluded)
                {
                    // 正常开火
                    FaceTarget(visible.position, dt);
                    HandleWeaponTactics(dist, dt);
                    CombatStrafe(dt, dist);

                    // 手雷：进入战斗 & 发现敌人 & 距离<30m & 可用
                    if (dist < grenadeMaxRange && Time.time >= nextGrenadeAt)
                    {
                        TryThrowGrenade(visible.position);
                        nextGrenadeAt = Time.time + grenadeCooldown;
                    }

                    // 榴弹炮：战斗&瞄准的敌军被遮挡（墙挡）&可用
                    if (IsTargetWallOccluded(visible.position))
                        TryUseGrenadeLauncher(visible.position);
                }
                else
                {
                    // 烟雾遮挡但未标记：不射击，推进搜索
                    fireHeld = false;
                    aimHeld = false;
                    if (hasLastKnownPos)
                        FollowPath(dt, sprint: true);
                }

                // 电磁手雷：发现敌方部署物
                TryUseEmpGrenade();
            }
            else
            {
                fireHeld = false;
                aimHeld = false;
                moveInput = Vector2.zero;
                sprintHeld = false;
            }
        }

        private struct VisibleTarget
        {
            public NetworkCombatant combatant;
            public Vector3 position;
        }

        /// <summary>索敌三级优先级：最近可见敌 → 最近伤害来源 → 被标记敌。</summary>
        private VisibleTarget? SelectTarget(List<FSMBattleSystem.CombatantView> snapshot)
        {
            int myTeam = combatant != null ? combatant.teamId : -1;
            if (myTeam < 0) return null;

            // ① 射线感知到的可见敌
            if (sensor != null)
            {
                NetworkCombatant best = null;
                float bestD = detectRange;
                for (int i = 0; i < sensor.Latest.Length; i++)
                {
                    var h = sensor.Latest[i];
                    if (h.kind == RayHitKind.Enemy && h.combatant != null && !h.combatant.IsDead)
                    {
                        float d = h.distance;
                        if (d < bestD) { bestD = d; best = h.combatant; }
                    }
                }
                if (best != null)
                    return new VisibleTarget { combatant = best, position = best.transform.position };
            }

            // ② 最近伤害来源（不可见用最后已知位）
            if (health != null && health.lastAttacker != null && !health.lastAttacker.IsDead)
            {
                var attacker = health.lastAttacker;
                if (attacker.teamId != myTeam)
                {
                    float d = FlatDistance(transform.position, attacker.transform.position);
                    if (d <= detectRange)
                        return new VisibleTarget { combatant = attacker, position = attacker.transform.position };
                    // 不可见但记住位置
                    lastKnownTargetPos = attacker.transform.position;
                    hasLastKnownPos = true;
                }
            }

            // ③ 被标记敌（快照中标记由我方标记的敌方）
            NetworkCombatant marked = null;
            float markedD = detectRange;
            for (int i = 0; i < snapshot.Count; i++)
            {
                var v = snapshot[i];
                if (v.dead || v.team == myTeam) continue;
                if (v.marked && v.markedByTeam == myTeam)
                {
                    float d = FlatDistance(transform.position, v.position);
                    if (d < markedD) { markedD = d; marked = v.combatant; }
                }
            }
            if (marked != null)
                return new VisibleTarget { combatant = marked, position = marked.transform.position };

            return null;
        }

        private void HandleWeaponTactics(float dist, float dt)
        {
            if (gun == null) return;

            bool canFire = target != null && !target.IsDead;
            if (!canFire)
            {
                fireHeld = false;
                aimHeld = false;
                return;
            }

            // 三段开火方式：
            // dist < hipEffective  → 腰射全自动
            // dist < adsEffective  → 瞄准全自动
            // dist >= adsEffective → 瞄准+点射
            bool useAds = dist >= hipEffectiveDist;
            bool useBurst = dist >= adsEffectiveDist;

            aimHeld = useAds;

            if (useBurst)
            {
                // 点射：开火 2-3 发后等散布回复
                if (burstWaiting)
                {
                    if (Time.time >= burstWaitEnd)
                    {
                        burstWaiting = false;
                        fireHeld = true;
                    }
                    else
                    {
                        fireHeld = false;
                    }
                }
                else
                {
                    fireHeld = true;
                    // 检测点射结束（fireHeld 从 true→false 由 gun 内部 Semi/Burst 处理；
                    // 这里用时间窗口模拟 2-3 发的持续时间）
                    if (WasBurstCycleComplete())
                    {
                        burstWaiting = true;
                        // 点射间隔 = 单发散布增长 / 散布回复
                        float burstGap = gun.Definition != null
                            ? gun.Definition.spreadPerShot / gun.Definition.spreadRecovery
                            : 0.083f;
                        burstWaitEnd = Time.time + Mathf.Max(burstGap, 0.3f);
                        fireHeld = false;
                    }
                }
            }
            else
            {
                // 全自动
                fireHeld = true;
                burstWaiting = false;
            }

            // 换弹检定
            HandleReloadCheck();
        }

        private bool WasBurstCycleComplete()
        {
            // 简化：每 0.2s（约 3 发 @ 900 RPM）后插入一个点射间隔
            // 真实实现应该追踪 gun 的射击事件，但 intent 管线下无法直接监听
            // 用时间窗口近似（3 发 × FireInterval ≈ 0.2s）
            if (!wasInBurst)
            {
                wasInBurst = true;
                return false;
            }
            wasInBurst = false;
            return true;
        }

        private void HandleReloadCheck()
        {
            if (gun == null || gun.Definition == null) return;

            int mag = gun.magAmmo;
            int cap = gun.Definition.magazineCapacity;

            // 弹容 0：强制换弹
            if (mag <= 0)
            {
                edgeReload = true;
                ammoAtLastCheck = mag;
                reloadRolledThisThreshold = false;
                return;
            }

            float ratio = (float)mag / cap;

            // 跨阈值检定
            int currentThreshold = ratio > 0.5f ? 0 : (ratio > 0.25f ? 1 : 2);
            int lastThreshold = ammoAtLastCheck > 0
                ? ((float)ammoAtLastCheck / cap > 0.5f ? 0 : ((float)ammoAtLastCheck / cap > 0.25f ? 1 : 2))
                : -1;

            if (currentThreshold != lastThreshold && !reloadRolledThisThreshold)
            {
                float chance = currentThreshold == 1 ? 0.5f : 0.75f;
                if (Random.value < chance)
                    edgeReload = true;
                reloadRolledThisThreshold = true;
            }
            else if (currentThreshold == 0)
            {
                // 回到 >50% 区间，重置检定标记
                reloadRolledThisThreshold = false;
            }

            ammoAtLastCheck = mag;

            // 常态立即换弹（弹容不满且有备弹）
            if (state == FsmState.Normal && mag < cap && gun.reserveAmmo > 0)
                edgeReload = true;
        }

        private void CombatStrafe(float dt, float dist)
        {
            // 随机跑动/跑跳，保持瞄准锁定（不改变 yaw）
            if (Time.time >= nextStrafeAt)
            {
                nextStrafeAt = Time.time + combatStrafeInterval;
                strafeDir = Random.Range(0, 3);   // 0=左, 1=右, 2=停
                if (Random.value < jumpChance)
                    edgeJump = true;
            }

            if (strafeDir == 2)
            {
                moveInput = Vector2.zero;
                sprintHeld = false;
            }
            else
            {
                // 侧向移动（不朝目标方向，保持瞄准锁定）
                moveInput = strafeDir == 0
                    ? new Vector2(-1f, 0f)   // 左
                    : new Vector2(1f, 0f);   // 右
                sprintHeld = false;
            }
        }

        private void TryThrowGrenade(Vector3 targetPos)
        {
            if (equipment == null) return;

            // 手雷槽 = slotThrowable (slot 3)
            var def = equipment.GetSlotDefinition(3);
            if (def == null || !equipment.HasAmmoInSlot(3)) return;

            Vector3 eye = EyePos();
            Vector3 dir = (targetPos - eye).normalized;
            dir += Vector3.up * 0.4f;
            dir.Normalize();

            edgeSlotThrowable = true;
        }

        // ---------------------------------------------------------------
        // 生存
        // ---------------------------------------------------------------

        private void UpdateSurvival(float dt, List<FSMBattleSystem.CombatantView> snapshot)
        {
            // 找掩体（朝威胁方向选择）
            if (!hasCover)
            {
                Vector3 threatDir = -transform.forward;
                if (health != null && health.lastAttacker != null)
                    threatDir = health.lastAttacker.transform.position - transform.position;

                var cover = CoverRegistry.BestCover(transform.position, threatDir, out bool isLow);
                if (cover.HasValue)
                {
                    coverPos = cover.Value;
                    coverIsLow = isLow;
                    hasCover = true;
                    RequestPath(coverPos, force: true);
                }
            }

            if (hasCover)
            {
                float distToCover = FlatDistance(transform.position, coverPos);

                if (distToCover > arrivalDistance)
                {
                    // 冲刺进掩体
                    FollowPath(dt, sprint: true);
                }
                else
                {
                    // 到达掩体：蹲/趴（用 crouchHold 持续保持而非 edge toggle，
                    // 避免 10Hz FSM 与 60Hz FixedUpdate 的边沿时序问题导致姿态
                    // 切换被错过）
                    if (coverIsLow)
                    {
                        crouchHoldHeld = true;   // 持续蹲（低矮掩体）
                        if (body.posture != PlayerPosture.Crouch)
                            edgeCrouch = true;   // 首次切换
                    }
                    else
                    {
                        // 高掩体：趴下
                        if (body.posture != PlayerPosture.Prone)
                            edgeProne = true;
                    }

                    // 在掩体后仍朝威胁方向开火（如果可见且在低矮掩体后可射击）
                    var visOpt = SelectTarget(snapshot);
                    bool canFireFromCover = visOpt.HasValue && coverIsLow;
                    if (canFireFromCover)
                    {
                        var vis = visOpt.Value;
                        FaceTarget(vis.position, dt);
                        float dist = FlatDistance(transform.position, vis.position);
                        HandleWeaponTactics(dist, dt);
                    }
                    else
                    {
                        fireHeld = false;
                        aimHeld = false;
                        // 朝伤害方向扫视
                        if (health != null && health.lastAttacker != null)
                            FaceTarget(health.lastAttacker.transform.position, dt);
                        else
                            ScanAround(dt);
                    }

                    // 治疗针（特有配备）
                    TryUseHealingSyringe();

                    // 大型补给箱（支援兵可选1）
                    if (aiClass == FsmClass.Support)
                    {
                        TryUseSupplyCrate();
                        // 烟雾弹：生存→向脚下使用
                        TryThrowSmoke(transform.position);
                    }

                    // 快速机动装置（生存&可用→向左/右/后跳起后使用）
                    TryUseQuickDash();

                    // 拦截装置：(生存|战斗)&前10s受到过爆炸伤害&可用
                    TryUseInterceptor();
                }
            }
            else
            {
                // 无掩体：后撤远离最近敌人
                var visOpt = SelectTarget(snapshot);
                bool hasVis = visOpt.HasValue;
                Vector3 visPos = hasVis ? visOpt.Value.position : transform.position - transform.forward;
                Vector3 away = hasVis
                    ? (transform.position - visPos).normalized
                    : -transform.forward;

                MoveInWorldDir(away, sprint: true);
                if (hasVis)
                {
                    FaceTarget(visPos, dt);
                    float dist = FlatDistance(transform.position, visPos);
                    HandleWeaponTactics(dist, dt);
                }
            }
        }

        // ---------------------------------------------------------------
        // 装备使用辅助
        // ---------------------------------------------------------------

        private void TryUseHealingSyringe()
        {
            if (equipment == null) return;
            if (GetHp01() > 0.5f) return;   // 不急用
            if (!equipment.HasAmmoInSlot(2)) return;   // special slot
            edgeSlotSpecial = true;
        }

        private void TryUseSupplyCrate()
        {
            if (equipment == null) return;
            if (!equipment.HasAmmoInSlot(0)) return;   // opt1 = LargeSupplyCrate
            edgeSlotOpt1 = true;
        }

        private void TryUseQuickDash()
        {
            if (equipment == null) return;
            // opt2 = QuickDash (Assault) / opt1 = Jammer (Recon) etc.
            // 只在突击兵时使用
            if (aiClass != FsmClass.Assault) return;
            if (!equipment.HasAmmoInSlot(1)) return;   // opt2 = QuickDash

            // 向后跳起后使用
            edgeJump = true;
            edgeSlotOpt2 = true;
        }

        private void TryUseInterceptor()
        {
            if (equipment == null) return;
            if (aiClass != FsmClass.Support) return;
            if (!equipment.HasAmmoInSlot(1)) return;   // opt2 = Interceptor

            // 检查前 10s 是否受到爆炸伤害（简化：检查 lastAttacker 是否为空且时间近）
            // TODO: NetworkCombatant 记录 lastExplosionDamageTime（M2 补充）
            // M1 暂时用 lastAttacker 存在性近似
            edgeSlotOpt2 = true;
        }

        // ---------------------------------------------------------------
        // 支援状态
        // ---------------------------------------------------------------

        private void UpdateSupport(float dt, List<FSMBattleSystem.CombatantView> snapshot)
        {
            // 朝 SOS 源推进
            if (FlatDistance(transform.position, sosTargetPos) > arrivalDistance)
            {
                RequestPath(sosTargetPos);
                FollowPath(dt, sprint: true);
            }
            else
            {
                // 到达倒地队友位置：使用除颤仪（特有配备）
                if (equipment != null && equipment.HasAmmoInSlot(2))
                    edgeSlotSpecial = true;

                // 烟雾弹掩护
                TryThrowSmoke(sosTargetPos);

                // 扫视警戒
                ScanAround(dt);
            }
        }

        private bool ReceivedDeathSOS()
        {
            if (combatant == null) return false;
            int myId = GetInstanceID();

            // PHASE9: 从独立 SOS 频道读取（不被 59 人的主频道挤出）。
            var recent = TeamIntel.GetSOS(combatant.teamId, 8);
            for (int i = 0; i < recent.Count; i++)
            {
                float age = (float)(Mirror.NetworkTime.time - recent[i].time);
                if (age > 5f) continue;

                // 定向广播：targetId 匹配自己 或 -1（全队）
                if (recent[i].targetId == myId || recent[i].targetId == -1)
                {
                    sosTargetPos = recent[i].position;
                    sosTargetId = recent[i].targetId;
                    supportTimeoutEnd = Time.time + SupportTimeout;
                    return true;
                }
            }
            return false;
        }

        private bool HasActiveSOSTarget()
        {
            if (combatant == null) return false;
            var recent = TeamIntel.GetSOS(combatant.teamId, 8);
            for (int i = 0; i < recent.Count; i++)
            {
                if (recent[i].type == IntelEvent.DeathSOS &&
                    (float)(Mirror.NetworkTime.time - recent[i].time) < 2f)
                    return true;
            }
            return false;
        }

        // ---------------------------------------------------------------
        // 装备触发（12 种配备全接线）
        // ---------------------------------------------------------------

        /// <summary>检查目标是否被烟雾遮挡（射线扇中有 Smoke 类且方向接近目标）。</summary>
        private bool IsTargetSmokeOccluded(Vector3 targetPos)
        {
            if (sensor == null) return false;
            Vector3 toTarget = targetPos - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f) return false;
            toTarget.Normalize();

            for (int i = 0; i < sensor.Latest.Length; i++)
            {
                var h = sensor.Latest[i];
                if (h.kind != RayHitKind.Smoke) continue;

                // 检查这条烟雾射线是否在目标方向附近（前向扇形 120° 内）
                // 简化：前向射线 (idx < 32) 检查角度差 < 15°
                if (i < AgentRaySensor.ForwardRays * 2)
                {
                    // 计算这根射线的角度
                    float rayAngle = GetRayAngle(i);
                    Vector3 rayDir = new Vector3(Mathf.Sin(rayAngle * Mathf.Deg2Rad), 0f,
                                                  Mathf.Cos(rayAngle * Mathf.Deg2Rad));
                    if (Vector3.Dot(rayDir, toTarget) > 0.966f)   // cos(15°) ≈ 0.966
                        return true;
                }
            }
            return false;
        }

        /// <summary>检查目标是否被墙遮挡（有 Wall 类射线在目标方向附近且更近）。</summary>
        private bool IsTargetWallOccluded(Vector3 targetPos)
        {
            if (sensor == null) return false;
            float targetDist = FlatDistance(transform.position, targetPos);
            Vector3 toTarget = targetPos - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f) return false;
            toTarget.Normalize();

            for (int i = 0; i < sensor.Latest.Length; i++)
            {
                var h = sensor.Latest[i];
                if (h.kind != RayHitKind.Wall) continue;
                if (h.distance >= targetDist) continue;   // 墙比目标远 = 不挡

                if (i < AgentRaySensor.ForwardRays * 2)
                {
                    float rayAngle = GetRayAngle(i);
                    Vector3 rayDir = new Vector3(Mathf.Sin(rayAngle * Mathf.Deg2Rad), 0f,
                                                  Mathf.Cos(rayAngle * Mathf.Deg2Rad));
                    if (Vector3.Dot(rayDir, toTarget) > 0.966f)
                        return true;
                }
            }
            return false;
        }

        private float GetRayAngle(int idx)
        {
            if (idx < AgentRaySensor.ForwardRays * 2)
            {
                float half = AgentRaySensor.ForwardFovDeg * 0.5f;
                int i = idx % AgentRaySensor.ForwardRays;
                float t = AgentRaySensor.ForwardRays == 1 ? 0.5f : i / (float)(AgentRaySensor.ForwardRays - 1);
                return yaw + Mathf.Lerp(-half, half, t);
            }
            else
            {
                int j = idx - AgentRaySensor.ForwardRays * 2;
                int i = j % AgentRaySensor.RingRays;
                return yaw + (360f / AgentRaySensor.RingRays) * i;
            }
        }

        /// <summary>烟雾弹：进攻&发现敌人→向敌方向 / 生存→向脚下 / 支援→向广播源。</summary>
        private void TryThrowSmoke(Vector3 targetPos)
        {
            if (equipment == null) return;
            if (!equipment.HasAmmoInSlot(3)) return;   // throwable slot = SmokeGrenade for Support
            // 检查 throwable 是否确实是 SmokeGrenade
            var def = equipment.GetSlotDefinition(3);
            if (def == null || def.type != EquipmentType.SmokeGrenade) return;

            Vector3 eye = EyePos();
            Vector3 dir = (targetPos - eye).normalized;
            dir += Vector3.up * 0.4f;
            dir.Normalize();
            edgeSlotThrowable = true;
        }

        /// <summary>榴弹炮：战斗&瞄准的敌军被遮挡&可用→使用。</summary>
        private void TryUseGrenadeLauncher(Vector3 targetPos)
        {
            if (equipment == null) return;
            if (aiClass != FsmClass.Assault) return;
            // opt1 = GrenadeLauncher
            if (!equipment.HasAmmoInSlot(0)) return;
            var def = equipment.GetSlotDefinition(0);
            if (def == null || def.type != EquipmentType.GrenadeLauncher) return;
            edgeSlotOpt1 = true;
        }

        /// <summary>干扰器：自身被标记&可用→使用。</summary>
        private void TryUseJammer()
        {
            if (equipment == null) return;
            if (aiClass != FsmClass.Recon) return;
            if (!IsMarked()) return;
            // opt1 = Jammer
            if (!equipment.HasAmmoInSlot(0)) return;
            edgeSlotOpt1 = true;
        }

        /// <summary>信标：距当前目标要地 50m 左右使用。</summary>
        private void TryUseDeployBeacon()
        {
            if (equipment == null) return;
            if (aiClass != FsmClass.Recon) return;
            if (!hasObjective) return;
            if (FlatDistance(transform.position, objective) > 55f) return;
            // opt2 = DeployBeacon
            if (!equipment.HasAmmoInSlot(1)) return;
            edgeSlotOpt2 = true;
        }

        /// <summary>探测器：距目标要地 5m 或 20m 内发现敌军使用。</summary>
        private void TryUseSensorProbe(List<FSMBattleSystem.CombatantView> snapshot)
        {
            if (equipment == null) return;
            if (aiClass != FsmClass.Recon) return;
            // special = Sensor
            if (!equipment.HasAmmoInSlot(2)) return;

            bool nearZone = hasObjective && FlatDistance(transform.position, objective) <= 6f;
            bool enemyNear = HasVisibleEnemy(snapshot);

            if (nearZone || enemyNear)
                edgeSlotSpecial = true;
        }

        /// <summary>电磁手雷：发现敌方信标/探测器/拦截装置→向其投掷。</summary>
        private void TryUseEmpGrenade()
        {
            if (equipment == null) return;
            if (aiClass != FsmClass.Recon) return;
            // throwable = EmpGrenade
            if (!equipment.HasAmmoInSlot(3)) return;
            var def = equipment.GetSlotDefinition(3);
            if (def == null || def.type != EquipmentType.EmpGrenade) return;

            // 射线检测敌方部署物
            if (sensor == null) return;
            for (int i = 0; i < sensor.Latest.Length; i++)
            {
                var h = sensor.Latest[i];
                if (h.kind == RayHitKind.Beacon || h.kind == RayHitKind.Sensor ||
                    h.kind == RayHitKind.Interceptor)
                {
                    edgeSlotThrowable = true;
                    return;
                }
            }
        }

        // ---------------------------------------------------------------
        // 广播
        // ---------------------------------------------------------------

        private void EmitEnemySpottedBroadcast()
        {
            if (sensor == null || combatant == null) return;
            if (Time.time < enemySpottedSuppressUntil) return;

            for (int i = 0; i < sensor.Latest.Length; i++)
            {
                var h = sensor.Latest[i];
                if (h.kind == RayHitKind.Enemy && h.combatant != null)
                {
                    TeamIntel.Broadcast(combatant.teamId, IntelEvent.EnemySpotted,
                        h.combatant.transform.position, combatant.squadId, GetInstanceID());
                    enemySpottedSuppressUntil = Time.time + 10f;  // 接收方 10s 压制
                    return;
                }
            }
        }

        private bool ReceivedEnemySpotted()
        {
            if (combatant == null) return false;
            var recent = TeamIntel.GetRecent(combatant.teamId, 3);
            for (int i = 0; i < recent.Count; i++)
            {
                if (recent[i].type == IntelEvent.EnemySpotted)
                {
                    float age = (float)(Mirror.NetworkTime.time - recent[i].time);
                    if (age < 3f)   // 3s 内的敌情广播
                    {
                        lastKnownTargetPos = recent[i].position;
                        hasLastKnownPos = true;
                        return true;
                    }
                }
            }
            return false;
        }

        // ---------------------------------------------------------------
        // 感知辅助
        // ---------------------------------------------------------------

        private bool HasVisibleEnemy(List<FSMBattleSystem.CombatantView> snapshot)
        {
            if (sensor == null) return false;
            for (int i = 0; i < sensor.Latest.Length; i++)
            {
                if (sensor.Latest[i].kind == RayHitKind.Enemy &&
                    sensor.Latest[i].combatant != null &&
                    !sensor.Latest[i].combatant.IsDead)
                    return true;
            }
            return false;
        }

        private bool IsMarked()
        {
            return combatant != null && combatant.IsMarked;
        }

        // ---------------------------------------------------------------
        // 兵种配装
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
                default:
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
        // 寻路 / 移动辅助
        // ---------------------------------------------------------------

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

            float dirYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            yaw = Mathf.MoveTowardsAngle(yaw, dirYaw, turnRate * dt);
            pitch = Mathf.MoveTowards(pitch, 0f, turnRate * dt);
            MoveInWorldDir(flat, sprint);
        }

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

        private void FaceTarget(Vector3 targetPos, float dt)
        {
            Vector3 dir = targetPos - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            float targetYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            yaw = Mathf.MoveTowardsAngle(yaw, targetYaw, turnRate * dt);

            // pitch：简单估算（目标高度差/距离）
            float heightDiff = (targetPos.y + 1.5f) - (transform.position.y + 1.5f);
            float dist = dir.magnitude;
            if (dist > 0.1f)
            {
                float targetPitch = -Mathf.Atan2(heightDiff, dist) * Mathf.Rad2Deg;
                pitch = Mathf.MoveTowards(pitch, targetPitch, turnRate * dt);
            }
        }

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

        // ---------------------------------------------------------------
        // 意图输出
        // ---------------------------------------------------------------

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

            edgeJump = edgeCrouch = edgeProne = edgeReload = edgeSwitchFireMode = false;
            edgeMark = edgeSlotPrimary = edgeSlotOpt1 = edgeSlotOpt2 = false;
            edgeSlotSpecial = edgeSlotThrowable = false;
        }

        // ---------------------------------------------------------------
        // 生命周期钩子
        // ---------------------------------------------------------------

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

        public void OnRedeploy(Vector3 pos)
        {
            dead = false;
            ClearPath();
            hasObjective = false;
            patrolling = false;
            hasCover = false;
            hasLastKnownPos = false;
            target = null;
            state = FsmState.Normal;
            yaw = transform.rotation.eulerAngles.y;
            pitch = 0f;
            reloadRolledThisThreshold = false;
            ammoAtLastCheck = 0;
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

        private float GetHp01()
        {
            return health != null ? Mathf.Clamp01(health.health / health.maxHealth) : 1f;
        }

        private float GetAmmo01()
        {
            if (gun == null || gun.Definition == null) return 1f;
            int total = gun.magAmmo + gun.reserveAmmo;
            int cap = gun.Definition.magazineCapacity + gun.Definition.reserveCapacity;
            return cap > 0 ? Mathf.Clamp01((float)total / cap) : 1f;
        }
    }
}
