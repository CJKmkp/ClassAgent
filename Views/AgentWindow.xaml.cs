using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassAgent.Config;
using ClassAgent.Resources;
using ModernMessageBox = iNKORE.UI.WPF.Modern.Controls.MessageBox;
using NavigationView = iNKORE.UI.WPF.Modern.Controls.NavigationView;
using NavigationViewSelectionChangedEventArgs = iNKORE.UI.WPF.Modern.Controls.NavigationViewSelectionChangedEventArgs;

namespace ClassAgent.Views
{
    public partial class AgentWindow : Window
    {
        private readonly ObservableCollection<ChatLine> _lines = new ObservableCollection<ChatLine>();
        private CancellationTokenSource _sendCancellation;
        private bool _allowClose;
        private ChatLine _assistantLine;

        public ClassAgentPlugin Plugin { get; set; }

        public AgentWindow()
        {
            InitializeComponent();
            MessageList.ItemsSource = _lines;
            Loaded += AgentWindow_Loaded;
        }

        private void AgentWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (Plugin != null)
            {
                ModelText.Text = Plugin.ConfigStore?.Current?.CurrentProvider?.Model ?? "";
                SettingsContent.Content = Plugin.GetSettingsView();
                HistoryList.ItemsSource = Plugin.ConfigStore?.Current?.History;
                UpdateHistoryEmptyState();
            }
            NavigationViewRoot.SelectedItem = ChatNavigationItem;
        }

        public void FocusInput()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                InputBox.Focus();
                Keyboard.Focus(InputBox);
            }));
        }

        public async Task<bool> ConfirmMutationAsync(string title, int count)
        {
            if (!Dispatcher.CheckAccess())
                return await Dispatcher.InvokeAsync(() => ConfirmMutationAsync(title, count))
                    .Task.Unwrap().ConfigureAwait(false);
            if (!IsVisible) Show();
            var message = Strings.Get("Window_ConfirmAnnotation", count);
            var result = await Dispatcher.InvokeAsync(() =>
                ModernMessageBox.Show(this, message, title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question));
            return result == MessageBoxResult.Yes;
        }

        public void PrepareForShutdown() => _allowClose = true;

        private void NavigationViewRoot_SelectionChanged(NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            ChatPage.Visibility = Visibility.Collapsed;
            HistoryPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            if (args.SelectedItem == HistoryNavigationItem) HistoryPage.Visibility = Visibility.Visible;
            else if (args.SelectedItem == SettingsNavigationItem) SettingsPage.Visibility = Visibility.Visible;
            else ChatPage.Visibility = Visibility.Visible;
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
            => await SendCurrentPromptAsync().ConfigureAwait(true);

        private async Task SendCurrentPromptAsync()
        {
            var prompt = InputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(prompt) || Plugin == null) return;
            InputBox.Clear();
            AddLine(Strings.Get("Window_Send"), prompt, true);
            _assistantLine = new ChatLine { Label = Strings.Get("Plugin_Name"), IsUser = false };
            _lines.Add(_assistantLine);
            ScrollMessagesToEnd();

            _sendCancellation?.Cancel();
            _sendCancellation?.Dispose();
            _sendCancellation = new CancellationTokenSource();
            InputBox.IsEnabled = false;
            SendButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            SetStatus(Strings.Get("Window_Streaming"));
            try
            {
                await Plugin.SendPromptAsync(prompt, IncludeScreenCheckBox.IsChecked == true,
                    AppendAssistantText,
                    AppendAssistantThinking,
                    SetStatus,
                    _sendCancellation.Token).ConfigureAwait(true);
                SetStatus(Strings.Get("Window_Ready"));
            }
            catch (OperationCanceledException)
            {
                SetStatus(Strings.Get("Window_Ready"));
            }
            catch (Exception ex)
            {
                AppendAssistantText(Strings.Get("Window_Failed", ex.Message));
                SetStatus(Strings.Get("Window_Ready"));
            }
            finally
            {
                RefreshHistory();
                InputBox.IsEnabled = true;
                StopButton.Visibility = Visibility.Collapsed;
                SendButton.Visibility = Visibility.Visible;
            }
        }

        private void AppendAssistantText(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_assistantLine == null) return;
                _assistantLine.Text += value;
                ScrollMessagesToEnd();
            }));
        }

        private void AppendAssistantThinking(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_assistantLine == null) return;
                _assistantLine.Thinking += value;
                ScrollMessagesToEnd();
            }));
        }

        private void SetStatus(string value)
            => Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusInfoBar.Message = value ?? "";
                StatusInfoBar.IsOpen = !string.IsNullOrWhiteSpace(value)
                    && !string.Equals(value, Strings.Get("Window_Ready"), StringComparison.Ordinal);
            }));

        private void AddLine(string label, string text, bool isUser)
        {
            _lines.Add(new ChatLine { Label = label, Text = text ?? "", IsUser = isUser });
            ScrollMessagesToEnd();
        }

        private void ScrollMessagesToEnd()
            => Dispatcher.BeginInvoke(new Action(() => MessagesScroll.ScrollToEnd()));

        private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(HistoryList.SelectedItem is ChatHistoryEntry entry)) return;
            _lines.Clear();
            _assistantLine = null;
            AddLine(Strings.Get("Window_Send"), entry.Prompt, true);
            if (!string.IsNullOrWhiteSpace(entry.Response))
                AddLine(Strings.Get("Plugin_Name"), entry.Response, false);
            HistoryList.SelectedItem = null;
            NavigationViewRoot.SelectedItem = ChatNavigationItem;
        }

        private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            Plugin?.ConfigStore?.ClearHistory();
            RefreshHistory();
        }

        private void RefreshHistory()
        {
            if (Plugin?.ConfigStore == null || HistoryList == null) return;
            HistoryList.ItemsSource = null;
            HistoryList.ItemsSource = Plugin.ConfigStore.Current.History;
            UpdateHistoryEmptyState();
        }

        private void UpdateHistoryEmptyState()
        {
            if (HistoryEmptyText == null || HistoryList == null) return;
            var empty = HistoryList.Items.Count == 0;
            HistoryEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            HistoryList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
            => _sendCancellation?.Cancel();

        private async void TransferToWhiteboardButton_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null) return;
            var success = await Plugin.TransferScreenToWhiteboardAsync().ConfigureAwait(true);
            SetStatus(success ? Strings.Get("Window_Inserted") : Strings.Get("Window_TransferFailed"));
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _lines.Clear();
            _assistantLine = null;
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                _ = SendCurrentPromptAsync();
            }
        }

        private async void CaptureExplainButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationViewRoot.SelectedItem = ChatNavigationItem;
            IncludeScreenCheckBox.IsChecked = true;
            InputBox.Text = "请根据当前屏幕内容讲解题目，给出清晰的解题步骤。";
            await SendCurrentPromptAsync().ConfigureAwait(true);
        }

        private async void AnnotateButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationViewRoot.SelectedItem = ChatNavigationItem;
            IncludeScreenCheckBox.IsChecked = true;
            InputBox.Text = "请识别当前屏幕中的重点，并生成安全的结构化批注计划；执行前先让我确认。";
            await SendCurrentPromptAsync().ConfigureAwait(true);
        }

        private void EnterWhiteboardButton_Click(object sender, RoutedEventArgs e)
            => Plugin?.WindowService?.EnterWhiteboard();

        private async void WriteBoardButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationViewRoot.SelectedItem = ChatNavigationItem;
            InputBox.Text = "请把当前讲解整理成适合课堂板书的标题、步骤和结论，并以墨迹写入画布。";
            await SendCurrentPromptAsync().ConfigureAwait(true);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        }

        private sealed class ChatLine : INotifyPropertyChanged
        {
            private string _text = "";
            private string _thinking = "";
            public string Label { get; set; } = "";
            public bool IsUser { get; set; }
            public string Text
            {
                get => _text;
                set { _text = value ?? ""; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
            }
            public string Thinking
            {
                get => _thinking;
                set { _thinking = value ?? ""; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thinking))); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
