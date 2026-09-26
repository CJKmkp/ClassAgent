using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using ClassAgent.Resources;

namespace ClassAgent.Views
{
    public partial class FloatingBallWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExNoActivate = 0x08000000;
        private bool _mouseDown;
        private bool _dragging;
        private Point _dragStart;
        private Point _windowStart;

        public ClassAgentPlugin Plugin { get; set; }

        public FloatingBallWindow()
        {
            InitializeComponent();
            BuildContextMenu();
            Loaded += (_, __) => PlaceByWorkArea();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var style = GetWindowLong(hwnd, GwlExStyle);
                SetWindowLong(hwnd, GwlExStyle, style | WsExNoActivate);
            }
            catch { }
        }

        private void PlaceByWorkArea()
        {
            if (!double.IsNaN(Left) && !double.IsNaN(Top)) return;
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 18;
            Top = area.Top + (area.Height - Height) / 2;
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();
            var open = new MenuItem { Header = Strings.Get("Floating_Open") };
            open.Click += (_, __) => Plugin?.ShowAgentWindow();
            var voice = new MenuItem { Header = Strings.Get("Floating_Voice") };
            voice.Click += (_, __) => Plugin?.ToggleVoiceWakeWord();
            var hide = new MenuItem { Header = Strings.Get("Floating_Hide") };
            hide.Click += (_, __) => Hide();
            menu.Items.Add(open);
            menu.Items.Add(voice);
            menu.Items.Add(new Separator());
            menu.Items.Add(hide);
            RootBorder.ContextMenu = menu;
        }

        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _mouseDown = true;
            _dragging = false;
            _dragStart = PointToScreen(e.GetPosition(this));
            _windowStart = new Point(Left, Top);
            RootBorder.CaptureMouse();
            e.Handled = true;
        }

        private void RootBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown || e.LeftButton != MouseButtonState.Pressed) return;
            var current = PointToScreen(e.GetPosition(this));
            var dx = current.X - _dragStart.X;
            var dy = current.Y - _dragStart.Y;
            if (!_dragging && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)) _dragging = true;
            if (_dragging)
            {
                Left = _windowStart.X + dx;
                Top = _windowStart.Y + dy;
            }
        }

        private void RootBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (RootBorder.IsMouseCaptured) RootBorder.ReleaseMouseCapture();
            var wasDragging = _dragging;
            _mouseDown = false;
            _dragging = false;
            if (!wasDragging) Plugin?.ShowAgentWindow();
            else SnapToNearestEdge();
            e.Handled = true;
        }

        private void SnapToNearestEdge()
        {
            var area = SystemParameters.WorkArea;
            var center = new Point(Left + Width / 2, Top + Height / 2);
            var right = area.Right - center.X;
            var left = center.X - area.Left;
            var bottom = area.Bottom - center.Y;
            var top = center.Y - area.Top;
            var min = Math.Min(Math.Min(right, left), Math.Min(bottom, top));
            if (min == right) Left = area.Right - Width;
            else if (min == left) Left = area.Left;
            else if (min == bottom) Top = area.Bottom - Height;
            else Top = area.Top;
            Left = Math.Max(area.Left, Math.Min(area.Right - Width, Left));
            Top = Math.Max(area.Top, Math.Min(area.Bottom - Height, Top));
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
    }
}
