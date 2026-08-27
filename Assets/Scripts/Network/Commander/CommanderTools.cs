using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE10 指挥官五个工具（LLM 口径：小队编号 1..6、武器编号 1..3、网格坐标）。
    ///
    /// get_snapshot 由 Orchestrator 特殊处理（需回图），此处不实现；
    /// 其余工具统一走 <see cref="Execute"/>，任何校验失败/异常都以
    /// {"error": "..."} 文本回传——反馈式纠错（Q22/Q25 定案），不丢轮次。
    /// </summary>
    public sealed class CommanderToolContext
    {
        private readonly CommanderOrchestrator orch;

        public CommanderToolContext(CommanderOrchestrator orchestrator)
        {
            orch = orchestrator;
        }

        /// <summary>工具 JSON Schema（原生 tools 参数）。</summary>
        /// <summary>工具 JSON Schema（原生 tools 参数）。opening=true 仅供门控阶段。</summary>
        public static List<JObject> BuildSchemas(bool openingPhase = false)
        {
            var squadOrder = new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = "squad_order",
                    ["description"] = "将小队派往目标网格（引用快照上印刷的网格代号，如 C4）。全灭小队无法执行。",
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["squadNumber"] = new JObject { ["type"] = "integer", ["description"] = "小队编号 1-6" },
                            ["cell"] = new JObject { ["type"] = "string", ["description"] = "目标网格代号，如 C4（列字母 A.. + 行数字）" },
                            ["x"] = new JObject { ["type"] = "number", ["description"] = "(备用)东向 X 米" },
                            ["z"] = new JObject { ["type"] = "number", ["description"] = "(备用)北向 Z 米" },
                        },
                        ["required"] = new JArray("squadNumber", "cell"),
                    }
                }
            };

            if (openingPhase)
            {
                // 门控阶段唯一可用工具（用户定案：仅小队部署，杜绝误用武器）。
                return new List<JObject> { squadOrder };
            }

            return new List<JObject>
            {
                squadOrder,
                new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = "commander_weapon",
                        ["description"] = "使用指挥官武器投放至指定网格坐标。冷却中会排队并于冷却结束自动投放；再次下达同编号即更新坐标。",
                        ["parameters"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["weaponNumber"] = new JObject { ["type"] = "integer", ["description"] = "1=广域侦测 2=广域电磁干扰 3=炮击支援" },
                                ["x"] = new JObject { ["type"] = "number" },
                                ["z"] = new JObject { ["type"] = "number" },
                            },
                            ["required"] = new JArray("weaponNumber", "x", "z"),
                        }
                    }
                },
                new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = "weapon_status",
                        ["description"] = "查询全部指挥官武器状态（可用 / 冷却剩余秒数 / 排队情况）。",
                        ["parameters"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject(),
                            ["required"] = new JArray()
                        }
                    }
                },
                new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = "wait",
                        ["description"] = "设定本回合结束后 N 秒唤醒自己（5-120 秒，超出截断）。用于有计划地推迟下一次观察。",
                        ["parameters"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["seconds"] = new JObject { ["type"] = "number" },
                            },
                            ["required"] = new JArray("seconds"),
                        }
                    }
                },
                new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = "get_snapshot",
                        ["description"] = "主动获取一张最新全局地图快照（作为图片追加进对话）。",
                        ["parameters"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject(),
                            ["required"] = new JArray()
                        }
                    }
                },
            };
        }

        // ---------------------------------------------------------------
        // 执行器
        // ---------------------------------------------------------------

        public string Execute(string name, JToken args)
        {
            try
            {
                switch (name)
                {
                    case "squad_order": return SquadOrder(args);
                    case "commander_weapon": return CommanderWeapon(args);
                    case "weapon_status":
                        return JToken.FromObject(new { result = orch.Weapons.DescribeStatus() })
                                     .ToString(Newtonsoft.Json.Formatting.None);
                    case "wait": return Wait(args);
                    default:
                        return Err($"未知工具 '{name}'");
                }
            }
            catch (Exception e)
            {
                return Err(e.Message);
            }
        }

        private string SquadOrder(JToken args)
        {
            int num;
            float gx, gz;
            string cellTxt = args?["cell"]?.ToString();

            if (!string.IsNullOrEmpty(cellTxt))
            {
                // PHASE10 v3：优先网格代号（LLM 只需"识字"不需测量）。
                gx = gz = 0f;
                if (cellTxt.Length < 2)
                    return Err($"非法网格代号 '{cellTxt}'");
                var sArg = args?["squadNumber"];
                num = sArg != null && int.TryParse(sArg.ToString(), out int sn) ? sn : -1;
                if (!orch.Overlay.TryCellToGrid(cellTxt, out gx, out gz))
                    return Err($"非法网格代号 '{cellTxt}'（列 A-{(char)('A' + orch.Overlay.Cols - 1)}，行 1-{orch.Overlay.Rows}）");
            }
            else
            {
                var a = ResolveArgs(args, "squadNumber", "x", "z");
                num = (int)a[0];
                gx = a[1]; gz = a[2];
            }

            if (num < 1 || num > orch.Config.squadsPerTeam)
                return Err($"小队编号必须在 1-{orch.Config.squadsPerTeam}");
            int squadId = num - 1;

            gx = Mathf.Clamp(gx, 0f, orch.Overlay.MapWidth);
            gz = Mathf.Clamp(gz, 0f, orch.Overlay.MapLength);

            // 同回合同小队重复下令 → 打断打转（PHASE10 v3）。
            if (orch.TryMarkSquadOrderedThisRound(squadId))
                return Err($"本回合已为小队{num}下达过指令，请勿重复；继续下一未部署的小队");

            // 找到该小队存活成员逐个下达目标。
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);

            int ordered = 0;
            Vector3 world = Vector3.zero;
            foreach (var c in buf)
            {
                if (c == null || c.teamId != orch.Team || c.squadId != squadId) continue;
                if (c.IsDead) continue;

                var fsm = c.GetComponent<FSMAIController>();
                if (fsm == null) continue;

                // 单源换算（西南原点笛卡尔式，与快照标尺/HUD 一致）。
                world = orch.Overlay.GridToWorld(gx, gz);
                world.y = c.transform.position.y;
                fsm.AssignObjective(world);
                ordered++;
            }

            if (ordered == 0)
                return Err($"小队{num}无存活可指挥成员（可能已被全歼）");

            // 同步客户端 HUD / 地图标记 + 服务器再主张缓存。
            orch.State?.SetSquadObjective(orch.Team, squadId, gx, gz);
            orch.StoreLastOrderPublic(num, gx, gz);
            return new JObject { ["result"] = $"完成:小队{num}→{cellTxt ?? $"({gx:F0},{gz:F0})"}" }
                .ToString(Newtonsoft.Json.Formatting.None);
        }

        private string CommanderWeapon(JToken args)
        {
            var a = ResolveArgs(args, "weaponNumber", "x", "z");
            int num = (int)a[0];
            float gx = a[1], gz = a[2];

            if (NetworkCommanderState.GateActive)
                return Err("对局尚未开始，请先完成所有小队的部署指令");

            string text = orch.Weapons.TryOrder(num, gx, gz);
            return new JObject { ["result"] = text }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private string Wait(JToken args)
        {
            float seconds = ResolveArgs(args, "seconds")[0];
            seconds = Mathf.Clamp(seconds, orch.Config.waitMin, orch.Config.waitMax);
            orch.RequestWakeAfter(seconds);
            return new JObject
            {
                ["result"] = $"已设置 {seconds:F0} 秒后唤醒（本轮结束时生效）"
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        // ---------------------------------------------------------------
        // 参数解析
        // ---------------------------------------------------------------

        // ---------------------------------------------------------------
        // 参数解析（命名优先；MiniCPM 实测会出现位置参数 "(3,20,60)" → 回退兼容）
        // ---------------------------------------------------------------

        internal static float[] ResolveArgs(JToken args, params string[] names)
        {
            var result = new float[names.Length];
            if (args is JArray arr)
            {
                for (int i = 0; i < names.Length; i++)
                    if (i < arr.Count) result[i] = ToFloat(arr[i]);
                return result;
            }
            if (args is JObject obj)
            {
                bool allNamed = true;
                foreach (var n in names)
                    if (obj.Property(n) == null) { allNamed = false; break; }

                if (allNamed)
                {
                    for (int i = 0; i < names.Length; i++)
                        result[i] = ToFloat(obj[names[i]]);
                    return result;
                }

                // 位置回退：按对象属性插入顺序取前 N 个数值。
                int idx = 0;
                foreach (var p in obj.Properties())
                {
                    if (idx >= names.Length) break;
                    if (p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Float)
                        result[idx++] = ToFloat(p.Value);
                }
                if (idx == names.Length) return result;
            }
            throw new ArgumentException($"参数缺失：需要 ({string.Join(", ", names)})");
        }

        private static float ToFloat(JToken v)
        {
            if (!float.TryParse(v.ToString(), out float f))
                throw new ArgumentException($"无法解析数值: {v}");
            return f;
        }

        private static string Err(string msg) =>
            new JObject { ["error"] = msg }.ToString(Newtonsoft.Json.Formatting.None);
    }
}
