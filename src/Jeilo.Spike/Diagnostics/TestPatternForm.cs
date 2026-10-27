using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Jeilo.Core.Recording;

namespace Jeilo.Spike.Diagnostics;

public sealed class TestPatternForm : Form
{
    private readonly RecordingClock _clock;
    private readonly TimeSpan _duration;
    private readonly System.Windows.Forms.Timer _timer;

    public TestPatternForm(RecordingClock clock, TimeSpan duration, Rectangle bounds)
    {
        _clock = clock;
        _duration = duration;

        Text = "Jeilo Phase 0 Test Pattern";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(11, 18, 32);
        DoubleBuffered = true;
        ResizeRedraw = true;

        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) => Invalidate();
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var background = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(11, 20, 28),
            Color.FromArgb(15, 45, 32),
            LinearGradientMode.Vertical);
        g.FillRectangle(background, ClientRectangle);

        DrawColorBars(g);
        DrawHeader(g);
        DrawClock(g);
        DrawMotionBar(g);
        DrawSyncMarker(g);
        DrawFooter(g);
    }

    private void DrawColorBars(Graphics g)
    {
        var colors = new[]
        {
            Color.White, Color.FromArgb(255, 220, 0), Color.FromArgb(0, 200, 255),
            Color.FromArgb(0, 190, 90), Color.FromArgb(230, 40, 200), Color.FromArgb(220, 40, 40),
            Color.FromArgb(20, 40, 220)
        };

        var barWidth = ClientSize.Width / (float)colors.Length;
        var barHeight = Math.Max(48, ClientSize.Height / 14f);

        for (var i = 0; i < colors.Length; i++)
        {
            using var brush = new SolidBrush(colors[i]);
            g.FillRectangle(brush, i * barWidth, 0, barWidth + 1, barHeight);
        }
    }

    private void DrawHeader(Graphics g)
    {
        var top = Math.Max(70, ClientSize.Height / 12);
        using var titleFont = new Font("Segoe UI", Math.Max(22, ClientSize.Height / 26f), FontStyle.Bold);
        using var subtitleFont = new Font("Segoe UI", Math.Max(12, ClientSize.Height / 55f), FontStyle.Regular);
        using var white = new SolidBrush(Color.White);
        using var green = new SolidBrush(Color.FromArgb(74, 222, 128));

        g.DrawString("JEILO RECORDER", titleFont, white, 32, top);
        g.DrawString("PHASE 0 · WGC VIDEO · WASAPI MIC + LOOPBACK", subtitleFont, green, 34, top + titleFont.Height + 8);

        var status = _clock.IsStarted ? "RECORDING" : "WARMING UP";
        using var statusFont = new Font("Segoe UI", Math.Max(12, ClientSize.Height / 60f), FontStyle.Bold);
        var statusSize = g.MeasureString(status, statusFont);
        var statusRect = new RectangleF(ClientSize.Width - statusSize.Width - 48, top + 4, statusSize.Width + 24, statusFont.Height + 12);
        using var statusBrush = new SolidBrush(_clock.IsStarted ? Color.FromArgb(22, 163, 74) : Color.FromArgb(202, 138, 4));
        g.FillRoundedRectangle(statusBrush, statusRect, 8);
        g.DrawString(status, statusFont, white, statusRect.X + 12, statusRect.Y + 6);
    }

    private void DrawClock(Graphics g)
    {
        var elapsed = _clock.IsStarted ? _clock.Elapsed : TimeSpan.Zero;
        using var elapsedFont = new Font("Consolas", Math.Max(48, ClientSize.Height / 8.5f), FontStyle.Bold);
        using var wallFont = new Font("Consolas", Math.Max(18, ClientSize.Height / 32f), FontStyle.Regular);
        using var white = new SolidBrush(Color.White);
        using var muted = new SolidBrush(Color.FromArgb(148, 163, 184));

        var elapsedText = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds:000}";
        var wallText = $"wall clock {DateTime.Now:HH:mm:ss.fff}";

        g.DrawString(elapsedText, elapsedFont, white, 32, ClientSize.Height * 0.28f);
        g.DrawString(wallText, wallFont, muted, 36, ClientSize.Height * 0.28f + elapsedFont.Height + 10);

        var local = DateTime.Now.ToString("HH:mm:ss.fff");
        using var localFont = new Font("Consolas", Math.Max(16, ClientSize.Height / 40f), FontStyle.Regular);
        g.DrawString($"local {local}", localFont, muted, 36, ClientSize.Height * 0.28f + elapsedFont.Height + 10 + wallFont.Height + 6);
    }

    private void DrawMotionBar(Graphics g)
    {
        var elapsed = _clock.IsStarted ? _clock.Elapsed : TimeSpan.Zero;
        var y = ClientSize.Height * 0.72f;
        var width = ClientSize.Width - 64;
        var height = Math.Max(18, ClientSize.Height / 40f);
        var x = 32f;

        using var track = new SolidBrush(Color.FromArgb(30, 64, 48));
        using var accent = new SolidBrush(Color.FromArgb(34, 197, 94));
        g.FillRoundedRectangle(track, new RectangleF(x, y, width, height), height / 2f);

        var phase = (float)((elapsed.TotalSeconds % 5.0) / 5.0);
        var knobX = x + phase * (width - height);
        g.FillEllipse(accent, knobX, y, height, height);

        using var labelFont = new Font("Segoe UI", Math.Max(11, ClientSize.Height / 65f), FontStyle.Regular);
        using var muted = new SolidBrush(Color.FromArgb(148, 163, 184));
        g.DrawString("moving marker", labelFont, muted, x, y - labelFont.Height - 6);
    }

    private void DrawSyncMarker(Graphics g)
    {
        if (!_clock.IsSyncMarkerActive())
        {
            return;
        }

        using var flash = new SolidBrush(Color.FromArgb(250, 204, 21));
        using var pen = new Pen(Color.FromArgb(250, 204, 21), 8);
        var size = Math.Min(ClientSize.Width, ClientSize.Height) * 0.18f;
        var rect = new RectangleF(
            ClientSize.Width - size - 48,
            ClientSize.Height - size - 80,
            size,
            size);
        g.FillRoundedRectangle(flash, rect, 12);
        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);

        using var font = new Font("Segoe UI", Math.Max(16, ClientSize.Height / 38f), FontStyle.Bold);
        using var dark = new SolidBrush(Color.FromArgb(11, 18, 32));
        var text = "SYNC";
        var textSize = g.MeasureString(text, font);
        g.DrawString(text, font, dark, rect.X + (rect.Width - textSize.Width) / 2, rect.Y + (rect.Height - textSize.Height) / 2);
    }

    private void DrawFooter(Graphics g)
    {
        var elapsed = _clock.IsStarted ? _clock.Elapsed : TimeSpan.Zero;
        var progress = Math.Clamp((float)(elapsed.TotalSeconds / _duration.TotalSeconds), 0f, 1f);
        var y = ClientSize.Height - 54;

        using var track = new SolidBrush(Color.FromArgb(30, 64, 48));
        using var accent = new SolidBrush(Color.FromArgb(34, 197, 94));
        g.FillRectangle(track, 0, y, ClientSize.Width, 10);
        g.FillRectangle(accent, 0, y, ClientSize.Width * progress, 10);

        using var font = new Font("Segoe UI", Math.Max(11, ClientSize.Height / 70f), FontStyle.Regular);
        using var muted = new SolidBrush(Color.FromArgb(148, 163, 184));
        g.DrawString(
            $"Speak into the H340 during this test. Sync tone and flash every {_clock.SyncIntervalSeconds:0}s. " +
            $"{elapsed.TotalSeconds:0.0}s / {_duration.TotalSeconds:0.0}s",
            font,
            muted,
            32,
            y + 18);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
