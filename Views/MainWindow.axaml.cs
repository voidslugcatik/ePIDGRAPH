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

        private GpuChart? _plotControl;
        private Canvas? _interactionCanvas;

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

            _plotControl = this.FindControl<GpuChart>("PlotControl");
            _interactionCanvas = this.FindControl<Canvas>("InteractionCanvas");

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
            if (_plotControl == null) return;
            var (sessions, tMin, tMax) = _viewModel.GetPlotData();
            if (sessions.Count == 0) return;

            double vMin = double.MaxValue, vMax = double.MinValue;
            foreach (var s in sessions)
            {
                vMin = Math.Min(vMin, Math.Min(s.Setpoints.Min(), s.Gyros.Min()));
                vMax = Math.Max(vMax, Math.Max(s.Setpoints.Max(), s.Gyros.Max()));
            }
            double vRange = vMax - vMin;
            _plotControl.VMin = vMin - vRange * 0.05;
            _plotControl.VMax = vMax + vRange * 0.05;
            _plotControl.TMin = tMin;
            _plotControl.TMax = tMax;
            _plotControl.Sessions = sessions;

            ResetZoom();
            ApplyZoomToControl();
        }

        private void ApplyZoomToControl()
        {
            if (_plotControl == null) return;
            _plotControl.ZoomX = _zoomX;
            _plotControl.ZoomY = _zoomY;
            _plotControl.PanX = _panX;
            _plotControl.PanY = _panY;
            _plotControl.InvalidateVisual();
        }

        private void ResetZoom()
        {
            _zoomX = 1.0;
            _zoomY = 1.0;
            _panX = 0;
            _panY = 0;
        }

        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (_interactionCanvas == null) return;
            var mousePos = e.GetPosition(_interactionCanvas);
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

            ApplyZoomToControl();
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
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
            var mousePos = e.GetPosition(_interactionCanvas);

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
                return;
            }

            UpdateCrosshair(mousePos);
        }

        private void UpdateCrosshair(Point mousePos)
        {
            if (_crosshairX == null || _crosshairY == null || _tooltipBorder == null || _tooltipText == null)
                return;

            double canvasWidth = _interactionCanvas.Bounds.Width;
            double canvasHeight = _interactionCanvas.Bounds.Height;
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
            var mousePos = e.GetPosition(_interactionCanvas);

            if (_isZoomSelecting)
            {
                _isZoomSelecting = false;
                if (_zoomRectangle != null)
                {
                    _interactionCanvas.Children.Remove(_zoomRectangle);

                    double width = Math.Abs(mousePos.X - _zoomStartPoint.X);
                    double height = Math.Abs(mousePos.Y - _zoomStartPoint.Y);

                    if (width > 10 && height > 10)
                    {
                        double x = Math.Min(_zoomStartPoint.X, mousePos.X);
                        double y = Math.Min(_zoomStartPoint.Y, mousePos.Y);
                        double canvasWidth = _interactionCanvas.Bounds.Width;
                        double canvasHeight = _interactionCanvas.Bounds.Height;

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