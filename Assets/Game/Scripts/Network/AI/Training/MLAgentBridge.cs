using System.Collections.Generic;
using Mirror;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// ML-Agents bridge on an AI entity (ML 训练共识 §3/§4). Owns:
    ///
    ///  - observation assembly (self / ray fan / squad channel / mark memory /
    ///    team intel / global strategic zones — all relative + normalized),
    ///  - action mapping (hybrid: 6 continuous + 6 discrete branches) into a
    ///    <see cref="NetworkInputState"/> pushed via
    ///    <see cref="NetworkAIController.SetIntent"/> (identical to the human
    ///    client path),
    ///  - rule broadcasts (enemy spotted / low ammo / need heal) at decision rate,
    ///  - reward registration with <see cref="RewardBus"/>.
    ///
    /// The interface is trainer-agnostic: phase A (mlagents-learn) and phase B
    /// (custom PyTorch) see the exact same vector.
    /// </summary>
    [RequireComponent(typeof(NetworkAIController))]
    public class MLAgentBridge : Agent
    {
        public const string BehaviorName = "HagenDaSquad";

        // ---- 共识 §4 动作规格 ----
        public const int ContinuousCount = 6;             // moveX, moveY, yawVel, pitchVel, fire, aim
        public static readonly int[] DiscreteBranches = { 4, 2, 2, 2, 2, 6 };
        // 姿态(无/站/蹲/趴), 跳, 冲刺(hold), 换弹, 标记, 槽位(保持/主武器/可选1/可选2/特有/投掷物)

        // ---- 视角角速度限幅（度/秒）----
        public const float MaxYawVel = 360f;
        public const float MaxPitchVel = 180f;

        // ---- 共识 §3 分块维度 ----
        public const int SelfSize = 38;
        public const int RaySize = AgentRaySensor.OutputSize;   // 480
        public const int SquadSlots = 4;
        public const int SquadPerSize = 12;
        public const int MarkSlots = 4;
        public const int MarkPerSize = 6;
        public const int IntelSlots = TeamIntel.MaxEntries;
        public const int IntelPerSize = 10;
        public const int GlobalSize = 24;   // 比分2 + 3 要地×7 + 计时1
        public const int TotalObsSize =
            SelfSize + RaySize + SquadSlots * SquadPerSize + MarkSlots * MarkPerSize +
            IntelSlots * IntelPerSize + GlobalSize;

        // ---- 兵种枚举（共识 §2）----
        public enum AgentClass { Assault = 0, Support = 1, Recon = 2 }

        // ---- 兵种（由会话管理器指定；观测 one-hot）。非同步：Agent 无网络身份，
        //      classId 由会话管理器在服务端直接设置（本组件仅存在于服务端 AI 实体上）。
        public int classId = (int)AgentClass.Assault;

        // ---- references ----
        public NetworkAIController ai;
        private NetworkPlayerController body;
        private NetworkCombatant combatant;
        private NetworkPlayerHealth health;
        private NetworkGun gun;
        private NetworkEquipment equipment;
        private AgentRaySensor sensor;

        // ---- look accumulator（等价客户端 localYaw/localPitch）----
        private float yawAccum;
        private float pitchAccum;

        // ---- S1 课程奖励：最近靶距离追踪（接近奖励，>8m 有效防贴身）----
        private float prevNearestTargetDist = float.MaxValue;
        public const float CurriculumMinDist = 8f;      // 贴身距离以下不奖励
        public const float CurriculumRewardPerMeter = 0.001f;

        // ---- scratch buffers ----
        private readonly float[] rayOut = new float[RaySize];
        private readonly float[] selfOut = new float[SelfSize];
        private readonly List<StrategicZoneState> zoneBuf = new List<StrategicZoneState>();

        // 坐标归一化基准（共识：全相对量 + 归一化，场地无关）。
        private const float PosNormX = 50f;   // 半宽
        private const float PosNormZ = 100f;  // 半长

        public NetworkCombatant Combatant => combatant;

        protected override void Awake()
        {
            base.Awake();
            ai = GetComponent<NetworkAIController>();
            body = GetComponent<NetworkPlayerController>();
            combatant = GetComponent<NetworkCombatant>();
            health = GetComponent<NetworkPlayerHealth>();
            gun = GetComponent<NetworkGun>();
            sensor = GetComponent<AgentRaySensor>();
            if (sensor == null) sensor = gameObject.AddComponent<AgentRaySensor>();
            equipment = GetComponent<NetworkEquipment>();

            // 视角累积器初始化为当前朝向。
            yawAccum = transform.rotation.eulerAngles.y;
            pitchAccum = 0f;

            ConfigureBehaviorParameters();
        }

        /// <summary>代码装配行为参数（prefab 构建器无需手工配置 Inspector）。</summary>
        private void ConfigureBehaviorParameters()
        {
            var bp = GetComponent<BehaviorParameters>();
            if (bp == null)
                bp = gameObject.AddComponent<BehaviorParameters>();
            bp.BehaviorName = BehaviorName;
            bp.BrainParameters.VectorObservationSize = TotalObsSize;
            bp.BrainParameters.NumStackedVectorObservations = 1;
            bp.BrainParameters.ActionSpec = new ActionSpec(
                ContinuousCount, DiscreteBranches);

            var dr = GetComponent<DecisionRequester>();
            if (dr == null)
                dr = gameObject.AddComponent<DecisionRequester>();
            dr.DecisionPeriod = MLTrainingConfig.DecisionPeriod;   // 60Hz / 6Hz = 10
            dr.TakeActionsBetweenDecisions = true;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (NetworkServer.active)
                RewardBus.Register(combatant, this);
        }

        protected override void OnDisable()
        {
            RewardBus.Unregister(combatant);
            base.OnDisable();
        }

        // ---------------------------------------------------------------
        // OBSERVATIONS (共识 §3)
        // ---------------------------------------------------------------

        public override void CollectObservations(VectorSensor sensor_)
        {
            if (combatant == null || body == null) return;
            bool dead = health != null && health.IsDead;

            // 每决策步采样射线（同时驱动"目击敌人"广播）。
            Vector3 basePos = transform.position;
            sensor.Sample(basePos, yawAccum, combatant.teamId);
            if (!dead)
                EmitRuleBroadcasts();

            // --- 自身块 ---
            WriteSelfBlock(dead);

            // --- 射线块 ---
            sensor.Write(rayOut);

            // --- 小队频道 ---
            float[] squad = new float[SquadSlots * SquadPerSize];
            WriteSquadBlock(squad);

            // --- 标记记忆 ---
            float[] marks = new float[MarkSlots * MarkPerSize];
            WriteMarkBlock(marks);

            // --- 团队广播 ---
            float[] intel = new float[IntelSlots * IntelPerSize];
            WriteIntelBlock(intel);

            // --- 全局块 ---
            float[] global = new float[GlobalSize];
            WriteGlobalBlock(global);

            sensor_.AddObservation(selfOut);
            sensor_.AddObservation(rayOut);
            sensor_.AddObservation(squad);
            sensor_.AddObservation(marks);
            sensor_.AddObservation(intel);
            sensor_.AddObservation(global);
        }

        private void WriteSelfBlock(bool dead)
        {
            int o = 0;
            float maxHp = health != null ? health.maxHealth : 100f;
            selfOut[o++] = health != null ? health.health / maxHp : 0f;
            selfOut[o++] = health != null ? Mathf.Clamp01(health.armor / 100f) : 0f;

            // 姿态 one-hot（3）。
            var p = body.posture;
            selfOut[o++] = p == PlayerPosture.Stand ? 1f : 0f;
            selfOut[o++] = p == PlayerPosture.Crouch ? 1f : 0f;
            selfOut[o++] = p == PlayerPosture.Prone ? 1f : 0f;

            selfOut[o++] = body.sliding ? 1f : 0f;
            selfOut[o++] = body.Diving ? 1f : 0f;
            selfOut[o++] = body.Grounded ? 1f : 0f;
            selfOut[o++] = body.Sprinting ? 1f : 0f;

            // 本地速度（自身坐标系，归一化到 7.5 m/s）。
            Vector3 v = body.GetComponent<Rigidbody>().velocity;
            Vector3 local = Quaternion.Euler(0f, -yawAccum, 0f) * v;
            selfOut[o++] = Mathf.Clamp(local.x / 7.5f, -1f, 1f);
            selfOut[o++] = Mathf.Clamp(local.z / 7.5f, -1f, 1f);
            selfOut[o++] = Mathf.Clamp(local.y / 7.5f, -1f, 1f);

            // 朝向（相对自身累积器，供记忆稳定）。
            selfOut[o++] = Mathf.Sin(yawAccum * Mathf.Deg2Rad);
            selfOut[o++] = Mathf.Cos(yawAccum * Mathf.Deg2Rad);
            selfOut[o++] = Mathf.Clamp(pitchAccum / 89f, -1f, 1f);

            // activeSlot one-hot（5: -1,0,1,2,3）。
            int slot = body.activeSlot;
            for (int i = 0; i < 5; i++)
                selfOut[o++] = (slot == i - 1) ? 1f : 0f;

            // 枪械。
            if (gun != null && gun.Definition != null)
            {
                selfOut[o++] = gun.magAmmo / (float)gun.Definition.magazineCapacity;
                selfOut[o++] = Mathf.Clamp01(
                    gun.reserveAmmo / (float)Mathf.Max(1, gun.Definition.reserveCapacity));
                selfOut[o++] = gun.reloading ? 1f : 0f;
            }
            else { selfOut[o++] = 0f; selfOut[o++] = 0f; selfOut[o++] = 0f; }

            // 4 槽装备：余量 + 冷却。
            for (int s = 0; s < 4; s++)
            {
                var def = equipment != null ? equipment.GetSlotDefinition(s) : null;
                int idx = equipment != null ? equipment.GetSlotIndex(s) : -1;
                float ammoNorm = 0f, cdNorm = 0f;
                if (def != null && equipment != null && idx >= 0)
                {
                    ammoNorm = Mathf.Clamp01(equipment.GetSlotAmmo(s) / (float)Mathf.Max(1, def.maxCarry));
                    // 冷却：quick dash / jammer 用秒数归一。
                    float cd = s == 0 ? equipment.dashCooldownRemaining
                              : (def.type == EquipmentType.Jammer ? equipment.jammerCooldownRemaining : 0f);
                    cdNorm = Mathf.Clamp01(cd / 30f);
                }
                selfOut[o++] = ammoNorm;
                selfOut[o++] = cdNorm;
            }

            // EMP 干扰剩余。
            selfOut[o++] = equipment != null ? Mathf.Clamp01(equipment.empExposure / 10f) : 0f;

            // 兵种 one-hot（3）。
            for (int i = 0; i < 3; i++)
                selfOut[o++] = classId == i ? 1f : 0f;

            // 近 2s 受击（由 RewardBus/TeamIntel 节流，简单用血量变化近似：低血量标志）。
            selfOut[o++] = health != null && health.health < maxHp * 0.5f ? 1f : 0f;

            // 干扰器状态。
            selfOut[o++] = equipment != null ? Mathf.Clamp01(equipment.jammerImmuneRemaining / 30f) : 0f;
            selfOut[o++] = dead ? 1f : 0f;
        }

        private void WriteSquadBlock(float[] buf)
        {
            // 4 槽 × [存活, 兵种3, 相对位置2, 血量, 姿态3, 相对速度2]
            int o = 0;
            int filled = 0;

            foreach (var kv in Squadmates())
            {
                if (filled >= SquadSlots) break;
                var mate = kv;
                if (mate == null || mate.IsDead) { /* 空槽补零 */ }

                buf[o++] = mate != null && !mate.IsDead ? 1f : 0f;
                int cls = ClassOf(mate);
                for (int i = 0; i < 3; i++) buf[o++] = cls == i ? 1f : 0f;

                Vector3 rel = mate != null ? mate.transform.position - transform.position : Vector3.zero;
                buf[o++] = Mathf.Clamp(rel.x / PosNormX, -1f, 1f);
                buf[o++] = Mathf.Clamp(rel.z / PosNormZ, -1f, 1f);

                var mateHealth = mate != null ? mate.Health : null;
                buf[o++] = mateHealth != null ? mateHealth.health / 100f : 0f;

                var mateBody = mate != null ? mate.GetComponent<NetworkPlayerController>() : null;
                var mp = mateBody != null ? mateBody.posture : PlayerPosture.Stand;
                buf[o++] = mp == PlayerPosture.Stand ? 1f : 0f;
                buf[o++] = mp == PlayerPosture.Crouch ? 1f : 0f;
                buf[o++] = mp == PlayerPosture.Prone ? 1f : 0f;

                Vector3 mv = mate != null && mateBody != null
                    ? mateBody.GetComponent<Rigidbody>().velocity : Vector3.zero;
                Vector3 relV = mv - (body != null ? body.GetComponent<Rigidbody>().velocity : Vector3.zero);
                Vector3 localRV = Quaternion.Euler(0f, -yawAccum, 0f) * relV;
                buf[o++] = Mathf.Clamp(localRV.x / 7.5f, -1f, 1f);
                buf[o++] = Mathf.Clamp(localRV.z / 7.5f, -1f, 1f);

                filled++;
            }
            // 剩余槽位保持零。
        }

        private void WriteMarkBlock(float[] buf)
        {
            // 4 条 × [相对位置2, 年龄(0 新), 有效, 敌兵种压缩(0/0.5/1)]
            int o = 0;
            int filled = 0;
            double now = NetworkTime.time;

            foreach (var c in EnumerateCombatants())
            {
                if (filled >= MarkSlots) break;
                if (c == null || c == combatant) continue;
                if (c.teamId == combatant.teamId) continue;
                if (!c.IsMarked || c.markedByTeam != combatant.teamId) continue;

                Vector3 rel = c.transform.position - transform.position;
                buf[o++] = Mathf.Clamp(rel.x / PosNormX, -1f, 1f);
                buf[o++] = Mathf.Clamp(rel.z / PosNormZ, -1f, 1f);
                float remaining = (float)(c.markedUntil - now);
                buf[o++] = Mathf.Clamp01(remaining / 10f);   // 剩余有效比例
                buf[o++] = 1f;
                buf[o++] = ClassOf(c) / 2f;
                buf[o++] = 0f;   // 预留
                filled++;
            }
        }

        private void WriteIntelBlock(float[] buf)
        {
            // 8 条 × [6 类 one-hot, 方向 sin, 方向 cos, 距离, 年龄]
            int o = 0;
            var entries = TeamIntel.GetRecent(combatant.teamId);
            double now = NetworkTime.time;

            for (int i = 0; i < entries.Count && i < IntelSlots; i++)
            {
                var e = entries[i];
                for (int k = 0; k < 6; k++)
                    buf[o++] = (int)e.type == k ? 1f : 0f;

                Vector3 rel = e.position - transform.position;
                float dx = rel.x, dz = rel.z;
                float mag = Mathf.Max(0.001f, new Vector2(dx, dz).magnitude);
                buf[o++] = dx / mag;
                buf[o++] = dz / mag;
                buf[o++] = Mathf.Clamp01(mag / 60f);
                buf[o++] = Mathf.Clamp01((float)(now - e.time) / TeamIntel.WindowSeconds);
            }
        }

        private void WriteGlobalBlock(float[] buf)
        {
            int o = 0;
            var mm = NetworkMatchManager.Instance;
            int win = mm != null ? Mathf.Max(1, mm.winScore) : 1;
            buf[o++] = mm != null ? Mathf.Clamp01(mm.redScore / (float)win) : 0f;
            buf[o++] = mm != null ? Mathf.Clamp01(mm.blueScore / (float)win) : 0f;

            // 3 个战略要地 × [相对位置2, 半径, 归属3, 争夺度]。
            StrategicZoneRegistry.Instance?.GetZones(zoneBuf);
            for (int z = 0; z < StrategicZoneRegistry.MaxZones; z++)
            {
                if (z < zoneBuf.Count)
                {
                    var zone = zoneBuf[z];
                    Vector3 rel = zone.position - transform.position;
                    buf[o++] = Mathf.Clamp(rel.x / PosNormX, -1f, 1f);
                    buf[o++] = Mathf.Clamp(rel.z / PosNormZ, -1f, 1f);
                    buf[o++] = Mathf.Clamp01(zone.radius / 20f);
                    buf[o++] = zone.ownerTeam == -1 ? 1f : 0f;
                    buf[o++] = zone.ownerTeam == 0 ? 1f : 0f;
                    buf[o++] = zone.ownerTeam == 1 ? 1f : 0f;
                    buf[o++] = Mathf.Clamp(zone.contest, -1f, 1f);
                }
                else
                {
                    for (int k = 0; k < 7; k++) buf[o++] = 0f;
                }
            }

            // 计时（会话管理器提供；无则 0）。
            buf[o++] = MLTrainingConfig.SessionTimeRemaining01();
        }

        // ---------------------------------------------------------------
        // RULE BROADCASTS（共识 §3：自动规则触发）
        // ---------------------------------------------------------------

        /// <summary>
        /// S1 课程奖励：接近最近的敌方靶。每决策步，若最近靶距离在
        /// CurriculumMinDist 以上且比上一步更近，按缩短米数给奖励。
        /// 距离低于 CurriculumMinDist 后不再给——防止贴身搏斗倾向。
        /// </summary>
        private void ApplyCurriculumReward()
        {
            float nearest = float.MaxValue;
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);
            foreach (var c in buf)
            {
                if (c == null || c == combatant) continue;
                if (c.teamId == combatant.teamId) continue;
                if (c.IsDead) continue;
                float d = Vector3.Distance(transform.position, c.transform.position);
                if (d < nearest) nearest = d;
            }

            if (nearest < float.MaxValue && prevNearestTargetDist < float.MaxValue)
            {
                // 只在 > CurriculumMinDist 时奖励接近。
                float effective = Mathf.Max(nearest, CurriculumMinDist);
                float effectivePrev = Mathf.Max(prevNearestTargetDist, CurriculumMinDist);
                float improvement = effectivePrev - effective;
                if (improvement > 0f)
                    AddReward(improvement * CurriculumRewardPerMeter);
            }

            prevNearestTargetDist = nearest;
        }

        private void EmitRuleBroadcasts()
        {
            // 目击敌人：任一射线命中敌方作战单位。
            if (sensor != null && sensor.Latest != null)
            {
                foreach (var h in sensor.Latest)
                {
                    if (h.kind == RayHitKind.Enemy && h.combatant != null)
                    {
                        TeamIntel.Broadcast(combatant.teamId, IntelEvent.EnemySpotted,
                            h.combatant.transform.position, combatant.squadId,
                            GetInstanceID());
                        break;   // 每决策步一条（TeamIntel 节流 3s）
                    }
                }
            }

            // 缺弹药：弹匣 + 备弹 < 20%。
            if (gun != null && gun.Definition != null)
            {
                int cap = gun.Definition.magazineCapacity + gun.Definition.reserveCapacity;
                if (cap > 0 && gun.magAmmo + gun.reserveAmmo < cap * 0.2f)
                    TeamIntel.Broadcast(combatant.teamId, IntelEvent.LowAmmo,
                        transform.position, combatant.squadId, GetInstanceID());
            }

            // 请求治疗：血量 < 40%。
            if (health != null && health.health < health.maxHealth * 0.4f)
                TeamIntel.Broadcast(combatant.teamId, IntelEvent.NeedHeal,
                    transform.position, combatant.squadId, GetInstanceID());
        }

        // ---------------------------------------------------------------
        // ACTIONS → NetworkInputState (共识 §4)
        // ---------------------------------------------------------------

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (ai == null || combatant == null) return;

            var cont = actions.ContinuousActions;
            var disc = actions.DiscreteActions;

            var s = default(NetworkInputState);

            // 连续：移动。
            s.move = new Vector2(
                Mathf.Clamp(cont[0], -1f, 1f),
                Mathf.Clamp(cont[1], -1f, 1f));

            // 连续：视角角速度 → 绝对角累积（等价客户端鼠标采样）。
            float yawVel = Mathf.Clamp(cont[2], -1f, 1f) * MaxYawVel;
            float pitchVel = Mathf.Clamp(cont[3], -1f, 1f) * MaxPitchVel;
            yawAccum = Mathf.Repeat(yawAccum + yawVel * MLTrainingConfig.DecisionDt, 360f);
            pitchAccum = Mathf.Clamp(pitchAccum + pitchVel * MLTrainingConfig.DecisionDt,
                                     body != null ? body.minPitch : -89f,
                                     body != null ? body.maxPitch : 89f);
            s.yaw = yawAccum;
            s.pitch = pitchAccum;

            // 连续：开火 / 瞄准保持。
            s.fire = cont[4] > 0.5f;
            s.aim = cont[5] > 0.5f;

            // 离散 0：姿态（无/站/蹲/趴 → toggle 翻译，任意姿态单 toggle 可达）。
            if (body != null)
            {
                int want = disc[0];
                if (want == 1 && body.posture != PlayerPosture.Stand)
                {
                    if (body.posture == PlayerPosture.Crouch) s.crouchToggle = true;
                    else s.proneToggle = true;            // prone → stand
                }
                else if (want == 2 && body.posture != PlayerPosture.Crouch)
                {
                    s.crouchToggle = true;                 // stand/prone → crouch 均可用 X
                }
                else if (want == 3 && body.posture != PlayerPosture.Prone)
                {
                    s.proneToggle = true;                  // stand/crouch → prone 均可用 C
                }
            }

            // 离散 1-5。
            if (disc[1] == 1) s.jump = true;
            if (disc[2] == 1) s.sprint = true;             // hold
            if (disc[3] == 1) s.reload = true;
            if (disc[4] == 1) s.mark = true;

            switch (disc[5])
            {
                case 1: s.slotPrimary = true; break;
                case 2: s.slotOpt1 = true; break;
                case 3: s.slotOpt2 = true; break;
                case 4: s.slotSpecial = true; break;
                case 5: s.slotThrowable = true; break;
            }

            ai.SetIntent(s);

            // S1 课程奖励：在动作接收后计算（reward-action 因果配对正确），
            // 而非 CollectObservations（那里加的 reward 会错配到上一个 step）。
            if (health == null || !health.IsDead)
                ApplyCurriculumReward();
        }

        // ---------------------------------------------------------------
        // EPISODE / ROUND (TrainingSessionManager drives)
        // ---------------------------------------------------------------

        /// <summary>回合结束：终局奖励 + 结束回合（由会话管理器调用）。</summary>
        public void EndRound(bool won)
        {
            AddReward(won ? RewardBus.WinReward : RewardBus.LossPenalty);
            EndEpisode();
        }

        /// <summary>
        /// 仅结束 episode 不给终局奖励（RewardBus.MatchEnd 已统一发放——
        /// 修复双重终局奖励；平局时也不惩罚）。
        /// </summary>
        public void EndEpisodeOnly()
        {
            EndEpisode();
        }

        /// <summary>死亡：重置 LSTM 记忆（共识 Q20-A：新生命新记忆）。</summary>
        public void OnLifeReset()
        {
            // AI 控制器在死亡时推送零输入；记忆重置由训练器在下一个 episode
            // 边界处理。此处仅通知 intent 清零。
            if (ai != null) ai.SetIntent(default);
            prevNearestTargetDist = float.MaxValue;   // 课程奖励基准重置
        }

        // ---------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------

        private IEnumerable<NetworkCombatant> Squadmates()
        {
            // 同队非自身，最多 4 人（1 小队 5 人配置）。
            int n = 0;
            foreach (var c in EnumerateCombatants())
            {
                if (c == null || c == combatant) continue;
                if (c.teamId != combatant.teamId) continue;
                if (n++ >= SquadSlots) yield break;
                yield return c;
            }
        }

        private static readonly List<NetworkCombatant> combatantScratch =
            new List<NetworkCombatant>();

        private List<NetworkCombatant> EnumerateCombatants()
        {
            combatantScratch.Clear();
            // NetworkMatchManager 的注册表（server 权威）。
            NetworkMatchManager.GetAllCombatants(combatantScratch);
            return combatantScratch;
        }

        private static int ClassOf(NetworkCombatant c)
        {
            if (c == null) return 0;
            var b = c.GetComponent<MLAgentBridge>();
            return b != null ? b.classId : 0;
        }
    }

    /// <summary>训练全局配置（会话管理器 M1 会填充；这里提供安全默认）。</summary>
    public static class MLTrainingConfig
    {
        /// <summary>Academy 60Hz 下的决策周期 → 6Hz。</summary>
        public const int DecisionPeriod = 10;
        public const float DecisionDt = DecisionPeriod / 60f;

        private static float timeRemaining01 = 0f;
        public static void SetTimeRemaining01(float v) => timeRemaining01 = Mathf.Clamp01(v);
        public static float SessionTimeRemaining01() => timeRemaining01;
    }
}
