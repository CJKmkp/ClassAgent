using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ClassAgent.Agent
{
    public enum AgentMessageRole
    {
        System,
        User,
        Assistant,
        Tool
    }

    public sealed class AgentContentPart
    {
        public string Type { get; set; } = "text";
        public string Text { get; set; } = "";
        public string DataUrl { get; set; } = "";
        public string MediaType { get; set; } = "image/png";

        public static AgentContentPart TextPart(string text)
            => new AgentContentPart { Type = "text", Text = text ?? "" };

        public static AgentContentPart ImagePart(string dataUrl, string mediaType = "image/png")
            => new AgentContentPart { Type = "image", DataUrl = dataUrl ?? "", MediaType = mediaType };
    }

    public sealed class AgentMessage
    {
        public AgentMessageRole Role { get; set; }
        public string Content { get; set; } = "";
        public List<AgentContentPart> Parts { get; set; } = new List<AgentContentPart>();
        public string ToolCallId { get; set; } = "";
        public List<AgentToolCall> ToolCalls { get; set; } = new List<AgentToolCall>();

        public static AgentMessage User(string content)
            => new AgentMessage { Role = AgentMessageRole.User, Content = content ?? "" };

        public static AgentMessage User(IReadOnlyList<AgentContentPart> parts)
        {
            var result = new AgentMessage { Role = AgentMessageRole.User };
            if (parts != null) result.Parts.AddRange(parts);
            return result;
        }

        public static AgentMessage Tool(string callId, string content,
            IReadOnlyList<AgentContentPart> parts = null)
        {
            var result = new AgentMessage
            {
                Role = AgentMessageRole.Tool,
                ToolCallId = callId ?? "",
                Content = content ?? ""
            };
            if (parts != null) result.Parts.AddRange(parts);
            return result;
        }
    }

    public sealed class AgentToolDefinition
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public JsonElement Parameters { get; set; }
        public bool MutatesCanvas { get; set; }
    }

    public sealed class AgentToolCall
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "{}";
    }

    public sealed class AgentCompletion
    {
        public string Text { get; set; } = "";
        public string Thinking { get; set; } = "";
        public List<AgentToolCall> ToolCalls { get; set; } = new List<AgentToolCall>();
        public string FinishReason { get; set; } = "";
    }

    public sealed class AgentHttpException : Exception
    {
        public int StatusCode { get; }
        public string Body { get; }

        public AgentHttpException(int statusCode, string body)
            : base($"HTTP {statusCode}: {Truncate(body, 240)}")
        {
            StatusCode = statusCode;
            Body = body ?? "";
        }

        private static string Truncate(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max
                ? value ?? "" : value.Substring(0, max) + "…";
    }

    /// <summary>
    /// Protocol-neutral completion contract used by the orchestrator and tests.
    /// </summary>
    public interface IAgentCompletionClient
    {
        System.Threading.Tasks.Task<AgentCompletion> CompleteAsync(
            IReadOnlyList<AgentMessage> messages,
            string systemPrompt,
            IReadOnlyList<AgentToolDefinition> tools,
            System.Threading.CancellationToken cancellationToken,
            Action<string> onText = null,
            Action<string> onThinking = null);

        System.Threading.Tasks.Task<IReadOnlyList<string>> ListModelsAsync(
            System.Threading.CancellationToken cancellationToken);
    }
}
