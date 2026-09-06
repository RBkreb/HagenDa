using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HagenDa.Networking
{
    // ---------------------------------------------------------------
    // 消息模型（OpenAI /v1/chat/completions 形状的文本子集；PHASE11 起无图像）
    // ---------------------------------------------------------------

    /// <summary>一条对话消息（纯文本；PHASE11 指挥官无图像输入）。</summary>
    public sealed class LlmMessage
    {
        public string role;          // system | user | assistant | tool
        public string text;          // 纯文本 content（可空）
        public JArray toolCalls;     // assistant 原样 tool_calls 透传（role=assistant 时）

        public string toolCallId;    // role=tool 必填

        public static LlmMessage System(string t) => new LlmMessage { role = "system", text = t };
        public static LlmMessage User(string t) => new LlmMessage { role = "user", text = t };
        public static LlmMessage Assistant(string t) => new LlmMessage { role = "assistant", text = t };

        /// <summary>assistant 工具调用回合（LM Studio 要求原样回传 tool_calls + 空/短 content）。</summary>
        public static LlmMessage AssistantToolCalls(JArray calls, string content = null) =>
            new LlmMessage { role = "assistant", text = content, toolCalls = calls };

        public static LlmMessage ToolResult(string toolCallId, string jsonText) =>
            new LlmMessage { role = "tool", toolCallId = toolCallId, text = jsonText };
    }

    /// <summary>一次对话请求。</summary>
    public sealed class LlmChatRequest
    {
        public List<LlmMessage> messages = new List<LlmMessage>();
        public List<JObject> tools;              // 可空：不带工具
        public float timeoutSeconds = 120f;

        /// <summary>
        /// tool_choice：auto（默认，允许"不调用"）/ required（强制工具调用）。
        /// LM Studio 仅支持 none/auto/required（实测）。开局部署用 required——
        /// 单发制没有反馈轮，qwen3.8-4b 在 auto 下曾把 tool_calls 写成纯文本
        /// JSON 导致覆盖 0/6（实测踩坑）。
        /// </summary>
        public string toolChoice = "auto";
    }

    /// <summary>一次对话结果（含 token/耗时统计，JSONL 记录用）。</summary>
    public sealed class LlmChatResult
    {
        public bool ok;
        public string error;

        public string content;                 // assistant 文本（analysis）
        public string reasoningContent;        // 思考型模型的 reasoning_content（只记日志，不入历史）
        public JArray toolCalls;               // 归一化后的 [{id,name,arguments_raw,arguments}]
        public string finishReason;

        public double elapsedSeconds;
        public int promptTokens = -1;
        public int completionTokens = -1;
    }

    // ---------------------------------------------------------------
    // REST 客户端
    // ---------------------------------------------------------------

    /// <summary>
    /// PHASE11 薄 REST 客户端：直连 LM Studio 的 OpenAI 兼容端点（纯文本协议）。
    ///
    /// 传输层走【纯 .NET HttpClient + 后台线程】而非 UnityWebRequest：
    /// 实测 Play 模式下 UnityWebRequest 的上传被编辑器主循环泵制约束
    /// （PHASE10 大体积 base64 图片时代的问题；文本协议下保留后台线程路径，
    /// 避免 Play 主循环泵制带来的时延抖动）。协议组装/解析为本类静态方法
    /// （与编辑器工具共用）。
    ///
    /// 已实测事实驱动的设计：
    ///  - 原生 tools / tool_calls 可用；tool_choice 仅 none/auto/required
    ///  - 思考型模型 reasoning_content 单列，max_tokens 需覆盖推理消耗
    /// </summary>
    public sealed class LlmRestClient
    {
        private readonly CommanderConfig config;

        /// <summary>共享连接池的单例客户端（Reuse 避免端口耗尽）。</summary>
        private static readonly Lazy<HttpClient> SharedHttp = new Lazy<HttpClient>(() =>
        {
            var c = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
            });
            c.DefaultRequestHeaders.ExpectContinue = false;
            c.Timeout = Timeout.InfiniteTimeSpan;   // 超时由每次请求的 CTS 控制
            return c;
        });

        public LlmRestClient(CommanderConfig config)
        {
            this.config = config ?? ScriptableObject.CreateInstance<CommanderConfig>();
        }

        private string ChatUrl => config.baseUrl.TrimEnd('/') + "/v1/chat/completions";
        private string ModelsUrl => config.baseUrl.TrimEnd('/') + "/v1/models";

        /// <summary>发起一次对话。调用方可位于任意线程；返回在原上下文继续。</summary>
        public async Task<LlmChatResult> ChatAsync(LlmChatRequest request)
        {
            var body = BuildChatBody(config, request.messages, request.tools,
                                     request.toolChoice);
            var json = body.ToString();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using (var cts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(Math.Max(1f, request.timeoutSeconds))))
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                {
                    if (!string.IsNullOrEmpty(config.apiKey))
                        SharedHttp.Value.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.apiKey);

                    var resp = await SharedHttp.Value.PostAsync(ChatUrl, content, cts.Token);
                    var text = await resp.Content.ReadAsStringAsync();
                    var result = ParseChatResponse((int)resp.StatusCode, text, null);
                    result.elapsedSeconds = sw.Elapsed.TotalSeconds;
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                return new LlmChatResult
                {
                    ok = false,
                    error = $"超时({request.timeoutSeconds:F0}s)",
                    elapsedSeconds = sw.Elapsed.TotalSeconds,
                };
            }
            catch (Exception e)
            {
                return new LlmChatResult
                {
                    ok = false,
                    error = e.Message,
                    elapsedSeconds = sw.Elapsed.TotalSeconds,
                };
            }
        }

        /// <summary>探活（GET /v1/models）。不触发模型加载，开销极小。</summary>
        public async Task<bool> ProbeAsync(int timeoutSeconds = 5)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                using (var resp = await SharedHttp.Value.GetAsync(ModelsUrl, cts.Token))
                    return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------
        // 请求组装（静态，编辑器测试复用）
        // ---------------------------------------------------------------

        public static JObject BuildChatBody(CommanderConfig cfg,
                                            List<LlmMessage> messages,
                                            List<JObject> tools,
                                            string toolChoice = "auto")
        {
            var msgs = new JArray();
            foreach (var m in messages)
            {
                var mo = new JObject { ["role"] = m.role };

                if (m.role == "assistant" && m.toolCalls != null)
                {
                    mo["content"] = m.text ?? "";
                    mo["tool_calls"] = m.toolCalls.DeepClone();
                }
                else if (m.role == "tool")
                {
                    mo["tool_call_id"] = m.toolCallId ?? "";
                    mo["content"] = m.text ?? "";
                }
                else
                {
                    mo["content"] = m.text ?? "";
                }

                msgs.Add(mo);
            }

            var body = new JObject
            {
                ["model"] = cfg.model,
                ["messages"] = msgs,
                ["temperature"] = cfg.temperature,
                ["max_tokens"] = cfg.maxTokens,
            };
            if (tools != null && tools.Count > 0)
            {
                var toolArray = new JArray();
                foreach (var t in tools) toolArray.Add(t.DeepClone());
                body["tools"] = toolArray;
                // 实测仅 none/auto/required 受支持；开局部署传 required 强制工具调用。
                body["tool_choice"] = string.IsNullOrEmpty(toolChoice) ? "auto" : toolChoice;
            }
            return body;
        }

        // ---------------------------------------------------------------
        // 响应解析（静态，容错 reasoning_content）
        // ---------------------------------------------------------------

        public static LlmChatResult ParseChatResponse(int status, string body, string transportError)
        {
            var r = new LlmChatResult();

            if (!string.IsNullOrEmpty(transportError) && status == 0)
            {
                r.error = transportError;
                return r;
            }

            JObject root = null;
            try { root = JObject.Parse(body ?? ""); }
            catch { /* fallthrough */ }

            if (status < 200 || status >= 300)
            {
                // LM Studio 的 error 形态不固定：可能是 {"error":{"message":..}}、
                // {"error":"字符串"}、或整包纯文本。逐型安全提取（次生异常会掩盖
                // 真实服务端错误，实测踩坑）。
                string detail = null;
                var errTok = root?["error"];
                if (errTok is JObject eo)
                    detail = eo["message"]?.ToString() ?? eo.ToString(Newtonsoft.Json.Formatting.None);
                else if (errTok != null)
                    detail = errTok.ToString();
                if (string.IsNullOrEmpty(detail)) detail = Truncate(body, 300);

                r.error = $"HTTP {status}: {detail}";
                return r;
            }
            if (root == null)
            {
                r.error = "响应不是合法 JSON";
                return r;
            }

            try
            {
                var choice = root["choices"]?[0];
                var message = choice?["message"];

                r.content = message?["content"]?.ToString() ?? "";
                r.reasoningContent = message?["reasoning_content"]?.ToString();
                r.finishReason = choice?["finish_reason"]?.ToString();

                var calls = message?["tool_calls"] as JArray;
                if (calls != null && calls.Count > 0)
                {
                    var normalized = new JArray();
                    foreach (var c in calls)
                    {
                        var fn = c["function"];
                        var item = new JObject
                        {
                            ["id"] = c["id"]?.ToString(),
                            ["name"] = fn?["name"]?.ToString(),
                            ["arguments_raw"] = fn?["arguments"]?.ToString(),
                        };
                        try { item["arguments"] = JToken.Parse(item["arguments_raw"]?.ToString() ?? "{}"); }
                        catch { item["arguments"] = new JObject(); }
                        normalized.Add(item);
                    }
                    r.toolCalls = normalized;
                }

                r.promptTokens = root["usage"]?["prompt_tokens"]?.Value<int>() ?? -1;
                r.completionTokens = root["usage"]?["completion_tokens"]?.Value<int>() ?? -1;

                r.ok = true;
            }
            catch (Exception e)
            {
                r.error = "解析响应失败: " + e.Message;
            }
            return r;
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
