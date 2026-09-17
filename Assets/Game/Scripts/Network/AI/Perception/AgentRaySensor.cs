using System.Collections.Generic;
using Mirror;
using Unity.Collections;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>射线命中分类（PHASE9：10 类 one-hot，新增 Smoke）。</summary>
    public enum RayHitKind : byte
    {
        None = 0,       // 无命中
        Wall = 1,       // 墙体/掩体（静物）
        Ground = 2,     // 地面/斜面
        Enemy = 3,      // 敌方作战单位
        Friend = 4,     // 友方作战单位
        Beacon = 5,     // 部署信标
        Crate = 6,      // 补给箱
        Sensor = 7,     // 感应器
        Interceptor = 8, // 拦截装置
        Smoke = 9       // 烟雾（PHASE9：视线遮挡但不挡子弹）
    }

    /// <summary>
    /// Server-side ray-fan sensor (ML 训练共识 §3)。前向扇形（脚+头双高度面 ×
    /// 16 根 @120° / 60m）+ 环形近距（双高度 × 8 根 @360° / 12m）。每根输出
    /// [归一化距离, 9 类 one-hot] = 10 float。
    ///
    /// 命中分类经 <see cref="Classify"/>：先查命中体的已知部署物组件缓存字典
    /// （按 instanceId），作战单位用 <see cref="NetworkCombatant"/> 判定敌我；
    /// 其余静物按 tag 归为墙或地。每决策步调用一次 <see cref="Sample"/>，
    /// 结果供观测收集与"目击敌人"广播复用。
    /// </summary>
    public class AgentRaySensor : MonoBehaviour
    {
        // ---- 共识规格 ----
        public const int ForwardRays = 16;
        public const float ForwardFovDeg = 120f;
        public const float ForwardRange = 60f;

        public const int RingRays = 8;
        public const float RingRange = 12f;

        public const float FootHeight = 0.30f;   // 脚面（低掩体/腿）
        public const float HeadHeight = 1.50f;   // 头面（人形目标/高掩体）

        /// <summary>总输出维度：48 根 × (距离 + 9 类 one-hot)。</summary>
        public const int OutputSize = (ForwardRays * 2 + RingRays * 2) * 10;

        /// <summary>总射线数（前向双高度 32 + 环形双高度 16）。批处理数组按此分配。</summary>
        public const int TotalRays = ForwardRays * 2 + RingRays * 2;

        /// <summary>每根射线的最新命中（供广播/调试读取；index 与输出一致）。</summary>
        public struct RayHit
        {
            public RayHitKind kind;
            public float distance;       // 世界距离
            public Vector3 point;        // 命中点
            public NetworkCombatant combatant; // 命中作战单位（敌/友类时有值）
        }

        public RayHit[] Latest { get; private set; }
        public Vector3 SensorOriginForward { get; private set; }
        public Vector3 SensorOriginRing { get; private set; }

        // 部署物分类组件缓存：instanceId → kind（每帧最多命中几十个不同物体，
        // 字典远快于逐射线 GetComponentInParent）。
        private readonly Dictionary<int, RayHitKind> kindCache = new Dictionary<int, RayHitKind>();
        private readonly Dictionary<int, NetworkCombatant> combatantCache =
            new Dictionary<int, NetworkCombatant>();

        private int myTeam = -1;
        private readonly RaycastHit[] hits = new RaycastHit[32];

        // PHASE9: 缓存本次采样的原点/朝向，供 ParseBatchHits 烟雾检测重建射线方向。
        private Vector3 lastOrigin;
        private float lastYaw;

        private void Awake()
        {
            Latest = new RayHit[TotalRays];
        }

        /// <summary>每决策步采样。originBase = 实体脚底位置，yaw = 当前朝向（度）。</summary>
        public void Sample(Vector3 originBase, float yaw, int team)
        {
            BeginSample(originBase, team);

            for (int i = 0; i < TotalRays; i++)
            {
                GetRay(i, originBase, yaw, out var origin, out var dir, out var range);
                CastRay(origin, dir, range, i);
            }
        }

        /// <summary>
        /// 批处理路径（PHASE9 FSM）：把本传感器的 48 根射线写入共享命令数组
        /// （从 offset 开始），由 FSMBattleSystem 统一 ScheduleBatch 后同帧
        /// Complete。语义与 <see cref="Sample"/> 一致（同样重置缓存、更新
        /// SensorOrigin*），仅发射方式不同。
        /// </summary>
        public void BuildBatchCommands(Vector3 originBase, float yaw, int team,
                                       NativeArray<RaycastCommand> commands, int offset)
        {
            BeginSample(originBase, team);
            lastYaw = yaw;

            for (int i = 0; i < TotalRays; i++)
            {
                GetRay(i, originBase, yaw, out var origin, out var dir, out var range);
                commands[offset + i] = new RaycastCommand(
                    origin, dir,
                    new QueryParameters(Physics.DefaultRaycastLayers, true,
                                        QueryTriggerInteraction.Ignore, true),
                    range);
            }
        }

        /// <summary>
        /// 批处理路径结果解析：读取共享命中数组（从 offset 开始），分类并填充
        /// <see cref="Latest"/>。在 ScheduleBatch 完成后由 FSMBattleSystem 调用。
        /// </summary>
        public void ParseBatchHits(NativeArray<RaycastHit> results, int offset)
        {
            for (int i = 0; i < TotalRays; i++)
            {
                RayHit hit = default;
                hit.kind = RayHitKind.None;
                hit.distance = RangeOf(i);

                var res = results[offset + i];
                if (res.collider != null)
                {
                    hit.point = res.point;
                    hit.distance = res.distance;
                    hit.kind = Classify(res.collider, out var c);
                    hit.combatant = c;
                }

                // PHASE9: 烟雾遮挡——如果烟雾比物理命中更近，视线被烟雾挡住。
                GetRay(i, lastOrigin, lastYaw, out var origin, out var dir, out var range);
                if (NetworkSmokeVolume.SegmentIntersects(origin, dir, hit.distance, out float smokeDist))
                {
                    hit.kind = RayHitKind.Smoke;
                    hit.distance = smokeDist;
                    hit.combatant = null;
                }

                Latest[i] = hit;
            }
        }

        private void BeginSample(Vector3 originBase, int team)
        {
            myTeam = team;
            kindCache.Clear();
            combatantCache.Clear();

            SensorOriginForward = originBase + Vector3.up * 0f;
            SensorOriginRing = originBase;
            lastOrigin = originBase;
            lastYaw = 0f;   // Set by BuildBatchCommands/GetRay caller
        }

        /// <summary>第 idx 根射线的原点/方向/长度（布局的唯一真源）。</summary>
        private void GetRay(int idx, Vector3 originBase, float yaw,
                            out Vector3 origin, out Vector3 dir, out float range)
        {
            if (idx < ForwardRays * 2)
            {
                // 前向扇形：两高度面 × 16 根，从左到右扫过 FOV。
                float half = ForwardFovDeg * 0.5f;
                int p = idx / ForwardRays;
                int i = idx % ForwardRays;
                float t = ForwardRays == 1 ? 0.5f : i / (float)(ForwardRays - 1);
                float ang = yaw + Mathf.Lerp(-half, half, t);
                range = ForwardRange;
                origin = originBase + Vector3.up * (p == 0 ? FootHeight : HeadHeight);
                dir = DirFromYaw(ang);
            }
            else
            {
                // 环形近距：两高度面 × 8 根，均匀 360°。
                int j = idx - ForwardRays * 2;
                int p = j / RingRays;
                int i = j % RingRays;
                float ang = yaw + (360f / RingRays) * i;
                range = RingRange;
                origin = originBase + Vector3.up * (p == 0 ? FootHeight : HeadHeight);
                dir = DirFromYaw(ang);
            }
        }

        private static float RangeOf(int idx)
        {
            return idx < ForwardRays * 2 ? ForwardRange : RingRange;
        }

        private static Vector3 DirFromYaw(float yawDeg)
        {
            return new Vector3(Mathf.Sin(yawDeg * Mathf.Deg2Rad), 0f,
                               Mathf.Cos(yawDeg * Mathf.Deg2Rad));
        }

        private void CastRay(Vector3 origin, Vector3 dir, float range, int idx)
        {
            RayHit hit = default;
            hit.kind = RayHitKind.None;
            hit.distance = range;

            int n = Physics.RaycastNonAlloc(
                new Ray(origin, dir), hits, range,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

            float best = float.PositiveInfinity;
            int bestIdx = -1;
            for (int i = 0; i < n; i++)
            {
                if (hits[i].distance < best)
                {
                    best = hits[i].distance;
                    bestIdx = i;
                }
            }

            if (bestIdx >= 0)
            {
                var col = hits[bestIdx].collider;
                hit.point = hits[bestIdx].point;
                hit.distance = hits[bestIdx].distance;
                hit.kind = Classify(col, out var c);
                hit.combatant = c;
            }

            // PHASE9: 烟雾遮挡（单射线路径，ML 兼容）。
            if (NetworkSmokeVolume.SegmentIntersects(origin, dir, hit.distance, out float smokeDist))
            {
                hit.kind = RayHitKind.Smoke;
                hit.distance = smokeDist;
                hit.combatant = null;
            }

            Latest[idx] = hit;
        }

        /// <summary>分类命中体；返回作战单位时输出实例。</summary>
        private RayHitKind Classify(Component col, out NetworkCombatant combatant)
        {
            combatant = null;
            int id = col.GetInstanceID();

            // 1) 作战单位（含敌我）。
            if (combatantCache.TryGetValue(id, out var cachedC))
                combatant = cachedC;
            else
            {
                var go = col.GetComponentInParent<NetworkCombatant>();
                if (go != null)
                {
                    // 一个作战单位多个碰撞体（站/蹲胶囊）共享缓存。
                    // 用父对象 id 也缓存一份。
                    combatantCache[col.transform.root.GetInstanceID()] = go;
                    combatantCache[id] = go;
                }
                else
                {
                    combatantCache[id] = null;
                }
                combatant = go;
            }

            if (combatant != null)
            {
                if (combatant.teamId == myTeam) return RayHitKind.Friend;
                return RayHitKind.Enemy;
            }

            // 2) 已知部署物组件（缓存）。
            if (kindCache.TryGetValue(id, out var kind))
                return kind;

            kind = ClassifyDeployable(col);
            kindCache[id] = kind;
            return kind;
        }

        private RayHitKind ClassifyDeployable(Component col)
        {
            // 组件检查顺序按共识词表：信标/补给箱/感应器/拦截。
            if (col.GetComponentInParent<DeployBeacon>() != null) return RayHitKind.Beacon;
            if (col.GetComponentInParent<LargeSupplyCrate>() != null) return RayHitKind.Crate;
            if (col.GetComponentInParent<SensorProbe>() != null) return RayHitKind.Sensor;
            if (col.GetComponentInParent<NetworkInterceptor>() != null) return RayHitKind.Interceptor;

            // 3) 静物：法线朝上 → 地面/斜面；否则墙体/掩体。
            if (col is Collider c)
            {
                // 用包围盒近似顶面法线（避免 MeshCollider 法线读取差异）。
                var b = c.bounds;
                if (b.size.y < 0.4f && b.extents.x > 0.5f && b.extents.z > 0.5f)
                    return RayHitKind.Ground;
            }
            return RayHitKind.Wall;
        }

        /// <summary>把最近一次采样写入输出数组（观测收集器调用）。返回写入长度。</summary>
        public int Write(float[] output)
        {
            const int Kinds = 9;
            int o = 0;

            for (int i = 0; i < Latest.Length; i++)
            {
                var h = Latest[i];
                float range = RangeOf(i);
                output[o++] = h.kind == RayHitKind.None ? 1f : h.distance / range;

                for (int k = 0; k < Kinds; k++)
                    output[o++] = h.kind == (RayHitKind)k ? 1f : 0f;
            }
            return o;
        }
    }
}
