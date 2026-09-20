using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace HagenDa.Tests.Tools
{
    /// <summary>
    /// 命令行/编辑器内触发测试并将结果落盘的小工具。
    ///
    /// 存在的理由：本项目按约定"只在编辑器 Test Runner 里跑测试"，但无头验证与后续 CI
    /// 需要机器可读的结果。<see cref="TestRunnerApi"/> 是 Unity 官方的编程式入口，
    /// 这里把它包一层：执行指定模式 → 等回调 → 把摘要与 NUnit XML 写到
    /// <c>.scratch/testrun/</c>，并落一个 <c>.done</c> 标记供外部轮询。
    ///
    /// 用法（编辑器内 C# 执行）：
    ///   HagenDa.Tests.Tools.TestRunnerCli.RunEditMode();
    ///   HagenDa.Tests.Tools.TestRunnerCli.RunPlayMode();
    /// 然后轮询 .scratch/testrun/&lt;mode&gt;.done 是否出现。
    /// </summary>
    public static class TestRunnerCli
    {
        private static readonly string OutputDir =
            Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "testrun");

        private static TestRunnerApi _api;
        private static ResultHandler _handler;

        /// <summary>把最终状态写入 &lt;mode&gt;.status，供外部读取。</summary>
        private class ResultHandler : ICallbacks
        {
            private string _modeTag;
            private readonly List<string> _failures = new List<string>();
            private int _passed;
            private int _failed;
            private int _skipped;
            private int _inconclusive;
            private string _runName = "";

            /// <summary>
            /// 只有当前轮登记的 handler 才上报。
            ///
            /// 原因：进入 PlayMode 会触发域重载，静态字段被重置，于是会新建 handler 注册；
            /// 而域重载**前**注册的旧 handler 仍会收到本轮事件（跨域仍存活）。
            /// 用"是否是当前 handler"这一身份判断，即可让旧实例静默。
            /// 域重载后 <c>_handler</c> 为 null，旧实例自然不等于它。
            /// </summary>
            private bool IsCurrent => ReferenceEquals(_handler, this);

            /// <summary>复用同一实例，因此每轮开始必须清空计数（否则跨轮累加）。</summary>
            public void Reset(string modeTag)
            {
                _modeTag = modeTag;
                _failures.Clear();
                _passed = 0;
                _failed = 0;
                _skipped = 0;
                _inconclusive = 0;
                _runName = "";
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                if (!IsCurrent) return;
                _runName = testsToRun != null ? testsToRun.Name : "";
                Write($"RUNNING tests={CountTests(testsToRun)} name={_runName}");
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                if (!IsCurrent) return;

                // 一律采用顶层 result 的统计——它对每次运行都是权威且隔离的。
                _passed = result.PassCount;
                _failed = result.FailCount;
                _skipped = result.SkipCount;
                _inconclusive = result.InconclusiveCount;

                Write($"FINISHED pass={_passed} fail={_failed} skip={_skipped} " +
                      $"inconclusive={_inconclusive} total={_passed + _failed + _skipped + _inconclusive}");

                var sb = new StringBuilder();
                sb.AppendLine($"mode={_modeTag}");
                sb.AppendLine($"run={_runName}");
                sb.AppendLine($"passed={_passed}");
                sb.AppendLine($"failed={_failed}");
                sb.AppendLine($"skipped={_skipped}");
                sb.AppendLine($"inconclusive={_inconclusive}");
                if (_failures.Count > 0)
                {
                    sb.AppendLine("---- failures ----");
                    foreach (var f in _failures)
                        sb.AppendLine(f);
                }

                try
                {
                    File.WriteAllText(Path.Combine(OutputDir, _modeTag + ".result"), sb.ToString());
                    File.WriteAllText(Path.Combine(OutputDir, _modeTag + ".done"), DateTime.UtcNow.ToString("O"));
                }
                catch (Exception e)
                {
                    Debug.LogError($"[TestRunnerCli] 写入结果失败: {e}");
                }

                Debug.Log($"[TestRunnerCli] {_modeTag} 完成：通过 {_passed}，失败 {_failed}，" +
                          $"跳过 {_skipped}，未决 {_inconclusive}。");
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (!IsCurrent) return;

                // 收集失败详情（用于结果文件）。计数以 RunFinished 的顶层统计为准，
                // 这里只负责记录失败原因。
                if (!result.HasChildren)
                {
                    if (result.TestStatus == TestStatus.Failed)
                    {
                        _failures.Add($"[FAIL] {result.FullName}\n       {FirstLines(result.Message, 4)}\n       {FirstLines(result.StackTrace, 6)}");
                        Debug.LogError($"[TestRunnerCli] FAIL {result.FullName}: {FirstLines(result.Message, 3)}");
                    }
                }
            }

            private static int CountTests(ITestAdaptor node)
            {
                if (node == null) return 0;
                if (!node.HasChildren) return 1;
                return node.Children.Sum(CountTests);
            }

            private static string FirstLines(string s, int n)
            {
                if (string.IsNullOrEmpty(s)) return "";
                var lines = s.Replace("\r\n", "\n").Split('\n');
                return string.Join("\n       ", lines.Take(n));
            }
        }

        private static void Write(string line)
        {
            try
            {
                Directory.CreateDirectory(OutputDir);
                File.AppendAllText(Path.Combine(OutputDir, "progress.log"),
                    $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { /* 日志失败不影响测试 */ }
        }

        private static void Run(TestMode mode, string modeTag)
        {
            Directory.CreateDirectory(OutputDir);

            // 清理上一轮标记，避免外部轮询读到旧结果。
            foreach (var suffix in new[] { ".done", ".result" })
            {
                var p = Path.Combine(OutputDir, modeTag + suffix);
                if (File.Exists(p)) File.Delete(p);
            }
            File.WriteAllText(Path.Combine(OutputDir, "progress.log"), "");

            // 只创建一次 API + 回调并复用：TestRunnerApi 是 Editor 单例式的，
            // 每轮新建实例会留下旧回调订阅，导致结果重复上报、计数跨轮累加。
            if (_api == null)
            {
                _handler = new ResultHandler();
                _api = ScriptableObject.CreateInstance<TestRunnerApi>();
                _api.RegisterCallbacks(_handler);
            }
            _handler.Reset(modeTag);

            var filter = new Filter { testMode = mode };
            var settings = new ExecutionSettings(filter) { runSynchronously = false };

            Debug.Log($"[TestRunnerCli] 开始 {modeTag} 测试…");
            _api.Execute(settings);
        }

        public static void RunEditMode() => Run(TestMode.EditMode, "editmode");
        public static void RunPlayMode() => Run(TestMode.PlayMode, "playmode");
    }
}
