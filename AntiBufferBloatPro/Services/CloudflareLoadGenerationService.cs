using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace AntiBufferBloatPro.Services
{
    public sealed class CloudflareLoadGenerationService
    {
        // Number of parallel streams for load generation — enough to saturate gigabit
        private const int DownloadStreams = 6;
        private const int UploadStreams   = 6;

        // 10 MB per upload POST — Cloudflare __up accepts up to ~10 MB reliably
        private const int UploadChunkBytes = 10_000_000;

        private static readonly Uri[] DownloadUris =
        {
            // Servers spread across regions — fastest RTT wins at probe time
            new Uri("https://fl-miami-speed.vultr.com/vultr.com.1000MB.bin"), // Miami FL (nearest to PR)
            new Uri("https://ash-speed.hetzner.com/100MB.bin"),               // Ashburn VA (US East)
            new Uri("https://hil-speed.hetzner.com/100MB.bin"),               // Hillsboro OR (US West)
            new Uri("https://speed.hetzner.de/100MB.bin"),                    // Nuremberg DE
        };

        // Upload endpoints that accept arbitrary POST body and return 200
        private static readonly Uri[] UploadUris =
        {
            new Uri("https://speed.cloudflare.com/__up"),   // Cloudflare anycast — nearest PoP
            new Uri("https://httpbin.org/post"),
            new Uri("https://postman-echo.com/post"),
        };

        private static readonly HttpClient HttpClient = BuildHttpClient();

        // Pre-generate a single random payload shared across all upload workers
        private static readonly byte[] UploadPayload = GeneratePayload(UploadChunkBytes);

        // ── Public API ────────────────────────────────────────────────────────

        public async Task<double> RunDownloadLoadAsync(TimeSpan duration, CancellationToken ct)
        {
            _bestDownloadUri = null; // re-probe each test run
            // Warm up: find fastest download server before spawning workers
            _bestDownloadUri = await ResolveBestAsync(DownloadUris, uri =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, AppendCacheBuster(uri));
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023); // 1 KB probe
                return req;
            }, ct);
            var tasks = new List<Task<long>>(DownloadStreams);
            for (int i = 0; i < DownloadStreams; i++)
                tasks.Add(DownloadWorkerAsync(duration, ct));

            var results = await Task.WhenAll(tasks);
            long totalBytes = 0;
            foreach (var b in results) totalBytes += b;
            return ToMbps(totalBytes, duration);
        }

        public async Task<double> RunUploadLoadAsync(TimeSpan duration, CancellationToken ct)
        {
            _bestUploadUri = null; // re-probe each test run
            var tasks = new List<Task<long>>(UploadStreams);
            for (int i = 0; i < UploadStreams; i++)
                tasks.Add(UploadWorkerAsync(duration, ct));

            var results = await Task.WhenAll(tasks);
            long totalBytes = 0;
            foreach (var b in results) totalBytes += b;
            return ToMbps(totalBytes, duration);
        }

        // ── Endpoint selection — probes all candidates in parallel, picks fastest ─

        // Cached best endpoints so every worker uses the same one
        private static Uri? _bestDownloadUri;
        private static Uri? _bestUploadUri;

        private static async Task<Uri> ResolveBestAsync(Uri[] candidates,
            Func<Uri, HttpRequestMessage> probeFactory, CancellationToken ct)
        {
            var probes = candidates.Select(async uri =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var req = probeFactory(uri);
                    using var resp = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    return resp.IsSuccessStatusCode ? (uri, sw.ElapsedMilliseconds) : (null, long.MaxValue);
                }
                catch { return (null, long.MaxValue); }
            });

            var results = await Task.WhenAll(probes);
            Uri? winner = null;
            long bestMs = long.MaxValue;
            foreach (var r in results)
            {
                if (r.Item1 != null && r.Item2 < bestMs) { winner = r.Item1; bestMs = r.Item2; }
            }
            return winner ?? candidates[0];
        }

        // ── Workers ──────────────────────────────────────────────────────────

        private static async Task<long> DownloadWorkerAsync(TimeSpan duration, CancellationToken ct)
        {
            // _bestDownloadUri is set by RunDownloadLoadAsync before workers start

            var sw = Stopwatch.StartNew();
            long totalBytes = 0;
            var buffer = new byte[64 * 1024];

            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, AppendCacheBuster(_bestDownloadUri));
                    using var response = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode) continue;

                    using var stream = await response.Content.ReadAsStreamAsync();
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        totalBytes += read;
                        if (sw.Elapsed >= duration) break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* transient error — retry */ }
            }

            return totalBytes;
        }

        private static async Task<long> UploadWorkerAsync(TimeSpan duration, CancellationToken ct)
        {
            _bestUploadUri ??= await ResolveBestAsync(UploadUris, uri =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new ByteArrayContent(new byte[1024])
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return req;
            }, ct);

            var sw = Stopwatch.StartNew();
            long totalBytes = 0;

            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, _bestUploadUri)
                    {
                        Content = new ByteArrayContent(UploadPayload)
                    };
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    using var response = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.IsSuccessStatusCode)
                        totalBytes += UploadPayload.Length;
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(100, ct).ConfigureAwait(false); }
            }

            return totalBytes;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static async Task<HttpResponseMessage> SendWithFallbackAsync(
            Uri[] uris, Func<Uri, HttpRequestMessage> factory, CancellationToken ct)
        {
            HttpRequestException? last = null;
            foreach (var uri in uris)
            {
                try
                {
                    using var req = factory(uri);
                    var resp = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (resp.IsSuccessStatusCode) return resp;
                    resp.Dispose();
                    last = new HttpRequestException($"Endpoint {uri} devolvió {(int)resp.StatusCode}");
                }
                catch (HttpRequestException ex) { last = ex; }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    last = new HttpRequestException($"Timeout contactando {uri}", ex);
                }
            }
            throw last ?? new HttpRequestException("No se pudo completar la carga de red.");
        }

        private static Uri AppendCacheBuster(Uri uri)
        {
            var sep = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
            return new Uri($"{uri}{sep}nocache={Guid.NewGuid():N}");
        }

        private static HttpClient BuildHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AntiBufferBloatPro/2.0");
            return client;
        }

        private static byte[] GeneratePayload(int size)
        {
            var buf = new byte[size];
            new Random(42).NextBytes(buf);
            return buf;
        }

        private static double ToMbps(long bytes, TimeSpan elapsed)
        {
            if (elapsed.TotalSeconds <= 0 || bytes <= 0) return 0;
            return Math.Round(bytes * 8.0 / elapsed.TotalSeconds / 1_000_000.0, 2);
        }
    }
}
