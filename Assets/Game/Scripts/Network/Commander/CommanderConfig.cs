using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE11 LLM 指挥官系统配置。红蓝两个指挥官实例共享同一份资产；
    /// 数值与 PHASE11.md 对齐（超时/间隔/记忆窗口/六边形编码/威胁度/武器）。
    /// </summary>
    [CreateAssetMenu(fileName = "CommanderConfig", menuName = "HagenDa/Commander Config")]
    public class CommanderConfig : ScriptableObject
    {
        [Header("LLM 端点（LM Studio，OpenAI 兼容）")]
        [Tooltip("推理服务基址。PHASE10 定案：仅 LM Studio 路径，LLM for Unity 不使用。")]
        public string baseUrl = "http://localhost:1234";

        [Tooltip("LM Studio 中的模型 id（/v1/models 列表中的名称）。PHASE11 起纯文本协议，无需多模态模型。")]
        public string model = "qwen3.8-4b";

        [Tooltip("可选 Bearer Token。LM Studio 默认留空。")]
        public string apiKey = "";

        [Tooltip("单次补全的最大 token（思考型模型 reasoning 消耗计入）。")]
        public int maxTokens = 8192;

        [Tooltip("采样温度（指挥决策偏低温度保稳定）。")]
        public float temperature = 0.02f;

        [Header("轮次时序（秒）")]
        [Tooltip("开局部署轮请求超时：含模型 JIT 加载。")]
        public float openingTimeout = 300f;

        [Tooltip("常规轮请求超时。")]
        public float roundTimeout = 120f;

        [Tooltip("空闲触发间隔：上一次输出后无其他触发的唤醒时间。")]
        public float idleInterval = 30f;

        [Tooltip("两次轮次之间的最小间隔（防抖；期间事件合并进缓冲）。")]
        public float minRoundGap = 5f;

        [Tooltip("休眠期探活周期（GET /v1/models）。")]
        public float probeInterval = 30f;

        [Header("协议参数")]
        [Tooltip("wait 工具允许的最小值（秒）。")]
        public float waitMin = 5f;

        [Tooltip("wait 工具允许的最大值（秒）。")]
        public float waitMax = 120f;

        [Tooltip("单轮工具循环上限（调用→结果 迭代次数）。")]
        public int maxToolIterations = 8;

        [Header("快速记忆")]
        [Tooltip("上下文保留的最近轮次数（滚动窗口）；被丢弃的轮次仍完整写入 JSONL。")]
        public int memoryRounds = 5;

        [Header("六边形空间编码")]
        [Tooltip("目标有效格数（格尺寸随地图 AABB 自动缩放逼近）。")]
        public int hexTargetCells = 64;

        [Header("侦测威胁度（按最近 POI 聚合的模糊档位）")]
        [Tooltip("大幅威胁：该据点附近被标记敌军 ≥ 此值。")]
        public int threatLargeEnemies = 20;

        [Tooltip("中度威胁：该据点附近被标记敌军 ≥ 此值。")]
        public int threatMediumEnemies = 10;

        [Tooltip("小幅威胁：该据点附近被标记敌军 ≥ 此值（低于则只报人数）。")]
        public int threatSmallEnemies = 5;

        [Tooltip("归入某据点\"附近\"的最大距离（米），超出归入游散。")]
        public float threatPoiRadius = 60f;

        [Header("指挥官武器 — 广域侦测")]
        public float radarRadius = 50f;
        public float radarDuration = 20f;
        public float radarScanEvery = 5f;
        public float radarMarkDuration = 1f;
        public float radarCooldown = 120f;

        [Header("指挥官武器 — 广域电磁干扰")]
        public float empRadius = 40f;
        public float empLifetime = 10f;
        public float empInterfereDuration = 10f;
        public float empCooldown = 180f;

        [Header("指挥官武器 — 炮击支援")]
        public float artilleryAreaRadius = 30f;
        public float artilleryDuration = 30f;
        public float artilleryEvery = 2f;
        public float artilleryYield = 150f;
        public float artilleryBlastRadius = 8f;
        public float artilleryCooldown = 300f;

        [Header("小队规模（工具校验用）")]
        public int squadsPerTeam = 6;

        /// <summary>加载项目固定路径的默认配置资产；缺失时创建内存实例。</summary>
        public static CommanderConfig LoadDefault()
        {
            var loaded = Resources.Load<CommanderConfig>("CommanderConfig");
            if (loaded != null) return loaded;
            return CreateInstance<CommanderConfig>();
        }
    }
}
