using UnityEngine;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// 地形采样小工具。装饰物必须贴合生成后的地形,所以放置前要拿到真实
    /// 高程与坡度 —— 这些都从 Unity <see cref="Terrain"/> 读,不去碰 MapMagic 内部矩阵。
    /// </summary>
    public static class MapTerrainUtil
    {
        /// <summary>找到 XZ 方向覆盖该世界点的地形块(多瓦片时取第一个命中的)。</summary>
        public static Terrain FindTerrain(Vector3 worldPos)
        {
            Terrain[] terrains = Terrain.activeTerrains;
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain t = terrains[i];
                if (t == null || t.terrainData == null) continue;

                Vector3 p = t.transform.position;
                Vector3 s = t.terrainData.size;
                if (worldPos.x >= p.x && worldPos.x <= p.x + s.x &&
                    worldPos.z >= p.z && worldPos.z <= p.z + s.z)
                    return t;
            }
            return null;
        }

        /// <summary>该点的地形表面世界 Y。找不到地形时返回 0。</summary>
        public static float SampleHeight(Vector3 worldPos)
        {
            Terrain t = FindTerrain(worldPos);
            if (t == null) return 0f;
            return t.SampleHeight(worldPos) + t.transform.position.y;
        }

        /// <summary>该点的坡度(度)。找不到地形时返回 0。</summary>
        public static float SampleSlope(Vector3 worldPos)
        {
            Terrain t = FindTerrain(worldPos);
            if (t == null || t.terrainData == null) return 0f;

            Vector3 p = t.transform.position;
            Vector3 s = t.terrainData.size;

            float u = Mathf.Clamp01((worldPos.x - p.x) / Mathf.Max(0.001f, s.x));
            float v = Mathf.Clamp01((worldPos.z - p.z) / Mathf.Max(0.001f, s.z));
            return t.terrainData.GetSteepness(u, v);
        }

        /// <summary>该点的高度(归一化 0..1,相对地形高度范围)。</summary>
        public static float SampleNormalizedHeight(Vector3 worldPos)
        {
            Terrain t = FindTerrain(worldPos);
            if (t == null || t.terrainData == null) return 0f;

            float y = SampleHeight(worldPos) - t.transform.position.y;
            return Mathf.Clamp01(y / Mathf.Max(0.001f, t.terrainData.size.y));
        }

        /// <summary>
        /// 把实例的**几何底边**对齐到地面。
        ///
        /// 素材包里有两种轴心约定:工业包 145 个预制体是几何中心轴心
        /// (minY ≈ -height/2,油桶/集装箱/围栏都是),自然包是底座轴心。
        /// 直接把 position.y 设为地面高会让中心轴心的物体有一半埋进地里,
        /// 所以统一用渲染包围盒底边去贴地 —— 与轴心约定无关。
        /// </summary>
        public static void AlignBottomToGround(GameObject instance, float groundY)
        {
            if (instance == null) return;

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                Vector3 p0 = instance.transform.position;
                instance.transform.position = new Vector3(p0.x, groundY, p0.z);
                return;
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            Vector3 pos = instance.transform.position;
            instance.transform.position = new Vector3(pos.x, pos.y + (groundY - bounds.min.y), pos.z);
        }

        /// <summary>剔除实例上的碰撞体(草丛/小花这类纯装饰必须做,否则会灌入成百上千个碰撞体)。</summary>
        public static void StripColliders(GameObject instance)
        {
            if (instance == null) return;
            Collider[] cols = instance.GetComponentsInChildren<Collider>();
            for (int i = 0; i < cols.Length; i++)
                Object.DestroyImmediate(cols[i], allowDestroyingAssets: false);
        }

        /// <summary>预制体是否自带碰撞体(用于决定是否需要补一个)。</summary>
        public static bool HasCollider(GameObject prefab)
        {
            if (prefab == null) return false;
            return prefab.GetComponentInChildren<Collider>() != null;
        }
    }
}
