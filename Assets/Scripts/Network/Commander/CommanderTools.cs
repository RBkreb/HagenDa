using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE11 指挥官三个工具（LLM 口径：小队编号 1..6、目标=格编号 #N 或据点编号）。
    ///
    /// 命令词汇表 = {格#N} ∪ {据点编号 HQ-A/B/C…}；安全区（GR）不可作为目标
    /// （双方 GR 均拒）。任何校验失败/异常都以 {"error": "..."} 文本回传——
    /// 反馈式纠错（Q22/Q25 定案），不丢轮次。
    ///
    /// squad_order 支持多小队同目标（squads 数组）；成功结果附带 squads/cell/wx/wz
    /// 结构化字段，供 Orchestrator 做再主张缓存与状态同步（LLM 亦能确认解析结果）。
    /// </summary>
    public sealed class CommanderToolContext
    {
        private readonly CommanderOrchestrator orch;

        public CommanderToolContext(CommanderOrchestrator orchestrator)
        {
            orch = orchestrator;
        }

        /// <summary>工具 JSON Schema（原生 tools 参数）。opening=true 仅供门控阶段。</summary>
        public static List<JObject> BuildSchemas(bool openingPhase = false)
        {
            var squadOrder = new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = "squad_order",
                    ["description"] = "把 1-6 支小队派往同一目标（格编号或据点编号）。全灭小队无法执行。",
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["squads"] = new JObject
                            {
                                ["type"] = "array",
                                ["items"] = new JObject { ["type"] = "integer" },
                                ["description"] = "小队编号列表 1-6，如 [1,2,3]",
                            },
                            ["target"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "目标：格编号（如 #31）或据点编号（如 HQ-A）",
                            },
                        },
                        ["required"] = new JArray("squads", "target"),
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
                        ["description"] = "使用指挥官武器投放至目标（格编号或据点编号）。冷却中会排队并于冷却结束自动投放；再次下达同编号即更新坐标。",
                        ["parameters"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject
                            {
                                ["weaponNumber"] = new JObject { ["type"] = "integer", ["description"] = "1=广域侦测 2=广域电磁干扰 3=炮击支援" },
                                ["target"] = new JObject { ["type"] = "string", ["description"] = "目标：格编号（如 #31）或据点编号（如 HQ-A）" },
                            },
                            ["required"] = new JArray("weaponNumber", "target"),
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
                                     .ToString(Formatting.None);
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

        // ---------------------------------------------------------------
        // squad_order：多小队同目标
        // ---------------------------------------------------------------

        private string SquadOrder(JToken args)
        {
            List<int> squads = ParseSquads(args);

            string target = args?["target"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(target))
                return Err("缺少 target（格编号 #N 或据点编号 HQ-A）");

            if (!TryResolveTarget(target, out Vector3 world, out int cellId,
                                  out string display, out string resolveError))
                return Err(resolveError);

            // 前置校验整组（防部分生效）：先逐队登记"本回合已下令"（防打转），
            // 再校验存活——任一小队全歼则整组拒绝，已登记的编号本回合不再受理。
            foreach (int n in squads)
            {
                if (orch.TryMarkSquadOrderedThisRound(n - 1))
                    return Err($"本回合已为小队{n}下达过指令，请勿重复；继续下一未部署的小队");
            }
            foreach (int n in squads)
            {
                if (AliveCount(orch, n - 1) == 0)
                    return Err($"小队{n}无存活可指挥成员（可能已被全歼）");
            }

            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);

            int ordered = 0;
            foreach (var c in buf)
            {
                if (c == null || c.teamId != orch.Team || c.IsDead) continue;
                if (!squads.Contains(c.squadId + 1)) continue;

                var fsm = c.GetComponent<FSMAIController>();
                if (fsm == null) continue;

                Vector3 goal = world;
                goal.y = c.transform.position.y;
                fsm.AssignObjective(goal);
                ordered++;
            }
            if (ordered == 0)
                return Err("无存活可指挥成员");

            foreach (int n in squads)
                orch.NoteSquadOrder(n, cellId, world.x, world.z);

            return new JObject
            {
                ["result"] = $"完成:小队{string.Join(",", squads)}→{display}",
                ["squads"] = new JArray(squads),
                ["cell"] = cellId,
                ["wx"] = Mathf.RoundToInt(world.x),
                ["wz"] = Mathf.RoundToInt(world.z),
            }.ToString(Formatting.None);
        }

        private List<int> ParseSquads(JToken args)
        {
            var list = new List<int>();
            var tok = args?["squads"];
            if (tok is JArray arr)
            {
                foreach (var v in arr)
                    if (int.TryParse(v.ToString(), out int n)) list.Add(n);
            }
            else if (tok != null && int.TryParse(tok.ToString(), out int single))
            {
                list.Add(single);   // 容错：单值也接受
            }

            if (list.Count == 0)
                throw new ArgumentException("缺少 squads（小队编号列表，如 [1,2]）");

            list.Sort();
            var deduped = new List<int>();
            foreach (int n in list)
            {
                if (n < 1 || n > orch.Config.squadsPerTeam)
                    throw new ArgumentException($"小队编号必须在 1-{orch.Config.squadsPerTeam}");
                if (!deduped.Contains(n)) deduped.Add(n);
            }
            return deduped;
        }

        // ---------------------------------------------------------------
        // 目标解析：格编号 / 据点编号 / 安全区显式拒绝
        // ---------------------------------------------------------------

        private bool TryResolveTarget(string token, out Vector3 world, out int cellId,
                                      out string display, out string error)
        {
            world = default;
            cellId = 0;
            display = null;
            error = null;

            string u = token.Trim().ToUpperInvariant();

            // 安全区不在词汇表（双方 GR 均不可作为目标——进入敌方 GR 会被强杀）。
            if (u.StartsWith("GR"))
            {
                error = "安全区不可作为目标（GR 仅供了解态势）";
                return false;
            }

            // 格编号 #N（容错：31 / 格31 / 格#31）。
            var hex = orch.HexGrid;
            if (hex != null && hex.TryParseCellToken(token, out int id, out world))
            {
                cellId = id;
                display = hex.CellDisplay(id);
                return true;
            }

            // 据点编号 HQ-A（容错：#HQ-A / 裸字母 A——模型偶发把 # 前缀混用到据点上）。
            string u2 = u.TrimStart('#');
            string letter = u2.StartsWith("HQ") ? u2.Substring(2).TrimStart('-', ' ') : u2;
            var mm = NetworkMatchManager.Instance;
            if (mm != null && letter.Length == 1 && char.IsLetter(letter[0]))
            {
                foreach (var cp in mm.capturePoints)
                {
                    if (cp == null ||
                        !string.Equals(cp.letter, letter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    world = cp.transform.position;
                    cellId = hex != null ? hex.NearestCellId(world) : 0;
                    display = $"HQ-{cp.letter}";
                    return true;
                }
            }

            error = $"非法目标 '{token}'（可用：格编号 #N 或据点编号 {HqList()}）";
            return false;
        }

        private static string HqList()
        {
            var mm = NetworkMatchManager.Instance;
            if (mm == null || mm.capturePoints.Count == 0) return "HQ-A…";
            var letters = new List<string>();
            foreach (var cp in mm.capturePoints)
                if (cp != null) letters.Add($"HQ-{cp.letter}");
            return string.Join("/", letters);
        }

        private static int AliveCount(CommanderOrchestrator orch, int squadIndex)
        {
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);
            int n = 0;
            foreach (var c in buf)
            {
                if (c == null || c.teamId != orch.Team || c.squadId != squadIndex) continue;
                if (!c.IsDead) n++;
            }
            return n;
        }

        // ---------------------------------------------------------------
        // commander_weapon / wait
        // ---------------------------------------------------------------

        private string CommanderWeapon(JToken args)
        {
            if (!int.TryParse(args?["weaponNumber"]?.ToString(), out int num))
                return Err($"缺少或非法 weaponNumber: {args?["weaponNumber"]}");

            string target = args?["target"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(target))
                return Err("缺少 target（格编号 #N 或据点编号 HQ-A）");

            if (NetworkCommanderState.GateActive)
                return Err("对局尚未开始，请先完成所有小队的部署指令");

            if (!TryResolveTarget(target, out Vector3 world, out int cellId,
                                  out string display, out string resolveError))
                return Err(resolveError);

            string text = orch.Weapons.TryOrder(num, world, display);
            return new JObject { ["result"] = text }.ToString(Formatting.None);
        }

        private string Wait(JToken args)
        {
            // 每回合限一次：重复 wait 只会覆盖唤醒时刻并空耗工具迭代（实测模型连发）。
            if (orch.TryMarkWaitUsedThisRound())
                return Err("本回合已使用过 wait（每回合限一次）；请基于当前态势行动，或等待默认空闲唤醒");

            float seconds = ResolveArgs(args, "seconds")[0];
            seconds = Mathf.Clamp(seconds, orch.Config.waitMin, orch.Config.waitMax);
            orch.RequestWakeAfter(seconds);
            return new JObject
            {
                ["result"] = $"已设置 {seconds:F0} 秒后唤醒（本轮结束时生效）"
            }.ToString(Formatting.None);
        }

        // ---------------------------------------------------------------
        // 参数解析（命名优先；位置回退兼容异常模型输出）
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
            new JObject { ["error"] = msg }.ToString(Formatting.None);
    }
}
