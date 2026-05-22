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
                // 1. Загружаем все сессии через сервис
                var sessions = await _bbFileService.LoadMultipleAsync(filePaths);

                // 2. Подготовка структур для сессий и общей статистики
                _sessions.Clear();
                double currentOffset = 0.0;
                const double gapBetweenSessions = 2.0; // секунды
                var allRecords = new List<FlightRecord>();

                foreach (var session in sessions)
                {
                    if (session.Records.Count == 0) continue;

                    // Вычисляем длительность сессии в микросекундах
                    double minTime = session.Records.Min(r => r.Time);
                    double maxTime = session.Records.Max(r => r.Time);
                    double durationSec = (maxTime - minTime) / 1_000_000.0;

                    // Массивы для одной сессии
                    var times = new double[session.Records.Count];
                    var setpoints = new double[session.Records.Count];
                    var gyros = new double[session.Records.Count];

                    for (int i = 0; i < session.Records.Count; i++)
                    {
                        var r = session.Records[i];
                        // Нормализованное время: начинается с 0 + смещение currentOffset
                        times[i] = currentOffset + (r.Time - minTime) / 1_000_000.0;
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

                    // Смещаем следующую сессию
                    currentOffset += durationSec + gapBetweenSessions;
                }

                // 3. Глобальные границы времени (для осей X)
                if (_sessions.Count > 0)
                {
                    _globalTMin = 0.0;
                    _globalTMax = currentOffset - gapBetweenSessions; // последний разрыв не нужен
                }

                // 4. Сохраняем общий массив для обратной совместимости (если где-то ещё используется)
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