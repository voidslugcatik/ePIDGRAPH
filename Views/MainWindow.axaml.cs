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

        // Зум и панорамирование
        private double _zoomX = 1.0, _zoomY = 1.0;
        private double _panX, _panY;
        private bool _isPanning;
        private Point _lastMousePosition;
        private Point _zoomStartPoint;
        private Rectangle? _zoomRectangle;
        private bool _isZoomSelecting;

        private const double ZoomSpeed = 1.1;
        private const double MinZoom = 0.1;
        private const double MaxZoom = 10000.0;

        // Контролы
        private Grid? _plotGrid;
        private Image? _plotImage;
        private Canvas? _interactionCanvas;
        private Line? _crosshairX, _crosshairY;
        private Border? _tooltipBorder;
        private TextBlock? _tooltipText;

        // Данные графика
        private List<SessionPlotData>? _sessions;
        private double _tMin, _tMax, _vMin, _vMax;
        private bool _hasData;

        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            _viewModel.PlotDataChanged += OnPlotDataChanged;

            _plotGrid = this.FindControl<Grid>("PlotGrid");
            _plotImage = this.FindControl<Image>("PlotImage");
            _interactionCanvas = this.FindControl<Canvas>("InteractionCanvas");

            _crosshairX = this.FindControl<Line>("CrosshairX");
            _crosshairY = this.FindControl<Line>("CrosshairY");
            _tooltipBorder = this.FindControl<Border>("TooltipBorder");
            _tooltipText = this.FindControl<TextBlock>("TooltipText");

            // Как только Grid получит размер — выполняем первый рендер, если данные уже загружены
            if (_plotGrid != null)
            {
                _plotGrid.SizeChanged += (_, e) =>
                {
                    if (_hasData && e.NewSize.Width > 0 && e.NewSize.Height > 0)
                        RenderToImage();
                };
            }
        }

        private async void OnLoadBblClick(object? sender, RoutedEventArgs e)
        {
            var storageProvider = StorageProvider;
            var bblFileType = new FilePickerFileType("Blackbox logs")
            {
                Patterns = new[] { "*.bbl" }
            };

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите BBL-файлы",
                AllowMultiple = true,
                FileTypeFilter = new[] { bblFileType }
            });

            if (files.Count > 0)
            {
                var paths = files.Select(f => f.Path.LocalPath).ToList();
                await _viewModel.LoadFilesAsync(paths);
            }
        }

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
            _hasData = true;

            // Если Grid уже имеет размер — рисуем сразу
            if (_plotGrid != null && _plotGrid.Bounds.Width > 0 && _plotGrid.Bounds.Height > 0)
                RenderToImage();
        }

        private void ResetZoom()
        {
            _zoomX = 1.0;
            _zoomY = 1.0;
            _panX = 0;
            _panY = 0;
        }

        private void RenderToImage()
        {
            if (_plotGrid is null || _plotImage is null || _sessions is null) return;

            int width = Math.Max(1, (int)_plotGrid.Bounds.Width);
            int height = Math.Max(1, (int)_plotGrid.Bounds.Height);
            if (width <= 1 || height <= 1) return;

            var bitmap = new WriteableBitmap(
                new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);

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
                    DrawDataLine(canvas, width, height, session.Times, session.Setpoints,
                                 ScaleX, ScaleY, SKColors.Blue, true);
                    DrawDataLine(canvas, width, height, session.Times, session.Gyros,
                                 ScaleX, ScaleY, SKColors.Red, false);
                }

                canvas.Flush();
            }

            _plotImage.Source = bitmap;
        }

        // ========== Skia-отрисовка ==========
        private void DrawGrid(SKCanvas canvas, double w, double h,
                              Func<double, double> sx, Func<double, double> sy)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.LightGray,
                StrokeWidth = 0.5f,
                Style = SKPaintStyle.Stroke
            };

            int yTicks = 8;
            double vStep = (_vMax - _vMin) / yTicks;
            for (int i = 0; i <= yTicks; i++)
            {
                double y = sy(_vMin + i * vStep);
                if (y >= 0 && y <= h)
                    canvas.DrawLine(0, (float)y, (float)w, (float)y, paint);
            }

            int xTicks = 10;
            double tStep = (_tMax - _tMin) / xTicks;
            for (int i = 0; i <= xTicks; i++)
            {
                double x = sx(_tMin + i * tStep);
                if (x >= 0 && x <= w)
                    canvas.DrawLine((float)x, 0, (float)x, (float)h, paint);
            }
        }

        private void DrawAxes(SKCanvas canvas, double w, double h,
                              Func<double, double> sx, Func<double, double> sy)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 1.5f,
                Style = SKPaintStyle.Stroke
            };

            double oy = sy(0);
            if (oy >= 0 && oy <= h)
                canvas.DrawLine(0, (float)oy, (float)w, (float)oy, paint);

            double ox = sx(0);
            if (ox >= 0 && ox <= w)
                canvas.DrawLine((float)ox, 0, (float)ox, (float)h, paint);
        }

        private void DrawDataLine(SKCanvas canvas, double w, double h,
                                  double[] times, double[] values,
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
            if (dashed)
                paint.PathEffect = SKPathEffect.CreateDash(new float[] { 4, 2 }, 0);

            double maxZoom = Math.Max(_zoomX, _zoomY);
            int targetPoints = (int)(w * maxZoom);
            int step = Math.Max(1, values.Length / Math.Max(1, targetPoints * 2));

            var path = new SKPath();
            bool first = true;
            for (int i = 0; i < values.Length; i += step)
            {
                double x = sx(times[i]), y = sy(values[i]);
                if (x >= -10000 && x <= w + 10000 && y >= -10000 && y <= h + 10000)
                {
                    if (first) { path.MoveTo((float)x, (float)y); first = false; }
                    else path.LineTo((float)x, (float)y);
                }
                else if (!first)
                {
                    canvas.DrawPath(path, paint);
                    path.Reset();
                    first = true;
                }
            }
            if (!first) canvas.DrawPath(path, paint);
        }

        // ========== Мышь ==========
        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (_interactionCanvas is null || !_hasData) return;
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
                _zoomX = nzx;
                _zoomY = nzy;
            }

            RenderToImage();
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_interactionCanvas is null) return;
            var point = e.GetCurrentPoint(_interactionCanvas);
            var mousePos = e.GetPosition(_interactionCanvas);

            if (point.Properties.IsRightButtonPressed)
            {
                _isZoomSelecting = true;
                _zoomStartPoint = mousePos;
                _zoomRectangle = new Rectangle
                {
                    Stroke = new SolidColorBrush(Colors.Gray),
                    StrokeThickness = 1,
                    StrokeDashArray = new AvaloniaList<double> { 4, 2 },
                    Fill = new SolidColorBrush(Colors.Transparent)
                };
                Canvas.SetLeft(_zoomRectangle, mousePos.X);
                Canvas.SetTop(_zoomRectangle, mousePos.Y);
                _interactionCanvas.Children.Add(_zoomRectangle);
            }
            else if (point.Properties.IsLeftButtonPressed)
            {
                _isPanning = true;
                _lastMousePosition = mousePos;
            }
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_interactionCanvas is null) return;
            var mousePos = e.GetPosition(_interactionCanvas);

            if (_isZoomSelecting && _zoomRectangle != null)
            {
                double x = Math.Min(_zoomStartPoint.X, mousePos.X);
                double y = Math.Min(_zoomStartPoint.Y, mousePos.Y);
                double w = Math.Abs(mousePos.X - _zoomStartPoint.X);
                double h = Math.Abs(mousePos.Y - _zoomStartPoint.Y);

                Canvas.SetLeft(_zoomRectangle, x);
                Canvas.SetTop(_zoomRectangle, y);
                _zoomRectangle.Width = w;
                _zoomRectangle.Height = h;
                return;
            }

            if (_isPanning)
            {
                double dx = mousePos.X - _lastMousePosition.X;
                double dy = mousePos.Y - _lastMousePosition.Y;
                _panX += dx;
                _panY += dy;
                _lastMousePosition = mousePos;
                RenderToImage();
                return;
            }

            UpdateCrosshair(mousePos);
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_interactionCanvas is null) return;
            var mousePos = e.GetPosition(_interactionCanvas);

            if (_isZoomSelecting)
            {
                _isZoomSelecting = false;
                if (_zoomRectangle != null)
                {
                    _interactionCanvas.Children.Remove(_zoomRectangle);

                    double w = Math.Abs(mousePos.X - _zoomStartPoint.X);
                    double h = Math.Abs(mousePos.Y - _zoomStartPoint.Y);

                    if (w > 10 && h > 10)
                    {
                        double x = Math.Min(_zoomStartPoint.X, mousePos.X);
                        double y = Math.Min(_zoomStartPoint.Y, mousePos.Y);
                        double cw = _interactionCanvas.Bounds.Width;
                        double ch = _interactionCanvas.Bounds.Height;

                        double nzx = Math.Clamp(cw / w, MinZoom, MaxZoom);
                        double nzy = Math.Clamp(ch / h, MinZoom, MaxZoom);

                        _zoomX = nzx;
                        _zoomY = nzy;
                        _panX = -x * nzx;
                        _panY = -y * nzy;
                    }
                    else
                    {
                        ResetZoom();
                    }

                    _zoomRectangle = null;
                    RenderToImage();
                }
            }
            else if (_isPanning)
            {
                _isPanning = false;
            }
        }

        private void OnPointerExited(object? sender, PointerEventArgs e)
        {
            if (_crosshairX != null) _crosshairX.IsVisible = false;
            if (_crosshairY != null) _crosshairY.IsVisible = false;
            if (_tooltipBorder != null) _tooltipBorder.IsVisible = false;
        }

        // ========== Крестовина ==========
        private void UpdateCrosshair(Point mousePos)
        {
            if (_crosshairX is null || _crosshairY is null || _tooltipBorder is null || _tooltipText is null) return;
            if (_plotGrid is null || _sessions is null || !_hasData) return;

            double cw = _plotGrid.Bounds.Width;
            double ch = _plotGrid.Bounds.Height;
            if (cw <= 0 || ch <= 0)
            {
                _crosshairX.IsVisible = false;
                _crosshairY.IsVisible = false;
                _tooltipBorder.IsVisible = false;
                return;
            }

            double cx = Math.Clamp(mousePos.X, 0, cw);
            double cy = Math.Clamp(mousePos.Y, 0, ch);

            _crosshairX.StartPoint = new Point(cx, 0);
            _crosshairX.EndPoint = new Point(cx, ch);
            _crosshairX.IsVisible = true;

            _crosshairY.StartPoint = new Point(0, cy);
            _crosshairY.EndPoint = new Point(cw, cy);
            _crosshairY.IsVisible = true;

            double timeAtCursor = _tMin + (cx - _panX) / (_zoomX * cw) * (_tMax - _tMin);
            double valueAtCursor = _vMax - (cy - _panY) / (_zoomY * ch) * (_vMax - _vMin);

            double bestSetpoint = 0, bestGyro = 0;
            bool found = false;

            if (_sessions.Count == 1)
            {
                found = FindClosestInSession(_sessions[0], timeAtCursor, out bestSetpoint, out bestGyro);
            }
            else
            {
                foreach (var s in _sessions)
                {
                    if (s.Times.Length == 0) continue;
                    if (timeAtCursor >= s.Times[0] && timeAtCursor <= s.Times[^1])
                    {
                        found = FindClosestInSession(s, timeAtCursor, out bestSetpoint, out bestGyro);
                        break;
                    }
                }
                if (!found)
                {
                    double minDist = double.MaxValue;
                    foreach (var s in _sessions)
                    {
                        if (s.Times.Length == 0) continue;
                        double dist = Math.Min(Math.Abs(s.Times[0] - timeAtCursor),
                                               Math.Abs(s.Times[^1] - timeAtCursor));
                        if (dist < minDist)
                        {
                            minDist = dist;
                            found = FindClosestInSession(s, timeAtCursor, out bestSetpoint, out bestGyro);
                        }
                    }
                }
            }

            if (!found)
            {
                bestSetpoint = valueAtCursor;
                bestGyro = valueAtCursor;
            }

            string tooltip = $"X: {timeAtCursor:F2}s\nY: {valueAtCursor:F1}\nSP: {bestSetpoint:F1}\nGyro: {bestGyro:F1}";
            _tooltipText.Text = tooltip;

            double offset = 10;
            double tx = cx + offset, ty = cy + offset;
            if (tx + 100 > cw) tx = cx - offset - 100;
            if (ty + 50 > ch) ty = cy - offset - 50;
            Canvas.SetLeft(_tooltipBorder, tx);
            Canvas.SetTop(_tooltipBorder, ty);
            _tooltipBorder.IsVisible = true;
        }

        private static bool FindClosestInSession(SessionPlotData session, double time,
                                                 out double setpoint, out double gyro)
        {
            setpoint = gyro = 0;
            if (session.Times.Length == 0) return false;

            int index = Array.BinarySearch(session.Times, time);
            if (index < 0)
            {
                index = ~index;
                if (index >= session.Times.Length) index = session.Times.Length - 1;
                else if (index > 0)
                {
                    double d1 = Math.Abs(session.Times[index - 1] - time);
                    double d2 = Math.Abs(session.Times[index] - time);
                    if (d1 < d2) index--;
                }
            }

            setpoint = session.Setpoints[index];
            gyro = session.Gyros[index];
            return true;
        }
    }
}