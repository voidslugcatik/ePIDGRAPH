using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ƎPIDGRAPH.Models;

namespace ƎPIDGRAPH.Services
{
    public class BBLFileService : IBBLFileService
    {
        public async Task<List<LogFile>> LoadMultipleAsync(IEnumerable<string> bblPaths)
        {
            var allSessions = new List<LogFile>();
            foreach (var bblPath in bblPaths)
            {
                string tempWorkDir = Path.Combine(Path.GetTempPath(), "ePIDGRAPH", Path.GetRandomFileName());
                Directory.CreateDirectory(tempWorkDir);
                try
                {
                    string tempBblPath = Path.Combine(tempWorkDir, Path.GetFileName(bblPath));
                    File.Copy(bblPath, tempBblPath, overwrite: true);

                    await RunBlackboxDecode(tempBblPath);

                    var csvFiles = FindAllCsvFiles(tempWorkDir, Path.GetFileNameWithoutExtension(tempBblPath));
                    foreach (var csvFile in csvFiles)
                    {
                        allSessions.Add(ParseCsvFile(csvFile, bblPath));
                    }
                }
                finally
                {
                    if (Directory.Exists(tempWorkDir))
                        Directory.Delete(tempWorkDir, true);
                }
            }
            return allSessions;
        }

        private List<string> FindAllCsvFiles(string directoryPath, string baseFileName)
        {
            return Directory.GetFiles(directoryPath, $"{baseFileName}*.csv")
                            .Where(f => !f.Contains(".event.", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(f => f)
                            .ToList();
        }

        private async Task RunBlackboxDecode(string bblPath)
        {
            string toolName = OperatingSystem.IsWindows() ? "blackbox_decode.exe" : "blackbox_decode";
            string? toolPath = FindTool(toolName);
            if (toolPath is null)
                throw new FileNotFoundException($"Утилита {toolName} не найдена.");

            string bblDir = Path.GetDirectoryName(bblPath)!;

            var psi = new ProcessStartInfo
            {
                FileName = toolPath,
                Arguments = $"\"{bblPath}\"",
                WorkingDirectory = bblDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Debug.WriteLine($"[BBLService] Запуск: {toolPath} {psi.Arguments}");
            Debug.WriteLine($"[BBLService] Рабочая папка: {bblDir}");

            using var process = Process.Start(psi)!;
            string stdOut = await process.StandardOutput.ReadToEndAsync();
            string stdErr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Debug.WriteLineIf(!string.IsNullOrEmpty(stdOut), $"[stdout] {stdOut}");
            Debug.WriteLineIf(!string.IsNullOrEmpty(stdErr), $"[stderr] {stdErr}");
            Debug.WriteLine($"[BBLService] Exit code: {process.ExitCode}");

            if (process.ExitCode != 0)
                throw new Exception($"blackbox_decode завершился с ошибкой (код {process.ExitCode}). Stderr: {stdErr}");
        }

        private LogFile ParseCsvFile(string csvPath, string originalBblPath)
        {
            var records = new List<FlightRecord>();

            using var reader = new StreamReader(csvPath);
            string? headerLine = reader.ReadLine();
            if (string.IsNullOrEmpty(headerLine))
                throw new InvalidDataException("CSV-файл пуст или не содержит заголовка.");

            string[] headers = headerLine.Split(',');

            int timeIdx = Array.IndexOf(headers, "time (us)");
            int gyroRollIdx = Array.IndexOf(headers, "gyroADC[0]");
            int setpointRollIdx = Array.IndexOf(headers, "setpoint[0]");
            int gyroPitchIdx = Array.IndexOf(headers, "gyroADC[1]");
            int setpointPitchIdx = Array.IndexOf(headers, "setpoint[1]");
            int gyroYawIdx = Array.IndexOf(headers, "gyroADC[2]");
            int setpointYawIdx = Array.IndexOf(headers, "setpoint[2]");

            if (timeIdx < 0 || gyroRollIdx < 0 || setpointRollIdx < 0 ||
                gyroPitchIdx < 0 || setpointPitchIdx < 0 ||
                gyroYawIdx < 0 || setpointYawIdx < 0)
                throw new InvalidDataException("Не найдены необходимые колонки в CSV-заголовке.");

            while (!reader.EndOfStream)
            {
                string? line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                string[] parts = line.Split(',');
                if (parts.Length <= Math.Max(timeIdx,
                    Math.Max(gyroRollIdx, Math.Max(setpointRollIdx,
                    Math.Max(gyroPitchIdx, Math.Max(setpointPitchIdx,
                    Math.Max(gyroYawIdx, setpointYawIdx)))))))
                    continue;

                if (double.TryParse(parts[timeIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double time) &&
                    double.TryParse(parts[gyroRollIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double gyroRoll) &&
                    double.TryParse(parts[setpointRollIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double setpointRoll) &&
                    double.TryParse(parts[gyroPitchIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double gyroPitch) &&
                    double.TryParse(parts[setpointPitchIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double setpointPitch) &&
                    double.TryParse(parts[gyroYawIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double gyroYaw) &&
                    double.TryParse(parts[setpointYawIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double setpointYaw))
                {
                    records.Add(new FlightRecord
                    {
                        Time = time,
                        GyroRoll = gyroRoll,
                        SetpointRoll = setpointRoll,
                        GyroPitch = gyroPitch,
                        SetpointPitch = setpointPitch,
                        GyroYaw = gyroYaw,
                        SetpointYaw = setpointYaw
                    });
                }
            }

            return new LogFile { FilePath = originalBblPath, Records = records };
        }

        private string? FindTool(string toolName)
        {
            string toolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
            string toolsPath = Path.Combine(toolsDir, toolName);
            if (File.Exists(toolsPath))
                return toolsPath;

            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathVar.Split(Path.PathSeparator))
            {
                string fullPath = Path.Combine(dir, toolName);
                if (File.Exists(fullPath))
                    return fullPath;
            }
            return null;
        }
    }
}