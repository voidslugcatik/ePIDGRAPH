using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ƎPIDGRAPH.ViewModels;

namespace ƎPIDGRAPH.Views
{
    public partial class MainWindow : Window
    {
        private enum LineStyle { Solid, Dash }

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

        private readonly IBrush _gridBrush = new SolidColorBrush(Colors.LightGray);
        private readonly IBrush _axisBrush = new SolidColorBrush(Colors.Black);
        private readonly IBrush _textBrush = new SolidColorBrush(Colors.Black);

        // Элементы крестовины
        private Line? _crosshairX, _crosshairY;
        private Border? _tooltipBorder;
        private TextBlock? _tooltipText;

        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            _viewModel.PlotDataChanged += OnPlotDataChanged;

            // Находим крестовину по именам
            _crosshairX = this.FindControl<Line>("CrosshairX");
            _crosshairY = this.FindControl<Line>("CrosshairY");
            _tooltipBorder = this.FindControl<Border>("TooltipBorder");
            _tooltipText = this.FindControl<TextBlock>("TooltipText");
        }

        private async void OnLoadBblClick(object sender, RoutedEventArgs e)
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
            ResetZoom();
            DrawPlot();
        }

        private void ResetZoom()
        {
            _zoomX = 1.0;
            _zoomY = 1.0;
            _panX = 0;
            _panY = 0;
        }

        private void DrawPlot()
        {
            PlotCanvas.Children.Clear();
            double canvasWidth = PlotCanvas.Bounds.Width;
            double canvasHeight = PlotCanvas.Bounds.Height;
            PlotCanvas.Clip = new RectangleGeometry(new Rect(0, 0, canvasWidth, canvasHeight));

            var (sessions, tMin, tMax) = _viewModel.GetPlotData();
            if (sessions.Count == 0) return;

            double vMin = double.MaxValue, vMax = double.MinValue;
            foreach (var s in sessions)
            {
                vMin = Math.Min(vMin, Math.Min(s.Setpoints.Min(), s.Gyros.Min()));
                vMax = Math.Max(vMax, Math.Max(s.Setpoints.Max(), s.Gyros.Max()));
            }
            double vRange = vMax - vMin;
            vMin -= vRange * 0.05;
            vMax += vRange * 0.05;

            double ScaleX(double t) => ((t - tMin) / (tMax - tMin) * canvasWidth * _zoomX) + _panX;
            double ScaleY(double v) => ((vMax - v) / (vMax - vMin) * canvasHeight * _zoomY) + _panY;

            DrawGridAndAxes(tMin, tMax, vMin, vMax, canvasWidth, canvasHeight, ScaleX, ScaleY);

            foreach (var session in sessions)
            {
                DrawPolyline(session.Times, session.Setpoints, ScaleX, ScaleY, Brushes.Blue, 1.5, LineStyle.Dash);
                DrawPolyline(session.Times, session.Gyros, ScaleX, ScaleY, Brushes.Red, 1.5, LineStyle.Solid);
            }
        }

        private void DrawGridAndAxes(double tMin, double tMax, double vMin, double vMax,
                                     double canvasWidth, double canvasHeight,
                                     Func<double, double> scaleX, Func<double, double> scaleY)
        {
            int xTicks = 10, yTicks = 8;
            double tStep = (tMax - tMin) / xTicks;
            double vStep = (vMax - vMin) / yTicks;

            for (int i = 0; i <= yTicks; i++)
            {
                double v = vMin + i * vStep;
                double y = scaleY(v);
                if (y >= 0 && y <= canvasHeight)
                {
                    PlotCanvas.Children.Add(new Line
                    {
                        StartPoint = new Point(0, y),
                        EndPoint = new Point(canvasWidth, y),
                        Stroke = _gridBrush,
                        StrokeThickness = 0.5
                    });
                    var label = new TextBlock { Text = v.ToString("F0"), FontSize = 10, Foreground = _textBrush };
                    Canvas.SetLeft(label, 2);
                    Canvas.SetTop(label, y - 8);
                    PlotCanvas.Children.Add(label);
                }
            }

            for (int i = 0; i <= xTicks; i++)
            {
                double t = tMin + i * tStep;
                double x = scaleX(t);
                if (x >= 0 && x <= canvasWidth)
                {
                    PlotCanvas.Children.Add(new Line
                    {
                        StartPoint = new Point(x, 0),
                        EndPoint = new Point(x, canvasHeight),
                        Stroke = _gridBrush,
                        StrokeThickness = 0.5
                    });
                    var label = new TextBlock { Text = t.ToString("F1") + "s", FontSize = 10, Foreground = _textBrush };
                    Canvas.SetLeft(label, x + 2);
                    Canvas.SetTop(label, canvasHeight - 16);
                    PlotCanvas.Children.Add(label);
                }
            }

            double originY = scaleY(0);
            if (originY >= 0 && originY <= canvasHeight)
                PlotCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(0, originY),
                    EndPoint = new Point(canvasWidth, originY),
                    Stroke = _axisBrush,
                    StrokeThickness = 1.5
                });

            double originX = scaleX(0);
            if (originX >= 0 && originX <= canvasWidth)
                PlotCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(originX, 0),
                    EndPoint = new Point(originX, canvasHeight),
                    Stroke = _axisBrush,
                    StrokeThickness = 1.5
                });
        }

        private void DrawPolyline(double[] times, double[] values,
                          Func<double, double> scaleX, Func<double, double> scaleY,
                          IBrush brush, double thickness, LineStyle style)
        {
            var points = new List<Point>();
            double canvasWidth = PlotCanvas.Bounds.Width;
            double canvasHeight = PlotCanvas.Bounds.Height;

            // Адаптивный шаг
            double maxZoom = Math.Max(_zoomX, _zoomY);
            int targetPoints = (int)(canvasWidth * maxZoom);
            int step = Math.Max(1, values.Length / Math.Max(1, targetPoints * 2));

            for (int i = 0; i < values.Length; i += step)
            {
                double x = scaleX(times[i]);
                double y = scaleY(values[i]);

                if (x >= -10000 && x <= canvasWidth + 10000 &&
                    y >= -10000 && y <= canvasHeight + 10000)
                {
                    points.Add(new Point(x, y));
                }
                else if (points.Count > 1)
                {
                    PlotCanvas.Children.Add(new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = thickness,
                        StrokeDashArray = style == LineStyle.Dash ? new AvaloniaList<double> { 4, 2 } : null,
                        Points = new AvaloniaList<Point>(points)
                    });
                    points.Clear();
                }
            }

            if (points.Count > 1)
            {
                PlotCanvas.Children.Add(new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = thickness,
                    StrokeDashArray = style == LineStyle.Dash ? new AvaloniaList<double> { 4, 2 } : null,
                    Points = new AvaloniaList<Point>(points)
                });
            }
        }

        private void OnPlotCanvasSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            PlotCanvas.SizeChanged -= OnPlotCanvasSizeChanged;
            DrawPlot();
        }

        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            var mousePos = e.GetPosition(PlotCanvas);
            double zoomDelta = e.Delta.Y > 0 ? ZoomSpeed : 1.0 / ZoomSpeed;
            var modifiers = e.KeyModifiers;

            if (modifiers.HasFlag(KeyModifiers.Shift))
            {
                double newZoomX = Math.Clamp(_zoomX * zoomDelta, MinZoom, MaxZoom);
                _panX = mousePos.X - (mousePos.X - _panX) * (newZoomX / _zoomX);
                _zoomX = newZoomX;
            }
            else if (modifiers.HasFlag(KeyModifiers.Control))
            {
                double newZoomY = Math.Clamp(_zoomY * zoomDelta, MinZoom, MaxZoom);
                _panY = mousePos.Y - (mousePos.Y - _panY) * (newZoomY / _zoomY);
                _zoomY = newZoomY;
            }
            else
            {
                double newZoomX = Math.Clamp(_zoomX * zoomDelta, MinZoom, MaxZoom);
                double newZoomY = Math.Clamp(_zoomY * zoomDelta, MinZoom, MaxZoom);
                _panX = mousePos.X - (mousePos.X - _panX) * (newZoomX / _zoomX);
                _panY = mousePos.Y - (mousePos.Y - _panY) * (newZoomY / _zoomY);
                _zoomX = newZoomX;
                _zoomY = newZoomY;
            }

            DrawPlot();
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(PlotCanvas);
            var mousePos = e.GetPosition(PlotCanvas);

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
                PlotCanvas.Children.Add(_zoomRectangle);
            }
            else if (point.Properties.IsLeftButtonPressed)
            {
                _isPanning = true;
                _lastMousePosition = mousePos;
            }
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            var mousePos = e.GetPosition(PlotCanvas);

            if (_isZoomSelecting && _zoomRectangle != null)
            {
                double x = Math.Min(_zoomStartPoint.X, mousePos.X);
                double y = Math.Min(_zoomStartPoint.Y, mousePos.Y);
                double width = Math.Abs(mousePos.X - _zoomStartPoint.X);
                double height = Math.Abs(mousePos.Y - _zoomStartPoint.Y);

                Canvas.SetLeft(_zoomRectangle, x);
                Canvas.SetTop(_zoomRectangle, y);
                _zoomRectangle.Width = width;
                _zoomRectangle.Height = height;
                return;
            }

            if (_isPanning)
            {
                double deltaX = mousePos.X - _lastMousePosition.X;
                double deltaY = mousePos.Y - _lastMousePosition.Y;
                _panX += deltaX;
                _panY += deltaY;
                _lastMousePosition = mousePos;
                DrawPlot();
                return;
            }

            UpdateCrosshair(mousePos);
        }

        private void UpdateCrosshair(Point mousePos)
        {
            if (_crosshairX == null || _crosshairY == null || _tooltipBorder == null || _tooltipText == null)
                return;

            double canvasWidth = PlotCanvas.Bounds.Width;
            double canvasHeight = PlotCanvas.Bounds.Height;
            if (canvasWidth <= 0 || canvasHeight <= 0)
            {
                _crosshairX.IsVisible = false;
                _crosshairY.IsVisible = false;
                _tooltipBorder.IsVisible = false;
                return;
            }

            double clampedX = Math.Clamp(mousePos.X, 0, canvasWidth);
            double clampedY = Math.Clamp(mousePos.Y, 0, canvasHeight);

            // Рисуем линии крестовины
            _crosshairX.StartPoint = new Point(clampedX, 0);
            _crosshairX.EndPoint = new Point(clampedX, canvasHeight);
            _crosshairX.IsVisible = true;

            _crosshairY.StartPoint = new Point(0, clampedY);
            _crosshairY.EndPoint = new Point(canvasWidth, clampedY);
            _crosshairY.IsVisible = true;

            var (sessions, tMin, tMax) = _viewModel.GetPlotData();
            if (sessions.Count == 0) return;

            // Границы по Y (глобальные)
            double vMin = double.MaxValue, vMax = double.MinValue;
            foreach (var s in sessions)
            {
                vMin = Math.Min(vMin, Math.Min(s.Setpoints.Min(), s.Gyros.Min()));
                vMax = Math.Max(vMax, Math.Max(s.Setpoints.Max(), s.Gyros.Max()));
            }
            double vRange = vMax - vMin;
            vMin -= vRange * 0.05;
            vMax += vRange * 0.05;

            // Значения под курсором
            double timeAtCursor = tMin + (clampedX - _panX) / (_zoomX * canvasWidth) * (tMax - tMin);
            double valueAtCursor = vMax - (clampedY - _panY) / (_zoomY * canvasHeight) * (vMax - vMin);

            // Поиск ближайшей точки с абсолютной точностью (бинарный поиск)
            double bestSetpoint = 0, bestGyro = 0;
            bool found = false;

            if (sessions.Count == 1)
            {
                found = FindClosestInSession(sessions[0], timeAtCursor, out bestSetpoint, out bestGyro);
            }
            else
            {
                // Ищем сессию, в которую попадает время курсора
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (s.Times.Length == 0) continue;
                    double sMin = s.Times[0];
                    double sMax = s.Times[^1];
                    if (timeAtCursor >= sMin && timeAtCursor <= sMax)
                    {
                        found = FindClosestInSession(s, timeAtCursor, out bestSetpoint, out bestGyro);
                        break;
                    }
                }
                // Если не попали ни в одну сессию, ищем ближайшую границу
                if (!found)
                {
                    double minGlobalDist = double.MaxValue;
                    foreach (var s in sessions)
                    {
                        if (s.Times.Length == 0) continue;
                        double distToStart = Math.Abs(s.Times[0] - timeAtCursor);
                        double distToEnd = Math.Abs(s.Times[^1] - timeAtCursor);
                        double dist = Math.Min(distToStart, distToEnd);
                        if (dist < minGlobalDist)
                        {
                            minGlobalDist = dist;
                            found = FindClosestInSession(s, timeAtCursor, out bestSetpoint, out bestGyro);
                        }
                    }
                }
            }

            // Если точку найти не удалось (пустые сессии), используем значение под курсором
            if (!found)
            {
                bestSetpoint = valueAtCursor;
                bestGyro = valueAtCursor;
            }

            string tooltip = $"X: {timeAtCursor:F2}s\nY: {valueAtCursor:F1}\nSP: {bestSetpoint:F1}\nGyro: {bestGyro:F1}";
            _tooltipText.Text = tooltip;

            double offset = 10;
            double tooltipX = clampedX + offset;
            double tooltipY = clampedY + offset;
            if (tooltipX + 100 > canvasWidth) tooltipX = clampedX - offset - 100;
            if (tooltipY + 50 > canvasHeight) tooltipY = clampedY - offset - 50;
            Canvas.SetLeft(_tooltipBorder, tooltipX);
            Canvas.SetTop(_tooltipBorder, tooltipY);
            _tooltipBorder.IsVisible = true;
        }

        // Вспомогательный метод точного поиска в одной сессии
        private static bool FindClosestInSession(SessionPlotData session, double time, out double setpoint, out double gyro)
        {
            setpoint = gyro = 0;
            if (session.Times.Length == 0) return false;

            int index = Array.BinarySearch(session.Times, time);
            if (index < 0)
            {
                index = ~index; // ближайший больший элемент
                if (index >= session.Times.Length)
                    index = session.Times.Length - 1;
                else if (index > 0)
                {
                    double dist1 = Math.Abs(session.Times[index - 1] - time);
                    double dist2 = Math.Abs(session.Times[index] - time);
                    if (dist1 < dist2)
                        index = index - 1;
                }
            }

            setpoint = session.Setpoints[index];
            gyro = session.Gyros[index];
            return true;
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            var mousePos = e.GetPosition(PlotCanvas);

            if (_isZoomSelecting)
            {
                _isZoomSelecting = false;
                if (_zoomRectangle != null)
                {
                    PlotCanvas.Children.Remove(_zoomRectangle);

                    double width = Math.Abs(mousePos.X - _zoomStartPoint.X);
                    double height = Math.Abs(mousePos.Y - _zoomStartPoint.Y);

                    if (width > 10 && height > 10)
                    {
                        double x = Math.Min(_zoomStartPoint.X, mousePos.X);
                        double y = Math.Min(_zoomStartPoint.Y, mousePos.Y);
                        double canvasWidth = PlotCanvas.Bounds.Width;
                        double canvasHeight = PlotCanvas.Bounds.Height;

                        double newZoomX = canvasWidth / width;
                        double newZoomY = canvasHeight / height;
                        newZoomX = Math.Clamp(newZoomX, MinZoom, MaxZoom);
                        newZoomY = Math.Clamp(newZoomY, MinZoom, MaxZoom);

                        _zoomX = newZoomX;
                        _zoomY = newZoomY;
                        _panX = -x * newZoomX;
                        _panY = -y * newZoomY;
                    }
                    else
                    {
                        ResetZoom();
                    }

                    _zoomRectangle = null;
                    DrawPlot();
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
    }
}