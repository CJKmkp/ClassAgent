using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClassAgent.Config;

namespace ClassAgent.Agent
{
    internal sealed class AgentRunResult
    {
        public string Text { get; set; } = "";
        public string Thinking { get; set; } = "";
        public int ToolRounds { get; set; }
        public bool ReachedToolLimit { get; set; }
    }

    internal sealed class AgentOrchestrator
    {
        public async Task<AgentRunResult> RunAsync(
            IAgentCompletionClient client,
            AgentConfig config,
            AgentMessage userMessage,
            AgentToolRegistry tools,
            AgentToolContext toolContext,
            CancellationToken cancellationToken,
            Action<string> onText = null,
            Action<string> onThinking = null,
            Action<string> onTool = null)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (userMessage == null) throw new ArgumentNullException(nameof(userMessage));

            var messages = new List<AgentMessage> { userMessage };
            var result = new AgentRunResult();
            var maxRounds = Math.Max(1, Math.Min(12, config.MaxToolRounds));
            var definitions = tools?.Definitions ?? Array.Empty<AgentToolDefinition>();

            for (var round = 0; round < maxRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completion = await client.CompleteAsync(messages, config.SystemPrompt,
                    definitions, cancellationToken, onText, onThinking).ConfigureAwait(false);
                result.ToolRounds = round + 1;
                result.Text = completion.Text ?? result.Text;
                result.Thinking += completion.Thinking ?? "";

                if (completion.ToolCalls == null || completion.ToolCalls.Count == 0)
                    return result;

                messages.Add(new AgentMessage
                {
                    Role = AgentMessageRole.Assistant,
                    Content = completion.Text ?? "",
                    ToolCalls = new List<AgentToolCall>(completion.ToolCalls)
                });

                foreach (var call in completion.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    onTool?.Invoke(call.Name ?? "");
                    AgentToolResult toolResult;
                    if (tools == null)
                    {
                        toolResult = AgentToolResult.Fail("工具注册表不可用");
                    }
                    else
                    {
                        toolResult = await tools.ExecuteAsync(call.Name, call.Arguments,
                            toolContext, cancellationToken).ConfigureAwait(false);
                    }

                    var content = toolResult.Content ?? "";
                    if (!toolResult.Success) content = "工具失败：" + content;
                    messages.Add(AgentMessage.Tool(call.Id, content, toolResult.Parts));
                }
            }

            result.ReachedToolLimit = true;
            return result;
        }
    }
}
