using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClassAgent.Agent
{
    internal sealed class SseEvent
    {
        public string Event { get; set; } = "";
        public string Data { get; set; } = "";
        public bool IsDone => Data == "[DONE]";
    }

    internal sealed class SseReader : IDisposable
    {
        private readonly StreamReader _reader;
        private bool _disposed;

        public SseReader(Stream stream)
        {
            _reader = new StreamReader(stream, Encoding.UTF8);
        }

        public async Task<SseEvent> ReadEventAsync(CancellationToken cancellationToken)
        {
            string eventName = "";
            string data = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    return data == null ? null : new SseEvent { Event = eventName, Data = data };
                }

                if (line.Length == 0)
                {
                    if (data != null)
                    {
                        var result = new SseEvent { Event = eventName, Data = data };
                        eventName = "";
                        data = null;
                        return result;
                    }
                    continue;
                }

                if (line.StartsWith(":", StringComparison.Ordinal)) continue;
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = TrimField(line, 6);
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var part = TrimField(line, 5);
                    data = data == null ? part : data + "\n" + part;
                }
            }
        }

        private static string TrimField(string line, int prefixLength)
        {
            var value = line.Length > prefixLength ? line.Substring(prefixLength) : "";
            return value.StartsWith(" ", StringComparison.Ordinal) ? value.Substring(1) : value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reader.Dispose();
        }
    }
}
