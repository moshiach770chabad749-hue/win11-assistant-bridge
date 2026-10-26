using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.Win32;

namespace Win11Bridge
{
    internal sealed class FakeRegistry : IRegistryStore
    {
        public readonly Dictionary<string, RegValue> Values = new Dictionary<string, RegValue>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int Writes;
        public int FailAt = -1;
        private static string K(string h, string p) { return h + "\\" + p; }
        private static string V(string h, string p, string n) { return K(h, p) + "|" + n; }
        public RegValue Read(string h, string p, string n) { RegValue v; return Values.TryGetValue(V(h, p, n), out v) ? v : RegValue.Missing(); }
        public void Write(string h, string p, string n, RegValue value)
        {
            if (++Writes == FailAt) throw new IOException("Injected registry write failure");
            if (value.Exists)
            {
                string part = p;
                while (part.Length > 0) { Keys.Add(K(h, part)); int i = part.LastIndexOf('\\'); part = i < 0 ? "" : part.Substring(0, i); }
                Values[V(h, p, n)] = value;
            }
            else Values.Remove(V(h, p, n));
        }
        public bool KeyExists(string h, string p) { return Keys.Contains(K(h, p)); }
        public void DeleteKeyIfEmpty(string h, string p)
        {
            string key = K(h, p);
            if (!Keys.Any(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)) && !Values.Keys.Any(k => k.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase))) Keys.Remove(key);
        }
        public IEnumerable<RegChange> ValuesBelow(string h, string p)
        {
            foreach (KeyValuePair<string, RegValue> pair in Values.ToArray())
            {
                string prefix = K(h, p);
                if (!pair.Key.StartsWith(prefix + "|", StringComparison.OrdinalIgnoreCase) && !pair.Key.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                int separator = pair.Key.IndexOf('|');
                yield return new RegChange { Hive = h, Path = pair.Key.Substring(h.Length + 1, separator - h.Length - 1), Name = pair.Key.Substring(separator + 1), Before = pair.Value, After = RegValue.Missing() };
            }
        }
    }
    public static class SelfTests
    {
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static SessionState NewState() { return new SessionState { Id = Guid.NewGuid().ToString("N"), CreatedUtc = DateTime.UtcNow.ToString("o") }; }
        public static int Run(string reportPath)
        {
            string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)), "test-work-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            List<string> results = new List<string>(); int failed = 0; int total = 0;
            Action<string, Action> test = (name, action) => { total++; try { action(); results.Add("PASS " + name); } catch (Exception ex) { failed++; results.Add("FAIL " + name + " :: " + ex); } };
            string journal = Path.Combine(folder, "state.json"); Action<string> log = delegate(string text) { };
            test("durable typed backup, apply, restore and idempotent restore", () =>
            {
                FakeRegistry store = new FakeRegistry(); SessionState state = NewState(); RegistryTransaction tx = new RegistryTransaction(store, log);
                RegValue[] values = {
                    new RegValue { Exists=true, Kind=(int)RegistryValueKind.String, Text="שלום" },
                    new RegValue { Exists=true, Kind=(int)RegistryValueKind.ExpandString, Text="%TEMP%\\unchanged" },
                    RegValue.Multi(new[] {"one", "שתיים"}), RegValue.Dword(-1),
                    new RegValue { Exists=true, Kind=(int)RegistryValueKind.QWord, Number=Int64.MinValue },
                    new RegValue { Exists=true, Kind=(int)RegistryValueKind.Binary, Bytes=new byte[] {0, 1, 255} },
                    new RegValue { Exists=true, Kind=(int)RegistryValueKind.None, Bytes=new byte[] {9, 0} }
                };
                for (int i=0; i<values.Length; i++) { store.Write("HKCU", "Existing", i.ToString(), values[i]); tx.Add(state, "HKCU", "Existing", i.ToString(), RegValue.Dword(1)); }
                tx.Add(state, "HKCU", @"New\Nested", "added", RegValue.Dword(1)); tx.Apply(state, journal);
                state = JsonFile.Read<SessionState>(journal);
                Check(tx.Restore(state, journal).Count == 0, "restore conflicts");
                for (int i=0; i<values.Length; i++) Check(store.Read("HKCU", "Existing", i.ToString()).Same(values[i]), "type mismatch " + i);
                Check(!store.KeyExists("HKCU", "New"), "created ancestors not pruned");
                Check(store.KeyExists("HKCU", "Existing"), "existing key lost"); Check(tx.Restore(state, journal).Count == 0, "idempotence");
            });
            test("write failure rolls back partial application from disk", () =>
            {
                FakeRegistry store = new FakeRegistry(); SessionState state = NewState(); RegistryTransaction tx = new RegistryTransaction(store, log);
                for (int i=0; i<5; i++) tx.Add(state, "HKCU", "Partial", i.ToString(), RegValue.Dword(1));
                store.FailAt = 3; bool threw = false; try { tx.Apply(state, journal); } catch (IOException) { threw = true; }
                Check(threw && store.Values.Count == 2, "failure injection ineffective");
                SessionState saved = JsonFile.Read<SessionState>(journal); Check(saved.Changes.Count == 5, "backup not complete before mutation");
                Check(tx.Restore(saved, journal).Count == 0 && store.Values.Count == 0, "partial rollback failed");
            });
            test("third-party change preserved; conflict remains retryable", () =>
            {
                FakeRegistry store = new FakeRegistry(); SessionState state = NewState(); RegistryTransaction tx = new RegistryTransaction(store, log);
                store.Write("HKCU", "Conflict", "x", RegValue.Dword(4)); tx.Add(state, "HKCU", "Conflict", "x", RegValue.Dword(1)); tx.Apply(state, journal);
                store.Write("HKCU", "Conflict", "x", RegValue.Dword(99));
                Check(tx.Restore(state, journal).Count == 1 && state.Phase == "RestoreConflicts", "conflict not detected"); Check(store.Read("HKCU", "Conflict", "x").Number == 99, "third-party data clobbered");
                store.Write("HKCU", "Conflict", "x", RegValue.Dword(1)); Check(tx.Restore(state, journal).Count == 0 && store.Read("HKCU", "Conflict", "x").Number == 4, "retry failed");
            });
            test("race between plan and apply stops before overwrite", () =>
            {
                FakeRegistry store = new FakeRegistry(); SessionState state = NewState(); RegistryTransaction tx = new RegistryTransaction(store, log);
                tx.Add(state, "HKCU", "Race", "x", RegValue.Dword(1)); store.Write("HKCU", "Race", "x", RegValue.Dword(8));
                bool stopped = false; try { tx.Apply(state, journal); } catch (IOException) { stopped = true; }
                Check(stopped && store.Read("HKCU", "Race", "x").Number == 8, "race overwrote data");
            });
            test("cache clearing restores values and preserves key structure", () =>
            {
                FakeRegistry store = new FakeRegistry(); store.Write("HKLM", BypassPlan.AppCompat + @"\Shared\Child", "x", RegValue.Dword(3)); store.Keys.Add(@"HKU\S-1-5-21-1-2-3-1001");
                SessionState state = BypassPlan.Create(store, "S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001", true, false, log);
                RegistryTransaction tx = new RegistryTransaction(store, log); tx.Apply(state, journal);
                Check(!store.Read("HKLM", BypassPlan.AppCompat + @"\Shared\Child", "x").Exists, "cache not cleared"); Check(store.KeyExists("HKLM", BypassPlan.AppCompat + @"\Shared\Child"), "cache key removed");
                Check(tx.Restore(state, journal).Count == 0 && store.Read("HKLM", BypassPlan.AppCompat + @"\Shared\Child", "x").Number == 3, "cache restore failed");
            });
            test("HwReqChkVars merge; two targeted identities; only three supported LabConfig names", () =>
            {
                FakeRegistry store = new FakeRegistry(); store.Keys.Add(@"HKU\S-1-5-21-1-2-3-1001"); store.Keys.Add(@"HKU\S-1-5-21-1-2-3-1002");
                store.Write("HKLM", BypassPlan.AppCompat + @"\HwReqChk", "HwReqChkVars", RegValue.Multi(new[] { "SQ_RamMB=1024", "Custom=keep", "SQ_TpmVersion=0" }));
                SessionState state = BypassPlan.Create(store, "S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1002", false, true, log);
                string[] merged = state.Changes.First().After.Strings;
                Check(merged.Contains("Custom=keep") && merged.Contains("SQ_RamMB=8192") && !merged.Contains("SQ_RamMB=1024"), "merge incorrect");
                Check(state.Changes.Count(c => c.Hive == "HKU") == 2, "identity scope wrong");
                Check(state.Changes.Count(c => c.Path.EndsWith("LabConfig")) == 3 && !state.Changes.Any(c => c.Name == "BypassCPUCheck" || c.Name == "BypassStorageCheck"), "invented bypass");
            });
            test("missing original hive rejected before applying anything", () =>
            {
                FakeRegistry store = new FakeRegistry(); bool rejected = false;
                try { BypassPlan.Create(store, "missing", "missing", false, false, log); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && store.Writes == 0, "unknown user hive modified");
            });
            test("unexpected HwReqChkVars type rejected", () =>
            {
                FakeRegistry store = new FakeRegistry(); store.Write("HKLM", BypassPlan.AppCompat + @"\HwReqChk", "HwReqChkVars", RegValue.Dword(7));
                bool rejected = false; try { BypassPlan.Create(store, "x", "x", false, false, log); } catch (InvalidDataException) { rejected = true; } Check(rejected, "unexpected type accepted");
            });
            test("strict HTTPS Microsoft redirect policy", () =>
            {
                Check(AssistantDownload.Allowed(new Uri(AssistantDownload.Source)), "official URL rejected");
                foreach (string url in new[] {"http://download.microsoft.com/a", "https://download.microsoft.com.evil.test/a", "https://evil.test/a", "https://user@download.microsoft.com/a", "https://download.microsoft.com:444/a", "file:///C:/x"}) Check(!AssistantDownload.Allowed(new Uri(url)), "unsafe URL allowed " + url);
            });
            test("unsigned executable rejected by native Authenticode verifier", () =>
            {
                bool rejected = false; try { SignatureVerifier.VerifyMicrosoft(System.Windows.Forms.Application.ExecutablePath); } catch (InvalidDataException) { rejected = true; } Check(rejected, "unsigned wrapper trusted");
            });
            test("launch arguments contain only explicit experimental compatibility option", () =>
            {
                Check(AssistantDownload.LaunchArguments(false) == "", "default launch is not interactive default");
                Check(AssistantDownload.LaunchArguments(true) == "/SkipCompatCheck", "unexpected launch switches");
            });
            test("native x64 CPUID returns distinct SSE4.2 and POPCNT bits", () =>
            {
                uint features = Machine.CpuFeatures(); Check(features != 0 && features != UInt32.MaxValue, "invalid CPUID"); results.Add("INFO CPUID.1.ECX=0x" + features.ToString("X8"));
            });
            test("global named mutex and exclusive file reject concurrent acquisition", () =>
            {
                string name = @"Global\Win11Bridge-Test-" + Guid.NewGuid().ToString("N");
                using (Mutex first = new Mutex(false, name))
                {
                    Check(InstanceGate.TryAcquire(first), "first mutex acquisition failed");
                    try
                    {
                        bool secondAcquired = Task.Run(() => { using (Mutex second = new Mutex(false, name)) { bool held = InstanceGate.TryAcquire(second); if (held) second.ReleaseMutex(); return held; } }).GetAwaiter().GetResult();
                        Check(!secondAcquired, "second mutex acquisition was allowed");
                    }
                    finally { first.ReleaseMutex(); }
                    Check(InstanceGate.TryAcquire(first), "mutex was not released"); first.ReleaseMutex();
                }
                string lockPath = Path.Combine(folder, "lock-test");
                using (FileStream first = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    bool denied = false;
                    try { using (FileStream second = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { } }
                    catch (IOException) { denied = true; }
                    Check(denied, "second file lock allowed");
                }
            });
            test("restore countdown gates a recovered launch until a full observed minute", () =>
            {
                DateTime start = new DateTime(2026, 10, 25, 8, 46, 5, DateTimeKind.Utc);
                Check(RestoreGuard.RemainingSeconds("Launched", start, start) == 60, "recovered launch must wait");
                Check(RestoreGuard.RemainingSeconds("Launched", start, start.AddSeconds(9)) == 51, "first reported early click");
                Check(RestoreGuard.RemainingSeconds("Launched", start, start.AddSeconds(26)) == 34, "second reported early click");
                Check(RestoreGuard.RemainingSeconds("LaunchRequested", start, start.AddSeconds(59.9)) == 1, "fractional last second must stay blocked");
                Check(RestoreGuard.RemainingSeconds("Launched", start, start.AddSeconds(60)) == 0, "full minute must release");
                Check(RestoreGuard.RemainingSeconds("Launched", start.AddSeconds(50), start.AddSeconds(60)) == 50, "new installer observation must reset wait");
                Check(RestoreGuard.RemainingSeconds("Applied", start, start) == 0, "unlaunched preparation should not wait");
                Check(RestoreGuard.RemainingSeconds(null, start, start) == 0, "no session should not wait");
                Check(RestoreGuard.RemainingSeconds("Launched", start, start.AddSeconds(-10)) == 60, "clock reversal should remain bounded and blocked");
            });
            test("actual form disables restore while waiting and enables it after quiet interval", () =>
            {
                using (MainForm form = new MainForm(folder, "test", null, false))
                {
                    BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    Type type = typeof(MainForm);
                    type.GetField("state", flags).SetValue(form, new SessionState { Phase = "Launched" });
                    type.GetField("lastSeen", flags).SetValue(form, DateTime.UtcNow);
                    MethodInfo update = type.GetMethod("UpdateButtons", flags);
                    System.Windows.Forms.Button button = (System.Windows.Forms.Button)type.GetField("restore", flags).GetValue(form);
                    update.Invoke(form, null); Check(!button.Enabled && button.Text.Contains("שניות"), "waiting restore is clickable or lacks countdown");
                    type.GetField("lastSeen", flags).SetValue(form, DateTime.UtcNow.AddSeconds(-61));
                    update.Invoke(form, null); Check(button.Enabled && button.Text == "שחזור הגדרות", "restore did not unlock after wait");
                    type.GetField("installerObserved", flags).SetValue(form, true);
                    update.Invoke(form, null); Check(!button.Enabled, "active installer must block restore");
                }
            });
            // Network simulations run without a UI synchronization context and never contact the Internet.
            Task.Run(() => DownloadTests.Run(test, folder)).GetAwaiter().GetResult();
            results.Add("TOTAL tests=" + total + " failed=" + failed + " UTC=" + DateTime.UtcNow.ToString("o"));
            results.Add("No OS upgrade, installer launch, eligibility registry writes, reboot or formatting performed.");
            File.WriteAllLines(reportPath, results, Encoding.UTF8); return failed == 0 ? 0 : 1;
        }
        public static int NativeRegistryTest(string reportPath)
        {
            string path = @"Software\Win11AssistantBridge.Tests\" + Guid.NewGuid().ToString("N");
            string folder = Path.GetDirectoryName(Path.GetFullPath(reportPath)); string journal = Path.Combine(folder, "native-test-state.json");
            WindowsRegistryStore store = new WindowsRegistryStore(); SessionState state = NewState(); RegistryTransaction tx = new RegistryTransaction(store, delegate(string s) { });
            List<string> lines = new List<string>();
            try
            {
                RegValue[] values = { RegValue.Dword(-7), RegValue.Multi(new[] { "אחד", "two" }), new RegValue { Exists=true, Kind=(int)RegistryValueKind.ExpandString, Text="%TEMP%\\literal" }, new RegValue { Exists=true, Kind=(int)RegistryValueKind.QWord, Number=Int64.MinValue }, new RegValue { Exists=true, Kind=(int)RegistryValueKind.Binary, Bytes=new byte[] { 0, 255, 4 } }, new RegValue { Exists=true, Kind=(int)RegistryValueKind.None, Bytes=new byte[] { 3, 9 } }, new RegValue { Exists=true, Kind=(int)RegistryValueKind.String, Text="שלום" } };
                for (int i=0; i<values.Length; i++) { store.Write("HKCU", path, i.ToString(), values[i]); tx.Add(state, "HKCU", path, i.ToString(), RegValue.Dword(1)); }
                tx.Add(state, "HKCU", path + @"\Created\Child", "added", RegValue.Multi(new string[0]));
                tx.Apply(state, journal); Check(tx.Restore(JsonFile.Read<SessionState>(journal), journal).Count == 0, "native restore conflicts");
                for (int i=0; i<values.Length; i++) Check(store.Read("HKCU", path, i.ToString()).Same(values[i]), "native type roundtrip " + i);
                Check(!store.KeyExists("HKCU", path + @"\Created"), "native empty key cleanup failed");
                lines.Add("PASS actual Windows Registry64 HKCU roundtrip: String, ExpandString, MultiString, DWORD, QWORD, Binary, REG_NONE; absent values; empty created ancestors; JSON recovery");
                lines.Add("Test namespace only: HKCU\\" + path); return 0;
            }
            catch (Exception ex) { lines.Add("FAIL " + ex); return 1; }
            finally
            {
                // Exact random test-owned namespace. Never delete an upgrade or user settings key.
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)) root.DeleteSubKeyTree(path, false);
                File.WriteAllLines(reportPath, lines, Encoding.UTF8);
            }
        }
    }
}
