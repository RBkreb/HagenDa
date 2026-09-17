using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>AI 大脑类型(ML 仅占位,当前构建器只装配 FSM)。</summary>
    public enum MatchBrain { Fsm = 0 }

    /// <summary>
    /// 真人分队策略:
    ///  - AllRed:所有真人进红队(历史行为);
    ///  - Balance:进人少的队,平局进红;
    ///  - FixedSlots:红队先填满 redHumanSlots,溢出进蓝队。
    /// </summary>
    public enum HumanTeamPolicy { AllRed = 0, Balance = 1, FixedSlots = 2 }

    /// <summary>指挥模式:无 / SquadCommander 规则调度 / PHASE10 LLM 指挥官。</summary>
    public enum CommanderMode { None = 0, RuleSquadCommander = 1, LlmCommander = 2 }

    /// <summary>
    /// 对局配置中间层(MATCH-LAYER):一份资产描述一场完整对局 ——
    /// 计分/小队结构、AI 数量与兵种、真人席位与分队策略、需要装配的系统
    /// (指挥官/相机/统计 HUD)。配合 <see cref="MapDefinition"/> 由
    /// MatchSceneBuilder 一键装配到任意场景(幂等,可重跑)。
    /// </summary>
    [CreateAssetMenu(menuName = "HagenDa/Match Config", fileName = "MatchConfig")]
    public class MatchConfig : ScriptableObject
    {
        [Header("Map")]
        public MapDefinition map;

        [Header("Score / Squad")]
        [Tooltip("先到该分获胜;999999 = 持续战(不终局)。")]
        public int winScore = 999999;
        public int squadsPerTeam = 6;
        public int squadSize = 5;

        [Tooltip("死亡后到可重新部署的秒数。")]
        public float redeployDelay = 10f;
        [Tooltip("部署菜单无操作自动回 GR 的秒数。")]
        public float autoDeployTimeout = 10f;

        [Header("AI")]
        public MatchBrain brain = MatchBrain.Fsm;

        [Tooltip("红队 FSM AI 数量(真人席位另计)。")]
        public int redAiCount = 29;
        [Tooltip("蓝队 FSM AI 数量。")]
        public int blueAiCount = 30;

        [Tooltip("按小队混编:2突击+2支援+1侦察;关闭则全员 aiClassOverride。")]
        public bool mixedClassComposition = true;
        public FsmClass aiClassOverride = FsmClass.Assault;

        [Header("Humans")]
        public HumanTeamPolicy humanTeamPolicy = HumanTeamPolicy.AllRed;

        [Tooltip("FixedSlots:红队真人席位数。")]
        public int redHumanSlots = 1;
        [Tooltip("FixedSlots:蓝队真人席位数。")]
        public int blueHumanSlots = 0;

        [Header("Systems")]
        public CommanderMode commander = CommanderMode.RuleSquadCommander;

        [Tooltip("FSMBattleSystem 旁的 tick 统计 HUD。")]
        public bool spawnFsmStatsHud = true;

        [Tooltip("自由观战相机(WASD/鼠标拖拽)。")]
        public bool spawnFreeCamera = true;

        [Tooltip("默认禁用的全景机位(需要时手动启用)。")]
        public bool spawnBattleCamera = false;
    }
}
