using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Windows 11 Assistant Bridge")]
[assembly: System.Reflection.AssemblyVersion(Win11Bridge.ReleaseInfo.AssemblyVersion)]
[assembly: System.Reflection.AssemblyFileVersion(Win11Bridge.ReleaseInfo.AssemblyVersion)]
[assembly: System.Reflection.AssemblyInformationalVersion(Win11Bridge.ReleaseInfo.Version)]
[assembly: System.Reflection.AssemblyDescription("Hebrew, reversible preparation for the original Microsoft Installation Assistant")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Win11Bridge
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                // Explicit developer modes do not elevate, change eligibility, or launch an installer.
                if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
                if (args.Length == 2 && args[0] == "--registry-test") return SelfTests.NativeRegistryTest(args[1]);
                if (args.Length == 3 && args[0] == "--verify")
                {
                    try { File.WriteAllText(args[2], SignatureVerifier.VerifyMicrosoft(args[1])); return 0; }
                    catch (Exception ex) { File.WriteAllText(args[2], ex.ToString()); return 1; }
                }
                if (args.Length == 2 && (args[0] == "--download-only" || args[0] == "--acquire-only"))
                {
                    Directory.CreateDirectory(args[1]);
                    using (OperationLog log = new OperationLog(Path.Combine(args[1], "download-test.log")))
                    {
                        try
                        {
                            if (args[0] == "--download-only") AssistantDownload.Download(args[1], log.Write, delegate(int p) { }).GetAwaiter().GetResult();
                            else AssistantCache.Acquire(args[1], true, log.Write, delegate(int p) { }, CancellationToken.None,
                                message => System.Threading.Tasks.Task.FromResult(false)).GetAwaiter().GetResult();
                            return 0;
                        }
                        catch (Exception ex) { log.Write("ERROR " + ex); return 1; }
                    }
                }
                if (args.Length == 2 && args[0] == "--connection-check")
                {
                    File.WriteAllText(args[1], ConnectionCheck.Run().GetAwaiter().GetResult(), System.Text.Encoding.UTF8); return 0;
                }
                if (args.Length == 2 && args[0] == "--probe")
                {
                    MachineReport report = Machine.Inspect();
                    File.WriteAllText(args[1], report.Summary + "\r\nBLOCKS:\r\n" + String.Join("\r\n", report.Blocks) + "\r\nWARNINGS:\r\n" + String.Join("\r\n", report.Warnings)); return 0;
                }
                if (args.Length == 2 && args[0] == "--preview")
                {
                    using (MainForm preview = new MainForm(null, null, null, true))
                    {
                        preview.ShowInTaskbar = false; preview.StartPosition = FormStartPosition.Manual; preview.Location = new Point(-30000, -30000);
                        preview.Show(); preview.PerformLayout(); Application.DoEvents();
                        using (Bitmap image = new Bitmap(preview.Width, preview.Height)) { preview.DrawToBitmap(image, new Rectangle(Point.Empty, preview.Size)); image.Save(Path.GetFullPath(args[1]), System.Drawing.Imaging.ImageFormat.Png); }
                        preview.Hide();
                    }
                    return 0;
                }
                bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                string sid = WindowsIdentity.GetCurrent().User.Value;
                if (!elevated)
                {
                    if (args.Length != 0) throw new ArgumentException("Start the application without command-line arguments.");
                    ProcessStartInfo start = new ProcessStartInfo(Application.ExecutablePath, "--origin-sid " + sid) { UseShellExecute = true, Verb = "runas" };
                    try { Process.Start(start); return 0; }
                    catch (System.ComponentModel.Win32Exception ex) { if (ex.NativeErrorCode == 1223) return 1223; throw; }
                }
                if (args.Length == 2 && args[0] == "--origin-sid")
                {
                    SecurityIdentifier original = new SecurityIdentifier(args[1]);
                    if (!original.IsAccountSid()) throw new ArgumentException("Invalid original user SID");
                    sid = original.Value;
                }
                else if (args.Length != 0) throw new ArgumentException("Unknown arguments");
                MutexSecurity security = new MutexSecurity();
                foreach (WellKnownSidType type in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid }) security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(type, null), MutexRights.FullControl, AccessControlType.Allow));
                bool created;
                using (Mutex mutex = new Mutex(false, @"Global\Win11AssistantBridge-v1", out created, security))
                {
                    bool held = InstanceGate.TryAcquire(mutex);
                    if (!held) { MessageBox.Show("הכלי כבר פועל במחשב זה.", "מעדכן Windows 11"); return 2; }
                    try
                    {
                        string root = SecureStorage.Open();
                        using (FileStream fileLock = new FileStream(Path.Combine(root, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        using (OperationLog log = new OperationLog(Path.Combine(root, "operations.log")))
                        using (MainForm form = new MainForm(root, sid, log, false)) Application.Run(form);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("הפעולה נעצרה: " + ex.Message, "מעדכן Windows 11", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign); return 1;
            }
        }
    }
    internal static class InstanceGate
    {
        public static bool TryAcquire(Mutex mutex)
        {
            try { return mutex.WaitOne(0); }
            catch (AbandonedMutexException) { return true; }
        }
    }
}
