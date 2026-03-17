namespace AntiBufferBloatPro.Models
{
    public sealed class AppSettings
    {
        public int PhaseDurationSeconds { get; set; } = 20;
        public string PingTarget { get; set; } = "1.1.1.1";
        public bool DimMode { get; set; } = false;
    }
}
