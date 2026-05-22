namespace ƎPIDGRAPH.Models
{
    public record FlightRecord
    {
        public double Time { get; init; }
        public double GyroRoll { get; init; }
        public double SetpointRoll { get; init; }
        public double GyroPitch { get; init; }
        public double SetpointPitch { get; init; }
        public double GyroYaw { get; init; }
        public double SetpointYaw { get; init; }
    }
}