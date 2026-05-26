using CommunityToolkit.Mvvm.ComponentModel;
using ƎPIDGRAPH.Models;
using ƎPIDGRAPH.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ƎPIDGRAPH.ViewModels
{
    public class SessionPlotData
    {
        public double[] Times { get; init; } = Array.Empty<double>();
        public double[] Setpoints { get; init; } = Array.Empty<double>();
        public double[] Gyros { get; init; } = Array.Empty<double>();
    }

    public partial class MainWindowViewModel : ObservableObject
    {
        private readonly IBBLFileService _bbFileService;

        [ObservableProperty]
        private string _statusText = "Готов";

        [ObservableProperty]
        private string _selectedAxis = "Roll";

        public List<string> AxisOptions { get; } = new() { "Roll", "Pitch", "Yaw" };

        // Кэшированные данные
        private double[]? _times;
        private double[]? _setpointRoll, _gyroRoll;
        private double[]? _setpointPitch, _gyroPitch;
        private double[]? _setpointYaw, _gyroYaw;

        public event Action? PlotDataChanged;

        public MainWindowViewModel(IBBLFileService bbFileService)
        {
            _bbFileService = bbFileService;
        }

        private List<SessionPlotData> _sessions = new();
        private double _globalTMin, _globalTMax;

        public async Task LoadFilesAsync(IEnumerable<string> filePaths)
        {
            if (filePaths == null || !filePaths.Any())
            {
                StatusText = "Файлы не выбраны";
                return;
            }

            StatusText = "Загрузка...";
            try
            {
                var sessions = await _bbFileService.LoadMultipleAsync(filePaths);

                _sessions.Clear();
                double globalMin = double.MaxValue, globalMax = double.MinValue;
                var allRecords = new List<FlightRecord>();

                foreach (var session in sessions)
                {
                    if (session.Records.Count == 0) continue;

                    double minTime = session.Records.Min(r => r.Time);
                    double maxTime = session.Records.Max(r => r.Time);
                    // Общие глобальные границы по всем сессиям (в микросекундах)
                    globalMin = Math.Min(globalMin, minTime);
                    globalMax = Math.Max(globalMax, maxTime);

                    var times = new double[session.Records.Count];
                    var setpoints = new double[session.Records.Count];
                    var gyros = new double[session.Records.Count];

                    for (int i = 0; i < session.Records.Count; i++)
                    {
                        var r = session.Records[i];
                        // Время храним как есть (в микросекундах), потом преобразуем в секунды при отрисовке
                        times[i] = r.Time;
                        setpoints[i] = GetSetpointForAxis(r);
                        gyros[i] = GetGyroForAxis(r);
                        allRecords.Add(r);
                    }

                    _sessions.Add(new SessionPlotData
                    {
                        Times = times,
                        Setpoints = setpoints,
                        Gyros = gyros
                    });
                }

                // Глобальные границы времени в секундах
                if (_sessions.Count > 0)
                {
                    _globalTMin = globalMin / 1_000_000.0;
                    _globalTMax = globalMax / 1_000_000.0;
                }

                // Обратная совместимость (если нужно)
                _times = allRecords.Select(r => r.Time / 1e6).ToArray();
                _setpointRoll = allRecords.Select(r => r.SetpointRoll).ToArray();
                _gyroRoll = allRecords.Select(r => r.GyroRoll).ToArray();
                _setpointPitch = allRecords.Select(r => r.SetpointPitch).ToArray();
                _gyroPitch = allRecords.Select(r => r.GyroPitch).ToArray();
                _setpointYaw = allRecords.Select(r => r.SetpointYaw).ToArray();
                _gyroYaw = allRecords.Select(r => r.GyroYaw).ToArray();

                StatusText = $"Загружено {allRecords.Count} записей из {sessions.Count} сессий";
                PlotDataChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки: {ex.Message}");
                StatusText = "Ошибка загрузки";
            }
        }

        // Вспомогательные методы (добавьте в класс MainWindowViewModel):
        private double GetSetpointForAxis(FlightRecord r) => SelectedAxis switch
        {
            "Pitch" => r.SetpointPitch,
            "Yaw" => r.SetpointYaw,
            _ => r.SetpointRoll
        };

        private double GetGyroForAxis(FlightRecord r) => SelectedAxis switch
        {
            "Pitch" => r.GyroPitch,
            "Yaw" => r.GyroYaw,
            _ => r.GyroRoll
        };

        partial void OnSelectedAxisChanged(string value)
        {
            PlotDataChanged?.Invoke();
        }

        public (List<SessionPlotData> sessions, double tMin, double tMax) GetPlotData()
        {
            return (_sessions, _globalTMin, _globalTMax);
        }
    }
}