using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BambuBar.Theme;

/// <summary>
/// A short burst of confetti over a window, for a moment worth celebrating (a supporter license activated): the Mac's
/// Confetti. It draws in a clear, click-through, topmost window over the host, so it never covers or blocks the
/// controls, and stays off when Windows' animations are off.
/// </summary>
public static partial class Confetti
{
    public const double Duration = 3.4;
    private static Window? _burst;

    public static void Celebrate(Window? host)
    {
        if (!AppTheme.AnimationsEnabled || host is not { IsVisible: true } || host.ActualWidth <= 0) return;
        _burst?.Close();
        var bounds = host.WindowState == WindowState.Maximized
            ? SystemParameters.WorkArea
            : new Rect(host.Left, host.Top, host.ActualWidth, host.ActualHeight);
        var canvas = new Pieces();
        var burst = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize, Focusable = false, IsHitTestVisible = false,
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height, Content = canvas,
        };
        burst.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(burst).Handle;
            // Clicks go through to the window underneath; never in Alt+Tab.
            SetWindowLongPtr(handle, GwlExStyle, GetWindowLongPtr(handle, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };
        _burst = burst;
        burst.Show();
        canvas.Start(() =>
        {
            if (ReferenceEquals(_burst, burst)) _burst = null;
            burst.Close();
        });
    }

    /// <summary>Paper pieces shot from the window's two bottom corners, falling back under gravity while they flutter.</summary>
    private sealed class Pieces : FrameworkElement
    {
        private static readonly Color[] Colors =
        [
            Color.FromRgb(0x00, 0xAD, 0x42), Color.FromRgb(0xFF, 0xC7, 0x00), Color.FromRgb(0xFF, 0x5C, 0x4A),
            Color.FromRgb(0x33, 0x8C, 0xFF), Color.FromRgb(0xBF, 0x59, 0xF2), Color.FromRgb(0x33, 0xD9, 0xD9),
        ];
        /// <summary>Gravity and air drag in fractions of the window height (and width) per second.</summary>
        private const double Gravity = 1.5, Drag = 1.1;

        private readonly Piece[] _pieces;
        private readonly SolidColorBrush[] _brushes = Colors.Select(Palette.Brush).ToArray();
        private DateTime _start;
        private Action? _done;

        public Pieces()
        {
            var random = new Random();
            double In(double a, double b) => a + random.NextDouble() * (b - a);
            _pieces = Enumerable.Range(0, 180).Select(i => new Piece(i % 2 == 0, In(0, 0.25), In(0.25, 0.95), In(1.4, 2.5), In(-9, 9),
                In(6, 14), In(0, 2 * Math.PI), In(6, 10), In(9, 15), random.Next(Colors.Length))).ToArray();
        }

        public void Start(Action done)
        {
            _done = done;
            _start = DateTime.UtcNow;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            if ((DateTime.UtcNow - _start).TotalSeconds >= Duration)
            {
                CompositionTarget.Rendering -= OnFrame;
                _done?.Invoke();
                return;
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var time = (DateTime.UtcNow - _start).TotalSeconds;
            var w = ActualWidth;
            var h = ActualHeight;
            var fade = Math.Clamp((Duration - time) / 0.6, 0, 1);
            if (fade <= 0) return;
            dc.PushOpacity(fade);
            foreach (var p in _pieces)
            {
                var t = time - p.Delay;
                if (t <= 0) continue;
                // Drag slows the sideways throw; the fall is gravity's, with drag capping its speed.
                var slow = (1 - Math.Exp(-Drag * t)) / Drag;
                var dx = p.SpeedX * slow * (p.FromLeft ? 1 : -1);
                var dy = -p.SpeedY * slow + Gravity * (t - slow) / Drag;
                var x = (p.FromLeft ? 0 : 1) + dx + 0.015 * Math.Sin(p.Flutter * 0.5 * t + p.Phase);
                var y = 1.02 + dy;
                if (y >= 1.1) continue;
                var group = new TransformGroup();
                group.Children.Add(new ScaleTransform(Math.Max(0.08, Math.Abs(Math.Cos(p.Flutter * t + p.Phase))), 1));
                group.Children.Add(new RotateTransform((p.Spin * t + p.Phase) * 180 / Math.PI));
                group.Children.Add(new TranslateTransform(x * w, y * h));
                dc.PushTransform(group);
                dc.DrawRoundedRectangle(_brushes[p.Color], null, new Rect(-p.Width / 2, -p.Length / 2, p.Width, p.Length), 1.5, 1.5);
                dc.Pop();
            }
            dc.Pop();
        }

        private sealed record Piece(bool FromLeft, double Delay, double SpeedX, double SpeedY, double Spin, double Flutter,
            double Phase, double Width, double Length, int Color);
    }

    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint window, int index, nint value);
}
