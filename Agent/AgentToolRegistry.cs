using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ClassAgent.Screen;
using Ink_Canvas.Plugins;

namespace ClassAgent.Agent
{
    internal sealed class AgentToolContext
    {
        public ICanvasInkService CanvasInk { get; set; }
        public ICanvasElementService CanvasElements { get; set; }
        public IScreenshotService Screenshot { get; set; }
        public IScreenElementService ScreenElements { get; set; }
        public ICanvasCoordinateService Coordinates { get; set; }
        public IInkTextService InkText { get; set; }
        public IWindowService Window { get; set; }
        public IWhiteboardDocumentService Whiteboard { get; set; }
        public IPowerPointService PowerPoint { get; set; }
        public Func<string, int, Task<bool>> ConfirmMutationAsync { get; set; }
        public Action<string> Log { get; set; }
    }

    internal sealed class AgentToolResult
    {
        public bool Success { get; set; }
        public bool Mutated { get; set; }
        public string Content { get; set; } = "";
        public List<AgentContentPart> Parts { get; } = new List<AgentContentPart>();

        public static AgentToolResult Ok(string content)
            => new AgentToolResult { Success = true, Content = content ?? "" };

        public static AgentToolResult Fail(string content)
            => new AgentToolResult { Success = false, Content = content ?? "" };
    }

    internal sealed class AgentToolRegistry
    {
        private static readonly JsonSerializerOptions AnnotationJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        static AgentToolRegistry()
        {
            AnnotationJsonOptions.Converters.Add(new JsonStringEnumConverter());
        }
        private sealed class Entry
        {
            public AgentToolDefinition Definition { get; set; }
            public Func<JsonElement, AgentToolContext, CancellationToken, Task<AgentToolResult>> Handler { get; set; }
        }

        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<AgentToolDefinition> Definitions
            => _entries.Values.Select(x => x.Definition).ToList().AsReadOnly();

        public void Register(string name, string description, string schema,
            bool mutatesCanvas,
            Func<JsonElement, AgentToolContext, CancellationToken, Task<AgentToolResult>> handler)
        {
            if (string.IsNullOrWhiteSpace(name) || handler == null) return;
            using var document = JsonDocument.Parse(schema ?? "{\"type\":\"object\"}");
            _entries[name] = new Entry
            {
                Definition = new AgentToolDefinition
                {
                    Name = name,
                    Description = description ?? "",
                    Parameters = document.RootElement.Clone(),
                    MutatesCanvas = mutatesCanvas
                },
                Handler = handler
            };
        }

        public async Task<AgentToolResult> ExecuteAsync(string name, string arguments,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            if (!_entries.TryGetValue(name ?? "", out var entry))
                return AgentToolResult.Fail("未知工具");

            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments)
                    ? "{}" : arguments);
                return await entry.Handler(document.RootElement, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return AgentToolResult.Fail("工具参数不是有效 JSON：" + ex.Message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                context?.Log?.Invoke($"Tool {name} failed: {ex.Message}");
                return AgentToolResult.Fail("工具执行失败：" + ex.Message);
            }
        }

        public static AgentToolRegistry CreateDefault()
        {
            var registry = new AgentToolRegistry();
            registry.Register("capture_screen", "Capture the full screen or a specified screen-pixel rectangle.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"width\":{\"type\":\"number\"},\"height\":{\"type\":\"number\"}}}",
                false, CaptureScreenAsync);
            registry.Register("inspect_screen_elements", "Read UI Automation element names and screen bounds.",
                "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}}}",
                false, InspectElementsAsync);
            registry.Register("get_canvas_state", "Read the current canvas size, page, freeze and ink counts.",
                "{\"type\":\"object\"}", false, GetCanvasStateAsync);
            registry.Register("select_canvas_tool", "Select Pen, Select, Eraser, StrokeEraser, Shape or Roaming.",
                "{\"type\":\"object\",\"required\":[\"tool\"],\"properties\":{\"tool\":{\"type\":\"string\",\"enum\":[\"pen\",\"select\",\"eraser\",\"strokeEraser\",\"shape\",\"roaming\"]}}}",
                true, SelectCanvasToolAsync);
            registry.Register("write_text_as_ink", "Write a short explanation or board note as real canvas ink in SimSun (standard Songti).",
                "{\"type\":\"object\",\"required\":[\"text\"],\"properties\":{\"text\":{\"type\":\"string\",\"maxLength\":2000},\"fontSize\":{\"type\":\"number\"}}}",
                true, WriteTextAsInkAsync);
            registry.Register("draw_annotation_plan", "Draw validated rectangles, circles, arrows, highlights or underlines on the canvas.",
                "{\"type\":\"object\",\"required\":[\"coordinateSpace\",\"marks\"],\"properties\":{\"coordinateSpace\":{\"type\":\"string\"},\"marks\":{\"type\":\"array\"}}}",
                true, DrawAnnotationPlanAsync);
            registry.Register("undo_canvas", "Undo the last canvas operation.",
                "{\"type\":\"object\"}", true, UndoCanvasAsync);
            registry.Register("clear_canvas", "Clear all current canvas ink.",
                "{\"type\":\"object\"}", true, ClearCanvasAsync);
            registry.Register("whiteboard_enter", "Enter the ICC-CE smart whiteboard mode.",
                "{\"type\":\"object\"}", true, EnterWhiteboardAsync);
            registry.Register("whiteboard_next_page", "Create or move to the next whiteboard page.",
                "{\"type\":\"object\"}", true, WhiteboardNextPageAsync);
            registry.Register("whiteboard_previous_page", "Move to the previous whiteboard page.",
                "{\"type\":\"object\"}", true, WhiteboardPreviousPageAsync);
            registry.Register("transfer_screen_to_whiteboard", "Capture the current problem screen and insert it into a new whiteboard page.",
                "{\"type\":\"object\"}", true, TransferScreenToWhiteboardAsync);
            registry.Register("ppt_next", "Move the connected PowerPoint presentation to the next slide.",
                "{\"type\":\"object\"}", true, PptNextAsync);
            registry.Register("ppt_previous", "Move the connected PowerPoint presentation to the previous slide.",
                "{\"type\":\"object\"}", true, PptPreviousAsync);
            return registry;
        }

        private static Task<AgentToolResult> CaptureScreenAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Screenshot == null) return Task.FromResult(AgentToolResult.Fail("截图服务不可用"));
            BitmapSource bitmap;
            if (TryGetPositive(args, "width", out var width)
                && TryGetPositive(args, "height", out var height))
            {
                bitmap = context.Screenshot.CaptureScreenArea(GetInt(args, "x", 0),
                    GetInt(args, "y", 0), width, height);
            }
            else
            {
                bitmap = context.Screenshot.CaptureFullScreen();
            }
            if (bitmap == null) return Task.FromResult(AgentToolResult.Fail("截图失败"));

            var result = AgentToolResult.Ok(JsonSerializer.Serialize(new
            {
                width = bitmap.PixelWidth,
                height = bitmap.PixelHeight,
                format = "image/png"
            }));
            result.Parts.Add(AgentContentPart.ImagePart(ToPngDataUrl(bitmap)));
            return Task.FromResult(result);
        }

        private static Task<AgentToolResult> InspectElementsAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.ScreenElements == null) return Task.FromResult(AgentToolResult.Fail("UI Automation 服务不可用"));
            PluginScreenElementSnapshot snapshot;
            if (TryGetNumber(args, "x", out var x) && TryGetNumber(args, "y", out var y))
            {
                snapshot = context.ScreenElements.GetElementAtPoint(new Point(x, y));
            }
            else
            {
                snapshot = new PluginScreenElementSnapshot
                {
                    Element = context.ScreenElements.GetWindowTree()
                };
            }
            return Task.FromResult(snapshot?.Element == null
                ? AgentToolResult.Fail("没有读取到屏幕元素")
                : AgentToolResult.Ok(JsonSerializer.Serialize(snapshot)));
        }

        private static Task<AgentToolResult> GetCanvasStateAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.CanvasInk == null) return Task.FromResult(AgentToolResult.Fail("画布服务不可用"));
            var state = new
            {
                canvasWidth = context.CanvasInk.CanvasSize.Width,
                canvasHeight = context.CanvasInk.CanvasSize.Height,
                strokeCount = context.CanvasInk.GetStrokes()?.Count ?? 0,
                isPenMode = context.CanvasInk.IsPenMode,
                isPageFrozen = context.CanvasInk.IsPageFrozen,
                currentWhiteboardPage = context.CanvasInk.CurrentWhiteboardPage,
                whiteboardPageCount = context.CanvasInk.WhiteboardPageCount
            };
            return Task.FromResult(AgentToolResult.Ok(JsonSerializer.Serialize(state)));
        }

        private static async Task<AgentToolResult> SelectCanvasToolAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            if (!TryParseTool(GetString(args, "tool"), out var tool))
                return AgentToolResult.Fail("未知画布工具");
            if (!await ConfirmAsync(context, "切换画布工具", 0).ConfigureAwait(false))
                return AgentToolResult.Fail("用户取消了画布工具切换");
            return context.CanvasInk != null && context.CanvasInk.SelectTool(tool)
                ? new AgentToolResult { Success = true, Mutated = true, Content = "画布工具已切换" }
                : AgentToolResult.Fail("画布工具切换失败");
        }

        private static async Task<AgentToolResult> WriteTextAsInkAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            var text = GetString(args, "text");
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2000)
                return AgentToolResult.Fail("板书文字为空或过长");
            if (context.InkText == null) return AgentToolResult.Fail("文字墨迹服务不可用");
            if (!await ConfirmAsync(context, "写入板书文字", Math.Max(1, text.Length)).ConfigureAwait(false))
                return AgentToolResult.Fail("用户取消了板书写入");
            var options = new PluginInkTextOptions { FontFamily = "SimSun", FallbackFontFamilies = "SimSun,宋体" };
            if (TryGetNumber(args, "fontSize", out var fontSize)) options.FontSize = fontSize;
            var ok = context.InkText.TryInsertTextAsInk(text, null, options);
            return ok
                ? new AgentToolResult { Success = true, Mutated = true, Content = "板书已写入画布" }
                : AgentToolResult.Fail("板书写入失败（可能画布已冻结）");
        }

        private static async Task<AgentToolResult> DrawAnnotationPlanAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            if (context.CanvasInk == null || context.Coordinates == null)
                return AgentToolResult.Fail("批注所需画布服务不可用");
            var plan = JsonSerializer.Deserialize<AnnotationPlan>(args.GetRawText(), AnnotationJsonOptions);
            var renderer = new AnnotationStrokeRenderer(context.Coordinates);
            if (!renderer.TryRender(plan, context.Coordinates.CanvasScreenBounds,
                out var strokes, out var error))
                return AgentToolResult.Fail(error);
            if (!await ConfirmAsync(context, "写入自动批注", strokes.Count).ConfigureAwait(false))
                return AgentToolResult.Fail("用户取消了自动批注");
            var ok = context.CanvasInk.TryAddStrokes(strokes);
            return ok
                ? new AgentToolResult { Success = true, Mutated = true, Content = $"已写入 {strokes.Count} 笔批注" }
                : AgentToolResult.Fail("批注写入失败（可能画布已冻结）");
        }

        private static async Task<AgentToolResult> UndoCanvasAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            if (!await ConfirmAsync(context, "撤销画布操作", 0).ConfigureAwait(false))
                return AgentToolResult.Fail("用户取消了撤销");
            context.CanvasInk?.Undo();
            return new AgentToolResult { Success = true, Mutated = true, Content = "已撤销画布操作" };
        }

        private static async Task<AgentToolResult> ClearCanvasAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            if (context.CanvasInk == null) return AgentToolResult.Fail("画布服务不可用");
            var count = context.CanvasInk.GetStrokes()?.Count ?? 0;
            if (!await ConfirmAsync(context, "清空画布墨迹", count).ConfigureAwait(false))
                return AgentToolResult.Fail("用户取消了清空");
            return context.CanvasInk.TryClearStrokes()
                ? new AgentToolResult { Success = true, Mutated = true, Content = "画布已清空" }
                : AgentToolResult.Fail("清空失败（可能画布已冻结或本来为空）");
        }

        private static Task<AgentToolResult> EnterWhiteboardAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            if (context.Window == null) return Task.FromResult(AgentToolResult.Fail("窗口服务不可用"));
            context.Window.EnterWhiteboard();
            return Task.FromResult(new AgentToolResult
            {
                Success = true,
                Mutated = true,
                Content = "已进入智慧白板模式"
            });
        }

        private static Task<AgentToolResult> WhiteboardNextPageAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            if (context.CanvasInk == null) return Task.FromResult(AgentToolResult.Fail("画布服务不可用"));
            context.CanvasInk.SwitchToNextPage();
            return Task.FromResult(new AgentToolResult
            {
                Success = true,
                Mutated = true,
                Content = "已切换到下一白板页"
            });
        }

        private static Task<AgentToolResult> WhiteboardPreviousPageAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            if (context.CanvasInk == null) return Task.FromResult(AgentToolResult.Fail("画布服务不可用"));
            context.CanvasInk.SwitchToPreviousPage();
            return Task.FromResult(new AgentToolResult
            {
                Success = true,
                Mutated = true,
                Content = "已切换到上一白板页"
            });
        }

        private static async Task<AgentToolResult> TransferScreenToWhiteboardAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Screenshot == null || context.CanvasInk == null || context.Window == null)
                return AgentToolResult.Fail("截图或白板服务不可用");
            var image = context.Screenshot.CaptureFullScreen();
            if (image == null) return AgentToolResult.Fail("题目截图失败");
            if (!await ConfirmAsync(context, "迁移题目截图到白板", 1).ConfigureAwait(false))
                return AgentToolResult.Fail("用户取消了题目迁移");

            context.Window.EnterWhiteboard();
            var occupied = (context.CanvasInk.GetStrokes()?.Count ?? 0) > 0
                || (context.CanvasElements?.GetElements()?.Count ?? 0) > 0;
            if (occupied) context.CanvasInk.AddWhiteboardPage();
            return context.CanvasInk.InsertBitmap(image)
                ? new AgentToolResult { Success = true, Mutated = true, Content = "题目截图已迁移到白板" }
                : AgentToolResult.Fail("题目截图插入白板失败");
        }

        private static Task<AgentToolResult> PptNextAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
            => Task.FromResult(ExecutePpt(context, true));

        private static Task<AgentToolResult> PptPreviousAsync(JsonElement args,
            AgentToolContext context, CancellationToken cancellationToken)
            => Task.FromResult(ExecutePpt(context, false));

        private static AgentToolResult ExecutePpt(AgentToolContext context, bool next)
        {
            if (context.PowerPoint == null || !context.PowerPoint.IsSlideshowActive)
                return AgentToolResult.Fail("当前没有正在放映的 PPT");
            if (next) context.PowerPoint.NextSlide(); else context.PowerPoint.PreviousSlide();
            return new AgentToolResult { Success = true, Mutated = true, Content = "PPT 已翻页" };
        }

        private static async Task<bool> ConfirmAsync(AgentToolContext context, string title, int count)
            => context?.ConfirmMutationAsync != null
                && await context.ConfirmMutationAsync(title, count).ConfigureAwait(false);

        private static bool TryParseTool(string value, out PluginInkTool tool)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "pen": tool = PluginInkTool.Pen; return true;
                case "select": tool = PluginInkTool.Select; return true;
                case "eraser": tool = PluginInkTool.Eraser; return true;
                case "strokeeraser": tool = PluginInkTool.StrokeEraser; return true;
                case "shape": tool = PluginInkTool.Shape; return true;
                case "roaming": tool = PluginInkTool.Roaming; return true;
                default: tool = PluginInkTool.Pen; return false;
            }
        }

        private static string ToPngDataUrl(BitmapSource source)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }

        private static bool TryGetPositive(JsonElement args, string name, out int value)
        {
            value = GetInt(args, name, 0);
            return value > 0;
        }

        private static bool TryGetNumber(JsonElement args, string name, out double value)
        {
            value = 0;
            return args.TryGetProperty(name, out var element) && element.TryGetDouble(out value);
        }

        private static int GetInt(JsonElement args, string name, int fallback)
            => args.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
                ? result : fallback;

        private static string GetString(JsonElement args, string name)
            => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
    }
}
