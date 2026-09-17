using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Mirror;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE11 指挥官结构化态势文本（纯静态构建器，无状态）。
    ///
    /// 系统提示词（静态，每请求重发）：角色 + 网格说明 + 完整可派驻格表 +
    /// 据点说明 + 争夺值符号约定 + 工具 + 三条战术示例 + 优先级 + 简练约束。
    /// 开局变体仅暴露 squad_order、只给部署任务。
    ///
    /// 每轮用户消息：触发原因 / 全局态势 / POI状态 / 小队状态 / 侦测敌情 /
    /// 上轮以来事件。排版为四轮访谈定案样例，字段语义见 PHASE11.md。
    ///
    /// 信息原则：只含 己方 + 己方标记敌 + 全局比分，无作弊信息
    /// （敌方存活人数不提供，敌方强度由比分与威胁度间接体现）。
    /// </summary>
    public static class CommanderSituationText
    {
        // ================================================================
        // 系统提示词
        // ================================================================

        public static string SystemPrompt(CommanderConfig cfg, int team,
                                          CommanderHexGrid hex, bool opening)
        {
            string side = team == (int)MatchTeam.Red ? "红方" : "蓝方";
            string foe = team == (int)MatchTeam.Red ? "蓝方" : "红方";

            var sb = new StringBuilder();
            sb.AppendLine($"你是战场对抗中的{side}指挥官。你通过结构化态势文本指挥己方 " +
                          $"{cfg.squadsPerTeam} 个小队争夺据点(HQ)并最终获胜，敌方为{foe}。");
            sb.AppendLine();
            sb.AppendLine("【地图网格】地图划分为六边形格，每格有唯一编号与整数中心坐标(X,Z)" +
                          "（米，原点=地图西南角，X向东为正，Z向北为正；格编号自西南角起按" +
                          "南→北、西→东递增——小编号=南，大编号=北）。全部可派驻格：");
            sb.AppendLine(hex != null && hex.CellCount > 0
                ? hex.BuildCellListText()
                : "（网格未就绪）");
            sb.AppendLine(hex != null && hex.CellCount > 0
                ? $"（共 {hex.CellCount} 格）小队与武器只能指向这些格或据点编号。"
                : "小队与武器只能指向据点编号。");
            sb.AppendLine();
            sb.AppendLine($"【据点】{HqTokenList()} 为争夺据点（可作为目标）；" +
                          "GR红/GR蓝 为双方安全区（仅供了解态势，不可作为目标）。");
            sb.AppendLine("【争夺值】−60..+60：负=红方推进，正=蓝方推进，±60=完全占领，过0=失去占领。");
            sb.AppendLine();

            if (opening)
            {
                sb.AppendLine("【当前阶段：开局部署】对局尚未开始。你唯一的任务是：" +
                              $"为全部 {cfg.squadsPerTeam} 个小队各下达一条 squad_order " +
                              "（squads=[小队编号]，target=格编号或据点编号），" +
                              "结合 HQ 分布与小队出生位置给出合理布防；" +
                              "每个小队恰好一条，不要重复同一小队。");
                sb.AppendLine();
                sb.AppendLine("【可用工具】 squad_order(squads, target)。");
                sb.AppendLine();
                sb.AppendLine("【输出要求】先用一两句话给出 analysis（简述你的布防思路），" +
                              $"随后通过工具调用逐队下达指令，直到 {cfg.squadsPerTeam} 个小队全部覆盖。");
                return sb.ToString().TrimEnd();
            }

            sb.AppendLine("【每轮输入】触发原因、全局态势、POI状态、小队状态、侦测敌情、" +
                          "以及自上轮以来的事件流。");
            sb.AppendLine();
            sb.AppendLine("【工具】");
            sb.AppendLine("- squad_order(squads, target)：把 1-" + cfg.squadsPerTeam +
                          " 支小队派往同一目标（格编号如 #31，或据点编号如 HQ-A）。" +
                          "全灭小队会返回错误；同一回合内请勿对同一小队重复下令。");
            sb.AppendLine($"- commander_weapon(weaponNumber, target)：1=广域侦测(半径50m,持续20s,冷却120s)；" +
                          $"2=广域电磁干扰(半径40m,存活10s,冷却180s)；" +
                          $"3=炮击支援(区域30m,持续30s,单发中心150伤害,冷却300s)。" +
                          "炮击不会伤害己方。冷却中下达会排队并于就绪后自动投放在最后给定的坐标；" +
                          "重复下达同编号即更新坐标。");
            sb.AppendLine("- weapon_status()：查询三件武器的当前状态。");
            sb.AppendLine("- wait(seconds)：5-120 秒，设定本轮结束后多少秒主动唤醒自己" +
                          "（代替默认 30 秒空闲节奏）。");
            sb.AppendLine();
            sb.AppendLine("【战术示例】");
            sb.AppendLine("- 敌方进攻我方据点 → 抽 1-2 支最近小队回防+广域电磁干扰。");
            sb.AppendLine("- 意图进攻某据点 → 集中 2-3 支小队+广域侦测+炮击支援。");
            sb.AppendLine("- 意图偷袭某据点 → 抽1-2小队前往据点侧翼/后方+广域侦测");
            sb.AppendLine("- 无紧迫威胁 → 优先派小队占领更多据点，侦测更多敌军。");
            sb.AppendLine("【懂得战术变通，灵活运用squad_order和commander_weapon】");
            sb.AppendLine("- 优先级：占领点数最大化 > 回防 > 恋战。");
            sb.AppendLine();
            sb.AppendLine("【输出要求】先用一两句 分析 概述判断（注明你读到的小队/据点状态），" +
                          "随后必须通过工具调用执行行动——仅输出文字而不调用工具等于放弃本回合指挥。" +
                          "思考简练，不要顾此失彼。没有要做的就什么都不调用。");

            return sb.ToString().TrimEnd();
        }

        private static string HqTokenList()
        {
            var mm = NetworkMatchManager.Instance;
            if (mm == null || mm.capturePoints.Count == 0) return "HQ-A/HQ-B/HQ-C";
            var letters = mm.capturePoints
                .Where(cp => cp != null)
                .Select(cp => $"HQ-{cp.letter}");
            return string.Join("/", letters);
        }

        // ================================================================
        // 每轮用户消息
        // ================================================================

        /// <param name="lastOrderTime">小队索引(0基) → 最近一次被下令的 Time.time；缺失=从未。</param>
        /// <param name="events">上轮以来事件流（已带时间戳前缀）。</param>
        public static string RoundInput(int team, string reason, CommanderHexGrid hex,
                                        CommanderConfig cfg,
                                        IReadOnlyDictionary<int, float> lastOrderTime,
                                        List<string> events, bool opening)
        {
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);

            var sb = new StringBuilder();
            sb.AppendLine($"【触发】{reason}");
            sb.AppendLine(GlobalSituation(team, cfg, buf));
            sb.AppendLine("【POI状态】");
            sb.Append(PoiStatus(team, hex, buf));
            sb.AppendLine("【小队状态】");
            sb.Append(SquadStatus(team, hex, cfg, lastOrderTime, buf));
            sb.AppendLine("【侦测敌情】");
            sb.Append(ThreatReport(team, hex, cfg, buf));
            sb.AppendLine(events != null && events.Count > 0
                ? "【上轮以来事件】\n" + string.Join("\n", events)
                : "【上轮以来事件】（无）");

            if (opening)
                sb.AppendLine("【要求】请在本次回复中一次性给出全部小队的部署指令，只调用工具、无需确认。");

            return sb.ToString().TrimEnd();
        }

        // ---- 全局态势 ---------------------------------------------------

        private static string GlobalSituation(int team, CommanderConfig cfg,
                                              List<NetworkCombatant> buf)
        {
            var mm = NetworkMatchManager.Instance;
            int own = team == (int)MatchTeam.Red ? mm?.redScore ?? 0 : mm?.blueScore ?? 0;
            int foe = team == (int)MatchTeam.Red ? mm?.blueScore ?? 0 : mm?.redScore ?? 0;
            int lead = own - foe;

            string state;
            if (mm == null || (!mm.matchOver && mm.winner < 0)) state = "进行中";
            else state = mm.winner == (int)MatchTeam.Red ? "红方胜利" : "蓝方胜利";

            double dur = NetworkCommanderState.Instance != null &&
                         NetworkCommanderState.Instance.matchStarted
                ? NetworkTime.time - NetworkCommanderState.MatchStartedAt
                : 0.0;

            int aliveSquads = 0, aliveMembers = 0;
            var seen = new HashSet<int>();
            foreach (var c in buf)
            {
                if (c == null || c.teamId != team || c.IsDead || c.squadId < 0) continue;
                aliveMembers++;
                if (seen.Add(c.squadId)) aliveSquads++;
            }

            string side = team == (int)MatchTeam.Red ? "红" : "蓝";
            string other = team == (int)MatchTeam.Red ? "蓝" : "红";
            return $"【全局态势】比分 {side}{own}:{other}{foe}（领先{lead:+0;-0;0}）｜" +
                   $"{state}｜时长{FmtDur(dur)}｜" +
                   $"己方存活 {aliveSquads}/{cfg.squadsPerTeam}队 {aliveMembers}人";
        }

        // ---- POI 状态 ---------------------------------------------------

        private static string PoiStatus(int team, CommanderHexGrid hex,
                                        List<NetworkCombatant> buf)
        {
            var rows = new List<(int sortKey, string line)>();
            var mm = NetworkMatchManager.Instance;

            if (mm != null)
            {
                foreach (var cp in mm.capturePoints)
                {
                    if (cp == null) continue;
                    cp.GetTeamCounts(out int red, out int blue);
                    bool contested = red > 0 && blue > 0;
                    string status = contested
                        ? "争夺中"
                        : cp.ownerTeam == (int)MatchTeam.Red ? "红方占领"
                        : cp.ownerTeam == (int)MatchTeam.Blue ? "蓝方占领"
                        : "中立";
                    int sortKey = contested ? 0
                        : cp.ownerTeam < 0 ? 3
                        : cp.ownerTeam == team ? 2 : 1;

                    rows.Add((sortKey,
                        $"HQ-{cp.letter}｜{status}｜争夺值{cp.contention:+0;-0;0}｜" +
                        $"红{red}蓝{blue}｜{CellAt(hex, cp.transform.position)}"));
                }
            }

            // 安全区（固定归属，仅展示；不可作为目标）。
            if (mm != null)
            {
                foreach (var g in mm.garrisons)
                {
                    if (g == null) continue;
                    GrPresence(g, buf, out int red, out int blue);
                    string role = g.teamId == team ? "己方安全区" : "敌方安全区";
                    rows.Add((4, $"GR{(g.teamId == (int)MatchTeam.Red ? "红" : "蓝")}｜{role}｜" +
                                 $"红{red}蓝{blue}｜{CellAt(hex, g.transform.position)}"));
                }
            }

            var sb = new StringBuilder();
            foreach (var row in rows.OrderBy(r => r.sortKey))
                sb.AppendLine(row.line);
            return sb.ToString();
        }

        /// <summary>安全区在场人数（XZ 包围盒/圆判定，与 GarrisonZone 判定同构）。</summary>
        private static void GrPresence(GarrisonZone g, List<NetworkCombatant> buf,
                                       out int red, out int blue)
        {
            red = 0;
            blue = 0;
            Vector3 p = g.transform.position;
            foreach (var c in buf)
            {
                if (c == null || c.IsDead || c.teamId < 0) continue;
                Vector3 d = c.transform.position - p;
                d.y = 0f;
                bool inside = g.IsRect
                    ? Mathf.Abs(d.x) <= g.extent.x && Mathf.Abs(d.z) <= g.extent.y
                    : d.sqrMagnitude <= g.radius * g.radius;
                if (!inside) continue;
                if (c.teamId == (int)MatchTeam.Red) red++;
                else if (c.teamId == (int)MatchTeam.Blue) blue++;
            }
        }

        // ---- 小队状态 ---------------------------------------------------

        private static string SquadStatus(int team, CommanderHexGrid hex, CommanderConfig cfg,
                                          IReadOnlyDictionary<int, float> lastOrderTime,
                                          List<NetworkCombatant> buf)
        {
            var sb = new StringBuilder();
            // 小队编制人数：权威在 NetworkMatchManager（指挥官 Config 无此字段）。
            int squadSize = NetworkMatchManager.Instance != null
                ? Mathf.Max(1, NetworkMatchManager.Instance.squadSize)
                : 5;
            for (int s = 0; s < cfg.squadsPerTeam; s++)
            {
                var members = new List<NetworkCombatant>();
                foreach (var c in buf)
                {
                    if (c == null || c.teamId != team || c.squadId != s || c.IsDead) continue;
                    members.Add(c);
                }

                if (members.Count == 0)
                {
                    sb.AppendLine($"小队{s + 1}｜0/{squadSize}｜全灭(重部署中)｜—｜—｜—");
                    continue;
                }

                Vector3 centroid = Vector3.zero;
                bool busy = false;
                foreach (var m in members)
                {
                    centroid += m.transform.position;
                    var fsm = m.GetComponent<FSMAIController>();
                    if (fsm != null && fsm.State != FsmState.Normal) busy = true;
                }
                centroid /= members.Count;

                // 距最近争夺点（仅 HQ 据点）。
                string distTxt = "—";
                var mm = NetworkMatchManager.Instance;
                if (mm != null)
                {
                    CapturePoint nearest = null;
                    float bestD = float.MaxValue;
                    foreach (var cp in mm.capturePoints)
                    {
                        if (cp == null) continue;
                        float dx = cp.transform.position.x - centroid.x;
                        float dz = cp.transform.position.z - centroid.z;
                        float d = dx * dx + dz * dz;
                        if (d < bestD) { bestD = d; nearest = cp; }
                    }
                    if (nearest != null)
                        distTxt = $"距HQ-{nearest.letter} {Mathf.Sqrt(bestD):F0}米";
                }

                string lastOrder = lastOrderTime != null &&
                                   lastOrderTime.TryGetValue(s, out float t)
                    ? $"上次指令{(int)Mathf.Max(0f, Time.time - t)}秒前"
                    : "从未";

                sb.AppendLine($"小队{s + 1}｜{members.Count}/{squadSize}｜" +
                              $"{CellAt(hex, centroid)}｜{distTxt}｜{lastOrder}｜" +
                              $"{(busy ? "繁忙(交战中)" : "空闲")}");
            }
            return sb.ToString();
        }

        // ---- 侦测敌情（被己方标记敌军按最近 POI 聚合） -------------------

        private static string ThreatReport(int team, CommanderHexGrid hex,
                                           CommanderConfig cfg, List<NetworkCombatant> buf)
        {
            var mm = NetworkMatchManager.Instance;
            var byPoi = new Dictionary<CapturePoint, int>();
            var stray = new List<NetworkCombatant>();

            foreach (var c in buf)
            {
                if (c == null || c.IsDead || c.teamId < 0) continue;
                if (c.teamId == team) continue;                       // 只统计敌方
                if (!c.IsMarked || c.markedByTeam != team) continue;  // 只统计被己方标记的

                CapturePoint nearest = null;
                float bestD = float.MaxValue;
                if (mm != null)
                {
                    foreach (var cp in mm.capturePoints)
                    {
                        if (cp == null) continue;
                        float dx = cp.transform.position.x - c.transform.position.x;
                        float dz = cp.transform.position.z - c.transform.position.z;
                        float d = dx * dx + dz * dz;
                        if (d < bestD) { bestD = d; nearest = cp; }
                    }
                }

                if (nearest != null &&
                    Mathf.Sqrt(bestD) <= cfg.threatPoiRadius)
                {
                    byPoi.TryGetValue(nearest, out int n);
                    byPoi[nearest] = n + 1;
                }
                else
                {
                    stray.Add(c);
                }
            }

            var sb = new StringBuilder();
            foreach (var kv in byPoi.OrderByDescending(kv => kv.Value))
            {
                string level = ThreatLevel(kv.Value, cfg);
                sb.AppendLine($"HQ-{kv.Key.letter} 附近 ~{kv.Value}名" +
                              $"{(string.IsNullOrEmpty(level) ? "" : $"(威胁:{level})")}");
            }
            if (stray.Count > 0)
            {
                var cellTxts = new List<string>();
                foreach (var c in stray)
                {
                    int id = hex != null ? hex.NearestCellId(c.transform.position) : 0;
                    if (id <= 0 || cellTxts.Contains($"#{id}")) continue;
                    cellTxts.Add($"#{id}");
                    if (cellTxts.Count >= 3) break;
                }
                sb.AppendLine($"游散 ~{stray.Count}名" +
                              (cellTxts.Count > 0 ? $"({string.Join(",", cellTxts)})" : ""));
            }
            if (byPoi.Count == 0 && stray.Count == 0)
                sb.AppendLine("（无标记敌情）");
            return sb.ToString();
        }

        /// <summary>模糊威胁档位：大/中/小；低于小幅阈值返回空（仅报人数）。</summary>
        private static string ThreatLevel(int count, CommanderConfig cfg)
        {
            if (count >= cfg.threatLargeEnemies) return "大";
            if (count >= cfg.threatMediumEnemies) return "中";
            if (count >= cfg.threatSmallEnemies) return "小";
            return "";
        }

        // ---- 共用 -------------------------------------------------------

        private static string CellAt(CommanderHexGrid hex, Vector3 world)
        {
            int id = hex != null ? hex.NearestCellId(world) : 0;
            return id > 0 ? hex.CellDisplay(id) : $"({world.x:F0},{world.z:F0})";
        }

        private static string FmtDur(double sec)
        {
            int total = (int)Mathf.Max(0f, (float)sec);
            return $"{total / 60:D2}:{total % 60:D2}";
        }
    }
}
