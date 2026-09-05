using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// FSM 视觉传感器（视觉重构：朝向方体物理查询 + LOS 遮挡射线）。
    ///
    /// 发现（粗筛，主线程，每决策步一次）：
    ///   - 前向方体 OverlapBox：以朝向 yaw 为轴、长 VisionRange、半角
    ///     FovHalfAngleDeg 的 FOV 包围盒，抓取前方作战单位与部署物；
    ///   - 近距球 OverlapSphere：半径 NearRadius 全向（替代旧 12m 环形射线，
    ///     保留贴身/侧后感知）。
    /// 验证（细筛，批处理 Job）：预算内目标各发 头面+脚面 2 根 LOS 射线，
    /// 先命中非目标体 → 几何遮挡；NetworkSmokeVolume 线段先于命中相交 →
    /// 烟雾遮挡（与旧传感器"烟雾比物理命中更近则覆盖"语义一致）。
    ///
    /// 与旧 <see cref="AgentRaySensor"/>（ML 训练共识 §3，48 根射线扇）的关系：
    /// 敌我/部署物词表复用 RayHitKind；静态墙体不再作为"看见的内容"，而是作为
    /// 遮挡物参与 LOS 判定。ML 桥（MLAgentBridge）观测维度依赖旧传感器，
    /// 保持原样不动。
    /// </summary>
    public class AgentVisionSensor : MonoBehaviour
    {
        // ---- 预算 ----
        public const int MaxCombatantTargets = 8;
        public const int MaxDeployableTargets = 4;
        public const int MaxTargets = MaxCombatantTargets + MaxDeployableTargets;
        public const int RaysPerTarget = 2;   // 头面 + 脚面
        /// <summary>每 agent 遮挡射线总数（FSMBattleSystem 按此分配批处理数组）。</summary>
        public const int MaxOcclusionRays = MaxTargets * RaysPerTarget;

        // ---- FOV 规格（前向距离/半角与旧前向扇形一致）----
        public const float FovHalfAngleDeg = 60f;   // 前向 120°
        public const float VisionRange = 60f;
        public const float NearRadius = 12f;        // 全向近距（替代旧环形射线）

        public const float HighRayHeight = 1.5f;    // 头面（旧 HeadHeight）
        public const float LowRayHeight = 0.35f;    // 脚面（旧 FootHeight ≈ 0.30）

        /// <summary>单目标 LOS 结论。</summary>
        public enum VisionLos : byte
        {
            Clear = 0,        // 至少一根射线直达目标
            WallBlocked = 1,  // 全部被几何遮挡
            SmokeBlocked = 2  // 全部被烟雾遮挡（几何未挡）
        }

        /// <summary>一个可见实体记录（发现即入列，LOS 状态由遮挡射线解析）。</summary>
        public struct VisionTarget
        {
            public RayHitKind kind;
            public NetworkCombatant combatant;   // Enemy/Friend 时有值
            public Vector3 position;
            public float distance;               // 水平距离
            public VisionLos los;
            /// <summary>被几何挡住的射线数（0..2）。Clear 但 &gt;0 = 掩体后半遮挡。</summary>
            public int geometryBlockedRays;
            public bool inForwardFov;            // false = 近距球发现（FOV 之外）
        }

        private enum RayLos : byte { Reach = 0, Geometry = 1, Smoke = 2 }

        // ---- 发现缓冲 ----
        private readonly Collider[] boxBuf = new Collider[256];
        private readonly Collider[] nearBuf = new Collider[64];

        private struct Candidate
        {
            public RayHitKind kind;
            public NetworkCombatant combatant;
            public Component root;         // LOS 射线命中归属判定用
            public Vector3 position;
            public float distance;
            public bool inFov;
        }

        private readonly List<Candidate> combatantCands = new List<Candidate>(32);
        private readonly List<Candidate> deployableCands = new List<Candidate>(32);
        private readonly HashSet<int> combatantSeen = new HashSet<int>();
        private readonly HashSet<int> deployableSeen = new HashSet<int>();

        // 分类缓存：instanceId → 组件（每步重建，避免逐候选 GetComponentInParent）
        private readonly Dictionary<int, NetworkCombatant> combatantCache =
            new Dictionary<int, NetworkCombatant>();
        private readonly Dictionary<int, RayHitKind> kindCache = new Dictionary<int, RayHitKind>();
        private readonly Dictionary<int, Component> deployableRootCache =
            new Dictionary<int, Component>();

        private static readonly System.Comparison<Candidate> ByDistance =
            (a, b) => a.distance.CompareTo(b.distance);

        // ---- 待解析目标（BuildVision 写入 → ParseVision 消费，同一 tick 内）----
        private readonly VisionTarget[] pending = new VisionTarget[MaxTargets];
        private readonly Component[] pendingRoots = new Component[MaxTargets];
        private int pendingCount;
        private Vector3 lastOriginBase;
        private float lastYaw;

        // ---- 已发布结果 ----
        private readonly List<VisionTarget> targets = new List<VisionTarget>(MaxTargets);
        /// <summary>本决策步的视觉记录（ParseVision 后有效，FSM 大脑只读）。</summary>
        public IReadOnlyList<VisionTarget> Targets => targets;

        private int myTeam = -1;
        private NetworkCombatant selfCombatant;

        /// <summary>
        /// 每决策步入口：方体/球查询发现候选 + 把预算内目标的遮挡射线写入共享
        /// 命令数组（从 offset 开始）。originBase = 脚底位置，yaw = 朝向（度）。
        /// </summary>
        public void BuildVision(Vector3 originBase, float yaw, int team,
                                NativeArray<RaycastCommand> commands, int offset)
        {
            myTeam = team;
            lastOriginBase = originBase;
            lastYaw = yaw;
            combatantCache.Clear();
            kindCache.Clear();
            deployableRootCache.Clear();
            combatantCands.Clear();
            deployableCands.Clear();
            combatantSeen.Clear();
            deployableSeen.Clear();
            pendingCount = 0;
            selfCombatant = GetComponentInParent<NetworkCombatant>();

            Discover(originBase, yaw);
            SelectPending();

            var q = new QueryParameters(Physics.DefaultRaycastLayers, true,
                QueryTriggerInteraction.Ignore, true);

            for (int i = 0; i < MaxTargets; i++)
            {
                if (i < pendingCount)
                {
                    WriteTargetRays(originBase, ref pending[i], q,
                        commands, offset + i * RaysPerTarget);
                }
                else
                {
                    // 空槽零填充：RaycastCommand 零值 = 无操作。
                    for (int r = 0; r < RaysPerTarget; r++)
                        commands[offset + i * RaysPerTarget + r] = default;
                }
            }
        }

        /// <summary>批处理完成后解析：逐射线判定 LOS 并按目标合并，发布 <see cref="Targets"/>。</summary>
        public void ParseVision(NativeArray<RaycastHit> results, int offset)
        {
            targets.Clear();
            for (int i = 0; i < pendingCount; i++)
            {
                var t = pending[i];
                Component root = pendingRoots[i];
                int reach = 0, geo = 0, smoke = 0;
                for (int r = 0; r < RaysPerTarget; r++)
                {
                    switch (ResolveRay(ref t, root, r, results[offset + i * RaysPerTarget + r]))
                    {
                        case RayLos.Reach: reach++; break;
                        case RayLos.Geometry: geo++; break;
                        default: smoke++; break;
                    }
                }

                t.los = reach > 0 ? VisionLos.Clear
                      : geo > 0 ? VisionLos.WallBlocked
                      : VisionLos.SmokeBlocked;
                t.geometryBlockedRays = geo;
                targets.Add(t);
            }
            pendingCount = 0;
        }

        // ---------------------------------------------------------------
        // 发现
        // ---------------------------------------------------------------

        private void Discover(Vector3 originBase, float yaw)
        {
            Vector3 fwd = DirFromYaw(yaw);
            float sinHalf = Mathf.Sin(FovHalfAngleDeg * Mathf.Deg2Rad);

            // 前向方体：恰好包住 FOV 锥（半宽 = range·sin(半角)，高 2.5m 覆盖卧倒~跳起）。
            Vector3 boxCenter = originBase + fwd * (VisionRange * 0.5f) + Vector3.up * 1.1f;
            Vector3 halfExtents = new Vector3(VisionRange * sinHalf, 1.25f, VisionRange * 0.5f);
            Quaternion orient = Quaternion.Euler(0f, yaw, 0f);
            int n = Physics.OverlapBoxNonAlloc(boxCenter, halfExtents, boxBuf, orient,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                TryCollect(boxBuf[i], originBase, fwd);

            // 近距球：全向贴身感知（替代旧环形射线，背后 12m 内仍可见）。
            int m = Physics.OverlapSphereNonAlloc(originBase + Vector3.up, NearRadius, nearBuf,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < m; i++)
                TryCollect(nearBuf[i], originBase, fwd);
        }

        private void TryCollect(Collider col, Vector3 originBase, Vector3 fwd)
        {
            int id = col.GetInstanceID();

            // 1) 作战单位（含敌我；跳过自己与尸体）。
            NetworkCombatant combatant;
            if (!combatantCache.TryGetValue(id, out combatant))
            {
                combatant = col.GetComponentInParent<NetworkCombatant>();
                combatantCache[id] = combatant;
            }
            if (combatant != null)
            {
                if (combatant == selfCombatant || combatant.IsDead) return;
                Vector3 pos = combatant.transform.position;
                float dist = FlatDist(originBase, pos);
                bool inFov = InFov(originBase, fwd, pos, dist);
                if (!inFov && dist > NearRadius) return;
                if (combatantSeen.Add(combatant.GetInstanceID()))
                {
                    combatantCands.Add(new Candidate
                    {
                        kind = combatant.teamId == myTeam ? RayHitKind.Friend : RayHitKind.Enemy,
                        combatant = combatant,
                        root = combatant,
                        position = pos,
                        distance = dist,
                        inFov = inFov,
                    });
                }
                return;
            }

            // 2) 部署物（信标/补给箱/探测器/拦截器）；静物不作为可见实体。
            Component root;
            if (!kindCache.TryGetValue(id, out var kind))
            {
                kind = ClassifyDeployable(col, out root);
                kindCache[id] = kind;
                deployableRootCache[id] = root;
            }
            else
            {
                root = deployableRootCache[id];
            }
            if (root == null) return;   // Wall/Ground 等静物

            Vector3 dpos = root.transform.position;
            float ddist = FlatDist(originBase, dpos);
            bool dfov = InFov(originBase, fwd, dpos, ddist);
            if (!dfov && ddist > NearRadius) return;
            if (deployableSeen.Add(root.GetInstanceID()))
            {
                deployableCands.Add(new Candidate
                {
                    kind = kind,
                    combatant = null,
                    root = root,
                    position = dpos,
                    distance = ddist,
                    inFov = dfov,
                });
            }
        }

        /// <summary>前向 FOV 判定：水平距离 ≤ VisionRange 且方向夹角 ≤ 半角。</summary>
        private static bool InFov(Vector3 originBase, Vector3 fwd, Vector3 pos, float flatDist)
        {
            if (flatDist > VisionRange) return false;
            if (flatDist < 0.01f) return true;
            Vector3 dir = pos - originBase;
            dir.y = 0f;
            dir.Normalize();
            return Vector3.Dot(dir, fwd) >= Mathf.Cos(FovHalfAngleDeg * Mathf.Deg2Rad);
        }

        private static RayHitKind ClassifyDeployable(Component col, out Component root)
        {
            root = null;
            var beacon = col.GetComponentInParent<DeployBeacon>();
            if (beacon != null) { root = beacon; return RayHitKind.Beacon; }
            var crate = col.GetComponentInParent<LargeSupplyCrate>();
            if (crate != null) { root = crate; return RayHitKind.Crate; }
            var probe = col.GetComponentInParent<SensorProbe>();
            if (probe != null) { root = probe; return RayHitKind.Sensor; }
            var interceptor = col.GetComponentInParent<NetworkInterceptor>();
            if (interceptor != null) { root = interceptor; return RayHitKind.Interceptor; }
            return RayHitKind.Wall;
        }

        // ---------------------------------------------------------------
        // 预算选择 + 遮挡射线
        // ---------------------------------------------------------------

        private void SelectPending()
        {
            combatantCands.Sort(ByDistance);
            deployableCands.Sort(ByDistance);

            for (int i = 0; i < combatantCands.Count && pendingCount < MaxCombatantTargets; i++)
            {
                pendingRoots[pendingCount] = combatantCands[i].root;
                pending[pendingCount++] = ToTarget(combatantCands[i]);
            }
            for (int i = 0; i < deployableCands.Count && pendingCount < MaxTargets; i++)
            {
                pendingRoots[pendingCount] = deployableCands[i].root;
                pending[pendingCount++] = ToTarget(deployableCands[i]);
            }
        }

        private static VisionTarget ToTarget(in Candidate c)
        {
            return new VisionTarget
            {
                kind = c.kind,
                combatant = c.combatant,
                position = c.position,
                distance = c.distance,
                los = VisionLos.Clear,
                geometryBlockedRays = 0,
                inForwardFov = c.inFov,
            };
        }

        /// <summary>写入目标的两根 LOS 射线（头面 + 脚面），布局与 ParseVision 的解析一一对应。</summary>
        private static void WriteTargetRays(Vector3 originBase, ref VisionTarget t,
                                            QueryParameters q,
                                            NativeArray<RaycastCommand> commands, int baseIndex)
        {
            Vector3 highOrigin = originBase + Vector3.up * HighRayHeight;
            Vector3 lowOrigin = originBase + Vector3.up * LowRayHeight;
            Vector3 highPoint = t.position + Vector3.up * HighRayHeight;
            Vector3 lowPoint = t.position + Vector3.up * LowRayHeight;

            Vector3 highDir = highPoint - highOrigin;
            Vector3 lowDir = lowPoint - lowOrigin;
            float highLen = Mathf.Max(0.01f, highDir.magnitude);
            float lowLen = Mathf.Max(0.01f, lowDir.magnitude);

            commands[baseIndex] = new RaycastCommand(
                highOrigin, highDir / highLen, q, highLen);
            commands[baseIndex + 1] = new RaycastCommand(
                lowOrigin, lowDir / lowLen, q, lowLen);
        }

        /// <summary>解析一根遮挡射线（端点必须与 WriteTargetRays 完全一致）。</summary>
        private RayLos ResolveRay(ref VisionTarget t, Component root, int rayIdx, RaycastHit res)
        {
            float height = rayIdx == 0 ? HighRayHeight : LowRayHeight;
            Vector3 origin = lastOriginBase + Vector3.up * height;
            Vector3 point = t.position + Vector3.up * height;
            Vector3 dir = point - origin;
            float segLen = dir.magnitude;
            if (segLen < 0.01f) return RayLos.Reach;   // 与目标重合：视为可达
            dir /= segLen;

            bool hitSomething = res.collider != null;
            float hitDist = hitSomething ? res.distance : float.PositiveInfinity;
            bool reached = !hitSomething || BelongsToTarget(res.collider, root);

            // 烟雾遮挡：线段在物理命中之前穿过烟雾（与旧传感器语义一致）。
            if (NetworkSmokeVolume.SegmentIntersects(origin, dir, Mathf.Min(segLen, hitDist), out _))
                return RayLos.Smoke;

            return reached ? RayLos.Reach : RayLos.Geometry;
        }

        /// <summary>命中碰撞体是否属于目标本体（目标自身碰撞体 = 达成 LOS）。</summary>
        private static bool BelongsToTarget(Collider col, Component root)
        {
            if (root == null) return false;
            if (root is NetworkCombatant)
                return col.GetComponentInParent<NetworkCombatant>() == root;
            if (root is DeployBeacon) return col.GetComponentInParent<DeployBeacon>() == root;
            if (root is LargeSupplyCrate) return col.GetComponentInParent<LargeSupplyCrate>() == root;
            if (root is SensorProbe) return col.GetComponentInParent<SensorProbe>() == root;
            if (root is NetworkInterceptor) return col.GetComponentInParent<NetworkInterceptor>() == root;
            return false;
        }

        // ---------------------------------------------------------------
        // 工具
        // ---------------------------------------------------------------

        private static Vector3 DirFromYaw(float yawDeg)
        {
            return new Vector3(Mathf.Sin(yawDeg * Mathf.Deg2Rad), 0f,
                               Mathf.Cos(yawDeg * Mathf.Deg2Rad));
        }

        private static float FlatDist(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            d.y = 0f;
            return d.magnitude;
        }
    }
}
