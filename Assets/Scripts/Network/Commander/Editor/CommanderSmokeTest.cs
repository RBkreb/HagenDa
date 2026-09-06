using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using HagenDa.Networking;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// PHASE11 编辑器冒烟测试：验证 LM Studio 端点连通性（/v1/models）与
    /// 文本+工具调用一次到位（算术探针 + report_result 工具；PHASE11 起
    /// 指挥官无图像输入）。
    ///
    /// 阻塞编辑器主线程是禁忌 → Task.Run 发请求（纯 .NET HttpClient，不碰
    /// UnityWebRequest），Debug.Log 线程安全，结果回主线程仅打日志。
    /// </summary>
    public static class CommanderSmokeTest
    {
        [MenuItem("HagenDa/Commander/Test LLM Endpoint")]
        public static void TestEndpoint()
        {
            var cfg = LoadConfigOrLog();
            if (cfg == null) return;

            Debug.Log($"[CommanderSmoke] 开始探测 {cfg.baseUrl} (model={cfg.model})…");
            Task.Run(async () =>
            {
                try
                {
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) })
                    {
                        // 1) 探活
                        var modelsResp = await http.GetAsync(
                            cfg.baseUrl.TrimEnd('/') + "/v1/models");
                        Debug.Log($"[CommanderSmoke] 探活: HTTP {(int)modelsResp.StatusCode}");

                        // 2) 文本 + 工具调用组合冒烟
                        var body = LlmRestClient.BuildChatBody(cfg,
                            new System.Collections.Generic.List<LlmMessage>
                            {
                                LlmMessage.User("1+1等于几？必须调用工具报告结果。"),
                            },
                            new System.Collections.Generic.List<JObject>
                            {
                                new JObject
                                {
                                    ["type"] = "function",
                                    ["function"] = new JObject
                                    {
                                        ["name"] = "report_result",
                                        ["description"] = "报告算术结果",
                                        ["parameters"] = new JObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JObject
                                            {
                                                ["result"] = new JObject { ["type"] = "string" }
                                            },
                                            ["required"] = new JArray("result"),
                                        }
                                    }
                                }
                            });

                        var content = new StringContent(body.ToString(), Encoding.UTF8,
                                                        "application/json");
                        var resp = await http.PostAsync(
                            cfg.baseUrl.TrimEnd('/') + "/v1/chat/completions", content);
                        string text = await resp.Content.ReadAsStringAsync();

                        var result = LlmRestClient.ParseChatResponse(
                            (int)resp.StatusCode, text, null);

                        if (!result.ok)
                        {
                            Debug.LogError("[CommanderSmoke] 失败: " + result.error);
                            return;
                        }

                        string callSummary = "-";
                        if (result.toolCalls != null && result.toolCalls.Count > 0)
                        {
                            var c0 = result.toolCalls[0];
                            callSummary = $"{c0["name"]}({c0["arguments_raw"]})";
                        }
                        Debug.Log($"[CommanderSmoke] 成功 finish={result.finishReason} " +
                                  $"content=\"{Truncate(result.content, 80)}\" " +
                                  $"toolCalls=[{callSummary}] " +
                                  $"tokens={result.promptTokens}/{result.completionTokens} " +
                                  $"reasoning={result.reasoningContent != null}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("[CommanderSmoke] 异常: " + e.Message);
                }
            });
        }

        private static CommanderConfig LoadConfigOrLog()
        {
            var guid = AssetDatabase.FindAssets("t:CommanderConfig");
            if (guid != null && guid.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid[0]);
                var cfg = AssetDatabase.LoadAssetAtPath<CommanderConfig>(path);
                if (cfg != null) return cfg;
            }
            // 未找到资产：内存默认值。
            return ScriptableObject.CreateInstance<CommanderConfig>();
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
