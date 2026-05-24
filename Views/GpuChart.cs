using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using System;
using System.Collections.Generic;
using ƎPIDGRAPH.ViewModels;

namespace ƎPIDGRAPH.Views
{
    public class GpuChart : Control
    {
        public List<SessionPlotData>? Sessions { get; set; }
        public double TMin { get; set; }
        public double TMax { get; set; }
        public double VMin { get; set; }
        public double VMax { get; set; }
        public double ZoomX { get; set; } = 1.0;
        public double ZoomY { get; set; } = 1.0;
        public double PanX { get; set; }
        public double PanY { get; set; }

        private WriteableBitmap? _bitmap;
        private SKSurface? _surface;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (Sessions is null || Sessions.Count == 0) return;

            int width = Math.Max(1, (int)Bounds.Width);
            int height = Math.Max(1, (int)Bounds.Height);

            // Создаём/пересоздаём битмап и поверхность при изменении размеров
            if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
            {
                _surface?.Dispose();
                _bitmap?.Dispose();

                _bitmap = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);

                using var locked = _bitmap.Lock();
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                _surface = SKSurface.Create(info, locked.Address, locked.RowBytes);
            }

            var skCanvas = _surface!.Canvas;
            skCanvas.Clear(SKColors.White);

            double ScaleX(double t) => ((t - TMin) / (TMax - TMin) * width * ZoomX) + PanX;
            double ScaleY(double v) => ((VMax - v) / (VMax - VMin) * height * ZoomY) + PanY;

            DrawGrid(skCanvas, width, height, ScaleX, ScaleY);
            DrawAxes(skCanvas, width, height, ScaleX, ScaleY);

            foreach (var session in Sessions)
            {
                DrawDataLine(skCanvas, session.Times, session.Setpoints, ScaleX, ScaleY, SKColors.Blue, true);
                DrawDataLine(skCanvas, session.Times, session.Gyros, ScaleX, ScaleY, SKColors.Red, false);
            }

            skCanvas.Flush();
            context.DrawImage(_bitmap, new Rect(0, 0, width, height));
        }

        private void DrawGrid(SKCanvas canvas, double width, double height,
                              Func<double, double> scaleX, Func<double, double> scaleY)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.LightGray,
                StrokeWidth = 0.5f,
                Style = SKPaintStyle.Stroke
            };

            int yTicks = 8;
            double vStep = (VMax - VMin) / yTicks;
            for (int i = 0; i <= yTicks; i++)
            {
                double v = VMin + i * vStep;
                double y = scaleY(v);
                if (y >= 0 && y <= height)
                    canvas.DrawLine(0, (float)y, (float)width, (float)y, paint);
            }

            int xTicks = 10;
            double tStep = (TMax - TMin) / xTicks;
            for (int i = 0; i <= xTicks; i++)
            {
                double t = TMin + i * tStep;
                double x = scaleX(t);
                if (x >= 0 && x <= width)
                    canvas.DrawLine((float)x, 0, (float)x, (float)height, paint);
            }
        }

        private void DrawAxes(SKCanvas canvas, double width, double height,
                              Func<double, double> scaleX, Func<double, double> scaleY)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                StrokeWidth = 1.5f,
                Style = SKPaintStyle.Stroke
            };

            double originY = scaleY(0);
            if (originY >= 0 && originY <= height)
                canvas.DrawLine(0, (float)originY, (float)width, (float)originY, paint);

            double originX = scaleX(0);
            if (originX >= 0 && originX <= width)
                canvas.DrawLine((float)originX, 0, (float)originX, (float)height, paint);
        }

        private void DrawDataLine(SKCanvas canvas, double[] times, double[] values,
                                  Func<double, double> scaleX, Func<double, double> scaleY,
                                  SKColor color, bool isDashed)
        {
            if (times.Length == 0) return;

            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = 1.5f,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true
            };
            if (isDashed)
                paint.PathEffect = SKPathEffect.CreateDash(new float[] { 4, 2 }, 0);

            double maxZoom = Math.Max(ZoomX, ZoomY);
            int targetPoints = (int)(Bounds.Width * maxZoom);
            int step = Math.Max(1, values.Length / Math.Max(1, targetPoints * 2));

            var path = new SKPath();
            bool first = true;
            for (int i = 0; i < values.Length; i += step)
            {
                double x = scaleX(times[i]);
                double y = scaleY(values[i]);
                if (x >= -10000 && x <= Bounds.Width + 10000 &&
                    y >= -10000 && y <= Bounds.Height + 10000)
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
    }
}