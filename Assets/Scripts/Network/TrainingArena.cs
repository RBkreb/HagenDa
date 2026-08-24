using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// 运行时训练地图实例（M1 附录 C）。TrainingSessionManager 每回合调用
    /// <see cref="ApplyLayout"/>：销毁旧几何、按 <see cref="TrainingMap"/> 布局
    /// 重建边界/地面/掩体，并报告 home 与中央抽象争夺点世界坐标。
    /// 非网络对象：仅服务端训练使用，客户端训练场景不渲染（编辑器单机训练）。
    /// </summary>
    public class TrainingArena : MonoBehaviour
    {
        public Transform root;

        private readonly List<GameObject> spawned = new List<GameObject>();

        public void ApplyLayout(TrainingMap map)
        {
            Clear();

            if (root == null)
            {
                var go = new GameObject("ArenaGeometry");
                root = go.transform;
                root.SetParent(transform, false);
            }

            BuildFloor(map);
            BuildWalls(map);
            BuildCovers(map);
        }

        public void Clear()
        {
            foreach (var go in spawned)
                if (go != null) Destroy(go);
            spawned.Clear();
            CoverRegistry.Clear();
        }

        private GameObject Box(Vector3 localPos, Vector3 scale, float rotY, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(root, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, rotY, 0f);
            go.transform.localScale = scale;
            spawned.Add(go);
            return go;
        }

        private void BuildFloor(TrainingMap map)
        {
            if (map.shape == TrainingMap.MapShape.Square)
            {
                Box(Vector3.zero, new Vector3(map.size, 0.5f, map.depth), 0f, "Floor");
            }
            else
            {
                // 波浪形：沿 X 分段铺地板，每段宽度按 MaxAbsZ 收缩。
                const float seg = 2f;
                int n = Mathf.CeilToInt(map.size / seg);
                for (int i = 0; i < n; i++)
                {
                    float x0 = -map.size * 0.5f + i * seg;
                    float xm = x0 + seg * 0.5f;
                    float zHalf = map.MaxAbsZ(xm);
                    Box(new Vector3(xm, -0.25f, 0f), new Vector3(seg + 0.1f, 0.5f, zHalf * 2f), 0f, "FloorSeg");
                }
            }
        }

        private void BuildWalls(TrainingMap map)
        {
            const float h = 4f, t = 1f;

            // 左右边界（两种形状共用直墙）。
            Box(new Vector3(-map.size * 0.5f - t * 0.5f, h * 0.5f, 0f),
                new Vector3(t, h, map.depth + 2f), 0f, "WallW");
            Box(new Vector3(map.size * 0.5f + t * 0.5f, h * 0.5f, 0f),
                new Vector3(t, h, map.depth + 2f), 0f, "WallE");

            // 前后边界：Square 直墙；Wave 按正弦分段贴边。
            if (map.shape == TrainingMap.MapShape.Square)
            {
                Box(new Vector3(0f, h * 0.5f, -map.depth * 0.5f - t * 0.5f),
                    new Vector3(map.size + 2f, h, t), 0f, "WallS");
                Box(new Vector3(0f, h * 0.5f, map.depth * 0.5f + t * 0.5f),
                    new Vector3(map.size + 2f, h, t), 0f, "WallN");
            }
            else
            {
                const float seg = 2f;
                int n = Mathf.CeilToInt(map.size / seg);
                for (int i = 0; i <= n; i++)
                {
                    float xm = -map.size * 0.5f + i * seg;
                    float zHalf = map.MaxAbsZ(xm);
                    Box(new Vector3(xm, h * 0.5f, -zHalf - t * 0.5f),
                        new Vector3(seg + 0.5f, h, t), 0f, "WallS_Seg");
                    Box(new Vector3(xm, h * 0.5f, zHalf + t * 0.5f),
                        new Vector3(seg + 0.5f, h, t), 0f, "WallN_Seg");
                }
            }
        }

        private void BuildCovers(TrainingMap map)
        {
            foreach (var c in map.covers)
            {
                var go = Box(new Vector3(c.position.x, c.scale.y * 0.5f, c.position.z),
                             c.scale, c.rotationY,
                             $"Cover_{c.kind}");
                CoverRegistry.Register(go);
            }
        }
    }

    /// <summary>
    /// 当前回合掩体注册表（服务端静态）。脚本陪练 AI 用它找最近掩体；
    /// 每回合 <see cref="Clear"/>。
    /// </summary>
    public static class CoverRegistry
    {
        private static readonly List<Collider> covers = new List<Collider>();

        public static void Register(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col != null) covers.Add(col);
        }

        public static void Clear() => covers.RemoveAll(c => c == null);

        /// <summary>距离 position 最近的掩体位置；无掩体返回 null。</summary>
        public static Vector3? NearestCover(Vector3 position, out bool isLow)
        {
            isLow = false;
            Collider best = null;
            float bestD = float.PositiveInfinity;
            foreach (var c in covers)
            {
                if (c == null) continue;
                float d = (c.bounds.center - position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best == null) return null;
            isLow = best.bounds.size.y <= 1.1f;
            return best.bounds.center;
        }
    }
}
