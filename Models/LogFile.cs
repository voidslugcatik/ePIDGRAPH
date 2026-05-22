using System.Collections.Generic;

namespace ƎPIDGRAPH.Models
{
    public class LogFile
    {
        public string FilePath { get; init; } = string.Empty;
        public List<FlightRecord> Records { get; init; } = [];
    }
}