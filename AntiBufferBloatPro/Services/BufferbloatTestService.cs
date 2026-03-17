using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using AntiBufferBloatPro.Models;

namespace AntiBufferBloatPro.Services
{
    public sealed class BufferbloatTestService
    {
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);

        private readonly CloudflareLoadGenerationService _loadService = new();
        private readonly BufferbloatAnalyzer _analyzer = new();

        public async Task<BufferbloatTestResult> RunAsync(IProgress<TestProgressUpdate> progress, CancellationToken ct,
            int phaseDurationSeconds = 20, string pingTarget = "1.1.1.1")
        {
            var duration = TimeSpan.FromSeconds(phaseDurationSeconds);
            var expectedSamples = (int)Math.Max(1, duration.TotalSeconds / PingInterval.TotalSeconds);

            // ── FASE IDLE ──
            Report(progress, "Idle", 0, "Midiendo latencia base en reposo...");
            var idleSamples = await CaptureAsync(pingTarget, duration, PingInterval,
                (sample, count) => ReportSample(progress, "Idle", sample, count, expectedSamples, 0, 32, "Midiendo latencia base..."), ct);

            // ── FASE DOWNLOAD ──
            Report(progress, "Download", 34, "Midiendo latencia bajo descarga...");
            var dlTask = _loadService.RunDownloadLoadAsync(duration, ct);
            var dlSamples = await CaptureAsync(pingTarget, duration, PingInterval,
                (sample, count) => ReportSample(progress, "Download", sample, count, expectedSamples, 34, 30, "Midiendo latencia bajo descarga..."), ct);
            var dlMbps = await dlTask;

            // ── FASE UPLOAD ──
            Report(progress, "Upload", 68, "Midiendo latencia bajo subida...");
            var ulTask = _loadService.RunUploadLoadAsync(duration, ct);
            var ulSamples = await CaptureAsync(pingTarget, duration, PingInterval,
                (sample, count) => ReportSample(progress, "Upload", sample, count, expectedSamples, 68, 24, "Midiendo latencia bajo subida..."), ct);
            var ulMbps = await ulTask;

            Report(progress, "Analizando", 94, "Calculando score y recomendación...");
            var result = _analyzer.Analyze(idleSamples, dlSamples, ulSamples, dlMbps, ulMbps);
            Report(progress, "Completado", 100, "Análisis completado.");
            return result;
        }

        private static async Task<IReadOnlyList<PingSample>> CaptureAsync(
            string target, TimeSpan duration, TimeSpan interval,
            Action<PingSample, int> onSample, CancellationToken ct)
        {
            var samples = new List<PingSample>();
            var started = DateTimeOffset.UtcNow;

            using var ping = new Ping();
            while (DateTimeOffset.UtcNow - started < duration)
            {
                ct.ThrowIfCancellationRequested();
                PingSample sample;
                try
                {
                    var reply = await ping.SendPingAsync(target, (int)Math.Max(1000, interval.TotalMilliseconds));
                    sample = new PingSample
                    {
                        LatencyMs = reply.Status == IPStatus.Success ? reply.RoundtripTime : -1,
                        Success = reply.Status == IPStatus.Success
                    };
                }
                catch
                {
                    sample = new PingSample { LatencyMs = -1, Success = false };
                }

                samples.Add(sample);
                onSample(sample, samples.Count);
                await Task.Delay(interval, ct);
            }

            return samples;
        }

        private static void Report(IProgress<TestProgressUpdate> p, string phase, double pct, string detail)
            => p.Report(new TestProgressUpdate { PhaseName = phase, ProgressPercent = pct, Detail = detail });

        private static void ReportSample(IProgress<TestProgressUpdate> p, string phase,
            PingSample sample, int count, int expected, double baseP, double range, string detail)
        {
            var normalized = Math.Min(1.0, count / (double)Math.Max(1, expected));
            p.Report(new TestProgressUpdate
            {
                PhaseName = phase,
                ProgressPercent = baseP + range * normalized,
                Detail = detail,
                LatestLatencyMs = sample.Success ? sample.LatencyMs : (long?)null
            });
        }
    }
}
