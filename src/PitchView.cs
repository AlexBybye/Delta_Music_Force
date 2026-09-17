using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeltaPlayer;

// Cached score drawing: animation redraws one drawing and a cursor, never the note collection.
public sealed class PitchView : FrameworkElement
{
    private DrawingGroup? score;
    private Plan? plan;
    private static readonly Brush Note = Frozen("#D79885"), Accent = Frozen("#B54F36"), Grid = Frozen("#E8DED5"), Label = Frozen("#897C72");
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double), typeof(PitchView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    private static Brush Frozen(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    public void SetPlan(Plan? value) { plan = value; Rebuild(); }
    public void MoveTo(double seconds, bool animate)
    {
        if (animate && SystemParameters.ClientAreaAnimation && seconds >= Position)
            BeginAnimation(PositionProperty, new DoubleAnimation(Position, seconds, TimeSpan.FromMilliseconds(100)) { FillBehavior = FillBehavior.Stop });
        else BeginAnimation(PositionProperty, null);
        Position = seconds;
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); Rebuild(); }
    private void Rebuild()
    {
        score = new DrawingGroup();
        using (var dc = score.Open())
        {
            if (plan is { Notes.Length: > 0 } && ActualWidth > 40 && ActualHeight > 20)
            {
                double width = ActualWidth - 36, height = ActualHeight - 24;
                int low = plan.Notes.Min(n => n.Finger.Pitch) - 1, high = plan.Notes.Max(n => n.Finger.Pitch) + 1;
                double Y(int pitch) => 12 + (high - pitch) * height / (high - low);
                for (int pitch = low; pitch <= high; pitch++)
                {
                    if (pitch % 12 != 0) continue;
                    double y = Y(pitch);
                    dc.DrawLine(new Pen(Grid, 1), new Point(32, y), new Point(ActualWidth, y));
                    dc.DrawText(new FormattedText($"C{pitch / 12 - 1}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 10, Label, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(0, y - 7));
                }
                // Bound the overview cost even for very large imported files.
                int step = Math.Max(1, (int)Math.Ceiling(plan.Notes.Length / 1600d));
                for (int i = 0; i < plan.Notes.Length; i += step)
                {
                    var n = plan.Notes[i];
                    double x = 32 + n.On / plan.Duration * width;
                    double w = Math.Clamp((n.Off - n.On) / plan.Duration * width, 2, Math.Max(2, ActualWidth - x));
                    dc.DrawRoundedRectangle(Note, null, new Rect(x, Y(n.Finger.Pitch) - 2.5, w, 5), 2, 2);
                }
            }
        }
        score.Freeze(); InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (score != null) dc.DrawDrawing(score);
        if (plan == null || ActualWidth <= 40) return;
        double x = 32 + Math.Clamp(Position / plan.Duration, 0, 1) * (ActualWidth - 36);
        dc.DrawLine(new Pen(Accent, 1.5), new Point(x, 4), new Point(x, ActualHeight - 4));
        dc.DrawEllipse(Accent, null, new Point(x, 4), 3, 3);
    }
}
