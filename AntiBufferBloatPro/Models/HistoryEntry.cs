using System;

namespace AntiBufferBloatPro.Models
{
    public sealed class HistoryEntry
    {
        public DateTime Timestamp { get; set; }
        public BufferbloatTestResult Result { get; set; } = new();
        public string Recommendation { get; set; } = "";
        public string FilePath { get; set; } = "";
    }
}
