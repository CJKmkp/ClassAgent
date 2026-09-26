using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassAgent.Config
{
    public enum AgentProtocol
    {
        OpenAiCompatible = 0,
        Anthropic = 1
    }

    public enum ToolConfirmationMode
    {
        Always = 0,
        Automatic = 1
    }

    public sealed class ProviderConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public AgentProtocol Protocol { get; set; } = AgentProtocol.OpenAiCompatible;
        public string BaseUrl { get; set; } = "";
        public string ApiKeyCipher { get; set; } = "";
        public string Model { get; set; } = "";
        public List<string> Models { get; set; } = new List<string>();
        public bool VisionEnabled { get; set; } = true;

        [JsonIgnore]
        public bool IsAnthropic => Protocol == AgentProtocol.Anthropic;
    }

    public sealed class ButtonPositionState
    {
        public double Left { get; set; } = double.NaN;
        public double Top { get; set; } = double.NaN;
    }

    /// <summary>让未定位的悬浮球坐标以 JSON null 保存，而不是非法的 NaN 数字。</summary>
    internal sealed class JsonDoubleNaNConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return double.NaN;
            if (reader.TokenType == JsonTokenType.Number) return reader.GetDouble();
            if (reader.TokenType == JsonTokenType.String
                && double.TryParse(reader.GetString(), out var value)) return value;
            return double.NaN;
        }

        public override void Write(Utf8JsonWriter writer, double value,
            JsonSerializerOptions options)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) writer.WriteNullValue();
            else writer.WriteNumberValue(value);
        }
    }

    public sealed class ChatHistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string Response { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public bool IncludedScreen { get; set; }
    }

    public sealed class AgentConfig
    {
        public List<ProviderConfig> Providers { get; set; } = new List<ProviderConfig>();
        public string CurrentProviderId { get; set; } = "";
        public string SystemPrompt { get; set; } =
            "你是一名严谨、清晰、耐心的课堂教学助手。优先给出可验证的步骤，" +
            "需要批注或板书时先输出结构化计划，不要编造屏幕上看不到的内容。";
        public ToolConfirmationMode ConfirmationMode { get; set; } = ToolConfirmationMode.Always;
        public bool EnableVoiceWakeWord { get; set; }
        public string WakeWord { get; set; } = "ClassAgent";
        public bool CaptureScreenOnExplain { get; set; } = true;
        public int MaxToolRounds { get; set; } = 6;
        public int MaxTokens { get; set; } = 4096;
        public double Temperature { get; set; }
        public ButtonPositionState ButtonPosition { get; set; } = new ButtonPositionState();
        public List<ChatHistoryEntry> History { get; set; } = new List<ChatHistoryEntry>();

        [JsonIgnore]
        public ProviderConfig CurrentProvider
        {
            get
            {
                EnsureProvider();
                foreach (var provider in Providers)
                {
                    if (string.Equals(provider.Id, CurrentProviderId, StringComparison.Ordinal))
                        return provider;
                }
                return Providers[0];
            }
        }

        public void EnsureProvider()
        {
            if (Providers == null) Providers = new List<ProviderConfig>();
            if (History == null) History = new List<ChatHistoryEntry>();
            foreach (var provider in Providers)
            {
                if (provider.Models == null) provider.Models = new List<string>();
            }
            if (Providers.Count == 0)
            {
                Providers.Add(new ProviderConfig
                {
                    Name = "OpenAI Compatible",
                    BaseUrl = "https://api.openai.com/v1",
                    Model = "gpt-4o-mini"
                });
            }

            if (string.IsNullOrWhiteSpace(CurrentProviderId)
                || Providers.TrueForAll(x => !string.Equals(x.Id, CurrentProviderId, StringComparison.Ordinal)))
                CurrentProviderId = Providers[0].Id;
        }
    }

    public sealed class AgentConfigStore
    {
        private readonly string _path;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        static AgentConfigStore()
        {
            JsonOptions.Converters.Add(new JsonDoubleNaNConverter());
        }

        public AgentConfig Current { get; private set; } = new AgentConfig();

        public AgentConfigStore(string folder)
        {
            _path = Path.Combine(folder ?? "", "config.json");
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    Current.EnsureProvider();
                    return;
                }

                var stored = File.ReadAllText(_path);
                if (SecretStore.TryUnprotectText(stored, out var decrypted))
                    stored = decrypted;
                Current = JsonSerializer.Deserialize<AgentConfig>(stored, JsonOptions) ?? new AgentConfig();
                Current.EnsureProvider();
            }
            catch
            {
                Current = new AgentConfig();
                Current.EnsureProvider();
            }
        }

        public void Save()
        {
            Current.EnsureProvider();
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(_path, SecretStore.ProtectText(json));
        }

        public ProviderConfig GetCurrentProvider()
            => Current.CurrentProvider;

        public void AddHistory(string prompt, string response, bool includedScreen)
        {
            Current.EnsureProvider();
            var normalizedPrompt = (prompt ?? "").Trim();
            if (normalizedPrompt.Length == 0) return;
            var title = normalizedPrompt.Replace("\r", " ").Replace("\n", " ").Trim();
            if (title.Length > 80) title = title.Substring(0, 80) + "…";
            Current.History.Insert(0, new ChatHistoryEntry
            {
                Title = title,
                Prompt = normalizedPrompt,
                Response = response ?? "",
                IncludedScreen = includedScreen
            });
            if (Current.History.Count > 50)
                Current.History.RemoveRange(50, Current.History.Count - 50);
            Save();
        }

        public void ClearHistory()
        {
            Current.History.Clear();
            Save();
        }

        public string GetApiKey()
        {
            var cipher = GetCurrentProvider()?.ApiKeyCipher;
            return SecretStore.TryUnprotect(cipher);
        }

        public void SetApiKey(string value)
        {
            var provider = GetCurrentProvider();
            if (provider == null) return;
            provider.ApiKeyCipher = string.IsNullOrEmpty(value)
                ? "" : SecretStore.ProtectText(value);
        }
    }
}
