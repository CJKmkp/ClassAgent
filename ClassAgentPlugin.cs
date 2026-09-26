using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ClassAgent.Agent;
using ClassAgent.Config;
using ClassAgent.Resources;
using ClassAgent.Views;
using ClassAgent.Voice;
using ClassAgent.Whiteboard;
using Ink_Canvas.Plugins;
using iNKORE.UI.WPF.Modern.Controls;
using SegoeFluentIcons = iNKORE.UI.WPF.Modern.Common.IconKeys.SegoeFluentIcons;
using Microsoft.Extensions.DependencyInjection;

namespace ClassAgent
{
    /// <summary>
    /// 独立的课堂 AI 插件入口。宿主能力通过 Plugin SDK 获取，模型配置与日志由插件自管。
    /// </summary>
    [PluginEntrance]
    public sealed class ClassAgentPlugin : PluginBase
    {
        private IThemeService _theme;
        private IEventService _events;
        private ICanvasInkService _canvas;
        private IWindowService _window;
        private IWhiteboardDocumentService _whiteboard;
        private SmartWhiteboardService _smartWhiteboard;
        private ICanvasElementService _canvasElements;
        private IInkTextService _inkText;
        private IScreenshotService _screenshot;
        private IScreenElementService _screenElements;
        private ICanvasCoordinateService _coordinates;
        private IPowerPointService _powerPoint;
        private IHotkeyService _hotkeys;
        private INotificationService _notifications;
        private CancellationTokenSource _requestCancellation;
        private SettingsView _settingsView;
        private readonly AgentOrchestrator _orchestrator = new AgentOrchestrator();
        private AgentToolRegistry _toolRegistry;
        private IWakeWordProvider _wakeWord;

        public AgentConfigStore ConfigStore { get; private set; }
        public FloatingBallWindow FloatingBall { get; private set; }
        public AgentWindow AgentWindow { get; private set; }

        public override string Id => "com.icc.class-agent";
        public override string Name => Strings.Get("Plugin_Name");
        public override string Version => Manifest?.Version ?? "0.1.0";
        public override string Author => "ICC-CE";
        public override string Description => Strings.Get("Plugin_Description");

        public IServiceProvider Services => Host?.ServiceProvider;

        public override void Initialize(IPluginHost host, IServiceCollection services)
        {
            base.Initialize(host, services);
            Log("ClassAgent initializing...");

            _theme = host.GetService<IThemeService>();
            _events = host.GetService<IEventService>();
            _canvas = host.GetService<ICanvasInkService>();
            _window = host.GetService<IWindowService>();
            _whiteboard = host.GetService<IWhiteboardDocumentService>();
            _canvasElements = host.GetService<ICanvasElementService>();
            _inkText = host.GetService<IInkTextService>();
            _screenshot = host.GetService<IScreenshotService>();
            _screenElements = host.GetService<IScreenElementService>();
            _coordinates = host.GetService<ICanvasCoordinateService>();
            _powerPoint = host.GetService<IPowerPointService>();
            _hotkeys = host.GetService<IHotkeyService>();
            _notifications = host.GetService<INotificationService>();
            _wakeWord = new WindowsSpeechWakeWordProvider();
            _wakeWord.WakeWordDetected += OnWakeWordDetected;
            _wakeWord.Error += OnWakeWordError;

            ConfigStore = new AgentConfigStore(PluginConfigFolder);
            ConfigStore.Load();
            _smartWhiteboard = new SmartWhiteboardService(
                () => _canvas?.CurrentWhiteboardPage ?? 0);
            _whiteboard?.RegisterPageStateProvider(Id, _smartWhiteboard);
            _toolRegistry = AgentToolRegistry.CreateDefault();
            CreateWindows();
            RegisterToolbar(host);
            RegisterHotkey();
            SubscribeHostEvents();
            if (ConfigStore.Current.EnableVoiceWakeWord)
                _ = SetVoiceWakeWordAsync(true);
            Log("ClassAgent initialized.");
        }

        public override void Shutdown()
        {
            try
            {
                UnsubscribeHostEvents();
                _whiteboard?.UnregisterPageStateProvider(Id);
                _hotkeys?.Unregister("classagent.open");
                _requestCancellation?.Cancel();
                _requestCancellation?.Dispose();
                _requestCancellation = null;
                _wakeWord?.Dispose();
                _wakeWord = null;
                try { AgentWindow?.PrepareForShutdown(); AgentWindow?.Close(); } catch { }
                try { FloatingBall?.Close(); } catch { }
                ConfigStore?.Save();
            }
            catch (Exception ex)
            {
                LogError("ClassAgent shutdown failed.", ex);
            }
            finally
            {
                AgentWindow = null;
                FloatingBall = null;
                base.Shutdown();
            }
        }

        public override object GetMainView() => null;

        public override object GetSettingsView()
        {
            if (_settingsView == null)
                _settingsView = new SettingsView { Plugin = this };
            return _settingsView;
        }

        public void ShowAgentWindow(bool activate = true)
        {
            if (AgentWindow == null) return;
            if (!AgentWindow.IsVisible) AgentWindow.Show();
            if (activate)
            {
                AgentWindow.Activate();
                AgentWindow.FocusInput();
            }
            _theme?.ApplyThemeToElement(AgentWindow);
        }

        public void HideAgentWindow() => AgentWindow?.Hide();

        public async void ToggleVoiceWakeWord()
        {
            await SetVoiceWakeWordAsync(!ConfigStore.Current.EnableVoiceWakeWord).ConfigureAwait(true);
        }

        public async Task SetVoiceWakeWordAsync(bool enabled)
        {
            ConfigStore.Current.EnableVoiceWakeWord = enabled;
            ConfigStore.Save();
            if (_wakeWord == null)
            {
                Notify(Strings.Get("Voice_Unavailable"), NotificationLevel.Warning);
                return;
            }
            try
            {
                if (enabled)
                {
                    await _wakeWord.StartAsync(ConfigStore.Current.WakeWord).ConfigureAwait(true);
                    Notify(Strings.Get("Voice_Listening"));
                }
                else
                {
                    await _wakeWord.StopAsync().ConfigureAwait(true);
                    Notify(Strings.Get("Voice_Off"));
                }
            }
            catch (Exception ex)
            {
                ConfigStore.Current.EnableVoiceWakeWord = false;
                ConfigStore.Save();
                LogError("Windows speech wake word failed.", ex);
                Notify(Strings.Get("Voice_Unavailable"), NotificationLevel.Warning);
            }
        }

        private void OnWakeWordDetected(object sender, WakeWordDetectedEventArgs e)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                ShowAgentWindow();
                Notify(e.Phrase);
            }));
        }

        private void OnWakeWordError(object sender, string message)
        {
            LogError("Windows speech recognition: " + message);
        }

        public async Task SendPromptAsync(string prompt, bool includeScreen,
            Action<string> onText, Action<string> onThinking,
            Action<string> onTool, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return;
            var provider = ConfigStore.GetCurrentProvider();
            var apiKey = ConfigStore.GetApiKey();
            if (provider == null || string.IsNullOrWhiteSpace(apiKey))
            {
                Notify(Strings.Get("Window_NoProvider"), NotificationLevel.Warning);
                return;
            }

            var client = CreateClient(provider, apiKey);
            if (client == null) return;
            var parts = new List<AgentContentPart> { AgentContentPart.TextPart(prompt) };
            if (includeScreen)
            {
                var image = CaptureScreenWithoutAgentWindows();
                if (image != null) parts.Add(AgentContentPart.ImagePart(ToPngDataUrl(image)));
            }

            _requestCancellation?.Cancel();
            _requestCancellation?.Dispose();
            _requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var context = new AgentToolContext
            {
                CanvasInk = _canvas,
                CanvasElements = _canvasElements,
                Screenshot = _screenshot,
                ScreenElements = _screenElements,
                Coordinates = _coordinates,
                InkText = _inkText,
                Window = _window,
                Whiteboard = _whiteboard,
                PowerPoint = _powerPoint,
                ConfirmMutationAsync = ConfirmMutationAsync,
                Log = Log
            };
            var run = await _orchestrator.RunAsync(client, ConfigStore.Current,
                AgentMessage.User(parts), _toolRegistry, context,
                _requestCancellation.Token, onText, onThinking, onTool).ConfigureAwait(false);
            ConfigStore.AddHistory(prompt, run.Text, includeScreen);
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync()
        {
            try
            {
                var provider = ConfigStore.GetCurrentProvider();
                var key = ConfigStore.GetApiKey();
                if (provider == null || string.IsNullOrWhiteSpace(key))
                    return (false, Strings.Get("Window_NoProvider"));
                var client = CreateClient(provider, key);
                if (client == null) return (false, Strings.Get("Window_NoProvider"));
                var result = await client.CompleteAsync(
                    new[] { AgentMessage.User("请用一句话说明你已连接成功。") },
                    ConfigStore.Current.SystemPrompt,
                    Array.Empty<AgentToolDefinition>(), CancellationToken.None).ConfigureAwait(false);
                return (true, string.IsNullOrWhiteSpace(result.Text) ? Strings.Get("Settings_Saved") : result.Text);
            }
            catch (Exception ex)
            {
                LogError("Connection test failed.", ex);
                return (false, ex.Message);
            }
        }

        public async Task<IReadOnlyList<string>> FetchModelsAsync(
            AgentProtocol protocol, string baseUrl, string apiKey,
            CancellationToken cancellationToken)
        {
            var provider = new ProviderConfig
            {
                Protocol = protocol,
                BaseUrl = baseUrl?.Trim() ?? "",
                Model = ""
            };
            var client = CreateClient(provider, apiKey);
            return await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }

        public void SaveConfiguration() => ConfigStore?.Save();

        public Task<bool> TransferScreenToWhiteboardAsync()
        {
            var image = CaptureScreenWithoutAgentWindows();
            if (image == null || _canvas == null || _window == null)
                return Task.FromResult(false);
            _window.EnterWhiteboard();
            var occupied = (_canvas.GetStrokes()?.Count ?? 0) > 0
                || (_canvasElements?.GetElements()?.Count ?? 0) > 0;
            if (occupied) _canvas.AddWhiteboardPage();
            return Task.FromResult(_canvas.InsertBitmap(image));
        }

        internal IThemeService ThemeService => _theme;
        internal ICanvasInkService CanvasService => _canvas;
        internal IWindowService WindowService => _window;
        internal IInkTextService InkTextService => _inkText;
        internal IScreenElementService ScreenElementService => _screenElements;
        internal ICanvasCoordinateService CoordinateService => _coordinates;

        private IAgentCompletionClient CreateClient(ProviderConfig provider, string apiKey)
        {
            if (provider == null) return null;
            return provider.Protocol == AgentProtocol.Anthropic
                ? new AnthropicClient(provider.BaseUrl, apiKey, provider.Model, ConfigStore.Current.MaxTokens)
                : new OpenAiCompatibleClient(provider.BaseUrl, apiKey, provider.Model,
                    ConfigStore.Current.Temperature, ConfigStore.Current.MaxTokens);
        }

        private void CreateWindows()
        {
            AgentWindow = new AgentWindow { Plugin = this, ShowInTaskbar = true };
            AgentWindow.Hide();
            FloatingBall = new FloatingBallWindow { Plugin = this };
            FloatingBall.Loaded += (_, __) => _theme?.ApplyThemeToElement(FloatingBall);
            FloatingBall.Show();
        }

        private void RegisterToolbar(IPluginHost host)
        {
            host.RegisterToolbarItem(new PluginToolbarItemInfo
            {
                Id = "classagent.open",
                DisplayName = Strings.Get("Toolbar_Name"),
                Description = Strings.Get("Toolbar_Description"),
                IconGeometry = "",
                ViewFactory = () =>
                {
                    var content = new StackPanel { Orientation = Orientation.Horizontal };
                    content.Children.Add(new FontIcon
                    {
                        Icon = SegoeFluentIcons.Message,
                        Margin = new Thickness(0, 0, 5, 0)
                    });
                    content.Children.Add(new TextBlock
                    {
                        Text = Strings.Get("Toolbar_Name"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    return new Button
                    {
                        Content = content,
                        MinWidth = 72,
                        MinHeight = 32,
                        ToolTip = Strings.Get("Toolbar_Description"),
                        Command = new SimpleCommand(() => ShowAgentWindow())
                    };
                },
                PopupContentFactory = () => new AgentPaletteView(this)
            });
        }

        private void RegisterHotkey()
        {
            _hotkeys?.Register("classagent.open", 2 | 1, 0x41, () =>
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => ShowAgentWindow())));
        }

        private void SubscribeHostEvents()
        {
            if (_events == null) return;
            _events.AppExiting += OnAppExiting;
            _events.TopMostChanged += OnTopMostChanged;
        }

        private void UnsubscribeHostEvents()
        {
            if (_events == null) return;
            _events.AppExiting -= OnAppExiting;
            _events.TopMostChanged -= OnTopMostChanged;
        }

        private void OnAppExiting()
        {
            try { ConfigStore?.Save(); } catch { }
            try { AgentWindow?.PrepareForShutdown(); AgentWindow?.Close(); } catch { }
            try { FloatingBall?.Close(); } catch { }
        }

        private void OnTopMostChanged(bool topMost)
        {
            if (AgentWindow != null) AgentWindow.Topmost = topMost;
            if (FloatingBall != null) FloatingBall.Topmost = topMost;
        }

        private async Task<bool> ConfirmMutationAsync(string title, int count)
        {
            if (ConfigStore.Current.ConfirmationMode == ToolConfirmationMode.Automatic)
                return true;
            if (AgentWindow == null) return false;
            return await AgentWindow.ConfirmMutationAsync(title, count).ConfigureAwait(false);
        }

        private BitmapSource CaptureScreenWithoutAgentWindows()
        {
            var agentVisibility = AgentWindow?.Visibility ?? Visibility.Hidden;
            var ballVisibility = FloatingBall?.Visibility ?? Visibility.Hidden;
            try
            {
                if (AgentWindow != null) AgentWindow.Hide();
                if (FloatingBall != null) FloatingBall.Hide();
                return _screenshot?.CaptureFullScreen();
            }
            catch (Exception ex)
            {
                LogError("Screen capture failed.", ex);
                return null;
            }
            finally
            {
                if (FloatingBall != null && ballVisibility == Visibility.Visible) FloatingBall.Show();
                if (AgentWindow != null && agentVisibility == Visibility.Visible) AgentWindow.Show();
            }
        }

        private static string ToPngDataUrl(BitmapSource source)
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using var stream = new System.IO.MemoryStream();
            encoder.Save(stream);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }

        private void Notify(string message, NotificationLevel level = NotificationLevel.Info)
        {
            try { _notifications?.Show(Name, message, level); } catch { }
        }

        private sealed class SimpleCommand : System.Windows.Input.ICommand
        {
            private readonly Action _action;
            public SimpleCommand(Action action) => _action = action;
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => _action?.Invoke();
            event EventHandler System.Windows.Input.ICommand.CanExecuteChanged
            {
                add { }
                remove { }
            }
        }
    }
}
