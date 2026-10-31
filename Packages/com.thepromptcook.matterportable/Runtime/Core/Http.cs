using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Matterportable.Core
{
    public sealed class HttpResult
    {
        public int Status;
        public byte[] Body;
        public string Error;
        public long Bytes;

        public bool Ok => Status >= 200 && Status < 300 && Error == null;
        public bool IsAuthFailure => Status == 401 || Status == 403;
        public bool IsTransient => Error != null || Status == 0 || Status == 408 || Status == 429 || Status >= 500;
        public string Text => Body == null ? "" : Encoding.UTF8.GetString(Body);
    }

    /// <summary>Network abstraction so the downloader can be tested offline and swapped for UnityWebRequest if needed.</summary>
    public interface IHttpTransport
    {
        Task<HttpResult> PostJsonAsync(string url, string json, CancellationToken ct);
        Task<HttpResult> GetToFileAsync(string url, string path, Action<long> onBytes, CancellationToken ct);
    }

    public sealed class HttpClientTransport : IHttpTransport, IDisposable
    {
        public const string UserAgent = "Matterportable/0.1 (Unity importer; +https://github.com/thepromptcook/Matterportable)";

        readonly HttpClient _client;

        public HttpClientTransport(TimeSpan? timeout = null)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
            };
            _client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(120) };
            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        }

        public async Task<HttpResult> PostJsonAsync(string url, string json, CancellationToken ct)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    req.Headers.TryAddWithoutValidation("Accept", "application/json");
                    using (var resp = await _client.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var body = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        return new HttpResult { Status = (int)resp.StatusCode, Body = body, Bytes = body.Length };
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { return new HttpResult { Error = Describe(e) }; }
        }

        public async Task<HttpResult> GetToFileAsync(string url, string path, Action<long> onBytes, CancellationToken ct)
        {
            string part = path + ".part";
            try
            {
                using (var resp = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    int status = (int)resp.StatusCode;
                    if (!resp.IsSuccessStatusCode)
                    {
                        var err = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        return new HttpResult { Status = status, Body = err };
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    long total = 0;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                    {
                        var buffer = new byte[1 << 16];
                        int n;
                        while ((n = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                            total += n;
                            onBytes?.Invoke(n);
                        }
                    }
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(part, path);
                    return new HttpResult { Status = status, Bytes = total };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(part);
                throw;
            }
            catch (Exception e)
            {
                TryDelete(part);
                return new HttpResult { Error = Describe(e) };
            }
        }

        static string Describe(Exception e)
        {
            var msg = e.Message;
            for (var inner = e.InnerException; inner != null; inner = inner.InnerException) msg += " -> " + inner.Message;
            return msg;
        }

        static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }

        public void Dispose() => _client.Dispose();
    }
}
