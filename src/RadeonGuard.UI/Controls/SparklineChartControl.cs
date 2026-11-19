using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Point = System.Windows.Point;

namespace RadeonGuard.UI.Controls;

public sealed class SparklineChartControl : Canvas
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(ObservableCollection<double>), typeof(SparklineChartControl),
        new FrameworkPropertyMetadata(null, OnValuesChanged));

    public static readonly DependencyProperty StrokeColorProperty = DependencyProperty.Register(
        nameof(StrokeColor), typeof(System.Windows.Media.Color), typeof(SparklineChartControl),
        new PropertyMetadata(System.Windows.Media.Color.FromRgb(237, 28, 36)));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double), typeof(SparklineChartControl), new PropertyMetadata(100.0));

    public ObservableCollection<double>? Values
    {
        get => (ObservableCollection<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public System.Windows.Media.Color StrokeColor { get => (System.Windows.Media.Color)GetValue(StrokeColorProperty); set => SetValue(StrokeColorProperty, value); }
    public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }

    public SparklineChartControl()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => Redraw();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (SparklineChartControl)d;
        if (e.OldValue is ObservableCollection<double> oldCol) oldCol.CollectionChanged -= ctrl.OnCollectionChanged;
        if (e.NewValue is ObservableCollection<double> newCol) newCol.CollectionChanged += ctrl.OnCollectionChanged;
        ctrl.Redraw();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    public void Redraw()
    {
        Children.Clear();
        if (Values is null || Values.Count < 2 || ActualWidth < 10 || ActualHeight < 10) return;

        double max = Math.Max(MaxValue, Values.Max());
        if (max <= 0) max = 100;

        var poly = new Polyline
        {
            Stroke = new SolidColorBrush(StrokeColor),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };

        int n = Values.Count;
        double dx = ActualWidth / Math.Max(1, n - 1);
        for (int i = 0; i < n; i++)
        {
            double x = i * dx;
            double y = ActualHeight - 4 - (Math.Clamp(Values[i] / max, 0, 1) * (ActualHeight - 8));
            poly.Points.Add(new Point(x, y));
        }
        Children.Add(poly);
    }
}
