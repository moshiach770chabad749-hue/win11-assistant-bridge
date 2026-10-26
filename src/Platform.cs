using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Win11Bridge
{
    public static class SecureStorage
    {
        public static string Open()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Win11AssistantBridge");
            RejectReparseParents(path);
            SecurityIdentifier admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            DirectorySecurity acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false); acl.SetOwner(admins);
            foreach (SecurityIdentifier sid in new[] { admins, system }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            if (Directory.Exists(path))
            {
                IdentityReference owner = Directory.GetAccessControl(path).GetOwner(typeof(SecurityIdentifier));
                if (!owner.Equals(admins) && !owner.Equals(system)) throw new IOException("Unsafe storage owner: " + path);
            }
            Directory.CreateDirectory(path, acl); Directory.SetAccessControl(path, acl);
            // Existing files must also have a trusted owner and no links; do not trust preplanted journals or executables.
            foreach (FileSystemInfo entry in SafeEntries(new DirectoryInfo(path)))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Storage contains a reparse point: " + entry.Name);
                FileSystemSecurity security = entry is DirectoryInfo ? (FileSystemSecurity)((DirectoryInfo)entry).GetAccessControl() : ((FileInfo)entry).GetAccessControl();
                IdentityReference owner = security.GetOwner(typeof(SecurityIdentifier));
                if (!owner.Equals(admins) && !owner.Equals(system) && !owner.Equals(WindowsIdentity.GetCurrent().User)) throw new IOException("Unsafe storage entry owner: " + entry.Name);
                // Reset old explicit write grants as well as inheritance.
                if (entry is DirectoryInfo) Directory.SetAccessControl(entry.FullName, acl);
                else
                {
                    FileSecurity fa = new FileSecurity(); fa.SetAccessRuleProtection(true, false); fa.SetOwner(admins);
                    foreach (SecurityIdentifier sid in new[] { admins, system }) fa.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                    File.SetAccessControl(entry.FullName, fa);
                }
            }
            return path;
        }
        private static IEnumerable<FileSystemInfo> SafeEntries(DirectoryInfo folder)
        {
            foreach (FileSystemInfo entry in folder.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Storage contains a reparse point: " + entry.Name);
                yield return entry;
                if (entry is DirectoryInfo) foreach (FileSystemInfo child in SafeEntries((DirectoryInfo)entry)) yield return child;
            }
        }
        public static void RejectReparseParents(string path)
        {
            for (DirectoryInfo d = new DirectoryInfo(Path.GetFullPath(path)); d != null; d = d.Parent)
                if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse point is not allowed: " + d.FullName);
        }
    }
    public sealed class OperationLog : IDisposable
    {
        private readonly StreamWriter writer;
        public event Action<string> Added;
        public OperationLog(string path)
        {
            writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)); writer.AutoFlush = true;
        }
        public void Write(string text)
        {
            string line = DateTimeOffset.Now.ToString("o") + " " + text;
            lock (writer) writer.WriteLine(line);
            Action<string> added = Added; if (added != null) added(line);
        }
        public void Dispose() { writer.Dispose(); }
    }
    public static class SignatureVerifier
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File; public IntPtr KnownSubject; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustData
        {
            public uint Size; public IntPtr Policy; public IntPtr Sip; public uint UI; public uint Revocation;
            public uint UnionChoice; public IntPtr File; public uint StateAction; public IntPtr State;
            public IntPtr Url; public uint Flags; public uint UIContext;
        }
        [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)] private static extern int WinVerifyTrust(IntPtr hwnd, [In] ref Guid action, ref TrustData data);
        public static string VerifyMicrosoft(string path)
        {
            // Keep the executable locked against writes/deletion for the full trust and identity check.
            using (FileStream hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                TrustFile file = new TrustFile { Size = (uint)Marshal.SizeOf(typeof(TrustFile)), Path = Path.GetFullPath(path) };
                IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));
                Marshal.StructureToPtr(file, pointer, false);
                Guid action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                TrustData data = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), UI = 2, Revocation = 1, UnionChoice = 1, File = pointer, StateAction = 1, Flags = 0x80 }; // Entire chain revocation, excluding root; online allowed.
                try
                {
                    int status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                    if (status != 0) throw new InvalidDataException("Authenticode verification failed: 0x" + unchecked((uint)status).ToString("X8") + ". Check connection, certificate revocation and system clock.");
                    using (X509Certificate2 certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                    {
                        if (certificate.GetNameInfo(X509NameType.SimpleName, false) != "Microsoft Corporation" ||
                            !certificate.Subject.Split(',').Any(s => s.Trim() == "O=Microsoft Corporation")) throw new InvalidDataException("Trusted signature is not from Microsoft Corporation");
                        FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
                        if (version.OriginalFilename != "WindowsInstallationAssistant.exe" && version.OriginalFilename != "Windows11InstallationAssistant.exe") throw new InvalidDataException("Signed file is not the expected Installation Assistant");
                        return certificate.Subject + " | version=" + version.FileVersion + " | SHA256=" + Hash(path);
                    }
                }
                finally
                {
                    data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                    Marshal.DestroyStructure(pointer, typeof(TrustFile)); Marshal.FreeHGlobal(pointer);
                }
            }
        }
        public static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create()) using (FileStream f = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
        }
    }
    public static class AssistantDownload
    {
        public const string Source = "https://go.microsoft.com/fwlink/?linkid=2171764";
        public static string LaunchArguments(bool skipCompatCheck) { return skipCompatCheck ? "/SkipCompatCheck" : ""; }
        public static bool Allowed(Uri uri)
        {
            return uri.Scheme == "https" && uri.Port == 443 && String.IsNullOrEmpty(uri.UserInfo) &&
                (uri.Host.Equals("go.microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("download.microsoft.com", StringComparison.OrdinalIgnoreCase));
        }
        public static Task<string> Download(string folder, Action<string> log, Action<int> progress)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            return Download(folder, log, progress, CancellationToken.None);
        }
        public static Task<string> Download(string folder, Action<string> log, Action<int> progress, CancellationToken cancellation)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            return DownloadCore(folder, log, progress, cancellation,
                () => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true }) { Timeout = TimeSpan.FromMinutes(2) },
                path => Task.Run(() => SignatureVerifier.VerifyMicrosoft(path)),
                (delay, token) => Task.Delay(delay, token));
        }
        internal static async Task<string> DownloadCore(string folder, Action<string> log, Action<int> progress, CancellationToken cancellation,
            Func<HttpClient> createClient, Func<string, Task<string>> verify, Func<TimeSpan, CancellationToken, Task> wait)
        {
            for (int attempt = 1; ; attempt++)
            {
                cancellation.ThrowIfCancellationRequested(); progress(0);
                Exception failure;
                try { return await DownloadOnce(folder, log, progress, cancellation, createClient, verify); }
                catch (Exception ex)
                {
                    if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                    if (attempt >= 3 || !NetworkFailure.IsTransient(ex)) throw;
                    failure = ex;
                }
                int seconds = attempt == 1 ? 2 : 5;
                log("RETRY attempt=" + attempt + "/3 waitSeconds=" + seconds + " reason=" + NetworkFailure.Describe(failure));
                await wait(TimeSpan.FromSeconds(seconds), cancellation);
            }
        }
        private static async Task<string> DownloadOnce(string folder, Action<string> log, Action<int> progress, CancellationToken cancellation,
            Func<HttpClient> createClient, Func<string, Task<string>> verify)
        {
            string partial = Path.Combine(folder, "assistant.download");
            string final = Path.Combine(folder, "Windows11InstallationAssistant.exe");
            try
            {
                using (HttpClient client = createClient())
                using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    deadline.CancelAfter(TimeSpan.FromMinutes(2));
                    CancellationToken token = deadline.Token;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Win11AssistantBridge/" + ReleaseInfo.Version);
                    Uri uri = new Uri(Source);
                    for (int hop = 0; hop < 6; hop++)
                    {
                        if (!Allowed(uri)) throw new InvalidDataException("Download redirect outside Microsoft allowlist: " + uri);
                        log("DOWNLOAD " + uri.AbsoluteUri);
                        using (HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token))
                        {
                            int code = (int)response.StatusCode;
                            if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                            {
                                if (response.Headers.Location == null) throw new IOException("Redirect missing Location");
                                uri = new Uri(uri, response.Headers.Location); continue;
                            }
                            if (!response.IsSuccessStatusCode) throw new DownloadHttpException(code);
                            long? expected = response.Content.Headers.ContentLength;
                            const long max = 100L * 1024 * 1024;
                            if (expected.HasValue && (expected.Value < 1024 || expected.Value > max)) throw new InvalidDataException("Unexpected Assistant size");
                            using (Stream input = await response.Content.ReadAsStreamAsync())
                            using (FileStream output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[65536]; long total = 0; int count;
                                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                                {
                                    total += count; if (total > max) throw new InvalidDataException("Assistant exceeds size limit");
                                    await output.WriteAsync(buffer, 0, count, token);
                                    if (expected.HasValue) progress((int)(total * 100 / expected.Value));
                                }
                                if (total < 1024 || (expected.HasValue && total != expected.Value)) throw new IOException("Incomplete download");
                                output.Flush(true);
                            }
                            log("SIGNATURE " + await verify(partial)); token.ThrowIfCancellationRequested();
                            if (File.Exists(final)) File.Replace(partial, final, null);
                            else File.Move(partial, final);
                            progress(100); return final;
                        }
                    }
                    throw new IOException("Too many download redirects");
                }
            }
            catch (OperationCanceledException ex)
            {
                if (cancellation.IsCancellationRequested) throw;
                throw new TimeoutException("Microsoft Assistant download exceeded the connection time limit", ex);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
    }
    public sealed class MachineReport
    {
        public readonly List<string> Blocks = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public string Summary;
    }
    public sealed class PendingSetupStatus
    {
        public bool SetupInProgress;
        public bool RestartPending;
        public string Summary;
    }
    public static class Machine
    {
        public static PendingSetupStatus PendingSetup()
        {
            PendingSetupStatus result = new PendingSetupStatus(); List<string> markers = new List<string>();
            using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                using (RegistryKey setup = root.OpenSubKey(@"SYSTEM\Setup"))
                    result.SetupInProgress = setup != null && Convert.ToInt32(setup.GetValue("SystemSetupInProgress", 0)) != 0;
                markers.Add("SystemSetupInProgress=" + (result.SetupInProgress ? "1" : "0"));
                foreach (string path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired" })
                    using (RegistryKey key = root.OpenSubKey(path)) if (key != null) { result.RestartPending = true; markers.Add(path.Substring(path.LastIndexOf('\\') + 1)); }
            }
            result.Summary = String.Join("; ", markers); return result;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
        {
            public uint Length; public uint Load; public ulong TotalPhys; public ulong AvailPhys; public ulong TotalPage; public ulong AvailPage; public ulong TotalVirtual; public ulong AvailVirtual; public ulong Extended;
        }
        [StructLayout(LayoutKind.Sequential)] private struct SystemInfo
        {
            public ushort Architecture; public ushort Reserved; public uint PageSize; public IntPtr Min; public IntPtr Max; public IntPtr Mask; public uint Processors; public uint Type; public uint Granularity; public ushort Level; public ushort Revision;
        }
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        [DllImport("kernel32.dll")] private static extern void GetNativeSystemInfo(out SystemInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);
        [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
        [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint ReadCpuFeatures();
        public static uint CpuFeatures()
        {
            // x64 CPUID(1).ECX. Preserve RBX. No downloaded code, hooks or binary patching.
            byte[] code = { 0x53, 0xB8, 1, 0, 0, 0, 0x31, 0xC9, 0x0F, 0xA2, 0x89, 0xC8, 0x5B, 0xC3 };
            IntPtr memory = VirtualAlloc(IntPtr.Zero, (UIntPtr)code.Length, 0x3000, 0x04);
            if (memory == IntPtr.Zero) throw new IOException("CPUID allocation denied");
            try
            {
                Marshal.Copy(code, 0, memory, code.Length); uint old;
                if (!VirtualProtect(memory, (UIntPtr)code.Length, 0x20, out old)) throw new IOException("CPUID execution denied by policy");
                if (!FlushInstructionCache(new IntPtr(-1), memory, (UIntPtr)code.Length)) throw new IOException("CPUID cache flush failed");
                return ((ReadCpuFeatures)Marshal.GetDelegateForFunctionPointer(memory, typeof(ReadCpuFeatures)))();
            }
            finally { VirtualFree(memory, UIntPtr.Zero, 0x8000); }
        }
        public static string Build()
        {
            using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) return Convert.ToString(key.GetValue("CurrentBuildNumber"));
        }
        public static string FullBuild()
        {
            using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                return Convert.ToString(key.GetValue("CurrentBuildNumber")) + "." + Convert.ToString(key.GetValue("UBR", 0));
        }
        public static MachineReport Inspect()
        {
            MachineReport report = new MachineReport(); SystemInfo system; GetNativeSystemInfo(out system);
            if (system.Architecture != 9) report.Blocks.Add("המעדכן של Microsoft דורש Windows x64; אינו מתאים ל־ARM64 או ל־32 סיביות.");
            int build; string buildText = Build();
            if (!Int32.TryParse(buildText, out build) || build < 22000) report.Blocks.Add("כלי זה מיועד למחשב שכבר מותקן בו Windows 11.");
            string edition; string release;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                edition = Convert.ToString(key.GetValue("EditionID"));
                release = Convert.ToString(key.GetValue("DisplayVersion"));
                if (Convert.ToString(key.GetValue("InstallationType")) != "Client") report.Blocks.Add("נדרשת מהדורת Windows למחשב אישי, לא שרת.");
            }
            uint features = 0;
            if (system.Architecture == 9)
            {
                try
                {
                    features = CpuFeatures();
                    if ((features & (1u << 20)) == 0 || (features & (1u << 23)) == 0) report.Blocks.Add("חסרות הוראות SSE4.2 או POPCNT במעבד. שינוי בדיקת זכאות אינו מוסיף אותן.");
                }
                catch (Exception ex) { report.Blocks.Add("לא ניתן לאמת הוראות מעבד: " + ex.Message); }
            }
            MemoryStatus memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (!GlobalMemoryStatusEx(ref memory)) report.Blocks.Add("לא ניתן לקרוא את הזיכרון הפיזי.");
            double ram = memory.TotalPhys / (1024.0 * 1024 * 1024);
            if (ram < 4) report.Warnings.Add("מותקנים פחות מ־4GB RAM. עקיפת הבדיקה אינה מוסיפה זיכרון; ייתכנו ביצועים ירודים או כשל בהתקנה.");
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
            double free = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            if (free < 9) report.Blocks.Add("פחות מ־9GB פנויים בכונן המערכת. יש לפנות מקום אמיתי לפני הפעלה.");
            else if (free < 30) report.Warnings.Add("פחות מ־30GB פנויים. ההתקנה והיכולת לחזור לגרסה קודמת עלולות לדרוש מקום נוסף.");
            report.Summary = String.Format("Windows {0} build {1} | {2} | RAM {3:F1} GB | פנוי {4:F1} GB | CPUID ECX 0x{5:X8}", release, FullBuild(), edition, ram, free, features);
            return report;
        }
        public static List<string> UpgradeProcesses()
        {
            List<string> found = new List<string>();
            HashSet<string> names = new HashSet<string>(new[] { "Windows11InstallationAssistant", "Windows10UpgraderApp", "WindowsInstallationAssistant", "setup", "setuphost", "setupprep", "WindowsUpdateBox" }, StringComparer.OrdinalIgnoreCase);
            foreach (Process p in Process.GetProcesses()) using (p)
            {
                try { if (names.Contains(p.ProcessName)) found.Add(p.ProcessName + " PID=" + p.Id); }
                catch (InvalidOperationException) { }
            }
            return found;
        }
    }
    public static class Evidence
    {
        public static string Collect(string root, SessionState state, Action<string> log)
        {
            string folder = Path.Combine(root, "evidence-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(folder);
            string system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] folders = { Path.Combine(system, @"$WINDOWS.~BT\Sources\Panther"), Path.Combine(system, @"$WINDOWS.~BT\Sources\Rollback"), Path.Combine(windows, "Panther"), Path.Combine(system, "Windows10Upgrade"), Path.Combine(windows, @"Logs\MoSetup"), Path.Combine(windows, @"Logs\SetupDiag") };
            List<string> index = new List<string>();
            index.Add("Collected UTC=" + DateTime.UtcNow.ToString("o")); index.Add("Current build=" + Machine.FullBuild());
            index.Add("Session=" + (state == null ? "none" : state.Id + " phase=" + state.Phase + " launch=" + state.LaunchUtc + " originalBuild=" + state.BuildBefore));
            index.Add("A process exit or old log is NOT proof of upgrade success or a current blocker.");
            index.Add("Processes=" + String.Join(", ", Machine.UpgradeProcesses()));
            index.Add("No installation speed, CPU priority, network, firewall or update service settings are changed by this tool.");
            index.Add("A percentage alone cannot distinguish a slow installation from a failure. Partial setup files are not removed.");
            int i = 0;
            foreach (string source in folders)
            {
                try
                {
                    if (!Directory.Exists(source)) { index.Add("NOT FOUND " + source); continue; }
                    foreach (string file in Directory.GetFiles(source).Where(f => Path.GetExtension(f).Equals(".log", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith("CompatData", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("SetupDiagResults.xml", StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).Take(12))
                    {
                        string target = Path.Combine(folder, (++i).ToString("D3") + "-" + Path.GetFileName(file));
                        long offset = CopyTail(file, target);
                        index.Add(file + " | modifiedUTC=" + File.GetLastWriteTimeUtc(file).ToString("o") + " | tailOffset=" + offset + " | saved=" + Path.GetFileName(target));
                    }
                }
                catch (Exception ex) { index.Add("READ ERROR " + source + ": " + ex.Message); }
            }
            foreach (string name in new[] { "operations.log", "connection-check.txt" })
            {
                try { string file = Path.Combine(root, name); if (File.Exists(file)) { long offset = CopyTail(file, Path.Combine(folder, name)); index.Add(file + " | tailOffset=" + offset); } }
                catch (Exception ex) { index.Add("READ ERROR " + name + ": " + ex.Message); }
            }
            if (state != null) index.Add("Preparation settings=" + String.Join(", ", state.Changes.Where(c => c.After.Exists).Select(c => c.Label)));
            File.WriteAllLines(Path.Combine(folder, "index.txt"), index, Encoding.UTF8); log("EVIDENCE " + folder); return folder;
        }
        private static long CopyTail(string source, string target)
        {
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (FileStream output = File.Create(target))
            {
                long length = input.Length; long offset = Math.Max(0, length - 2 * 1024 * 1024); input.Position = offset;
                long remaining = length - offset; byte[] buffer = new byte[65536];
                while (remaining > 0)
                {
                    int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); if (count == 0) break;
                    output.Write(buffer, 0, count); remaining -= count;
                }
                return offset;
            }
        }
        public static void Export(string folder, string destination)
        {
            string staging = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                ZipFile.CreateFromDirectory(folder, staging, CompressionLevel.Optimal, false);
                if (File.Exists(destination)) File.Replace(staging, destination, null); else File.Move(staging, destination);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
    }
}
