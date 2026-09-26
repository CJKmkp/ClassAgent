using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ClassAgent.Config;
using ClassAgent.Resources;

namespace ClassAgent.Views
{
    public partial class SettingsView : UserControl
    {
        private bool _loaded;
        private CancellationTokenSource _modelFetchCancellation;
        public ClassAgentPlugin Plugin { get; set; }

        public SettingsView()
        {
            InitializeComponent();
            Loaded += (_, __) => LoadSettings();
            Unloaded += (_, __) => _modelFetchCancellation?.Cancel();
        }

        private void LoadSettings()
        {
            if (Plugin?.ConfigStore == null) return;
            _loaded = false;
            var config = Plugin.ConfigStore.Current;
            var provider = config.CurrentProvider;
            ProviderNameBox.Text = provider?.Name ?? "";
            BaseUrlBox.Text = provider?.BaseUrl ?? "";
            ApiKeyBox.Password = Plugin.ConfigStore.GetApiKey();
            ModelCombo.ItemsSource = new List<string>(provider?.Models ?? new List<string>());
            ModelCombo.Text = provider?.Model ?? "";
            SystemPromptBox.Text = config.SystemPrompt ?? "";
            WakeWordBox.Text = config.WakeWord ?? "";
            VoiceCard.IsOn = config.EnableVoiceWakeWord;
            SelectComboByTag(ProtocolCombo, provider?.Protocol.ToString());
            SelectComboByTag(ConfirmationCombo, config.ConfirmationMode.ToString());
            _loaded = true;
        }

        private void SaveSettings()
        {
            if (Plugin?.ConfigStore == null) return;
            var config = Plugin.ConfigStore.Current;
            var provider = config.CurrentProvider;
            provider.Name = ProviderNameBox.Text?.Trim() ?? "";
            provider.BaseUrl = BaseUrlBox.Text?.Trim() ?? "";
            provider.Model = ModelCombo.Text?.Trim() ?? "";
            Plugin.ConfigStore.SetApiKey(ApiKeyBox.Password);
            config.SystemPrompt = SystemPromptBox.Text ?? "";
            config.WakeWord = string.IsNullOrWhiteSpace(WakeWordBox.Text)
                ? "ClassAgent" : WakeWordBox.Text.Trim();
            config.EnableVoiceWakeWord = VoiceCard.IsOn;
            if (ConfirmationCombo.SelectedItem is ComboBoxItem confirmation
                && Enum.TryParse<ToolConfirmationMode>(confirmation.Tag?.ToString(), out var mode))
                config.ConfirmationMode = mode;
            Plugin.SaveConfiguration();
            StatusText.Text = Strings.Get("Settings_Saved");
        }

        private async void FetchModelsButton_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin?.ConfigStore == null) return;
            var baseUrl = BaseUrlBox.Text?.Trim();
            var apiKey = ApiKeyBox.Password;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                StatusText.Text = Strings.Get("Settings_EnterBaseUrl");
                BaseUrlBox.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                StatusText.Text = Strings.Get("Settings_EnterApiKey");
                ApiKeyBox.Focus();
                return;
            }

            var protocol = Plugin.ConfigStore.Current.CurrentProvider.Protocol;
            if (ProtocolCombo.SelectedItem is ComboBoxItem item
                && Enum.TryParse<AgentProtocol>(item.Tag?.ToString(), out var selected))
                protocol = selected;

            var enteredModel = ModelCombo.Text;
            var request = new CancellationTokenSource();
            _modelFetchCancellation = request;
            FetchModelsButton.IsEnabled = false;
            StatusText.Text = Strings.Get("Settings_FetchingModels");
            try
            {
                var models = await Plugin.FetchModelsAsync(protocol, baseUrl, apiKey, request.Token);
                if (models.Count == 0)
                {
                    StatusText.Text = Strings.Get("Settings_NoModels");
                    return;
                }

                var list = new List<string>(models);
                Plugin.ConfigStore.Current.CurrentProvider.Models = list;
                ModelCombo.ItemsSource = list;
                ModelCombo.Text = string.IsNullOrWhiteSpace(enteredModel)
                    ? list[0] : enteredModel;
                ModelCombo.IsDropDownOpen = true;
                StatusText.Text = Strings.Get("Settings_ModelsLoaded", list.Count);
            }
            catch (OperationCanceledException)
            {
                // 离开设置页时中止请求，不更改已输入的模型和已有列表。
            }
            catch (Exception ex)
            {
                StatusText.Text = Strings.Get("Settings_FetchFailed", ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_modelFetchCancellation, request))
                    _modelFetchCancellation = null;
                request.Dispose();
                FetchModelsButton.IsEnabled = true;
            }
        }

        private void CopyPromptButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is string key)) return;
            try
            {
                Clipboard.SetText(Strings.Get("Settings_" + key));
                StatusText.Text = Strings.Get("Settings_CopyPrompt");
            }
            catch (Exception ex)
            {
                StatusText.Text = Strings.Get("Window_Failed", ex.Message);
            }
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            StatusText.Text = Strings.Get("Window_Streaming");
            var result = await Plugin.TestConnectionAsync();
            StatusText.Text = result.Success ? result.Message : Strings.Get("Window_Failed", result.Message);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveSettings();

        private void VoiceCard_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded || Plugin?.ConfigStore == null) return;
            _ = Plugin.SetVoiceWakeWordAsync(VoiceCard.IsOn);
        }

        private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded || Plugin?.ConfigStore?.Current?.CurrentProvider == null) return;
            if (ProtocolCombo.SelectedItem is ComboBoxItem item
                && Enum.TryParse<AgentProtocol>(item.Tag?.ToString(), out var protocol))
            {
                Plugin.ConfigStore.Current.CurrentProvider.Protocol = protocol;
            }
        }

        private void ConfirmationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded || Plugin?.ConfigStore == null) return;
            if (ConfirmationCombo.SelectedItem is ComboBoxItem item
                && Enum.TryParse<ToolConfirmationMode>(item.Tag?.ToString(), out var mode))
            {
                Plugin.ConfigStore.Current.ConfirmationMode = mode;
            }
        }

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            if (combo == null) return;
            foreach (var item in combo.Items)
            {
                if (item is ComboBoxItem comboItem
                    && string.Equals(comboItem.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = comboItem;
                    return;
                }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }
    }
}
