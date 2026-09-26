using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClassAgent.Agent
{
    internal sealed class AnthropicClient : IAgentCompletionClient
    {
        private const string ApiVersion = "2023-06-01";
        private static readonly System.Net.Http.HttpClient HttpClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly int _maxTokens;

        public AnthropicClient(string baseUrl, string apiKey, string model, int maxTokens = 8192)
        {
            _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
            if (_baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                _baseUrl = _baseUrl.Substring(0, _baseUrl.Length - 3);
            _apiKey = apiKey ?? "";
            _model = model ?? "";
            _maxTokens = maxTokens > 0 ? maxTokens : 8192;
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
            if (messages != null)
            {
                foreach (var message in messages)
                {
                    if (message == null || message.Role == AgentMessageRole.System) continue;
                    requestMessages.Add(ToRequestMessage(message));
                }
            }

            var body = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["max_tokens"] = _maxTokens,
                ["stream"] = true,
                ["messages"] = requestMessages
            };
            if (!string.IsNullOrWhiteSpace(systemPrompt)) body["system"] = systemPrompt;
            if (tools != null && tools.Count > 0) body["tools"] = ToRequestTools(tools);

            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post, _baseUrl + "/v1/messages")
            {
                Content = new System.Net.Http.StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await HttpClient.SendAsync(request,
                System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
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
                var json = await new StreamReader(stream, Encoding.UTF8)
                    .ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                return ParseFullResponse(json, onText, onThinking);
            }

            var completion = new AgentCompletion();
            var text = new StringBuilder();
            var thinking = new StringBuilder();
            var toolsByIndex = new Dictionary<int, ToolBuilder>();

            using var sse = new SseReader(stream);
            while (true)
            {
                var item = await sse.ReadEventAsync(cancellationToken).ConfigureAwait(false);
                if (item == null) break;
                if (string.IsNullOrWhiteSpace(item.Data)) continue;

                try
                {
                    using var document = JsonDocument.Parse(item.Data);
                    var root = document.RootElement;
                    if (item.Event == "content_block_start")
                        ReadToolStart(root, toolsByIndex);
                    else if (item.Event == "content_block_delta")
                        ReadDelta(root, text, thinking, toolsByIndex, onText, onThinking);
                    else if (item.Event == "message_delta"
                        && root.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("stop_reason", out var stop)
                        && stop.ValueKind == JsonValueKind.String)
                        completion.FinishReason = stop.GetString() ?? "";
                }
                catch (JsonException)
                {
                    // Ignore malformed provider keep-alive payloads.
                }
            }

            completion.Text = text.ToString();
            completion.Thinking = thinking.ToString();
            foreach (var pair in toolsByIndex)
            {
                var builder = pair.Value;
                completion.ToolCalls.Add(new AgentToolCall
                {
                    Id = builder.Id,
                    Name = builder.Name,
                    Arguments = builder.Arguments.Length == 0 ? "{}" : builder.Arguments.ToString()
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

            var models = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visitedCursors = new HashSet<string>(StringComparer.Ordinal);
            string cursor = null;
            do
            {
                var url = _baseUrl + "/v1/models?limit=100";
                if (!string.IsNullOrEmpty(cursor))
                    url += "&after_id=" + Uri.EscapeDataString(cursor);
                using var request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
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
                var root = document.RootElement;
                if (!root.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                    break;
                foreach (var model in data.EnumerateArray())
                {
                    if (model.ValueKind != JsonValueKind.Object) continue;
                    var id = GetString(model, "id").Trim();
                    if (id.Length > 0 && seen.Add(id)) models.Add(id);
                }
                var hasMore = root.TryGetProperty("has_more", out var more)
                    && more.ValueKind == JsonValueKind.True;
                if (!hasMore) break;
                cursor = GetString(root, "last_id");
                if (cursor.Length == 0 || !visitedCursors.Add(cursor)) break;
            } while (true);

            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }

        private static object ToRequestMessage(AgentMessage message)
        {
            if (message.Role == AgentMessageRole.Tool)
            {
                var resultContent = new List<object>();
                if (message.Parts != null && message.Parts.Count > 0)
                {
                    foreach (var part in message.Parts)
                    {
                        if (part == null) continue;
                        if (string.Equals(part.Type, "image", StringComparison.OrdinalIgnoreCase))
                        {
                            var data = SplitDataUrl(part.DataUrl, part.MediaType);
                            resultContent.Add(new
                            {
                                type = "image",
                                source = new { type = "base64", media_type = data.MediaType, data = data.Data }
                            });
                        }
                        else
                        {
                            resultContent.Add(new { type = "text", text = part.Text ?? "" });
                        }
                    }
                }
                if (resultContent.Count == 0)
                    resultContent.Add(new { type = "text", text = message.Content ?? "" });
                return new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "tool_result",
                            tool_use_id = message.ToolCallId,
                            content = resultContent
                        }
                    }
                };
            }

            var content = new List<object>();
            if (message.Parts != null && message.Parts.Count > 0)
            {
                foreach (var part in message.Parts)
                {
                    if (part == null) continue;
                    if (string.Equals(part.Type, "image", StringComparison.OrdinalIgnoreCase))
                    {
                        var data = SplitDataUrl(part.DataUrl, part.MediaType);
                        content.Add(new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = data.MediaType,
                                data = data.Data
                            }
                        });
                    }
                    else
                    {
                        content.Add(new { type = "text", text = part.Text ?? "" });
                    }
                }
            }
            else
            {
                content.Add(new { type = "text", text = message.Content ?? "" });
            }

            if (message.Role == AgentMessageRole.Assistant && message.ToolCalls != null)
            {
                foreach (var call in message.ToolCalls)
                {
                    JsonElement input;
                    try { input = JsonDocument.Parse(call.Arguments ?? "{}").RootElement.Clone(); }
                    catch { input = JsonSerializer.SerializeToElement(new { }); }
                    content.Add(new { type = "tool_use", id = call.Id, name = call.Name, input });
                }
            }

            return new
            {
                role = message.Role == AgentMessageRole.Assistant ? "assistant" : "user",
                content
            };
        }

        private static List<object> ToRequestTools(IReadOnlyList<AgentToolDefinition> tools)
        {
            var result = new List<object>();
            foreach (var tool in tools)
            {
                var schema = tool.Parameters.ValueKind == JsonValueKind.Undefined
                    ? JsonSerializer.SerializeToElement(new { type = "object" })
                    : tool.Parameters;
                result.Add(new
                {
                    name = tool.Name,
                    description = tool.Description,
                    input_schema = schema
                });
            }
            return result;
        }

        private static void ReadToolStart(JsonElement root,
            Dictionary<int, ToolBuilder> toolsByIndex)
        {
            var index = GetInt(root, "index", toolsByIndex.Count);
            if (!root.TryGetProperty("content_block", out var block)
                || block.ValueKind != JsonValueKind.Object) return;
            if (!string.Equals(GetString(block, "type"), "tool_use", StringComparison.Ordinal)) return;
            if (!toolsByIndex.TryGetValue(index, out var builder))
            {
                builder = new ToolBuilder();
                toolsByIndex[index] = builder;
            }
            builder.Id = GetString(block, "id");
            builder.Name = GetString(block, "name");
            if (block.TryGetProperty("input", out var input))
                builder.Arguments.Append(input.GetRawText());
        }

        private static void ReadDelta(JsonElement root, StringBuilder text,
            StringBuilder thinking, Dictionary<int, ToolBuilder> toolsByIndex,
            Action<string> onText, Action<string> onThinking)
        {
            var index = GetInt(root, "index", -1);
            if (!root.TryGetProperty("delta", out var delta)
                || delta.ValueKind != JsonValueKind.Object) return;
            var type = GetString(delta, "type");
            if (type == "text_delta")
            {
                var value = GetString(delta, "text");
                if (value.Length == 0) return;
                text.Append(value);
                onText?.Invoke(value);
            }
            else if (type == "thinking_delta")
            {
                var value = GetString(delta, "thinking");
                if (value.Length == 0) return;
                thinking.Append(value);
                onThinking?.Invoke(value);
            }
            else if (type == "input_json_delta" && index >= 0)
            {
                if (!toolsByIndex.TryGetValue(index, out var builder))
                {
                    builder = new ToolBuilder();
                    toolsByIndex[index] = builder;
                }
                builder.Arguments.Append(GetString(delta, "partial_json"));
            }
        }

        private static AgentCompletion ParseFullResponse(string json,
            Action<string> onText, Action<string> onThinking)
        {
            var completion = new AgentCompletion();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            completion.FinishReason = GetString(root, "stop_reason");
            if (!root.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array) return completion;
            foreach (var block in content.EnumerateArray())
            {
                var type = GetString(block, "type");
                if (type == "text")
                {
                    var value = GetString(block, "text");
                    completion.Text += value;
                    onText?.Invoke(value);
                }
                else if (type == "thinking")
                {
                    var value = GetString(block, "thinking");
                    completion.Thinking += value;
                    onThinking?.Invoke(value);
                }
                else if (type == "tool_use")
                {
                    completion.ToolCalls.Add(new AgentToolCall
                    {
                        Id = GetString(block, "id"),
                        Name = GetString(block, "name"),
                        Arguments = block.TryGetProperty("input", out var input)
                            ? input.GetRawText() : "{}"
                    });
                }
            }
            return completion;
        }

        private static (string MediaType, string Data) SplitDataUrl(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return (fallback ?? "image/png", "");
            const string prefix = "data:";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (fallback ?? "image/png", value);
            var separator = value.IndexOf(",", StringComparison.Ordinal);
            if (separator < 0) return (fallback ?? "image/png", value);
            var header = value.Substring(prefix.Length, separator - prefix.Length);
            var media = header.Split(';')[0];
            return (string.IsNullOrWhiteSpace(media) ? fallback ?? "image/png" : media,
                value.Substring(separator + 1));
        }

        private static string GetString(JsonElement element, string property)
            => element.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

        private static int GetInt(JsonElement element, string property, int fallback)
            => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
                ? result : fallback;

        private sealed class ToolBuilder
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public StringBuilder Arguments { get; } = new StringBuilder();
        }
    }
}
