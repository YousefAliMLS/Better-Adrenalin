using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RadeonGuard.Core.Models;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace RadeonGuard.UI.Controls;

public sealed class EditableFanPoint : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private int _index;
    private int _temperatureC;
    private int _fanSpeedPercent;

    public int Index { get => _index; set => SetProperty(ref _index, value); }
    public int TemperatureC { get => _temperatureC; set => SetProperty(ref _temperatureC, value); }
    public int FanSpeedPercent { get => _fanSpeedPercent; set => SetProperty(ref _fanSpeedPercent, value); }
}

/// <summary>
/// Interactive 5-point hardware fan curve canvas showing Temperature (°C) vs PWM (%)
/// with live Hotspot Temperature indicator and monotonic drag constraints.
/// </summary>
public sealed class FanCurveCanvasControl : Canvas
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(ObservableCollection<EditableFanPoint>), typeof(FanCurveCanvasControl),
        new FrameworkPropertyMetadata(null, OnPointsChanged));

    public static readonly DependencyProperty LiveHotspotTempProperty = DependencyProperty.Register(
        nameof(LiveHotspotTemp), typeof(double), typeof(FanCurveCanvasControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (_, _) => { }));

    public static readonly DependencyProperty MinTempProperty = DependencyProperty.Register(
        nameof(MinTemp), typeof(int), typeof(FanCurveCanvasControl), new PropertyMetadata(20));

    public static readonly DependencyProperty MaxTempProperty = DependencyProperty.Register(
        nameof(MaxTemp), typeof(int), typeof(FanCurveCanvasControl), new PropertyMetadata(105));

    public static readonly DependencyProperty MinPwmProperty = DependencyProperty.Register(
        nameof(MinPwm), typeof(int), typeof(FanCurveCanvasControl), new PropertyMetadata(15));

    public ObservableCollection<EditableFanPoint>? Points
    {
        get => (ObservableCollection<EditableFanPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double LiveHotspotTemp
    {
        get => (double)GetValue(LiveHotspotTempProperty);
        set => SetValue(LiveHotspotTempProperty, value);
    }

    public int MinTemp { get => (int)GetValue(MinTempProperty); set => SetValue(MinTempProperty, value); }
    public int MaxTemp { get => (int)GetValue(MaxTempProperty); set => SetValue(MaxTempProperty, value); }
    public int MinPwm { get => (int)GetValue(MinPwmProperty); set => SetValue(MinPwmProperty, value); }

    public event EventHandler? CurveEdited;

    private int _dragIndex = -1;
    private const double PadL = 44;
    private const double PadR = 20;
    private const double PadT = 20;
    private const double PadB = 34;

    public FanCurveCanvasControl()
    {
        Background = new SolidColorBrush(Color.FromRgb(22, 24, 30));
        ClipToBounds = true;
        SizeChanged += (_, _) => Redraw();
        MouseLeftButtonUp += (_, _) =>
        {
            if (_dragIndex != -1)
            {
                _dragIndex = -1;
                ReleaseMouseCapture();
                CurveEdited?.Invoke(this, EventArgs.Empty);
            }
        };
        MouseMove += OnCanvasMouseMove;
    }

    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (FanCurveCanvasControl)d;
        if (e.OldValue is ObservableCollection<EditableFanPoint> oldCol)
        {
            oldCol.CollectionChanged -= ctrl.OnCollectionChanged;
            foreach (var p in oldCol) p.PropertyChanged -= ctrl.OnPointPropChanged;
        }
        if (e.NewValue is ObservableCollection<EditableFanPoint> newCol)
        {
            newCol.CollectionChanged += ctrl.OnCollectionChanged;
            foreach (var p in newCol) p.PropertyChanged += ctrl.OnPointPropChanged;
        }
        ctrl.Redraw();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (EditableFanPoint p in e.NewItems) p.PropertyChanged += OnPointPropChanged;
        }
        Redraw();
    }

    private void OnPointPropChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Redraw();

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == LiveHotspotTempProperty) Redraw();
    }

    private double PlotW => Math.Max(10, ActualWidth - PadL - PadR);
    private double PlotH => Math.Max(10, ActualHeight - PadT - PadB);

    private double TempToX(double t) => PadL + Math.Clamp((t - MinTemp) / Math.Max(1.0, MaxTemp - MinTemp), 0, 1) * PlotW;
    private double PwmToY(double p) => PadT + (1.0 - Math.Clamp(p / 100.0, 0, 1)) * PlotH;
    private int XToTemp(double x) => (int)Math.Round(MinTemp + Math.Clamp((x - PadL) / PlotW, 0, 1) * (MaxTemp - MinTemp));
    private int YToPwm(double y) => (int)Math.Round((1.0 - Math.Clamp((y - PadT) / PlotH, 0, 1)) * 100.0);

    private void OnCanvasMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragIndex < 0 || Points is null || _dragIndex >= Points.Count) return;

        var pos = e.GetPosition(this);
        int newTemp = XToTemp(pos.X);
        int newPwm = YToPwm(pos.Y);

        int minAllowedTemp = _dragIndex == 0 ? MinTemp : Points[_dragIndex - 1].TemperatureC + 1;
        int maxAllowedTemp = _dragIndex == Points.Count - 1 ? MaxTemp : Points[_dragIndex + 1].TemperatureC - 1;
        int minAllowedPwm = _dragIndex == 0 ? MinPwm : Points[_dragIndex - 1].FanSpeedPercent;
        int maxAllowedPwm = _dragIndex == Points.Count - 1 ? 100 : Points[_dragIndex + 1].FanSpeedPercent;

        Points[_dragIndex].TemperatureC = Math.Clamp(newTemp, minAllowedTemp, Math.Max(minAllowedTemp, maxAllowedTemp));
        Points[_dragIndex].FanSpeedPercent = Math.Clamp(newPwm, minAllowedPwm, Math.Max(minAllowedPwm, maxAllowedPwm));
        Redraw();
    }

    public void Redraw()
    {
        Children.Clear();
        if (ActualWidth < 40 || ActualHeight < 40) return;

        var gridBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        var labelBrush = new SolidColorBrush(Color.FromRgb(165, 173, 186));

        // Horizontal PWM grid lines
        for (int p = 0; p <= 100; p += 20)
        {
            double y = PwmToY(p);
            Children.Add(new Line { X1 = PadL, X2 = PadL + PlotW, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
            var tb = new TextBlock { Text = $"{p}%", Foreground = labelBrush, FontSize = 11 };
            SetLeft(tb, 6);
            SetTop(tb, y - 8);
            Children.Add(tb);
        }

        // Vertical Temp grid lines
        for (int t = 30; t <= MaxTemp; t += 15)
        {
            double x = TempToX(t);
            Children.Add(new Line { X1 = x, X2 = x, Y1 = PadT, Y2 = PadT + PlotH, Stroke = gridBrush, StrokeThickness = 1 });
            var tb = new TextBlock { Text = $"{t}°C", Foreground = labelBrush, FontSize = 11 };
            SetLeft(tb, x - 14);
            SetTop(tb, PadT + PlotH + 8);
            Children.Add(tb);
        }

        // Live Hotspot Temp vertical indicator
        if (LiveHotspotTemp > 0)
        {
            double hx = TempToX(LiveHotspotTemp);
            Children.Add(new Line
            {
                X1 = hx, X2 = hx, Y1 = PadT, Y2 = PadT + PlotH,
                Stroke = new SolidColorBrush(Color.FromArgb(180, 255, 165, 0)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection([4, 3])
            });
            var htb = new TextBlock
            {
                Text = $"Hotspot {LiveHotspotTemp:F0}°C",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 185, 64)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };
            SetLeft(htb, Math.Min(hx + 4, PadL + PlotW - 85));
            SetTop(htb, PadT + 2);
            Children.Add(htb);
        }

        if (Points is null || Points.Count == 0) return;

        // Curve polyline & fill
        var poly = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(237, 28, 36)),
            StrokeThickness = 2.8,
            StrokeLineJoin = PenLineJoin.Round
        };
        foreach (var pt in Points)
        {
            poly.Points.Add(new System.Windows.Point(TempToX(pt.TemperatureC), PwmToY(pt.FanSpeedPercent)));
        }
        Children.Add(poly);

        // Draggable nodes
        for (int i = 0; i < Points.Count; i++)
        {
            int idx = i;
            var pt = Points[i];
            double cx = TempToX(pt.TemperatureC);
            double cy = PwmToY(pt.FanSpeedPercent);

            var node = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(Color.FromRgb(255, 77, 85)),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"P{i + 1}: {pt.TemperatureC}°C → {pt.FanSpeedPercent}%"
            };
            node.MouseLeftButtonDown += (_, args) =>
            {
                _dragIndex = idx;
                CaptureMouse();
                args.Handled = true;
            };
            SetLeft(node, cx - 7);
            SetTop(node, cy - 7);
            Children.Add(node);

            var tag = new TextBlock
            {
                Text = $"{pt.TemperatureC}° / {pt.FanSpeedPercent}%",
                Foreground = Brushes.White,
                FontSize = 10
            };
            SetLeft(tag, Math.Clamp(cx - 20, PadL, PadL + PlotW - 60));
            SetTop(tag, Math.Max(2, cy - 22));
            Children.Add(tag);
        }
    }
}
