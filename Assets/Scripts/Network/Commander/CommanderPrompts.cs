using System.Text;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE10 静态提示词（中文，Q8）。系统提示词在每轮请求中重发一次（LM Studio
    /// 无状态）；地图图例与坐标约定必须与 CommanderMapOverlay 的渲染严格一致。
    /// </summary>
    public static class CommanderPrompts
    {
        /// <summary>
        /// 系统提示词。openingPhase=true 时输出"部署专用版"：
        /// 工具仅 squad_order、无武器/等待说明，并明确本阶段唯一任务（用户定案：
        /// 两套提示词 + 两套工具暴露，杜绝门控阶段误用武器导致的重复下单）。
        /// </summary>
        public static string SystemPrompt(CommanderConfig cfg, int team, bool openingPhase = false)
        {
            string side = team == (int)MatchTeam.Red ? "红方" : "蓝方";
            string foe = team == (int)MatchTeam.Red ? "蓝方" : "红方";

            var sb = new StringBuilder();
            sb.AppendLine($"你是《HagenDa》战场对抗中的{side}指挥官。你通过俯视全局地图快照" +
                          $"指挥己方 {cfg.squadsPerTeam} 个小队争夺据点(HQ)并最终获胜。敌方为{foe}。");
            sb.AppendLine();
            sb.AppendLine("【快照图例】");
            sb.AppendLine("- 顶视为全局地图，图像上方=北、右方=东。背景含网格线与贴边标尺数字（单位：米）。");
            sb.AppendLine("- 坐标系：标准笛卡尔式。原点(0,0)在地图西南角（画面左下角有白色 (0,0) 角标），" +
                          "X 向东为正，Z 向北为正。快照上的网格中央印有【网格代号】（列字母 A-F + 行数字 1-N，如 C4）。" +
                          "你的指令一律引用【网格代号】——不需要估算精确米数。");
            sb.AppendLine("- 己方小队成员 = 己方颜色小点；小队中心 = 同色圆盘 + 黑色编号数字（编号即小队号）。阵亡/倒地成员显示为暗灰色点。");
            sb.AppendLine("- 红色圆点 = 被你方标记的敌军位置。");
            sb.AppendLine("- HQ = 大号彩色字母盘 A/B/C…（颜色代表归属：红/蓝/中立黄）。GR = 字母 G 盘（双方安全区，不可占领）。");

            if (openingPhase)
            {
                sb.AppendLine();
                sb.AppendLine("【当前阶段：开局部署】对局尚未开始。你唯一的任务是：" +
                              $"为全部 {cfg.squadsPerTeam} 个小队各下达一条 squad_order 指令（cell=快照上印刷的网格代号），" +
                              "结合 HQ 分布与小队出生位置给出合理布防；每个小队恰好一条，不要重复同一小队。");
                sb.AppendLine();
                sb.AppendLine("【可用工具】仅有 squad_order(squadNumber,cell)。");
                sb.AppendLine("不存在其他工具；任何武器/等待请求在本阶段都不可用。");
                sb.AppendLine();
                sb.AppendLine("【输出要求】先用一两句话给出 analysis（简述你的布防思路），" +
                              $"随后通过工具调用逐队下达指令，直到 {cfg.squadsPerTeam} 个小队全部覆盖。");
                return sb.ToString().TrimEnd();
            }

            sb.AppendLine();
            sb.AppendLine("【每轮输入】触发原因、对局时长、己方各小队存活人数、各 HQ 归属与争夺值、" +
                          "以及自上轮以来的事件流。空间布局只体现在快照图中——请先读图再决策。");
            sb.AppendLine();
            sb.AppendLine("【工具】");
            sb.AppendLine("- squad_order(squadNumber,cell)：把某小队派往该网格代号所对应格子作为抽象争夺目标。" +
                          "全灭的小队会返回错误；同一回合内请勿对同一小队重复下令。");
            sb.AppendLine($"- commander_weapon(weaponNumber,x,z)：1=广域侦测(半径50m,持续20s,每5s标记1s,冷却120s)；" +
                          $"2=广域电磁干扰(半径40m,存活10s,干扰10s,冷却180s)；" +
                          $"3=炮击支援(区域30m,持续30s,每2s一发,单发中心150伤害/爆炸半径8m,冷却300s)。炮击不会伤害己方。" +
                          "冷却中下达会排队并在就绪后自动投放在最后给定的坐标；重复下达同编号即更新坐标。");
            sb.AppendLine("- weapon_status()：查询三件武器的当前状态。");
            sb.AppendLine("- get_snapshot()：立即追加一张最新快照图片。");
            sb.AppendLine("- wait(seconds)：5-120 秒，设定本轮结束后多少秒主动唤醒自己（代替默认 30 秒空闲节奏）。");
            sb.AppendLine();
            sb.AppendLine("【输出要求】先用一两句话给出 analysis（你对态势的判断，注明你读到的小队分布），" +
                          "随后必须通过工具调用执行行动——仅输出文字而不调用工具等于放弃本回合指挥。" +
                          "没有要做的就什么都不调用。");
            sb.AppendLine();
            sb.AppendLine("【计分规则】击杀+1 分；完全占领 HQ +10 分；持有 HQ 每 10 秒 +3 分；先到目标分数获胜。");

            return sb.ToString().TrimEnd();
        }
    }
}
