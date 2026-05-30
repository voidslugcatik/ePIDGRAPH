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
        private bool _isPanning;
        private Point _lastMousePosition;
        private Point _zoomStartPoint;
        private Rectangle? _zoomRectangle;
        private bool _isZoomSelecting;

        private const double ZoomSpeed = 1.1;
        private const double MinZoom = 1.0;
        private const double MaxZoom = 1000.0;

        private Grid? _plotGrid;
        private Image? _plotImage;
        private Canvas? _interactionCanvas;
        private Line? _crosshairX, _crosshairY;
        private Border? _tooltipBorder;
        private TextBlock? _tooltipText;

        private List<SessionPlotData>? _sessions;
        private double _tMin, _tMax, _vMin, _vMax;
        private bool _hasData;

        private SKPaint? _gridPaint;
        private SKPaint? _axisPaint;
        private SKPaint? _textPaint;
        private SKFont? _textFont;
        private SKPathEffect? _dashEffect;

        // Расширенная палитра на 12 цветов + названия для тултипа
        private static readonly SKColor[] SessionColors =
        {
            SKColors.Blue,      SKColors.Red,       SKColors.Green,
            SKColors.Orange,    SKColors.Purple,    SKColors.Brown,
            SKColors.Magenta,   SKColors.Cyan,      SKColors.DarkGreen,
            SKColors.DeepPink,  SKColors.Olive,     SKColors.Teal
        };

        private static readonly string[] ColorShortNames =
        {
            "син", "крс", "зел", "орж", "фиол", "кор",
            "мадж", "гол", "т-зел", "роз", "олив", "бирюз"
        };

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

            _gridPaint = new SKPaint
            {
                Color = SKColors.LightGray,
                StrokeWidth = 0.5f,
                Style = SKPaintStyle.Stroke
            };
            _axisPaint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 1.5f,
                Style = SKPaintStyle.Stroke
            };
            _textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            _textFont = new SKFont(SKTypeface.FromFamilyName("Arial"), 11);
            _dashEffect = SKPathEffect.CreateDash(new float[] { 4, 2 }, 0);

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

        // ==================== ДАННЫЕ ====================
        private void OnPlotDataChanged()
        {
            var (sessions, _, _) = _viewModel.GetPlotData();
            if (sessions.Count == 0) return;

            _sessions = new List<SessionPlotData>(sessions.Count);
            double globalMaxDurationSec = 0;

            foreach (var s in sessions)
            {
                if (s.Times.Length == 0) continue;
                double localMinUs = s.Times.Min();
                double localMaxUs = s.Times.Max();
                double durationSec = (localMaxUs - localMinUs) / 1_000_000.0;
                if (durationSec > globalMaxDurationSec) globalMaxDurationSec = durationSec;

                var shiftedTimes = new double[s.Times.Length];
                for (int i = 0; i < s.Times.Length; i++)
                    shiftedTimes[i] = s.Times[i] - localMinUs;

                _sessions.Add(new SessionPlotData
                {
                    Times = shiftedTimes,
                    Setpoints = s.Setpoints,
                    Gyros = s.Gyros
                });
            }

            _tMin = 0.0;
            _tMax = globalMaxDurationSec > 0 ? globalMaxDurationSec : 1.0;

            double vMin = double.MaxValue, vMax = double.MinValue;
            foreach (var s in _sessions)
            {
                vMin = Math.Min(vMin, Math.Min(s.Setpoints.Min(), s.Gyros.Min()));
                vMax = Math.Max(vMax, Math.Max(s.Setpoints.Max(), s.Gyros.Max()));
            }
            double vRange = vMax - vMin;
            _vMin = vMin - vRange * 0.05;
            _vMax = vMax + vRange * 0.05;

            ResetZoom();
            _hasData = true;

            if (_plotGrid != null && _plotGrid.Bounds.Width > 0 && _plotGrid.Bounds.Height > 0)
                RenderToImage();
        }

        private void ResetZoom()
        {
            _zoomX = 1.0; _zoomY = 1.0;
            _panX = 0; _panY = 0;
        }

        // ==================== РЕНДЕР ====================
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

                for (int i = 0; i < _sessions.Count; i++)
                {
                    var session = _sessions[i];
                    var color = SessionColors[i % SessionColors.Length];
                    DrawDataLine(canvas, width, height, session.Times, session.Setpoints,
                                 ScaleX, ScaleY, color, true);
                    DrawDataLine(canvas, width, height, session.Times, session.Gyros,
                                 ScaleX, ScaleY, color, false);
                }

                DrawLegend(canvas, width);
                canvas.Flush();
            }

            _plotImage.Source = bitmap;
        }

        // ==================== SKIA ОТРИСОВКА ====================
        private void DrawGrid(SKCanvas canvas, double w, double h,
                              Func<double, double> sx, Func<double, double> sy)
        {
            var linePaint = _gridPaint!;
            var labelPaint = _textPaint!;
            var font = _textFont!;

            int yTicks = 8;
            double vStep = (_vMax - _vMin) / yTicks;
            for (int i = 0; i <= yTicks; i++)
            {
                double v = _vMin + i * vStep;
                double y = sy(v);
                if (y >= 0 && y <= h)
                {
                    canvas.DrawLine(0, (float)y, (float)w, (float)y, linePaint);
                    string label = v.ToString("F0");
                    float textY = (float)y - 4;
                    if (textY < 10) textY = (float)y + 14;
                    canvas.DrawText(label, 2, textY, SKTextAlign.Left, font, labelPaint);
                }
            }

            int xTicks = 10;
            double tStep = (_tMax - _tMin) / xTicks;
            for (int i = 0; i <= xTicks; i++)
            {
                double t = _tMin + i * tStep;
                double x = sx(t);
                if (x >= 0 && x <= w)
                {
                    canvas.DrawLine((float)x, 0, (float)x, (float)h, linePaint);
                    string label = t.ToString("F1") + "s";
                    float textX = (float)x + 2;
                    if (textX > w - 40) textX = (float)x - 40;
                    canvas.DrawText(label, textX, (float)h - 4, SKTextAlign.Left, font, labelPaint);
                }
            }
        }

        private void DrawAxes(SKCanvas canvas, double w, double h,
                              Func<double, double> sx, Func<double, double> sy)
        {
            var paint = _axisPaint!;
            double oy = sy(0);
            if (oy >= 0 && oy <= h) canvas.DrawLine(0, (float)oy, (float)w, (float)oy, paint);
            double ox = sx(0);
            if (ox >= 0 && ox <= w) canvas.DrawLine((float)ox, 0, (float)ox, (float)h, paint);
        }

        private void DrawLegend(SKCanvas canvas, double graphWidth)
        {
            if (_sessions is null || _sessions.Count <= 1) return;

            var font = _textFont!;
            var labelPaint = _textPaint!;
            float x = 8, y = 8;
            float boxSize = 10, gap = 4;

            for (int i = 0; i < _sessions.Count; i++)
            {
                var color = SessionColors[i % SessionColors.Length];
                using var fill = new SKPaint { Color = color, Style = SKPaintStyle.Fill };
                canvas.DrawRect(x, y, boxSize, boxSize, fill);

                string label = $"Сессия {i + 1}";
                canvas.DrawText(label, x + boxSize + 4, y + boxSize - 2, SKTextAlign.Left, font, labelPaint);
                y += boxSize + gap;
            }
        }

        private void DrawDataLine(SKCanvas canvas, double w, double h,
                                  double[] timesUs, double[] values,
                                  Func<double, double> sx, Func<double, double> sy,
                                  SKColor color, bool dashed)
        {
            if (timesUs.Length == 0) return;

            double tPerPixel = (_tMax - _tMin) / (w * _zoomX);
            double tStart = _tMin + (-_panX) / (w * _zoomX) * (_tMax - _tMin) - tPerPixel;
            double tEnd = _tMin + (w - _panX) / (w * _zoomX) * (_tMax - _tMin) + tPerPixel;

            double tStartUs = tStart * 1_000_000.0;
            double tEndUs = tEnd * 1_000_000.0;

            int startIdx = Array.BinarySearch(timesUs, tStartUs);
            if (startIdx < 0) startIdx = ~startIdx;
            startIdx = Math.Max(0, startIdx - 1);

            int endIdx = Array.BinarySearch(timesUs, tEndUs);
            if (endIdx < 0) endIdx = ~endIdx;
            endIdx = Math.Min(timesUs.Length - 1, endIdx + 1);

            if (startIdx >= timesUs.Length || endIdx < 0 || startIdx > endIdx) return;

            int visibleCount = endIdx - startIdx + 1;
            int targetPoints = (int)(w * Math.Max(_zoomX, 1.0));
            int step = Math.Max(1, visibleCount / Math.Max(1, targetPoints * 2));

            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = 1.5f,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };
            if (dashed) paint.PathEffect = _dashEffect;

            var path = new SKPath();
            bool first = true;
            for (int i = startIdx; i <= endIdx; i += step)
            {
                double t = timesUs[i] / 1_000_000.0;
                double x = sx(t), y = sy(values[i]);
                if (first) { path.MoveTo((float)x, (float)y); first = false; }
                else path.LineTo((float)x, (float)y);
            }
            canvas.DrawPath(path, paint);
        }

        // ==================== МЫШЬ ====================
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
                _zoomX = nzx; _zoomY = nzy;
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
                _panX += dx; _panY += dy;
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

                        _zoomX = nzx; _zoomY = nzy;
                        _panX = -x * nzx; _panY = -y * nzy;
                    }
                    else ResetZoom();

                    _zoomRectangle = null;
                    RenderToImage();
                }
            }
            else if (_isPanning) _isPanning = false;
        }

        private void OnPointerExited(object? sender, PointerEventArgs e)
        {
            if (_crosshairX != null) _crosshairX.IsVisible = false;
            if (_crosshairY != null) _crosshairY.IsVisible = false;
            if (_tooltipBorder != null) _tooltipBorder.IsVisible = false;
        }

        // ==================== КРЕСТОВИНА (МУЛЬТИ-ЦВЕТ, 12 СЕССИЙ) ====================
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

            // Построение тултипа: каждая сессия на отдельной строке
            var lines = new List<string> { $"Время: {timeAtCursor:F6} с" };

            for (int i = 0; i < _sessions.Count; i++)
            {
                var s = _sessions[i];
                string colorTag = ColorShortNames[i % ColorShortNames.Length];

                if (s.Times.Length > 0 &&
                    FindClosestInSession(s, timeAtCursor, out double sp, out double gyro))
                {
                    lines.Add($"[{colorTag}] Сессия {i + 1}  SP: {sp:G}  Gyro: {gyro:G}");
                }
                else
                {
                    lines.Add($"[{colorTag}] Сессия {i + 1}  —");
                }
            }

            _tooltipText.Text = string.Join("\n", lines);

            double offset = 10;
            double tx = cx + offset, ty = cy + offset;
            double estimatedWidth = 280;
            double estimatedHeight = 20 + _sessions.Count * 20;
            if (tx + estimatedWidth > cw) tx = cx - offset - estimatedWidth;
            if (ty + estimatedHeight > ch) ty = cy - offset - estimatedHeight;
            Canvas.SetLeft(_tooltipBorder, tx);
            Canvas.SetTop(_tooltipBorder, ty);
            _tooltipBorder.IsVisible = true;
        }

        private static bool FindClosestInSession(SessionPlotData session, double time,
                                                 out double setpoint, out double gyro)
        {
            setpoint = gyro = 0;
            if (session.Times.Length == 0) return false;

            int index = Array.BinarySearch(session.Times, time * 1_000_000.0);
            if (index < 0)
            {
                index = ~index;
                if (index >= session.Times.Length) index = session.Times.Length - 1;
                else if (index > 0)
                {
                    double d1 = Math.Abs(session.Times[index - 1] / 1e6 - time);
                    double d2 = Math.Abs(session.Times[index] / 1e6 - time);
                    if (d1 < d2) index--;
                }
            }

            setpoint = session.Setpoints[index];
            gyro = session.Gyros[index];
            return true;
        }
    }
}