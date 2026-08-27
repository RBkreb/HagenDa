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
    /// PHASE10 编辑器冒烟测试：验证 LM Studio 端点连通性（/v1/models）与
    /// 视觉+工具调用一次到位（8×8 红色测试图 + report_color 工具）。
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

            // 视觉编码必须在主线程完成（Texture2D / EncodeToPNG）。
            byte[] probePng = CreateProbePng();

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

                        // 2) 视觉 + 工具调用组合冒烟
                        var body = LlmRestClient.BuildChatBody(cfg,
                            new System.Collections.Generic.List<LlmMessage>
                            {
                                LlmMessage.UserImage(probePng, "图中是什么颜色？必须调用工具报告。"),
                            },
                            new System.Collections.Generic.List<JObject>
                            {
                                new JObject
                                {
                                    ["type"] = "function",
                                    ["function"] = new JObject
                                    {
                                        ["name"] = "report_color",
                                        ["description"] = "报告看到的颜色",
                                        ["parameters"] = new JObject
                                        {
                                            ["type"] = "object",
                                            ["properties"] = new JObject
                                            {
                                                ["color"] = new JObject { ["type"] = "string" }
                                            },
                                            ["required"] = new JArray("color"),
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

        /// <summary>8×8 红色 PNG（视觉通路探针），须在主线程调用。</summary>
        private static byte[] CreateProbePng()
        {
            var tex = new Texture2D(8, 8, TextureFormat.RGB24, false);
            var pixels = new Color32[64];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(220, 30, 30, 255);
            tex.SetPixels32(pixels);
            tex.Apply(false);
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return png;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
