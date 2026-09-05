using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking
{
    public enum MapKind { Procedural = 0, SceneReference = 1 }

    /// <summary>地图锚点角色:GR ×2 / 据点 ×N / 真人出生点。</summary>
    public enum AnchorRole { GarrisonRed = 0, GarrisonBlue = 1, CapturePoint = 2, PlayerSpawn = 3 }

    /// <summary>锚点解析方式:按场景标记物名,或直接用资产内世界坐标。</summary>
    public enum AnchorResolve { BySceneMarker = 0, ByCoordinates = 1 }

    /// <summary>
    /// 规范化地图锚点。矩形判定时 extent.x/y &gt; 0 生效(半宽/半深),
    /// 否则用圆形 radius。FBX 地图可手工摆空物体(命名 markerName)做锚点,
    /// 程序生成地图直接填坐标。
    /// </summary>
    [System.Serializable]
    public class MapAnchor
    {
        public AnchorRole role;

        [Tooltip("CapturePoint 字母(A/B/C)。")]
        public string letter = "A";

        [Tooltip("BySceneMarker:场景对象名(含未激活);留空则回退 position。")]
        public string markerName;

        [Tooltip("ByCoordinates:世界坐标(标记物缺失时的回退)。")]
        public Vector3 position;

        public float radius = 12f;

        [Tooltip("矩形半宽/半深 (XZ)。>0 时为矩形判定,忽略 radius。")]
        public Vector2 extent;

        [Tooltip("DeployPointSet 生成点位数。")]
        public int deployPointCount = 4;

        [Header("Movement (可选)")]
        [Tooltip("区域沿航点移动(MovingZone)。")]
        public bool moving;

        [Tooltip("航点(世界坐标)。留空且 BySceneMarker 时,自动收集标记物下 WP* 子物体。")]
        public Vector3[] waypoints = new Vector3[0];

        public float moveSpeed = 3f;
        public float dwellSeconds = 0f;

        [Tooltip("true = 折返;false = 循环。")]
        public bool pingPong = true;

        [Tooltip("开局等待秒数(部署阶段不动)。")]
        public float startDelay = 5f;
    }

    /// <summary>
    /// 规范化地图存储(MATCH-LAYER)。一份资产描述一张地图,兼容两类来源:
    ///
    ///  - <see cref="MapKind.Procedural"/>:尺寸/墙高/种子化掩体参数化,
    ///    几何由 MatchSceneBuilder 在编辑期生成并存入场景(无需运行时重建);
    ///  - <see cref="MapKind.SceneReference"/>:引用 FBX 导入的地图根
    ///    (如 HGTR_map / Map_v1),按 ceilingKeyword 归层,锚点按
    ///    标记物名或坐标解析。
    /// </summary>
    [CreateAssetMenu(menuName = "HagenDa/Map Definition", fileName = "MapDefinition")]
    public class MapDefinition : ScriptableObject
    {
        public MapKind kind = MapKind.Procedural;

        [Header("Procedural")]
        [Tooltip("X 方向宽度 (m)。")]
        public float sizeX = 120f;
        [Tooltip("Z 方向深度 (m)。")]
        public float sizeZ = 220f;
        public float wallHeight = 6f;

        [Tooltip("静态掩体数量(种子化,GR/据点周围自动净空)。")]
        public int coverCount = 64;
        public int coverSeed = 20260825;
        [Tooltip("掩体最小间距 (m)。")]
        public float coverMinGap = 5f;

        [Header("SceneReference (FBX)")]
        [Tooltip("地图根对象名列表(如 {\"HGTR_map\"} 或 {\"Map_v1\",\"Map_v2\",\"GroundFloor_Grid\"})。")]
        public string[] mapRootNames = new string[0];

        [Tooltip("名字含该关键字的子物体归入 ceiling 层,其余归 Ground。")]
        public string ceilingKeyword = "ceiling";

        [Tooltip("启用地图材质双面渲染(HGTR 单面墙需要)。")]
        public bool doubleSidedMaterials = true;

        [Header("Anchors")]
        public AnchorResolve anchorResolve = AnchorResolve.ByCoordinates;

        public List<MapAnchor> anchors = new List<MapAnchor>();
    }
}
