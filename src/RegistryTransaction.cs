using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Microsoft.Win32;

namespace Win11Bridge
{
    [DataContract]
    public sealed class RegValue
    {
        [DataMember] public bool Exists;
        [DataMember] public int Kind;
        [DataMember] public string Text;
        [DataMember] public string[] Strings;
        [DataMember] public byte[] Bytes;
        [DataMember] public long Number;
        public static RegValue Missing() { return new RegValue(); }
        public static RegValue Dword(int n) { return new RegValue { Exists = true, Kind = (int)RegistryValueKind.DWord, Number = n }; }
        public static RegValue Multi(string[] s) { return new RegValue { Exists = true, Kind = (int)RegistryValueKind.MultiString, Strings = s }; }
        public object Data()
        {
            switch ((RegistryValueKind)Kind)
            {
                case RegistryValueKind.DWord: return unchecked((int)Number);
                case RegistryValueKind.QWord: return Number;
                case RegistryValueKind.Binary: case RegistryValueKind.None: return Bytes;
                case RegistryValueKind.MultiString: return Strings;
                case RegistryValueKind.String: case RegistryValueKind.ExpandString: return Text;
                default: throw new InvalidDataException("Unsupported registry type: " + Kind);
            }
        }
        public static RegValue Read(RegistryKey key, string name)
        {
            if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return Missing();
            RegistryValueKind kind = key.GetValueKind(name);
            object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            RegValue r = new RegValue { Exists = true, Kind = (int)kind };
            switch (kind)
            {
                case RegistryValueKind.DWord: r.Number = (int)value; break;
                case RegistryValueKind.QWord: r.Number = (long)value; break;
                case RegistryValueKind.Binary: case RegistryValueKind.None: r.Bytes = (byte[])value; break;
                case RegistryValueKind.MultiString: r.Strings = (string[])value; break;
                case RegistryValueKind.String: case RegistryValueKind.ExpandString: r.Text = (string)value; break;
                default: throw new InvalidDataException("Cannot back up registry kind " + kind);
            }
            return r;
        }
        public bool Same(RegValue other)
        {
            return other != null && Exists == other.Exists && (!Exists ||
                (Kind == other.Kind && Text == other.Text && Number == other.Number &&
                EqualArray(Strings, other.Strings) && EqualArray(Bytes, other.Bytes)));
        }
        private static bool EqualArray<T>(T[] a, T[] b) { return a == null ? b == null : b != null && a.SequenceEqual(b); }
    }

    [DataContract]
    public sealed class RegChange
    {
        [DataMember] public string Hive;
        [DataMember] public string Path;
        [DataMember] public string Name;
        [DataMember] public RegValue Before;
        [DataMember] public RegValue After;
        public string Label { get { return Hive + "\\" + Path + " :: " + Name; } }
    }
    [DataContract]
    public sealed class KeyRef
    {
        [DataMember] public string Hive;
        [DataMember] public string Path;
    }
    [DataContract]
    public sealed class SessionState
    {
        [DataMember] public int Schema = 1;
        [DataMember] public string Id;
        [DataMember] public string CreatedUtc;
        [DataMember] public string OriginalSid;
        [DataMember] public string BuildBefore;
        [DataMember] public string Phase;
        [DataMember] public string AssistantHash;
        [DataMember] public string LaunchUtc;
        [DataMember] public bool SkipCompatCheck;
        [DataMember] public List<RegChange> Changes = new List<RegChange>();
        [DataMember] public List<KeyRef> CreatedKeys = new List<KeyRef>();
    }
    public interface IRegistryStore
    {
        RegValue Read(string hive, string path, string name);
        void Write(string hive, string path, string name, RegValue value);
        bool KeyExists(string hive, string path);
        void DeleteKeyIfEmpty(string hive, string path);
        IEnumerable<RegChange> ValuesBelow(string hive, string path);
    }
    public sealed class WindowsRegistryStore : IRegistryStore
    {
        private RegistryKey Root(string hive)
        {
            RegistryHive h;
            if (hive == "HKLM") h = RegistryHive.LocalMachine;
            else if (hive == "HKU") h = RegistryHive.Users;
            else if (hive == "HKCU") h = RegistryHive.CurrentUser;
            else throw new InvalidDataException("Unknown hive");
            return RegistryKey.OpenBaseKey(h, RegistryView.Registry64);
        }
        public RegValue Read(string hive, string path, string name)
        {
            using (RegistryKey root = Root(hive)) using (RegistryKey key = root.OpenSubKey(path)) return RegValue.Read(key, name);
        }
        public void Write(string hive, string path, string name, RegValue value)
        {
            using (RegistryKey root = Root(hive))
            {
                if (value.Exists)
                {
                    using (RegistryKey key = root.CreateSubKey(path)) { key.SetValue(name, value.Data(), (RegistryValueKind)value.Kind); key.Flush(); }
                }
                else using (RegistryKey key = root.OpenSubKey(path, true)) { if (key != null) { key.DeleteValue(name, false); key.Flush(); } }
            }
        }
        public bool KeyExists(string hive, string path)
        {
            using (RegistryKey root = Root(hive)) using (RegistryKey key = root.OpenSubKey(path)) return key != null;
        }
        public void DeleteKeyIfEmpty(string hive, string path)
        {
            using (RegistryKey root = Root(hive))
            {
                bool empty;
                using (RegistryKey key = root.OpenSubKey(path)) empty = key != null && key.SubKeyCount == 0 && key.ValueCount == 0;
                if (empty) root.DeleteSubKey(path, false); // Never recursive; preserve concurrent additions.
            }
        }
        public IEnumerable<RegChange> ValuesBelow(string hive, string path)
        {
            List<RegChange> result = new List<RegChange>();
            using (RegistryKey root = Root(hive)) using (RegistryKey key = root.OpenSubKey(path)) Capture(hive, path, key, result, 0);
            return result;
        }
        private void Capture(string hive, string path, RegistryKey key, List<RegChange> result, int depth)
        {
            if (key == null) return;
            if (depth > 64 || result.Count > 20000) throw new InvalidDataException("Compatibility cache exceeds backup limits");
            foreach (string name in key.GetValueNames()) result.Add(new RegChange { Hive = hive, Path = path, Name = name, Before = RegValue.Read(key, name), After = RegValue.Missing() });
            foreach (string sub in key.GetSubKeyNames()) using (RegistryKey child = key.OpenSubKey(sub)) Capture(hive, path + "\\" + sub, child, result, depth + 1);
        }
    }
    public static class JsonFile
    {
        public static void Save<T>(string path, T value)
        {
            string temp = path + ".new";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".prev");
            else File.Move(temp, path);
        }
        public static T Read<T>(string path)
        {
            using (FileStream stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
    }
    public sealed class RegistryTransaction
    {
        private readonly IRegistryStore store;
        private readonly Action<string> log;
        public RegistryTransaction(IRegistryStore store, Action<string> log) { this.store = store; this.log = log; }
        public void Add(SessionState state, string hive, string path, string name, RegValue after)
        {
            if (state.Changes.Any(c => c.Hive == hive && c.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Duplicate change");
            state.Changes.Add(new RegChange { Hive = hive, Path = path, Name = name, Before = store.Read(hive, path, name), After = after });
            string current = path;
            while (!String.IsNullOrEmpty(current) && !store.KeyExists(hive, current))
            {
                if (!state.CreatedKeys.Any(k => k.Hive == hive && k.Path.Equals(current, StringComparison.OrdinalIgnoreCase))) state.CreatedKeys.Add(new KeyRef { Hive = hive, Path = current });
                int i = current.LastIndexOf('\\'); current = i < 0 ? "" : current.Substring(0, i);
            }
        }
        public void Apply(SessionState state, string journal)
        {
            state.Phase = "Prepared"; JsonFile.Save(journal, state); // Durable undo data BEFORE any mutation.
            foreach (RegChange c in state.Changes)
            {
                if (!store.Read(c.Hive, c.Path, c.Name).Same(c.Before)) throw new IOException("Registry changed during preparation: " + c.Label);
                store.Write(c.Hive, c.Path, c.Name, c.After);
                if (!store.Read(c.Hive, c.Path, c.Name).Same(c.After)) throw new IOException("Read-back failed: " + c.Label);
                log("APPLIED " + c.Label);
            }
            state.Phase = "Applied"; JsonFile.Save(journal, state);
        }
        public List<string> Restore(SessionState state, string journal)
        {
            List<string> conflicts = new List<string>();
            foreach (RegChange c in state.Changes.AsEnumerable().Reverse())
            {
                try
                {
                    RegValue now = store.Read(c.Hive, c.Path, c.Name);
                    if (now.Same(c.Before)) continue;
                    if (!now.Same(c.After)) { conflicts.Add(c.Label); log("RESTORE CONFLICT " + c.Label); continue; }
                    store.Write(c.Hive, c.Path, c.Name, c.Before);
                    if (!store.Read(c.Hive, c.Path, c.Name).Same(c.Before)) throw new IOException("Restore read-back failed");
                    log("RESTORED " + c.Label);
                }
                catch (Exception ex) { conflicts.Add(c.Label + ": " + ex.Message); log("RESTORE ERROR " + c.Label + ": " + ex.Message); }
            }
            foreach (KeyRef k in state.CreatedKeys.OrderByDescending(k => k.Path.Length))
            {
                try { store.DeleteKeyIfEmpty(k.Hive, k.Path); }
                catch (Exception ex) { conflicts.Add(k.Hive + "\\" + k.Path + ": " + ex.Message); }
            }
            state.Phase = conflicts.Count == 0 ? "Restored" : "RestoreConflicts";
            JsonFile.Save(journal, state); return conflicts;
        }
    }
    public static class BypassPlan
    {
        public const string AppCompat = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags";
        public static SessionState Create(IRegistryStore store, string sid, string adminSid, bool clearCache, bool labConfig, Action<string> log)
        {
            SessionState state = new SessionState { Id = Guid.NewGuid().ToString("N"), CreatedUtc = DateTime.UtcNow.ToString("o"), OriginalSid = sid, BuildBefore = Machine.Build() };
            RegistryTransaction tx = new RegistryTransaction(store, log);
            RegValue old = store.Read("HKLM", AppCompat + @"\HwReqChk", "HwReqChkVars");
            if (old.Exists && old.Kind != (int)RegistryValueKind.MultiString) throw new InvalidDataException("HwReqChkVars is not REG_MULTI_SZ; refusing to replace an unexpected type");
            string[] overrides = { "SQ_SecureBootCapable=TRUE", "SQ_SecureBootEnabled=TRUE", "SQ_TpmVersion=2", "SQ_RamMB=8192" };
            List<string> merged = new List<string>(old.Exists ? old.Strings : new string[0]);
            foreach (string value in overrides)
            {
                string prefix = value.Split('=')[0] + "=";
                merged.RemoveAll(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)); merged.Add(value);
            }
            tx.Add(state, "HKLM", AppCompat + @"\HwReqChk", "HwReqChkVars", RegValue.Multi(merged.ToArray()));
            tx.Add(state, "HKLM", @"SYSTEM\Setup\MoSetup", "AllowUpgradesWithUnsupportedTPMOrCPU", RegValue.Dword(1));
            foreach (string user in new[] { sid, adminSid }.Distinct())
            {
                if (!store.KeyExists("HKU", user)) throw new InvalidOperationException("User hive not loaded: " + user);
                tx.Add(state, "HKU", user + @"\Software\Microsoft\PCHC", "UpgradeEligibility", RegValue.Dword(1));
            }
            if (labConfig) foreach (string name in new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck" })
                tx.Add(state, "HKLM", @"SYSTEM\Setup\LabConfig", name, RegValue.Dword(1));
            if (clearCache) foreach (string key in new[] { "CompatMarkers", "Shared", "TargetVersionUpgradeExperienceIndicators" })
                foreach (RegChange c in store.ValuesBelow("HKLM", AppCompat + "\\" + key)) tx.Add(state, c.Hive, c.Path, c.Name, RegValue.Missing());
            return state;
        }
    }
}
