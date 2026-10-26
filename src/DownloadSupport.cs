using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Win11Bridge
{
    public sealed class DownloadHttpException : HttpRequestException
    {
        public readonly int StatusCode;
        public DownloadHttpException(int status) : base("Microsoft download returned HTTP " + status) { StatusCode = status; }
    }
    public static class NetworkFailure
    {
        private static IEnumerable<Exception> Chain(Exception error)
        {
            for (Exception e = error; e != null; e = e.InnerException) yield return e;
        }
        public static bool IsDns(Exception error)
        {
            foreach (Exception e in Chain(error))
            {
                WebException web = e as WebException;
                if (web != null && (web.Status == WebExceptionStatus.NameResolutionFailure || web.Status == WebExceptionStatus.ProxyNameResolutionFailure)) return true;
                SocketException socket = e as SocketException;
                if (socket != null && (socket.SocketErrorCode == SocketError.HostNotFound || socket.SocketErrorCode == SocketError.TryAgain || socket.SocketErrorCode == SocketError.NoData)) return true;
            }
            return false;
        }
        public static bool IsTransient(Exception error)
        {
            if (error is InvalidDataException) return false;
            foreach (Exception e in Chain(error))
            {
                DownloadHttpException http = e as DownloadHttpException;
                if (http != null) return http.StatusCode == 408 || http.StatusCode == 429 || http.StatusCode >= 500 && http.StatusCode <= 599;
                WebException web = e as WebException;
                if (web != null)
                {
                    switch (web.Status)
                    {
                        case WebExceptionStatus.NameResolutionFailure: case WebExceptionStatus.ProxyNameResolutionFailure:
                        case WebExceptionStatus.ConnectFailure: case WebExceptionStatus.Timeout:
                        case WebExceptionStatus.ConnectionClosed: case WebExceptionStatus.ReceiveFailure:
                        case WebExceptionStatus.SendFailure: case WebExceptionStatus.KeepAliveFailure: return true;
                        default: return false;
                    }
                }
                if (e is TaskCanceledException || e is TimeoutException || e is SocketException) return true;
            }
            return false;
        }
        public static string Describe(Exception error)
        {
            if (IsDns(error)) return "כשל DNS: המחשב לא הצליח למצוא את כתובת שרת Microsoft. בדוק חיבור לאינטרנט, DNS והגדרות VPN/Proxy. הכלי לא משנה הגדרות רשת.";
            foreach (Exception e in Chain(error))
            {
                DownloadHttpException http = e as DownloadHttpException;
                if (http != null) return "שרת Microsoft החזיר HTTP " + http.StatusCode + ".";
                WebException web = e as WebException;
                if (web != null && (web.Status == WebExceptionStatus.TrustFailure || web.Status == WebExceptionStatus.SecureChannelFailure))
                    return "אימות חיבור TLS נכשל. בדוק תאריך מערכת, תעודות והגדרות רשת; אימות האבטחה נשאר פעיל.";
            }
            if (IsTransient(error)) return "חיבור ההורדה נכשל או עבר את זמן ההמתנה. בדוק את חיבור האינטרנט ונסה שוב.";
            return error.Message;
        }
    }
    public static class AssistantCache
    {
        public static Task<string> Acquire(string folder, bool preferCache, Action<string> log, Action<int> progress,
            CancellationToken cancellation, Func<string, Task<bool>> confirmOlder)
        {
            return AcquireCore(folder, preferCache, log, progress, cancellation, confirmOlder,
                path => Task.Run(() => SignatureVerifier.VerifyMicrosoft(path)),
                () => AssistantDownload.Download(folder, log, progress, cancellation));
        }
        internal static async Task<string> AcquireCore(string folder, bool preferCache, Action<string> log, Action<int> progress,
            CancellationToken cancellation, Func<string, Task<bool>> confirmOlder, Func<string, Task<string>> verify, Func<Task<string>> download)
        {
            string cached = Path.Combine(folder, "Windows11InstallationAssistant.exe");
            bool trusted = false;
            if (preferCache && File.Exists(cached))
            {
                cancellation.ThrowIfCancellationRequested();
                try { log("CACHE SIGNATURE " + await verify(cached)); trusted = true; }
                catch (Exception ex) { log("CACHE REJECTED " + ex.Message); }
                cancellation.ThrowIfCancellationRequested();
                if (trusted)
                {
                    double ageHours = (DateTime.UtcNow - File.GetLastWriteTimeUtc(cached)).TotalHours;
                    if (ageHours >= 0 && ageHours <= 24)
                    {
                        log("CACHE USE recent verified Microsoft Assistant; Windows download still requires Internet"); progress(100); return cached;
                    }
                }
            }
            Exception downloadFailure;
            try { return await download(); }
            catch (Exception ex)
            {
                if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                if (!trusted || !NetworkFailure.IsTransient(ex)) throw;
                downloadFailure = ex;
            }
            string question = "ההורדה נכשלה: " + NetworkFailure.Describe(downloadFailure) + "\r\n\r\nקיים אשף Microsoft שהורד קודם, וחתימתו אומתה מחדש. הוא עשוי להיות ישן. להשתמש בו?\r\nהאינטרנט עדיין נדרש להורדת Windows. האשף לא יופעל לפני אישור נפרד.";
            log("CACHE FALLBACK OFFER " + NetworkFailure.Describe(downloadFailure));
            if (!await confirmOlder(question)) throw new OperationCanceledException("Older cached Assistant declined by user");
            log("CACHE FALLBACK SIGNATURE " + await verify(cached)); cancellation.ThrowIfCancellationRequested();
            log("CACHE USE older verified Assistant after explicit confirmation"); progress(100); return cached;
        }
        public static async Task Import(string source, string folder, Action<string> log)
        {
            string final = Path.Combine(folder, "Windows11InstallationAssistant.exe");
            if (Path.GetFullPath(source).Equals(Path.GetFullPath(final), StringComparison.OrdinalIgnoreCase))
            {
                log("IMPORT existing SIGNATURE " + await Task.Run(() => SignatureVerifier.VerifyMicrosoft(final))); return;
            }
            string staging = Path.Combine(folder, "assistant.import");
            try
            {
                using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (input.Length < 1024 || input.Length > 100L * 1024 * 1024) throw new InvalidDataException("Unexpected Assistant size");
                    await input.CopyToAsync(output); output.Flush(true);
                }
                log("IMPORT SIGNATURE " + await Task.Run(() => SignatureVerifier.VerifyMicrosoft(staging)));
                if (File.Exists(final)) File.Replace(staging, final, null); else File.Move(staging, final);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
    }
    public static class ConnectionCheck
    {
        private static void ObserveLateFailure(Task task)
        {
            task.ContinueWith(t => { Exception ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        public static async Task<string> Run()
        {
            StringBuilder report = new StringBuilder(); report.AppendLine("בדיקת חיבור לקריאה בלבד — " + DateTimeOffset.Now.ToString("o"));
            foreach (string host in new[] { "go.microsoft.com", "download.microsoft.com" })
            {
                try
                {
                    Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
                    if (await Task.WhenAny(lookup, Task.Delay(8000)) != lookup)
                    {
                        // Observe a late DNS failure as well; the resolver has no cancellation API in .NET Framework.
                        ObserveLateFailure(lookup);
                        report.AppendLine(host + ": DNS timeout"); continue;
                    }
                    report.AppendLine(host + ": DNS OK, addresses=" + (await lookup).Length);
                }
                catch (Exception ex) { report.AppendLine(host + ": " + NetworkFailure.Describe(ex)); }
            }
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true }) { Timeout = TimeSpan.FromSeconds(15) })
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, AssistantDownload.Source))
                using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                    report.AppendLine("HTTPS go.microsoft.com: HTTP " + (int)response.StatusCode + " (not a complete Windows download test)");
            }
            catch (Exception ex) { report.AppendLine("HTTPS: " + NetworkFailure.Describe(ex)); }
            report.AppendLine("לא שונו DNS, Proxy, Firewall, תעודות או שירותי Windows. חתימה תקינה אינה מבטיחה חיבור להורדת מערכת ההפעלה.");
            return report.ToString();
        }
    }
}
