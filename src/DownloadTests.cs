using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Win11Bridge
{
    internal sealed class SimulatedHttp : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response;
        public SimulatedHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) { this.response = response; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) { return response(request, cancellation); }
    }
    internal static class DownloadTests
    {
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static Exception DnsError() { return new HttpRequestException("send failed", new WebException("unknown host", WebExceptionStatus.NameResolutionFailure)); }
        private static string Work(string root) { string p = Path.Combine(root, "network-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
        private static HttpResponseMessage Payload() { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) }; }
        public static void Run(Action<string, Action> test, string root)
        {
            Action<string> log = delegate(string message) { }; Action<int> progress = delegate(int p) { };
            Func<string, Task<string>> simulatedTrust = p => Task.FromResult("MOCK trust; native signature verification is tested separately");
            Func<TimeSpan, CancellationToken, Task> noWait = (delay, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(0); };
            test("DNS error from user log classified by nested exception status", () =>
            {
                Check(NetworkFailure.IsDns(DnsError()) && NetworkFailure.IsTransient(DnsError()), "nested DNS error not recognized");
                Check(NetworkFailure.Describe(DnsError()).Contains("DNS"), "missing useful explanation");
                Check(!NetworkFailure.IsTransient(new InvalidDataException("bad signature")), "trust failure was retryable");
                Check(!NetworkFailure.IsTransient(new IOException("disk write failed")), "disk failure was treated as network failure");
            });
            test("simulated DNS twice then success; three attempts and atomic verified replacement", () =>
            {
                string folder = Work(root); string final = Path.Combine(folder, "Windows11InstallationAssistant.exe"); File.WriteAllText(final, "old executable");
                int calls = 0, waits = 0, verified = 0;
                Func<HttpClient> client = () => new HttpClient(new SimulatedHttp((request, token) => { if (++calls <= 2) throw DnsError(); return Task.FromResult(Payload()); }));
                string result = AssistantDownload.DownloadCore(folder, log, progress, CancellationToken.None, client,
                    p => { verified++; Check(new FileInfo(p).Length == 2048, "verification before full download"); return simulatedTrust(p); },
                    (delay, token) => { waits++; return noWait(delay, token); }).GetAwaiter().GetResult();
                Check(result == final && calls == 3 && waits == 2 && verified == 1, "retry counts incorrect");
                Check(new FileInfo(final).Length == 2048 && !File.Exists(Path.Combine(folder, "assistant.download")), "replace/cleanup failed");
            });
            test("persistent DNS stops after three attempts and preserves existing Assistant", () =>
            {
                string folder = Work(root); string final = Path.Combine(folder, "Windows11InstallationAssistant.exe"); File.WriteAllText(final, "keep old"); int calls = 0; bool failed = false;
                try
                {
                    AssistantDownload.DownloadCore(folder, log, progress, CancellationToken.None,
                        () => new HttpClient(new SimulatedHttp((request, token) => { calls++; throw DnsError(); })), simulatedTrust, noWait).GetAwaiter().GetResult();
                }
                catch (HttpRequestException ex) { failed = NetworkFailure.IsDns(ex); }
                Check(failed && calls == 3 && File.ReadAllText(final) == "keep old", "DNS retry was unbounded or deleted cache");
            });
            test("HTTP 503 retried; HTTP 403 not retried", () =>
            {
                int calls = 0;
                AssistantDownload.DownloadCore(Work(root), log, progress, CancellationToken.None,
                    () => new HttpClient(new SimulatedHttp((request, token) => Task.FromResult(++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Payload()))), simulatedTrust, noWait).GetAwaiter().GetResult();
                Check(calls == 2, "503 not retried"); calls = 0; bool denied = false;
                try
                {
                    AssistantDownload.DownloadCore(Work(root), log, progress, CancellationToken.None,
                        () => new HttpClient(new SimulatedHttp((request, token) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); })), simulatedTrust, noWait).GetAwaiter().GetResult();
                }
                catch (DownloadHttpException ex) { denied = ex.StatusCode == 403; }
                Check(denied && calls == 1, "403 was repeatedly requested");
            });
            test("redirect outside Microsoft rejected before second request", () =>
            {
                int calls = 0; bool denied = false;
                try
                {
                    AssistantDownload.DownloadCore(Work(root), log, progress, CancellationToken.None,
                        () => new HttpClient(new SimulatedHttp((request, token) => { calls++; HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://example.invalid/installer.exe"); return Task.FromResult(response); })), simulatedTrust, noWait).GetAwaiter().GetResult();
                }
                catch (InvalidDataException) { denied = true; }
                Check(denied && calls == 1, "unsafe redirect followed or retried");
            });
            test("simulated invalid signature preserves cache and removes untrusted staging", () =>
            {
                string folder = Work(root); string final = Path.Combine(folder, "Windows11InstallationAssistant.exe"); File.WriteAllText(final, "old"); int calls = 0; bool denied = false;
                try
                {
                    AssistantDownload.DownloadCore(folder, log, progress, CancellationToken.None,
                        () => new HttpClient(new SimulatedHttp((request, token) => { calls++; return Task.FromResult(Payload()); })),
                        p => { throw new InvalidDataException("simulated invalid signature"); }, noWait).GetAwaiter().GetResult();
                }
                catch (InvalidDataException) { denied = true; }
                Check(denied && calls == 1 && File.ReadAllText(final) == "old" && !File.Exists(Path.Combine(folder, "assistant.download")), "untrusted payload replaced cache");
            });
            test("cancel during retry delay prevents further requests", () =>
            {
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    int calls = 0; bool stopped = false;
                    try
                    {
                        AssistantDownload.DownloadCore(Work(root), log, progress, cancel.Token,
                            () => new HttpClient(new SimulatedHttp((request, token) => { calls++; throw DnsError(); })), simulatedTrust,
                            (delay, token) => { cancel.Cancel(); return Task.Delay(1, token); }).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) { stopped = true; }
                    Check(stopped && calls == 1, "cancellation retried network request");
                }
            });
            test("HTTP timeout retries three times and is not reported as a user cancellation", () =>
            {
                string folder = Work(root); int calls = 0; bool timedOut = false;
                try
                {
                    AssistantDownload.DownloadCore(folder, log, progress, CancellationToken.None,
                        () => new HttpClient(new SimulatedHttp((request, token) => { calls++; throw new TaskCanceledException("simulated HttpClient timeout"); })),
                        simulatedTrust, noWait).GetAwaiter().GetResult();
                }
                catch (TimeoutException) { timedOut = true; }
                Check(timedOut && calls == 3, "timeout was confused with user cancellation or was not retried");
                Check(!File.Exists(Path.Combine(folder, "assistant.download")), "timeout left a partial download");
            });
            test("recent cache is reverified and needs no bootstrap download", () =>
            {
                string folder = Work(root); string cached = Path.Combine(folder, "Windows11InstallationAssistant.exe"); File.WriteAllText(cached, "simulated cache");
                int verified = 0, downloads = 0;
                string result = AssistantCache.AcquireCore(folder, true, log, progress, CancellationToken.None, question => Task.FromResult(false),
                    p => { verified++; return simulatedTrust(p); }, () => { downloads++; throw DnsError(); }).GetAwaiter().GetResult();
                Check(result == cached && verified == 1 && downloads == 0, "recent cache was not reverified or unnecessary download occurred");
            });
            test("cache verification failure falls back to download without using untrusted cache", () =>
            {
                string folder = Work(root); string cached = Path.Combine(folder, "Windows11InstallationAssistant.exe"); File.WriteAllText(cached, "invalid"); int downloads = 0;
                string result = AssistantCache.AcquireCore(folder, true, log, progress, CancellationToken.None, question => { throw new Exception("untrusted fallback offered"); },
                    p => { throw new InvalidDataException("simulated invalid cache"); }, () => { downloads++; return Task.FromResult("new-verified-download"); }).GetAwaiter().GetResult();
                Check(result == "new-verified-download" && downloads == 1, "untrusted cache accepted");
            });
            test("unsigned local import is rejected without replacing existing Assistant", () =>
            {
                string folder = Work(root); string final = Path.Combine(folder, "Windows11InstallationAssistant.exe"); File.WriteAllText(final, "preserve"); bool rejected = false;
                try { AssistantCache.Import(System.Windows.Forms.Application.ExecutablePath, folder, log).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected && File.ReadAllText(final) == "preserve" && !File.Exists(Path.Combine(folder, "assistant.import")), "untrusted local import was promoted or staging left behind");
            });
            test("older cache after DNS failure requires consent and a second verification", () =>
            {
                string folder = Work(root); string cached = Path.Combine(folder, "Windows11InstallationAssistant.exe"); File.WriteAllText(cached, "cache"); File.SetLastWriteTimeUtc(cached, DateTime.UtcNow.AddDays(-2));
                int verified = 0, prompts = 0;
                string result = AssistantCache.AcquireCore(folder, true, log, progress, CancellationToken.None, question => { prompts++; return Task.FromResult(true); },
                    p => { verified++; return simulatedTrust(p); }, () => { throw DnsError(); }).GetAwaiter().GetResult();
                Check(result == cached && prompts == 1 && verified == 2, "older cache was not explicitly approved/reverified");
            });
            test("TLS trust failure does not offer older-cache network fallback", () =>
            {
                string folder = Work(root); string cached = Path.Combine(folder, "Windows11InstallationAssistant.exe"); File.WriteAllText(cached, "cache"); File.SetLastWriteTimeUtc(cached, DateTime.UtcNow.AddDays(-2));
                int prompts = 0; bool stopped = false;
                try
                {
                    AssistantCache.AcquireCore(folder, true, log, progress, CancellationToken.None, question => { prompts++; return Task.FromResult(true); }, simulatedTrust,
                        () => { throw new HttpRequestException("TLS", new WebException("untrusted", WebExceptionStatus.TrustFailure)); }).GetAwaiter().GetResult();
                }
                catch (HttpRequestException) { stopped = true; }
                Check(stopped && prompts == 0, "TLS failure was relaxed");
            });
            test("relaunch allows only prepared or observed-idle past launch; conflicts remain blocked", () =>
            {
                Check(SessionRules.CanLaunch("Applied", false, 0), "prepared launch denied");
                Check(SessionRules.CanLaunch("Launched", false, 0), "idle retry denied");
                Check(!SessionRules.CanLaunch("Launched", false, 1), "handoff wait bypassed");
                Check(!SessionRules.CanLaunch("Launched", true, 0), "active installer duplicate allowed");
                Check(!SessionRules.CanLaunch("RestoreConflicts", false, 0) && !SessionRules.CanLaunch("Prepared", false, 0), "partial/conflicted session launched");
            });
            test("evidence ZIP contains source files and atomically replaces prior export", () =>
            {
                string folder = Work(root); File.WriteAllText(Path.Combine(folder, "index.txt"), "source times and phase");
                string zip = Path.Combine(root, "evidence-export.zip"); File.WriteAllText(zip, "prior export"); Evidence.Export(folder, zip);
                using (ZipArchive archive = ZipFile.OpenRead(zip)) Check(archive.GetEntry("index.txt") != null, "evidence missing from ZIP");
                Check(Directory.GetFiles(root, "evidence-export.zip.partial-*").Length == 0, "ZIP staging left behind");
            });
        }
    }
}
