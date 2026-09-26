using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassAgent.Agent
{
    internal sealed class OpenAiCompatibleClient : IAgentCompletionClient
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly double _temperature;
        private readonly int _maxTokens;

        public OpenAiCompatibleClient(string baseUrl, string apiKey, string model,
            double temperature = 0, int maxTokens = 4096)
        {
            _baseUrl = NormalizeBaseUrl(baseUrl);
            _apiKey = apiKey ?? "";
            _model = model ?? "";
            _temperature = temperature;
            _maxTokens = maxTokens;
        }

        public async Task<AgentCompletion> CompleteAsync(
            IReadOnlyList<AgentMessage> messages,
            string systemPrompt,
            IReadOnlyList<AgentToolDefinition> tools,
            CancellationToken cancellationToken,
            Action<string> onText = null,
            Action<string> onThinking = null)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl)) throw new InvalidOperationException("Base URL 未设置");
            if (string.IsNullOrWhiteSpace(_apiKey)) throw new InvalidOperationException("API Key 未设置");
            if (string.IsNullOrWhiteSpace(_model)) throw new InvalidOperationException("模型未设置");

            var requestMessages = new List<object>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                requestMessages.Add(new { role = "system", content = systemPrompt });
            if (messages != null)
            {
                foreach (var message in messages)
                    requestMessages.Add(ToRequestMessage(message));
            }

            var body = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["messages"] = requestMessages,
                ["stream"] = true
            };
            if (_maxTokens > 0) body["max_tokens"] = _maxTokens;
            if (_temperature > 0) body["temperature"] = _temperature;
            if (tools != null && tools.Count > 0) body["tools"] = ToRequestTools(tools);

            using var request = new HttpRequestMessage(HttpMethod.Post,
                _baseUrl + "/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await HttpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new AgentHttpException((int)response.StatusCode, error);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                var json = await new System.IO.StreamReader(stream, Encoding.UTF8)
                    .ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                return ParseFullResponse(json, onText, onThinking);
            }

            var completion = new AgentCompletion();
            var text = new StringBuilder();
            var thinking = new StringBuilder();
            var toolBuilders = new Dictionary<int, ToolBuilder>();

            using var sse = new SseReader(stream);
            while (true)
            {
                var item = await sse.ReadEventAsync(cancellationToken).ConfigureAwait(false);
                if (item == null || item.IsDone) break;
                if (string.IsNullOrWhiteSpace(item.Data)) continue;

                try
                {
                    using var document = JsonDocument.Parse(item.Data);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("choices", out var choices)
                        || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                        continue;
                    var choice = choices[0];
                    if (choice.TryGetProperty("finish_reason", out var finish)
                        && finish.ValueKind == JsonValueKind.String)
                        completion.FinishReason = finish.GetString() ?? "";
                    if (!choice.TryGetProperty("delta", out var delta)
                        || delta.ValueKind != JsonValueKind.Object) continue;

                    AppendString(delta, "content", text, onText);
                    AppendString(delta, "reasoning_content", thinking, onThinking);
                    if (delta.TryGetProperty("tool_calls", out var toolCalls)
                        && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var toolCall in toolCalls.EnumerateArray())
                            AppendToolCall(toolCall, toolBuilders);
                    }
                }
                catch (JsonException)
                {
                    // 服务端偶尔会在 SSE 中插入 keep-alive 或诊断文本。
                }
            }

            completion.Text = text.ToString();
            completion.Thinking = thinking.ToString();
            foreach (var pair in toolBuilders)
            {
                var value = pair.Value;
                completion.ToolCalls.Add(new AgentToolCall
                {
                    Id = value.Id,
                    Name = value.Name,
                    Arguments = value.Arguments.ToString()
                });
            }
            return completion;
        }

        public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
                throw new InvalidOperationException("Base URL 未设置");
            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("API Key 未设置");

            using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var response = await HttpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                throw new AgentHttpException((int)response.StatusCode, error);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var models = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in data.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object
                    || !model.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String)
                    continue;
                var name = id.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) models.Add(name);
            }
            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }

        private static object ToRequestMessage(AgentMessage message)
        {
            var role = message.Role switch
            {
                AgentMessageRole.Assistant => "assistant",
                AgentMessageRole.Tool => "tool",
                _ => "user"
            };

            if (message.Role == AgentMessageRole.Tool)
            {
                object toolContent = message.Parts != null && message.Parts.Count > 0
                    ? ToRequestParts(message.Parts)
                    : message.Content ?? "";
                return new
                {
                    role,
                    tool_call_id = message.ToolCallId,
                    content = toolContent
                };
            }

            object content = message.Parts != null && message.Parts.Count > 0
                ? ToRequestParts(message.Parts)
                : message.Content ?? "";

            var toolCalls = new List<object>();
            if (message.ToolCalls != null)
            {
                foreach (var call in message.ToolCalls)
                {
                    toolCalls.Add(new
                    {
                        id = call.Id,
                        type = "function",
                        function = new { name = call.Name, arguments = call.Arguments ?? "{}" }
                    });
                }
            }

            if (toolCalls.Count == 0)
                return new { role, content };
            return new { role, content, tool_calls = toolCalls };
        }

        private static List<object> ToRequestParts(IReadOnlyList<AgentContentPart> parts)
        {
            var result = new List<object>();
            foreach (var part in parts)
            {
                if (part == null) continue;
                if (string.Equals(part.Type, "image", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new { type = "image_url", image_url = new { url = part.DataUrl } });
                }
                else
                {
                    result.Add(new { type = "text", text = part.Text ?? "" });
                }
            }
            return result;
        }

        private static List<object> ToRequestTools(IReadOnlyList<AgentToolDefinition> tools)
        {
            var result = new List<object>();
            foreach (var tool in tools)
            {
                var parameters = tool.Parameters.ValueKind == JsonValueKind.Undefined
                    ? JsonSerializer.SerializeToElement(new { type = "object" })
                    : tool.Parameters;
                result.Add(new
                {
                    type = "function",
                    function = new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        parameters
                    }
                });
            }
            return result;
        }

        private static void AppendString(JsonElement element, string property,
            StringBuilder builder, Action<string> callback)
        {
            if (!element.TryGetProperty(property, out var value)
                || value.ValueKind != JsonValueKind.String) return;
            var text = value.GetString();
            if (string.IsNullOrEmpty(text)) return;
            builder.Append(text);
            callback?.Invoke(text);
        }

        private static void AppendToolCall(JsonElement element,
            Dictionary<int, ToolBuilder> builders)
        {
            var index = element.TryGetProperty("index", out var indexElement)
                && indexElement.TryGetInt32(out var parsed) ? parsed : 0;
            if (!builders.TryGetValue(index, out var builder))
            {
                builder = new ToolBuilder();
                builders[index] = builder;
            }
            if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                builder.Id = id.GetString() ?? builder.Id;
            if (!element.TryGetProperty("function", out var function)
                || function.ValueKind != JsonValueKind.Object) return;
            if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                builder.Name = name.GetString() ?? builder.Name;
            if (function.TryGetProperty("arguments", out var arguments)
                && arguments.ValueKind == JsonValueKind.String)
                builder.Arguments.Append(arguments.GetString());
        }

        private static AgentCompletion ParseFullResponse(string json,
            Action<string> onText, Action<string> onThinking)
        {
            var completion = new AgentCompletion();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return completion;
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var reason)
                && reason.ValueKind == JsonValueKind.String)
                completion.FinishReason = reason.GetString() ?? "";
            if (!choice.TryGetProperty("message", out var message)) return completion;
            if (message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                completion.Text = content.GetString() ?? "";
                onText?.Invoke(completion.Text);
            }
            if (message.TryGetProperty("reasoning_content", out var thinking)
                && thinking.ValueKind == JsonValueKind.String)
            {
                completion.Thinking = thinking.GetString() ?? "";
                onThinking?.Invoke(completion.Thinking);
            }
            if (message.TryGetProperty("tool_calls", out var toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in toolCalls.EnumerateArray())
                {
                    var call = new AgentToolCall
                    {
                        Id = GetString(item, "id"),
                        Arguments = "{}"
                    };
                    if (item.TryGetProperty("function", out var function))
                    {
                        call.Name = GetString(function, "name");
                        call.Arguments = GetString(function, "arguments");
                    }
                    completion.ToolCalls.Add(call);
                }
            }
            return completion;
        }

        private static string GetString(JsonElement element, string property)
            => element.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

        private static string NormalizeBaseUrl(string value)
        {
            var url = (value ?? "").Trim().TrimEnd('/');
            if (url.Length == 0) return url;
            if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
            var separator = url.IndexOf("://", StringComparison.Ordinal);
            var rest = separator >= 0 ? url.Substring(separator + 3) : url;
            if (!rest.Contains("/", StringComparison.Ordinal)
                && !url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                url += "/v1";
            return url;
        }

        private sealed class ToolBuilder
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public StringBuilder Arguments { get; } = new StringBuilder();
        }
    }
}
