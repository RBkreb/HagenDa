using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE10 多模态 LLM 指挥官系统配置。红蓝两个指挥官实例共享同一份资产；
    /// 数值与 PHASE10.md 对齐（超时/间隔/记忆窗口/快照/武器）。
    /// </summary>
    [CreateAssetMenu(fileName = "CommanderConfig", menuName = "HagenDa/Commander Config")]
    public class CommanderConfig : ScriptableObject
    {
        [Header("LLM 端点（LM Studio，OpenAI 兼容）")]
        [Tooltip("推理服务基址。PHASE10 定案：仅 LM Studio 路径，LLM for Unity 不使用。")]
        public string baseUrl = "http://localhost:1234";

        [Tooltip("LM Studio 中的模型 id（/v1/models 列表中的名称）。")]
        public string model = "minicpm-v-4_6";

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
        public int memoryRounds = 10;

        [Header("快照")]
        [Tooltip("快照长边像素（短边按地图比例自适应）。")]
        public int snapshotLongSide = 2048;

        [Tooltip("网格间距（米），也是 LLM 坐标系的标尺步长。")]
        public float gridSizeMeters = 20f;

        [Tooltip("调试：快照 PNG 落盘到 Logs/Commander/snapshots/。")]
        public bool saveSnapshotPng = true;

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
