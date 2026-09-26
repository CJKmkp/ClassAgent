using System.Windows;
using System.Windows.Controls;
using Ink_Canvas.Plugins;

namespace ClassAgent.Views
{
    public partial class AgentPaletteView : UserControl
    {
        private readonly ClassAgentPlugin _plugin;

        public AgentPaletteView(ClassAgentPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
        }

        private void PenButton_Click(object sender, RoutedEventArgs e) => Select(PluginInkTool.Pen);
        private void SelectButton_Click(object sender, RoutedEventArgs e) => Select(PluginInkTool.Select);
        private void EraserButton_Click(object sender, RoutedEventArgs e) => Select(PluginInkTool.Eraser);
        private void ShapeButton_Click(object sender, RoutedEventArgs e) => Select(PluginInkTool.Shape);
        private void RoamingButton_Click(object sender, RoutedEventArgs e) => Select(PluginInkTool.Roaming);
        private void OpenButton_Click(object sender, RoutedEventArgs e) => _plugin?.ShowAgentWindow();

        private void Select(PluginInkTool tool)
        {
            try { _plugin?.CanvasService?.SelectTool(tool); } catch { }
        }
    }
}
