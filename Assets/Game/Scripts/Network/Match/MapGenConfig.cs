using System;
using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>镜像/旋转对称模式(竞技公平性保证)。</summary>
    public enum MapSymmetry { None = 0, Rotate180 = 1, MirrorX = 2, MirrorZ = 3, MirrorDiagonal = 4 }

    /// <summary>据点平台形状。</summary>
    public enum MapPadShape { Circle = 0, Rectangle = 1 }

    /// <summary>
    /// 一条通路。坐标用**世界坐标**(地图中心在原点),内部按 sizeX/sizeZ 换算到栅格。
    /// </summary>
    [Serializable]
    public class MapLaneDef
    {
        public string name = "Lane";

        [Tooltip("走廊世界坐标(米),地图中心为原点。")]
        public Vector2 from = new Vector2(-200f, 0f);
        public Vector2 to = new Vector2(200f, 0f);

        [Tooltip("走廊宽度 (m)。")]
        public float width = 40f;

        [Tooltip("边缘过渡宽度 (m)。")]
        public float transition = 25f;

        [Tooltip("向高度基准压平的程度 0..1。0 = 完全保留噪声,1 = 完全压平。")]
        [Range(0f, 1f)] public float flatten = 0.55f;

        [Tooltip("压平目标高度(归一化 0..1,乘 heightMax 得米)。")]
        public float levelNormalized = 0.10f;

        [Tooltip("走廊额外抬升(归一化)。负数 = 洼地,正数 = 台地。")]
        public float heightBias = 0f;
    }

    /// <summary>
    /// 一条装饰规则。可撒在地图任意位置,也可只在据点/安全区内。
    /// </summary>
    [Serializable]
    public class MapDecorRule
    {
        public string name = "Decor";

        [Tooltip("候选预制体,按面积权重随机选取。")]
        public GameObject[] prefabs = new GameObject[0];

        [Tooltip("按面积密度放置(每 1000 m² 的数量)。为 0 时用 count。")]
        public float densityPer1000m2 = 0f;

        [Tooltip("固定数量(densityPer1000m2 为 0 时生效)。")]
        public int count = 0;

        [Tooltip("同类之间的最小间距 (m)。")]
        public float minSpacing = 10f;

        [Tooltip("可放置的最大坡度(度)。")]
        public float slopeMaxDeg = 22f;

        [Tooltip("可放置的高度带(归一化 0..1,乘 heightMax 得米)。")]
        public Vector2 heightBand = new Vector2(0f, 1f);

        [Tooltip("缩放范围(均匀随机)。")]
        public Vector2 scaleRange = new Vector2(1f, 1f);

        [Tooltip("绕 Y 随机朝向。关闭时保留预制体原始朝向。")]
        public bool randomYaw = true;

        [Tooltip("贴合地面法线(倒伏的石头/植被用)。")]
        public bool alignToSlope = false;

        [Tooltip("聚簇噪声尺度 (m)。0 = 不聚簇,均匀散布。")]
        public float clusterScale = 0f;

        [Tooltip("聚簇阈值 0..1,越大越稀疏成团。")]
        [Range(0f, 1f)] public float clusterThreshold = 0.5f;

        [Tooltip("禁止放置的额外净空半径 (m),会叠加在据点净空之上。")]
        public float clearanceRadius = 0f;

        [Tooltip("避开据点和安全区。")]
        public bool avoidZones = true;

        [Tooltip("剔除实例上的碰撞体(草丛/花等纯装饰必须开启,否则会灌入数百个碰撞体)。")]
        public bool stripColliders = false;
    }

    /// <summary>地形贴图层规则:由坡度/高度/遮罩决定。</summary>
    [Serializable]
    public class MapLayerRule
    {
        public string name = "Layer";

        [Tooltip("漫反射贴图。首次构建时会据此生成 TerrainLayer 资产。")]
        public Texture2D diffuse;

        [Tooltip("贴图平铺尺寸 (m)。必须能整除地形尺寸,否则块间会有接缝。")]
        public Vector2 tileSize = new Vector2(20f, 20f);

        [Tooltip("坡度区间(度),落在此区间内的像素为本层权重。")]
        public Vector2 slopeRange = new Vector2(0f, 90f);

        public float slopeSmooth = 8f;

        [Tooltip("高度区间(归一化 0..1)。")]
        public Vector2 heightRange = new Vector2(0f, 1f);

        public float heightSmooth = 0.05f;
    }

    /// <summary>
    /// HagenDa MapMagic 地图生成参数(单一参数源)。
    ///
    /// 坐标约定:所有空间字段用**世界坐标**,原点在地图中心,范围
    /// <c>[-sizeX/2, +sizeX/2] × [-sizeZ/2, +sizeZ/2]</c>。这与
    /// <see cref="MapAnchor.position"/> 及既有场景约定一致。
    /// </summary>
    [CreateAssetMenu(menuName = "HagenDa/Map Gen Config", fileName = "MapGenConfig")]
    public class MapGenConfig : ScriptableObject
    {
        // ------------------------------------------------------------------
        // 地形
        // ------------------------------------------------------------------

        [Header("地形尺寸")]
        [Tooltip("X 方向边长 (m)。")]
        public float sizeX = 500f;

        [Tooltip("Z 方向边长 (m)。")]
        public float sizeZ = 500f;

        [Tooltip("高度图分辨率。1025 → 500 m 上约 0.49 m/像素。")]
        public int resolution = 1025;

        [Tooltip("地形最大高度 (m) = MapMagic globals.height。")]
        public float heightMax = 120f;

        [Tooltip("随机种子。改它会得到完全不同但同样合法的地图。")]
        public int seed = 20260917;

        [Tooltip("地形材质。留空则用 MapMagic 默认(HDRP/TerrainLit)。")]
        public Material terrainMaterial;

        // ------------------------------------------------------------------
        // 对称
        // ------------------------------------------------------------------

        [Header("对称(竞技公平)")]
        [Tooltip("把地形按此变换与其自身平均,保证双方地形完全一致。")]
        public MapSymmetry symmetry = MapSymmetry.Rotate180;

        // ------------------------------------------------------------------
        // 边界山脊
        // ------------------------------------------------------------------

        [Header("边界")]
        [Tooltip("不可通行外圈宽度 (m)。")]
        public float ringWidth = 50f;

        [Tooltip("山脊顶高度(归一化 0..1)。")]
        [Range(0f, 1f)] public float ringHeight = 0.85f;

        [Tooltip("山脊内侧过渡宽度 (m)。")]
        public float ringTransition = 25f;

        // ------------------------------------------------------------------
        // 基础地形
        // ------------------------------------------------------------------

        [Header("基础地形")]
        [Tooltip("谷底基准高度(归一化)。")]
        [Range(0f, 1f)] public float baseHeight = 0.09f;

        [Tooltip("起伏振幅(归一化)。")]
        [Range(0f, 1f)] public float hillAmplitude = 0.22f;

        [Tooltip("起伏主尺度 (m)。越小越细碎。")]
        public float hillScale = 180f;

        [Tooltip("细节分层偏置 0..1。>0.5 更粗糙。")]
        [Range(0f, 1f)] public float hillDetail = 0.45f;

        [Tooltip("起伏锐度:>1 出山脊,<1 出圆丘。")]
        public float hillSharpness = 1f;

        public List<MapLaneDef> lanes = new List<MapLaneDef>();

        // ------------------------------------------------------------------
        // 中央地标
        // ------------------------------------------------------------------

        [Header("中央地标")]
        [Tooltip("在地图中心生成一个可通行的台地/高点,用于打断出生点对穿视线。")]
        public bool centerFeature = true;

        [Tooltip("顶部平台半径 (m)。")]
        public float centerRadius = 45f;

        [Tooltip("台地边缘过渡 (m)。")]
        public float centerTransition = 40f;

        [Tooltip("中心抬升(归一化,叠加在基础地形上)。")]
        [Range(0f, 0.6f)] public float centerHeight = 0.22f;

        // ------------------------------------------------------------------
        // 据点 / 安全区平台
        // ------------------------------------------------------------------

        [Header("据点 / 安全区")]
        [Tooltip("按锚点自动压平平台。平台高度由下方向锚点高度插值决定,保证道路连续。")]
        public bool flattenZonePads = true;

        [Tooltip("平台边缘过渡 (m)。")]
        public float padTransition = 30f;

        [Tooltip("CapturePoint 平台高度(归一化)。")]
        [Range(0f, 1f)] public float capturePadHeight = 0.14f;

        [Tooltip("Garrison(安全区)平台高度(归一化)。")]
        [Range(0f, 1f)] public float garrisonPadHeight = 0.10f;

        [Tooltip("平台形状。")]
        public MapPadShape padShape = MapPadShape.Circle;

        [Tooltip("Rect 形状的平台半宽/半深 (m)。")]
        public Vector2 padExtent = new Vector2(60f, 60f);

        // ------------------------------------------------------------------
        // 锚点
        // ------------------------------------------------------------------

        [Header("锚点")]
        [Tooltip("GENERATED:由 Build Default Anchors 按下面的规则自动填。")]
        public List<MapAnchor> anchors = new List<MapAnchor>();

        [Tooltip("自动生成锚点时,安全区在有效战斗区角上的额外内缩 (m)。")]
        public float spawnInset = 5f;

        [Tooltip("自动生成锚点时,据点 A 距中心的距离 (m)。")]
        public float captureAOffset = 110f;

        [Tooltip("自动生成锚点时,B/C 距中心的距离 (m)。")]
        public float captureBCOffset = 115f;

        [Tooltip("据点判定半径 (m)。")]
        public float captureRadius = 30f;

        [Tooltip("安全区判定半径 (m)。")]
        public float garrisonRadius = 70f;

        // ------------------------------------------------------------------
        // 装饰
        // ------------------------------------------------------------------

        [Header("地图装饰物")]
        public List<MapDecorRule> mapDecor = new List<MapDecorRule>();

        [Header("据点 / 安全区内装饰")]
        [Tooltip("按锚点朝向做结构化布置(不随机),平台已压平所以不会悬空。")]
        public List<MapDecorRule> zoneDecor = new List<MapDecorRule>();

        [Tooltip("据点内装饰的环半径(相对锚点半径的倍率)。")]
        public Vector2 zoneDecorRing = new Vector2(0.45f, 0.85f);

        [Tooltip("据点内装饰的内圈净空(倍率),保证出生/复活不被卡住。")]
        [Range(0f, 1f)] public float zoneDecorInnerClearance = 0.35f;

        // ------------------------------------------------------------------
        // 掩体
        // ------------------------------------------------------------------

        [Header("掩体")]
        [Tooltip("掩体总数。按「15 m 内至少 1 个半身掩体」反推:400×400 m 有效战斗区" +
                 "约需 226 个理想铺满,取 2.5 倍余量 ≈ 600。")]
        public int coverCount = 600;

        public int coverSeed = 20260917;

        [Tooltip("掩体最小间距 (m)。")]
        public float coverMinGap = 9f;

        [Tooltip("半身掩体(约 1.0–1.3 m)。")]
        public GameObject[] coverLowPrefabs = new GameObject[0];

        [Tooltip("全身掩体(约 1.8 m+,能挡步枪)。")]
        public GameObject[] coverTallPrefabs = new GameObject[0];

        [Tooltip("大型块状掩体/可绕行结构(3 m+)。")]
        public GameObject[] coverBlockPrefabs = new GameObject[0];

        [Tooltip("掩体最大落点坡度(度)。")]
        public float coverSlopeMaxDeg = 18f;

        [Tooltip("掩体距据点中心的最小净空(倍率,乘锚点半径)。")]
        public float coverZoneClearance = 1.15f;

        // ------------------------------------------------------------------
        // 地表贴图
        // ------------------------------------------------------------------

        [Header("地表贴图(最多 8 层,建议 4–5)")]
        public List<MapLayerRule> terrainLayers = new List<MapLayerRule>();

        // ------------------------------------------------------------------
        // 地图魔法图
        // ------------------------------------------------------------------

        [Header("MapMagic 图")]
        [Tooltip("叠加的细节噪声强度(归一化)。")]
        [Range(0f, 0.3f)] public float detailAmplitude = 0.045f;

        [Tooltip("细节噪声尺度 (m)。")]
        public float detailScale = 45f;

        [Tooltip("侵蚀迭代次数。0 = 关闭(最快)。每块秒级。")]
        [Range(0, 6)] public int erosionIterations = 0;

        [Tooltip("输出前模糊迭代次数,用于消除侵蚀毛刺。")]
        [Range(0, 10)] public int blurIterations = 1;

        [Tooltip("图资产输出路径。")]
        public string graphPath = "Assets/Game/Match/Graphs/HagenDaMacro.asset";

        [Tooltip("生成的掩码资产输出路径。")]
        public string maskPath = "Assets/Game/Match/Graphs/HagenDaMask.asset";

        // ------------------------------------------------------------------
        // 派生
        // ------------------------------------------------------------------

        public float HalfX => sizeX * 0.5f;
        public float HalfZ => sizeZ * 0.5f;

        /// <summary>世界坐标 → 归一化 [0,1](x 向右,z 向上)。</summary>
        public Vector2 WorldToNormalized(Vector2 world) =>
            new Vector2((world.x + HalfX) / Mathf.Max(1f, sizeX), (world.y + HalfZ) / Mathf.Max(1f, sizeZ));

        public Vector2 NormalizedToWorld(Vector2 n) =>
            new Vector2(n.x * sizeX - HalfX, n.y * sizeZ - HalfZ);

        /// <summary>有效战斗区半宽(外圈山脊之内)。</summary>
        public float PlayHalfX => Mathf.Max(1f, HalfX - ringWidth);
        public float PlayHalfZ => Mathf.Max(1f, HalfZ - ringWidth);

        /// <summary>
        /// 按当前尺寸/半径参数生成一套默认锚点:
        /// 两个安全区在对角,据点 A 在中心偏一侧,据点 B/C 对称分布在另一侧。
        ///
        /// 安全区中心按 <c>PlayHalf - garrisonRadius - spawnInset</c> 内缩,保证
        /// 半径 70 m 的安全区**完整落在有效战斗区内**,不会压进外圈山脊或溢出地图。
        /// 据点同理,保证压平后的平台不越界。
        /// </summary>
        public void BuildDefaultAnchors()
        {
            anchors = new List<MapAnchor>();

            float gx = Mathf.Max(1f, PlayHalfX - garrisonRadius - spawnInset);
            float gz = Mathf.Max(1f, PlayHalfZ - garrisonRadius - spawnInset);

            // 安全区:对角线
            anchors.Add(new MapAnchor
            {
                role = AnchorRole.GarrisonRed,
                position = new Vector3(-gx, 0f, -gz),
                radius = garrisonRadius,
                deployPointCount = 8,
            });
            anchors.Add(new MapAnchor
            {
                role = AnchorRole.GarrisonBlue,
                position = new Vector3(gx, 0f, gz),
                radius = garrisonRadius,
                deployPointCount = 8,
            });

            // 据点:A 在中心一侧;B/C 关于中心对称,在另一侧
            float bcx = captureBCOffset * 0.85f;
            float bcz = captureBCOffset * 0.55f;

            var a = new MapAnchor
            {
                role = AnchorRole.CapturePoint,
                letter = "A",
                position = new Vector3(0f, 0f, captureAOffset),
                radius = captureRadius,
                deployPointCount = 6,
            };
            var b = new MapAnchor
            {
                role = AnchorRole.CapturePoint,
                letter = "B",
                position = new Vector3(-bcx, 0f, -bcz),
                radius = captureRadius,
                deployPointCount = 6,
            };
            var c = new MapAnchor
            {
                role = AnchorRole.CapturePoint,
                letter = "C",
                position = new Vector3(bcx, 0f, -bcz),
                radius = captureRadius,
                deployPointCount = 6,
            };
            anchors.Add(a);
            anchors.Add(b);
            anchors.Add(c);

            // 真人出生点:两侧安全区各一组,散布在安全区半径内
            anchors.Add(new MapAnchor
            {
                role = AnchorRole.PlayerSpawn,
                markerName = "SpawnRed",
                position = new Vector3(-gx, 0f, -gz),
                radius = garrisonRadius * 0.8f,
                deployPointCount = 30,
            });
            anchors.Add(new MapAnchor
            {
                role = AnchorRole.PlayerSpawn,
                markerName = "SpawnBlue",
                position = new Vector3(gx, 0f, gz),
                radius = garrisonRadius * 0.8f,
                deployPointCount = 30,
            });
        }
    }
}
