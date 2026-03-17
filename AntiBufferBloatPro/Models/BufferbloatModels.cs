namespace AntiBufferBloatPro.Models
{
    public sealed class PingSample
    {
        public long LatencyMs { get; init; }
        public bool Success { get; init; }
    }

    public sealed class LoadTestPhaseResult
    {
        public string PhaseName { get; init; } = "";
        public double AverageLatencyMs { get; init; }
        public double P95LatencyMs { get; init; }
        public double JitterMs { get; init; }
        public double PacketLossPercent { get; init; }
        public double ThroughputMbps { get; init; }
        public int SampleCount { get; init; }
    }

    public sealed class BufferbloatTestResult
    {
        public LoadTestPhaseResult Idle { get; init; } = new();
        public LoadTestPhaseResult Download { get; init; } = new();
        public LoadTestPhaseResult Upload { get; init; } = new();
        public string Grade { get; init; } = "--";
        public string PrimaryBottleneck { get; init; } = "--";
        public double WorstLatencyIncreaseMs { get; init; }
        public string Summary { get; init; } = "";
    }

    public sealed class ShapingRecommendation
    {
        public bool RecommendDownloadLimit { get; init; }
        public bool RecommendUploadLimit { get; init; }
        public double? DownloadLimitMbps { get; init; }
        public double? UploadLimitMbps { get; init; }
        public string Reasoning { get; init; } = "";
    }

    public sealed class TestProgressUpdate
    {
        public string PhaseName { get; init; } = "";
        public double ProgressPercent { get; init; }
        public string Detail { get; init; } = "";
        public long? LatestLatencyMs { get; init; }
    }
}
