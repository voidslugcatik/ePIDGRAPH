using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ƎPIDGRAPH.ViewModels;

namespace ƎPIDGRAPH.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;

        private double _zoomX = 1.0, _zoomY = 1.0;
        private double _panX, _panY;

        private bool _isPanning = false;
        private Point _lastMousePosition;
        private Point _zoomStartPoint;
        private Rectangle? _zoomRectangle;
        private bool _isZoomSelecting = false;

        private const double ZoomSpeed = 1.1;
        private const double MinZoom = 1;
        private const double MaxZoom = 1000.0;

        // Новые контролы
        private Image? _plotImage;
        private Canvas? _interactionCanvas;

        private Line? _crosshairX, _crosshairY;
        private Border? _tooltipBorder;
        private TextBlock? _tooltipText;

        // Данные для рендеринга (сохраняем локально)
        private List<SessionPlotData>? _sessions;
        private double _tMin, _tMax, _vMin, _vMax;

        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            _viewModel.PlotDataChanged += OnPlotDataChanged;

            _plotImage = this.FindControl<Image>("PlotImage");
            _interactionCanvas = this.FindControl<Canvas>("InteractionCanvas");

            _crosshairX = this.FindControl<Line>("CrosshairX");
            _crosshairY = this.FindControl<Line>("CrosshairY");
            _tooltipBorder = this.FindControl<Border>("TooltipBorder");
            _tooltipText = this.FindControl<TextBlock>("TooltipText");
        }

        private async void OnLoadBblClick(object sender, RoutedEventArgs e) { /* ... без изменений ... */ }

        // ======================= ОБНОВЛЕНИЕ ГРАФИКА =======================
        private void OnPlotDataChanged()
        {
            var (sessions, tMin, tMax) = _viewModel.GetPlotData();
            if (sessions.Count == 0) return;

            _sessions = sessions;
            _tMin = tMin;
            _tMax = tMax;

            double vMin = double.MaxValue, vMax = double.MinValue;
            foreach (var s in sessions)
            {
                vMin = Math.Min(vMin, Math.Min(s.Setpoints.Min(), s.Gyros.Min()));
                vMax = Math.Max(vMax, Math.Max(s.Setpoints.Max(), s.Gyros.Max()));
            }
            double vRange = vMax - vMin;
            _vMin = vMin - vRange * 0.05;
            _vMax = vMax + vRange * 0.05;

            ResetZoom();
            RenderToImage();
        }

        private void ResetZoom()
        {
            _zoomX = 1.0;
            _zoomY = 1.0;
            _panX = 0;
            _panY = 0;
        }

        /// <summary> Перерисовывает график в Image. Вызывается при изменении данных, зума или панорамирования. </summary>
        private void RenderToImage()
        {
            if (_plotImage is null || _sessions is null) return;

            int width = Math.Max(1, (int)_plotImage.Bounds.Width);
            int height = Math.Max(1, (int)_plotImage.Bounds.Height);
            if (width <= 0 || height <= 0) return;

            var bitmap = new WriteableBitmap(
                new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

            using (var locked = bitmap.Lock())
            {
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info, locked.Address, locked.RowBytes);
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.White);

                double ScaleX(double t) => ((t - _tMin) / (_tMax - _tMin) * width * _zoomX) + _panX;
                double ScaleY(double v) => ((_vMax - v) / (_vMax - _vMin) * height * _zoomY) + _panY;

                DrawGrid(canvas, width, height, ScaleX, ScaleY);
                DrawAxes(canvas, width, height, ScaleX, ScaleY);

                foreach (var session in _sessions)
                {
                    DrawDataLine(canvas, session.Times, session.Setpoints, ScaleX, ScaleY, SKColors.Blue, true);
                    DrawDataLine(canvas, session.Times, session.Gyros, ScaleX, ScaleY, SKColors.Red, false);
                }

                canvas.Flush();
            }

            _plotImage.Source = bitmap;
        }

        // ======================= МЕТОДЫ ОТРИСОВКИ =======================
        private void DrawGrid(SKCanvas canvas, double w, double h,
                              Func<double, double> sx, Func<double, double> sy)
        {
            using var paint = new SKPaint { Color = SKColors.LightGray, StrokeWidth = 0.5f, Style = SKPaintStyle.Stroke };
            int yTicks = 8;
            double vStep = (_vMax - _vMin) / yTicks;
            for (int i = 0; i <= yTicks; i++)
            {
                double y = sy(_vMin + i * vStep);
                if (y >= 0 && y <= h) canvas.DrawLine(0, (float)y, (float)w, (float)y, paint);
            }
            int xTicks = 10;
            double tStep = (_tMax - _tMin) / xTicks;
            for (int i = 0; i <= xTicks; i++)
            {
                double x = sx(_tMin + i * tStep);
                if (x >= 0 && x <= w) canvas.DrawLine((float)x, 0, (float)x, (float)h, paint);
            }
        }

        private void DrawAxes(SKCanvas canvas, double w, double h,
                              Func<double, double> sx, Func<double, double> sy)
        {
            using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };
            double oy = sy(0);
            if (oy >= 0 && oy <= h) canvas.DrawLine(0, (float)oy, (float)w, (float)oy, paint);
            double ox = sx(0);
            if (ox >= 0 && ox <= w) canvas.DrawLine((float)ox, 0, (float)ox, (float)h, paint);
        }

        private void DrawDataLine(SKCanvas canvas, double[] times, double[] values,
                                  Func<double, double> sx, Func<double, double> sy,
                                  SKColor color, bool dashed)
        {
            if (times.Length == 0) return;
            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = 1.5f,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };
            if (dashed) paint.PathEffect = SKPathEffect.CreateDash(new float[] { 4, 2 }, 0);

            // LOD: количество точек зависит от размера окна и зума
            double maxZoom = Math.Max(_zoomX, _zoomY);
            int targetPoints = (int)(Bounds.Width * maxZoom);
            int step = Math.Max(1, values.Length / Math.Max(1, targetPoints * 2));

            var path = new SKPath();
            bool first = true;
            for (int i = 0; i < values.Length; i += step)
            {
                double x = sx(times[i]), y = sy(values[i]);
                if (x >= -10000 && x <= Bounds.Width + 10000 &&
                    y >= -10000 && y <= Bounds.Height + 10000)
                {
                    if (first) { path.MoveTo((float)x, (float)y); first = false; }
                    else path.LineTo((float)x, (float)y);
                }
                else if (!first) { canvas.DrawPath(path, paint); path.Reset(); first = true; }
            }
            if (!first) canvas.DrawPath(path, paint);
        }

        // ======================= СОБЫТИЯ МЫШИ (обновлены) =======================
        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (_interactionCanvas is null) return;
            var mousePos = e.GetPosition(_interactionCanvas);
            double zoomDelta = e.Delta.Y > 0 ? ZoomSpeed : 1.0 / ZoomSpeed;
            var modifiers = e.KeyModifiers;

            if (modifiers.HasFlag(KeyModifiers.Shift))
            {
                double nz = Math.Clamp(_zoomX * zoomDelta, MinZoom, MaxZoom);
                _panX = mousePos.X - (mousePos.X - _panX) * (nz / _zoomX);
                _zoomX = nz;
            }
            else if (modifiers.HasFlag(KeyModifiers.Control))
            {
                double nz = Math.Clamp(_zoomY * zoomDelta, MinZoom, MaxZoom);
                _panY = mousePos.Y - (mousePos.Y - _panY) * (nz / _zoomY);
                _zoomY = nz;
            }
            else
            {
                double nzx = Math.Clamp(_zoomX * zoomDelta, MinZoom, MaxZoom);
                double nzy = Math.Clamp(_zoomY * zoomDelta, MinZoom, MaxZoom);
                _panX = mousePos.X - (mousePos.X - _panX) * (nzx / _zoomX);
                _panY = mousePos.Y - (mousePos.Y - _panY) * (nzy / _zoomY);
                _zoomX = nzx; _zoomY = nzy;
            }
            RenderToImage();
        }

        // OnPointerPressed, OnPointerMoved, OnPointerReleased — аналогично,
        // везде заменяем DrawPlot() на RenderToImage(), и PlotCanvas на _interactionCanvas.
        // Остальные методы (крестовина, FindClosestInSession) остаются без изменений,
        // только вместо _plotControl.Bounds используем _plotImage.Bounds,
        // а вместо _plotControl.VMin и т.д. – _vMin, _vMax и т.д.
    }
}