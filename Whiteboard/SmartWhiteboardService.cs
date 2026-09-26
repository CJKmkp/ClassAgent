using System;
using System.Collections.Generic;
using System.Text.Json;
using Ink_Canvas.Plugins;

namespace ClassAgent.Whiteboard
{
    /// <summary>
    /// ClassAgent 在白板文档中的轻量结构化状态；真实墨迹仍由宿主白板历史负责。
    /// </summary>
    internal sealed class SmartWhiteboardService : IWhiteboardPageStateProvider, IWhiteboardInitialHistoryProvider
    {
        private readonly Dictionary<int, string> _pageStates = new Dictionary<int, string>();
        private readonly Func<int> _currentPage;

        public SmartWhiteboardService(Func<int> currentPage)
        {
            _currentPage = currentPage ?? (() => 0);
        }

        public string CaptureState()
        {
            var page = _currentPage();
            return _pageStates.TryGetValue(page, out var state) ? state : "{}";
        }

        public void RestoreState(string state)
        {
            var page = _currentPage();
            _pageStates[page] = string.IsNullOrWhiteSpace(state) ? "{}" : state;
        }

        public string CaptureEmptyState() => "{}";

        public void SetCurrentBoardNote(string note)
        {
            var page = _currentPage();
            _pageStates[page] = JsonSerializer.Serialize(new
            {
                note = note ?? "",
                updatedAt = DateTimeOffset.UtcNow
            });
        }
    }
}
