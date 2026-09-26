using System;
using System.Globalization;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;

namespace ClassAgent.Voice
{
    public sealed class WakeWordDetectedEventArgs : EventArgs
    {
        public string Phrase { get; }
        public float Confidence { get; }

        public WakeWordDetectedEventArgs(string phrase, float confidence)
        {
            Phrase = phrase ?? "";
            Confidence = confidence;
        }
    }

    public interface IWakeWordProvider : IDisposable
    {
        bool IsListening { get; }
        event EventHandler<WakeWordDetectedEventArgs> WakeWordDetected;
        event EventHandler<string> Error;
        Task StartAsync(string phrase, CancellationToken cancellationToken = default);
        Task StopAsync();
    }

    /// <summary>
    /// 基于 Windows SAPI 的短语唤醒。仅加载用户指定的短语，不进行后台自由语音转写。
    /// </summary>
    internal sealed class WindowsSpeechWakeWordProvider : IWakeWordProvider
    {
        private readonly object _gate = new object();
        private SpeechRecognitionEngine _engine;
        private CancellationTokenRegistration _cancellation;
        private bool _disposed;

        public bool IsListening
        {
            get { lock (_gate) return _engine != null; }
        }

        public event EventHandler<WakeWordDetectedEventArgs> WakeWordDetected;
        public event EventHandler<string> Error;

        public Task StartAsync(string phrase, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(phrase))
                throw new ArgumentException("Wake phrase is empty.", nameof(phrase));

            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WindowsSpeechWakeWordProvider));
                if (_engine != null) return Task.CompletedTask;
                try
                {
                    var culture = CultureInfo.CurrentUICulture;
                    SpeechRecognitionEngine engine;
                    try { engine = new SpeechRecognitionEngine(culture); }
                    catch { engine = new SpeechRecognitionEngine(); }

                    var choices = new Choices(phrase.Trim());
                    var builder = new GrammarBuilder { Culture = engine.RecognizerInfo.Culture };
                    builder.Append(choices);
                    var grammar = new Grammar(builder);
                    engine.LoadGrammar(grammar);
                    engine.SpeechRecognized += Engine_SpeechRecognized;
                    engine.RecognizeCompleted += Engine_RecognizeCompleted;
                    engine.SetInputToDefaultAudioDevice();
                    engine.RecognizeAsync(RecognizeMode.Multiple);
                    _engine = engine;
                    _cancellation = cancellationToken.Register(() => _ = StopAsync());
                }
                catch (Exception ex)
                {
                    Error?.Invoke(this, ex.Message);
                    throw;
                }
            }
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            SpeechRecognitionEngine engine;
            lock (_gate)
            {
                engine = _engine;
                _engine = null;
                _cancellation.Dispose();
                _cancellation = default;
            }
            if (engine == null) return Task.CompletedTask;
            try { engine.RecognizeAsyncCancel(); } catch { }
            try { engine.RecognizeAsyncStop(); } catch { }
            engine.SpeechRecognized -= Engine_SpeechRecognized;
            engine.RecognizeCompleted -= Engine_RecognizeCompleted;
            engine.Dispose();
            return Task.CompletedTask;
        }

        private void Engine_SpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            if (e.Result == null || e.Result.Confidence < 0.55f) return;
            WakeWordDetected?.Invoke(this,
                new WakeWordDetectedEventArgs(e.Result.Text, e.Result.Confidence));
        }

        private void Engine_RecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
        {
            if (e.Error != null) Error?.Invoke(this, e.Error.Message);
        }

        public void Dispose()
        {
            lock (_gate) _disposed = true;
            StopAsync().GetAwaiter().GetResult();
        }
    }
}
