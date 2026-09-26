using System;
using System.Globalization;
using System.Resources;

namespace ClassAgent.Resources
{
    /// <summary>
    /// ClassAgent 的三语资源入口。XAML 只绑定该入口，不直接写用户可见文案。
    /// </summary>
    public static class Strings
    {
        private static readonly ResourceManager ResourceManager =
            new ResourceManager("ClassAgent.Resources.ClassAgentStrings", typeof(Strings).Assembly);

        public static string Get(string key, params object[] args)
        {
            var value = ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
            return args == null || args.Length == 0 ? value : string.Format(value, args);
        }
    }

    public sealed class LocalizedAccessor
    {
        public string PluginName => Strings.Get("Plugin_Name");
        public string ToolbarName => Strings.Get("Toolbar_Name");
        public string ToolbarDescription => Strings.Get("Toolbar_Description");
        public string WindowTitle => Strings.Get("Window_Title");
        public string InputPlaceholder => Strings.Get("Window_InputPlaceholder");
        public string Send => Strings.Get("Window_Send");
        public string Stop => Strings.Get("Window_Stop");
        public string Clear => Strings.Get("Window_Clear");
        public string Capture => Strings.Get("Window_Capture");
        public string Explain => Strings.Get("Window_Explain");
        public string Annotate => Strings.Get("Window_Annotate");
        public string WriteInk => Strings.Get("Window_WriteInk");
        public string Settings => Strings.Get("Window_Settings");
        public string Ready => Strings.Get("Window_Ready");
        public string Thinking => Strings.Get("Window_Thinking");
        public string Streaming => Strings.Get("Window_Streaming");
        public string ConfirmTitle => Strings.Get("Window_ConfirmTitle");
        public string SettingsTitle => Strings.Get("Settings_Title");
        public string Provider => Strings.Get("Settings_Provider");
        public string ProviderName => Strings.Get("Settings_ProviderName");
        public string BaseUrl => Strings.Get("Settings_BaseUrl");
        public string ApiKey => Strings.Get("Settings_ApiKey");
        public string Model => Strings.Get("Settings_Model");
        public string FetchModels => Strings.Get("Settings_FetchModels");
        public string SystemPrompt => Strings.Get("Settings_SystemPrompt");
        public string PromptGuide => Strings.Get("Settings_PromptGuide");
        public string PromptGuideDescription => Strings.Get("Settings_PromptGuideDescription");
        public string PromptExplain => Strings.Get("Settings_PromptExplain");
        public string PromptAnnotate => Strings.Get("Settings_PromptAnnotate");
        public string PromptBoard => Strings.Get("Settings_PromptBoard");
        public string PromptTransfer => Strings.Get("Settings_PromptTransfer");
        public string CopyPrompt => Strings.Get("Settings_CopyPrompt");
        public string PromptExplainText => Strings.Get("Settings_PromptExplainText");
        public string PromptAnnotateText => Strings.Get("Settings_PromptAnnotateText");
        public string PromptBoardText => Strings.Get("Settings_PromptBoardText");
        public string PromptTransferText => Strings.Get("Settings_PromptTransferText");
        public string ConfirmMode => Strings.Get("Settings_ConfirmMode");
        public string ConfirmAlways => Strings.Get("Settings_ConfirmAlways");
        public string ConfirmAuto => Strings.Get("Settings_ConfirmAuto");
        public string Voice => Strings.Get("Settings_Voice");
        public string WakeWord => Strings.Get("Settings_WakeWord");
        public string EnableVoice => Strings.Get("Settings_EnableVoice");
        public string ChatNav => Strings.Get("Window_ChatNav");
        public string HistoryNav => Strings.Get("Window_HistoryNav");
        public string HistoryEmpty => Strings.Get("Window_HistoryEmpty");
        public string ClearHistory => Strings.Get("Window_ClearHistory");
        public string ScreenNav => Strings.Get("Window_ScreenNav");
        public string BoardNav => Strings.Get("Window_BoardNav");
        public string SettingsNav => Strings.Get("Window_SettingsNav");
        public string ContextTitle => Strings.Get("Window_ContextTitle");
        public string ScreenHint => Strings.Get("Window_ScreenHint");
        public string BoardHint => Strings.Get("Window_BoardHint");
        public string CaptureAndExplain => Strings.Get("Window_CaptureAndExplain");
        public string AnnotateAction => Strings.Get("Window_AnnotateAction");
        public string WriteBoard => Strings.Get("Window_WriteBoard");
        public string EnterWhiteboard => Strings.Get("Window_EnterWhiteboard");
        public string Hide => Strings.Get("Window_Hide");
        public string TransferToWhiteboard => Strings.Get("Window_TransferToWhiteboard");
        public string TransferHint => Strings.Get("Window_TransferHint");
        public string Protocol => Strings.Get("Settings_Protocol");
        public string OpenAi => Strings.Get("Settings_OpenAi");
        public string Anthropic => Strings.Get("Settings_Anthropic");
        public string Save => Strings.Get("Settings_Save");
        public string Test => Strings.Get("Settings_Test");
        public string FloatingTooltip => Strings.Get("Floating_Tooltip");
        public string FloatingOpen => Strings.Get("Floating_Open");
        public string FloatingHide => Strings.Get("Floating_Hide");
        public string FloatingVoice => Strings.Get("Floating_Voice");
        public string ToolPen => Strings.Get("Tool_Pen");
        public string ToolSelect => Strings.Get("Tool_Select");
        public string ToolEraser => Strings.Get("Tool_Eraser");
        public string ToolShape => Strings.Get("Tool_Shape");
        public string ToolRoaming => Strings.Get("Tool_Roaming");
    }

    public static class Localized
    {
        public static LocalizedAccessor Current { get; } = new LocalizedAccessor();
    }
}
