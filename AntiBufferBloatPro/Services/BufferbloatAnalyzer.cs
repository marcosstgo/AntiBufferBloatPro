using System;
using System.Collections.Generic;
using System.Linq;
using AntiBufferBloatPro.Models;

namespace AntiBufferBloatPro.Services
{
    public sealed class BufferbloatAnalyzer
    {
        public BufferbloatTestResult Analyze(
            IReadOnlyList<PingSample> idleSamples,
            IReadOnlyList<PingSample> downloadSamples,
            IReadOnlyList<PingSample> uploadSamples,
            double measuredDownloadMbps,
            double measuredUploadMbps)
        {
            var idle = BuildPhase("Idle", idleSamples, 0);
            var download = BuildPhase("Download", downloadSamples, measuredDownloadMbps);
            var upload = BuildPhase("Upload", uploadSamples, measuredUploadMbps);

            var downloadIncrease = Math.Max(0, download.P95LatencyMs - idle.AverageLatencyMs);
            var uploadIncrease = Math.Max(0, upload.P95LatencyMs - idle.AverageLatencyMs);
            var worstIncrease = Math.Max(downloadIncrease, uploadIncrease);

            var bottleneck = downloadIncrease >= uploadIncrease ? "Download" : "Upload";
            var grade = GetGrade(worstIncrease);

            return new BufferbloatTestResult
            {
                Idle = idle,
                Download = download,
                Upload = upload,
                Grade = grade,
                PrimaryBottleneck = worstIncrease <= 10 ? "Ninguno" : bottleneck,
                WorstLatencyIncreaseMs = worstIncrease,
                Summary = BuildSummary(grade, worstIncrease, bottleneck)
            };
        }

        public ShapingRecommendation BuildRecommendation(BufferbloatTestResult result)
        {
            var recommendDownload = result.Download.ThroughputMbps > 0 && result.PrimaryBottleneck == "Download";
            var recommendUpload = result.Upload.ThroughputMbps > 0 && result.PrimaryBottleneck == "Upload";

            return new ShapingRecommendation
            {
                RecommendDownloadLimit = recommendDownload,
                RecommendUploadLimit = recommendUpload,
                DownloadLimitMbps = recommendDownload ? Math.Round(result.Download.ThroughputMbps * 0.92, 2) : (double?)null,
                UploadLimitMbps = recommendUpload ? Math.Round(result.Upload.ThroughputMbps * 0.90, 2) : (double?)null,
                Reasoning = BuildReasoning(result, recommendDownload, recommendUpload)
            };
        }

        private static LoadTestPhaseResult BuildPhase(string phaseName, IReadOnlyList<PingSample> samples, double throughputMbps)
        {
            var successful = samples.Where(s => s.Success).Select(s => (double)s.LatencyMs).OrderBy(v => v).ToArray();

            if (successful.Length == 0)
                return new LoadTestPhaseResult { PhaseName = phaseName, ThroughputMbps = throughputMbps, PacketLossPercent = 100, SampleCount = samples.Count };

            return new LoadTestPhaseResult
            {
                PhaseName = phaseName,
                AverageLatencyMs = successful.Average(),
                P95LatencyMs = Percentile(successful, 0.95),
                JitterMs = CalculateJitter(successful),
                PacketLossPercent = samples.Count(s => !s.Success) * 100.0 / samples.Count,
                ThroughputMbps = throughputMbps,
                SampleCount = samples.Count
            };
        }

        private static double CalculateJitter(IReadOnlyList<double> values)
        {
            if (values.Count < 2) return 0;
            double total = 0;
            for (int i = 1; i < values.Count; i++)
                total += Math.Abs(values[i] - values[i - 1]);
            return total / (values.Count - 1);
        }

        private static double Percentile(IReadOnlyList<double> sorted, double p)
        {
            if (sorted.Count == 1) return sorted[0];
            var pos = (sorted.Count - 1) * p;
            var lo = (int)Math.Floor(pos);
            var hi = (int)Math.Ceiling(pos);
            if (lo == hi) return sorted[lo];
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }

        private static string GetGrade(double ms) => ms switch
        {
            <= 10 => "A",
            <= 25 => "B",
            <= 50 => "C",
            <= 100 => "D",
            _ => "F"
        };

        private static string BuildSummary(string grade, double worstMs, string bottleneck)
        {
            if (worstMs <= 10)
                return "La latencia bajo carga se mantiene estable. No hay bufferbloat significativo.";
            return $"Bufferbloat grado {grade}. Mayor degradación en {bottleneck.ToLower()} con incremento de ~{worstMs:0} ms.";
        }

        private static string BuildReasoning(BufferbloatTestResult result, bool recDl, bool recUl)
        {
            if (result.PrimaryBottleneck == "Ninguno")
                return "No se recomienda shaping. Tu conexión maneja bien la carga.";

            var lines = new System.Text.StringBuilder();

            // Paso 1 — siempre sugerir restricted primero
            lines.AppendLine("Paso 1: Aplica el perfil GAMING (Auto-Tuning restricted).");
            lines.AppendLine("        Reduce bufferbloat con mínimo impacto en velocidad.");
            lines.AppendLine("        Luego corre el test de nuevo para validar.");

            // Paso 2 — si restricted no es suficiente
            lines.AppendLine("");
            lines.AppendLine("Paso 2: Si el bufferbloat persiste, prueba Auto-Tuning disabled.");
            lines.AppendLine("        Advertencia: puede reducir la velocidad de descarga/subida.");

            // Paso 3 — shaping si hay throughput medido
            if (recDl)
                lines.AppendLine($"\nPaso 3: Limitar download a {result.Download.ThroughputMbps * 0.92:0.0} Mbps (92% del medido) en el router/QoS.");
            else if (recUl)
                lines.AppendLine($"\nPaso 3: Limitar upload a {result.Upload.ThroughputMbps * 0.90:0.0} Mbps (90% del medido) en el router/QoS.");

            return lines.ToString().TrimEnd();
        }
    }
}
