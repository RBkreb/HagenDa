using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// 训练地图布局描述（M1 附录 C：多地图轮换防过拟合）。纯运行时数据——
    /// TrainingMapBuilder 程序化生成几何，TrainingSessionManager 每回合按
    /// <see cref="mapRotation"/> 轮换 <see cref="TrainingMap"/> 布局：
    /// 传送实体、重建掩体、重设 home/中央抽象争夺点。
    ///
    /// 两类布局：Square（正方形）/ Wave（水平波浪形，纵深正弦扰动）。
    /// 掩体规格：高掩体(2.2m) / 矮掩体(1.0m) / 高位掩体(1.4m 台阶) / 斜面；
    /// 掩体间距 ≥4m 保证走位空间。
    /// </summary>
    [CreateAssetMenu(menuName = "HagenDa/Training Map", fileName = "TrainingMap")]
    public class TrainingMap : ScriptableObject
    {
        public enum MapShape { Square = 0, Wave = 1 }

        [Header("Shape")]
        public MapShape shape = MapShape.Square;
        public float size = 60f;              // X 宽度
        public float depth = 60f;             // Z 深度（Square=size×depth；Wave 深度按正弦收缩）
        public float waveAmplitude = 12f;     // Wave：正弦幅度（纵深边界向内收缩）
        public float waveLength = 30f;        // Wave：正弦波长（沿 X）

        [Header("Cover (procedural, seed-reproducible)")]
        public int coverCount = 14;
        public int layoutSeed = 12345;

        [Header("Zones")]
        public float homeOffset = 24f;        // 双方 home 距中心
        public float zoneRadius = 8f;         // 中央抽象争夺点半径

        // ---- 运行时生成结果（BuildLayout 填充）----
        [System.Serializable]
        public struct Cover
        {
            public enum Kind { Tall = 0, Low = 1, High = 2, Ramp = 3 }
            public Kind kind;
            public Vector3 position;   // 相对地图中心
            public float rotationY;
            public Vector3 scale;
        }

        public List<Cover> covers = new List<Cover>();
        public Vector3 redHome;    // 相对中心（-Z 侧）
        public Vector3 blueHome;   // 相对中心（+Z 侧）
        public Vector3 zoneCenter;

        public void BuildLayout()
        {
            var rng = new System.Random(layoutSeed);
            covers.Clear();

            zoneCenter = Vector3.zero;
            redHome = new Vector3(0f, 0f, -homeOffset);
            blueHome = new Vector3(0f, 0f, homeOffset);

            // 掩体泊松式投放：拒绝采样保证间距 ≥ minGap。
            const float minGap = 4f;
            var placed = new List<Vector3>();
            int guard = 0;
            while (covers.Count < coverCount && guard++ < coverCount * 40)
            {
                float x = ((float)rng.NextDouble() * 2f - 1f) * (size * 0.5f - 4f);
                float maxZ = MaxAbsZ(x);
                float z = ((float)rng.NextDouble() * 2f - 1f) * (maxZ - 3f);

                var p = new Vector3(x, 0f, z);

                // 中央争夺点保持净空（半径+2m），home 半径 3m 净空。
                if (new Vector2(p.x, p.z).magnitude < zoneRadius + 2f) continue;
                if (Vector2.Distance(new Vector2(p.x, p.z), new Vector2(0f, -homeOffset)) < 3f) continue;
                if (Vector2.Distance(new Vector2(p.x, p.z), new Vector2(0f, homeOffset)) < 3f) continue;

                bool ok = true;
                foreach (var q in placed)
                    if (Vector2.Distance(new Vector2(p.x, p.z), new Vector2(q.x, q.z)) < minGap)
                    { ok = false; break; }
                if (!ok) continue;

                placed.Add(p);

                var c = new Cover();
                c.position = p;
                c.rotationY = (float)rng.NextDouble() * 360f;

                // 种类分布：高4 / 矮5 / 高位3 / 斜面2（比例固定，顺序随机）。
                int roll = rng.Next(100);
                if (roll < 29) { c.kind = Cover.Kind.Tall; c.scale = new Vector3(2f, 2.2f, 0.6f); }
                else if (roll < 64) { c.kind = Cover.Kind.Low; c.scale = new Vector3(2f, 1.0f, 0.8f); }
                else if (roll < 86) { c.kind = Cover.Kind.High; c.scale = new Vector3(3f, 1.4f, 3f); }
                else { c.kind = Cover.Kind.Ramp; c.scale = new Vector3(3f, 1.2f, 6f); }

                covers.Add(c);
            }
        }

        /// <summary>某 X 坐标处可用的最大 |Z|（边界）。Wave：正弦内收。</summary>
        public float MaxAbsZ(float x)
        {
            float half = depth * 0.5f;
            if (shape == MapShape.Square) return half;
            float shrink = waveAmplitude * (0.5f + 0.5f * Mathf.Sin(x * Mathf.PI * 2f / waveLength));
            return half - shrink;
        }

        /// <summary>Z 坐标是否在地图内（含边界墙判定用）。</summary>
        public bool IsInside(float x, float z)
        {
            if (Mathf.Abs(x) > size * 0.5f) return false;
            return Mathf.Abs(z) <= MaxAbsZ(x);
        }
    }
}
