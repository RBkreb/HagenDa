using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE10 指挥官轮次日志（JSONL 追加写）。验收要求：
    ///  - 每轮完整记录：触发原因、输入事件、非空间情报、assistant 输出
    ///    （content + reasoning + tool_calls）、工具执行结果、耗时、token 用量；
    ///  - 记忆滚动窗口丢弃的轮次也永久保留在这里；
    ///  - 目录 Logs/Commander/（项目根下，与 Unity 的 Logs/ 无关，单独新建）。
    ///
    /// 纯 C# 类（非 MonoBehaviour），Orchestrator 持有。Debug.Log 同时进 Console。
    /// </summary>
    public class CommanderRoundLogger
    {
        public static string RootDir =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Logs", "Commander");

        private readonly string filePath;
        private readonly string teamLabel;

        internal CommanderRoundLogger(string teamLabel)
        {
            this.teamLabel = teamLabel;
            Directory.CreateDirectory(RootDir);
            filePath = Path.Combine(
                RootDir, $"commander_{teamLabel}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            File.AppendAllText(filePath, "");   // ensure file exists
        }

        /// <summary>按阵营标签创建（红方 / 蓝方）。失败时返回不可用实例（写入静默吞掉）。</summary>
        public static CommanderRoundLogger Create(string teamLabel)
        {
            try { return new CommanderRoundLogger(teamLabel); }
            catch (Exception e)
            {
                Debug.LogWarning($"[CommanderLog] 创建日志失败: {e.Message}");
                return new CommanderRoundLoggerDisabled();
            }
        }

        /// <summary>追加一条结构化记录并换行。</summary>
        public void Write(object record)
        {
            try
            {
                var line = JsonConvert.SerializeObject(record,
                    Formatting.None,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                File.AppendAllText(filePath, line + "\n");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CommanderLog] 写入失败: {e.Message}");
            }
        }

        /// <summary>调试快照 PNG 落盘，返回文件路径（失败返回 null）。</summary>
        public string WriteSnapshot(byte[] pngBytes, string tag)
        {
            if (pngBytes == null || pngBytes.Length == 0) return null;
            try
            {
                var dir = Path.Combine(RootDir, "snapshots");
                Directory.CreateDirectory(dir);
                var p = Path.Combine(dir, $"{teamLabel}_{tag}_{DateTime.Now:HHmmss_fff}.png");
                File.WriteAllBytes(p, pngBytes);
                return p;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public string FilePath => filePath;

        private sealed class CommanderRoundLoggerDisabled : CommanderRoundLogger
        {
            public CommanderRoundLoggerDisabled() : base("disabled") { }
        }
    }
}
