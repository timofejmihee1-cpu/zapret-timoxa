using System.Windows;
using System.Windows.Media;

namespace Zapret2UI.Controls;

/// <summary>
/// High-performance animated 3D particle wave background with white dots.
/// Undulates smoothly in perspective, hardware accelerated via Direct3D/WPF DrawingContext.
/// </summary>
public sealed class DotWaveBackground : FrameworkElement
{
    private double _phase;
    private DateTime _lastRender = DateTime.UtcNow;
    private static readonly SolidColorBrush[] BrushesPool;

    static DotWaveBackground()
    {
        // 16 pre-allocated frozen brushes with graduated alpha (pure white dots fading with depth)
        BrushesPool = new SolidColorBrush[16];
        for (int i = 0; i < BrushesPool.Length; i++)
        {
            byte alpha = (byte)Math.Clamp((int)(12 + (i / 15.0) * 195), 0, 255);
            var b = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
            b.Freeze();
            BrushesPool[i] = b;
        }
    }

    private static readonly List<DotWaveBackground> Instances = new();
    private static bool _globalPaused;

    /// <summary>Global pause switch (e.g. during game mode) to eliminate 100% of GPU/CPU rendering overhead.</summary>
    public static bool IsPaused
    {
        get => _globalPaused;
        set
        {
            if (_globalPaused == value) return;
            _globalPaused = value;
            lock (Instances)
            {
                foreach (var inst in Instances.ToArray())
                {
                    if (value) inst.StopAnimation();
                    else if (inst.IsVisible) inst.StartAnimation();
                }
            }
        }
    }

    public DotWaveBackground()
    {
        IsHitTestVisible = false;
        lock (Instances) Instances.Add(this);
        Loaded += (_, _) => StartAnimation();
        Unloaded += (_, _) =>
        {
            StopAnimation();
            lock (Instances) Instances.Remove(this);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) StartAnimation();
            else StopAnimation();
        };
    }

    private bool _animating;

    private void StartAnimation()
    {
        if (_globalPaused || _animating) return;
        _animating = true;
        _lastRender = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible || ActualWidth < 20 || ActualHeight < 20) return;

        var now = DateTime.UtcNow;
        double dt = (now - _lastRender).TotalSeconds;
        _lastRender = now;
        if (dt > 0.1) dt = 0.1;

        _phase += dt * 0.92; // smooth wave cycle
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 20 || h < 20) return;

        const int cols = 48;
        const int rows = 28;

        double cx = w * 0.5;
        double horizonY = h * 0.36;
        double groundHeight = h * 0.62;

        for (int iz = 0; iz < rows; iz++)
        {
            double v = (double)iz / (rows - 1); // 0 (far horizon) to 1 (near foreground)
            double depth = 0.20 + v * 0.80;     // perspective depth multiplier
            double spreadX = w * (0.32 + 0.88 * depth);

            for (int ix = 0; ix < cols; ix++)
            {
                double u = ((double)ix / (cols - 1)) - 0.5; // -0.5 to 0.5

                // Dual harmonic undulating wave function (sine + cosine cross-wave)
                double w1 = Math.Sin(u * 5.8 + _phase) * Math.Cos(v * 4.4 + _phase * 0.82) * 34.0;
                double w2 = Math.Sin((u * 1.5 + v) * 4.6 + _phase * 1.15) * 16.0;
                double w3 = Math.Cos(u * 10.5 - _phase * 0.55) * 7.0;
                double y3d = w1 + w2 + w3;

                double sx = cx + u * spreadX;
                double sy = horizonY + v * groundHeight - (y3d * depth);

                // Cull off-screen points
                if (sx < -20 || sx > w + 20 || sy < -20 || sy > h + 20) continue;

                // Opacity & sizing based on depth and wave crest height
                double crestNorm = (y3d + 45.0) / 90.0;
                double alphaFactor = Math.Clamp((0.08 + 0.72 * (v * v)) * (0.65 + 0.35 * crestNorm), 0.0, 1.0);
                int brushIndex = (int)(alphaFactor * (BrushesPool.Length - 1));
                var brush = BrushesPool[Math.Clamp(brushIndex, 0, BrushesPool.Length - 1)];

                double radius = 1.0 + 2.3 * depth;

                dc.DrawEllipse(brush, null, new Point(sx, sy), radius, radius * 0.88);
            }
        }
    }
}
