using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Win11Bridge
{
    public sealed class MainForm : Form
    {
        private readonly string root;
        private readonly string originalSid;
        private readonly OperationLog log;
        private readonly bool preview;
        private readonly IRegistryStore registry = new WindowsRegistryStore();
        private readonly Label status = new Label();
        private readonly TextBox machine = new TextBox();
        private readonly TextBox journalView = new TextBox();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly CheckBox backup = new CheckBox();
        private readonly CheckBox unsupported = new CheckBox();
        private readonly CheckBox cache = new CheckBox();
        private readonly CheckBox lab = new CheckBox();
        private readonly CheckBox skipCompat = new CheckBox();
        private readonly CheckBox reuseAssistant = new CheckBox();
        private readonly CheckBox restartConsent = new CheckBox();
        private readonly Button prepare = new Button();
        private readonly Button launch = new Button();
        private readonly Button restore = new Button();
        private readonly Button collect = new Button();
        private readonly Button open = new Button();
        private readonly Button connection = new Button();
        private readonly Button import = new Button();
        private readonly Button cancelDownload = new Button();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private CancellationTokenSource downloadCancellation;
        private SessionState state;
        private bool busy;
        private string lastProcesses;
        private DateTime lastSeen;
        private bool installerObserved;
        private bool evidenceCaptured;
        private string StatePath { get { return Path.Combine(root, "state.json"); } }
        public MainForm(string root, string originalSid, OperationLog log, bool preview)
        {
            this.root = root; this.originalSid = originalSid; this.log = log; this.preview = preview;
            Text = "מעדכן Windows 11 | גרסה " + ReleaseInfo.Version;
            Font = new Font("Segoe UI", 10F); BackColor = Color.FromArgb(244, 247, 251);
            RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
            ClientSize = new Size(960, 820); MinimumSize = new Size(900, 820); StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, Padding = new Padding(24) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            float[] heights = { 48, 68, 70, 150, 62, 50, 34, 64, 0, 50 };
            for (int i = 0; i < heights.Length; i++) layout.RowStyles.Add(i == 8 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, heights[i]));
            Controls.Add(layout);
            Label title = new Label { Text = "Windows 11 · עדכון במקום", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 23F, FontStyle.Bold), ForeColor = Color.FromArgb(20, 49, 85), TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(new Label { Text = "הכנה הפיכה והפעלת Installation Assistant המקורי של Microsoft.\r\nהאשף יוריד ויתקין את Windows. מיועד לשמירת הקבצים והיישומים במחשב שכבר מריץ Windows 11.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            machine.Multiline = true; machine.ReadOnly = true; machine.Dock = DockStyle.Fill; machine.BackColor = Color.White; machine.BorderStyle = BorderStyle.FixedSingle; machine.ScrollBars = ScrollBars.Vertical;
            machine.Text = preview ? "Windows 11 x64 | בדיקת מעבד, זיכרון ושטח פנוי תופיע כאן\r\nעקיפת בדיקת זכאות אינה מוסיפה חומרה חסרה." : "קורא נתוני מערכת…";
            machine.TabStop = false;
            layout.Controls.Add(machine, 0, 2);
            FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            SetupCheck(backup, "יצרתי גיבוי לקבצים החשובים. גיבוי הכלי מכסה רק את ההגדרות שישתנו.");
            SetupCheck(unsupported, "אני מבין שבחומרה לא נתמכת ייתכנו חסימה נוספת, כשל או היעדר תמיכה ועדכונים.");
            SetupCheck(cache, "נקה דגלי תאימות ישנים לאחר גיבוי (ייתכן שייבנו מחדש)"); cache.Checked = true;
            SetupCheck(lab, "עקיפות התקנה נוספות ל־TPM, Secure Boot וזיכרון (ניסיוני)");
            SetupCheck(skipCompat, "דלג על בדיקת התאימות הראשונית של האשף (ניסיוני; אינו מבטיח מעבר בדיקה מאוחרת)");
            SetupCheck(reuseAssistant, "השתמש באשף שכבר הורד לאחר אימות חתימתו; להורדת Windows עדיין נדרש אינטרנט"); reuseAssistant.Checked = true;
            options.Controls.AddRange(new Control[] { backup, unsupported, cache, lab, skipCompat, reuseAssistant }); layout.Controls.Add(options, 0, 3);
            Label boundary = new Label { Text = "עקיפות הזכאות אינן מבטיחות מעבר של בדיקה מאוחרת. אם מוצעת מחיקת נתונים — בטל.\r\nפס ההתקדמות כאן מציג את הורדת האשף בלבד. התקדמות התקנת Windows מוצגת בחלון Microsoft.", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(130, 74, 9), TextAlign = ContentAlignment.MiddleLeft }; layout.Controls.Add(boundary, 0, 4);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            SetupButton(prepare, "1. הורדה והכנה", true); SetupButton(launch, "2. הפעלת אשף Microsoft", true); SetupButton(restore, "שחזור הגדרות", false);
            buttons.Controls.AddRange(new Control[] { prepare, launch, restore }); layout.Controls.Add(buttons, 0, 5);
            SetupCheck(restartConsent, "אני מאשר התקנה ואתחולים בהמשך על ידי אשף Microsoft לאחר הפעלתו."); restartConsent.Dock = DockStyle.Fill; layout.Controls.Add(restartConsent, 0, 6);
            status.Text = "מוכן להכנה. האשף יופעל רק לאחר אישור נפרד."; status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Color.FromArgb(20, 49, 85); layout.Controls.Add(status, 0, 7);
            journalView.Multiline = true; journalView.ReadOnly = true; journalView.Dock = DockStyle.Fill; journalView.ScrollBars = ScrollBars.Both; journalView.WordWrap = false; journalView.RightToLeft = RightToLeft.No; journalView.Font = new Font("Consolas", 9F); journalView.BackColor = Color.White;
            journalView.Text = preview ? "היומן יכלול שלב, נתיב, זמן ותוצאות אימות.\r\nגיבוי יישמר לפני שינוי ראשון." : ""; layout.Controls.Add(journalView, 0, 8);
            journalView.TabStop = false;
            FlowLayoutPanel footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            SetupButton(collect, "שמירת יומן ל־ZIP", false); SetupButton(open, "יומן וגיבוי", false);
            SetupButton(connection, "בדיקת אינטרנט", false); SetupButton(import, "בחירת אשף מקומי", false); SetupButton(cancelDownload, "ביטול הורדה", false);
            progress.Width = 90; progress.Height = 25; progress.Margin = new Padding(8, 6, 3, 3);
            footer.Controls.AddRange(new Control[] { collect, open, connection, import, cancelDownload, progress }); layout.Controls.Add(footer, 0, 9);
            prepare.Click += async delegate { await Prepare(); }; launch.Click += async delegate { await LaunchAssistant(); }; restore.Click += async delegate { await Restore(); };
            collect.Click += async delegate { await Collect(); }; open.Click += delegate { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + root + "\"") { UseShellExecute = true }); };
            connection.Click += async delegate { await CheckConnection(); }; import.Click += async delegate { await ImportAssistant(); };
            cancelDownload.Click += delegate { if (downloadCancellation != null) { downloadCancellation.Cancel(); status.Text = "מבטל את הורדת האשף. אם אימות חתימה כבר התחיל, ממתין לסיומו. מתקין Windows אינו נעצר."; cancelDownload.Enabled = false; } };
            backup.CheckedChanged += delegate { UpdateButtons(); }; unsupported.CheckedChanged += delegate { UpdateButtons(); }; restartConsent.CheckedChanged += delegate { UpdateButtons(); };
            FormClosing += HandleClosing; Shown += async delegate { if (!preview) await InitializeState(); };
            timer.Interval = 1000; timer.Tick += delegate { Monitor(); };
            if (log != null) log.Added += OnLog;
            UpdateButtons();
        }
        private static void SetupCheck(CheckBox c, string text) { c.Text = text; c.AutoSize = true; c.Margin = new Padding(3, 1, 3, 1); }
        private static void SetupButton(Button b, string text, bool primary)
        {
            b.Text = text; b.AutoSize = true; b.Height = 37; b.Padding = new Padding(10, 3, 10, 3); b.Margin = new Padding(4); b.FlatStyle = FlatStyle.Flat;
            b.BackColor = primary ? Color.FromArgb(29, 91, 170) : Color.White; b.ForeColor = primary ? Color.White : Color.FromArgb(20, 49, 85); b.FlatAppearance.BorderSize = primary ? 0 : 1;
        }
        private void OnLog(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnLog), line); return; }
            if (journalView.TextLength > 100000) journalView.Text = journalView.Text.Substring(journalView.TextLength - 50000);
            journalView.AppendText(line + Environment.NewLine);
        }
        private void UpdateButtons()
        {
            bool active = state != null && state.Phase != "Restored";
            prepare.Enabled = !busy && !active && backup.Checked && unsupported.Checked;
            int remaining = RestoreGuard.RemainingSeconds(state == null ? null : state.Phase, lastSeen, DateTime.UtcNow);
            launch.Enabled = !busy && active && SessionRules.CanLaunch(state.Phase, installerObserved, remaining) && restartConsent.Checked;
            launch.Text = active && RestoreGuard.RequiresWait(state.Phase) ? "2. ניסיון נוסף באשף" : "2. הפעלת אשף Microsoft";
            restore.Enabled = !busy && active && !installerObserved && remaining == 0; collect.Enabled = !busy;
            restore.Text = installerObserved ? "שחזור — המתקין פעיל" : remaining > 0 ? "שחזור — עוד " + remaining + " שניות" : "שחזור הגדרות";
            cache.Enabled = lab.Enabled = skipCompat.Enabled = backup.Enabled = unsupported.Enabled = reuseAssistant.Enabled = !busy && !active;
            restartConsent.Enabled = !busy; open.Enabled = connection.Enabled = !busy;
            import.Enabled = !busy && !active && !installerObserved;
            cancelDownload.Enabled = busy && downloadCancellation != null && !downloadCancellation.IsCancellationRequested;
            if (preview) { prepare.Enabled = true; launch.Enabled = false; restore.Enabled = false; }
        }
        private async Task InitializeState()
        {
            busy = true; UpdateButtons();
            try
            {
                log.Write("START version=" + ReleaseInfo.Version + " originSID=" + originalSid + " elevatedSID=" + WindowsIdentity.GetCurrent().User.Value);
                if (File.Exists(StatePath))
                {
                    state = JsonFile.Read<SessionState>(StatePath);
                    if (state == null || state.Schema != 1 || state.Changes == null || state.CreatedKeys == null) throw new InvalidDataException("Invalid backup schema");
                    log.Write("RECOVERY session=" + state.Id + " phase=" + state.Phase + " originalBuild=" + state.BuildBefore + " currentBuild=" + Machine.Build());
                    if (RestoreGuard.RequiresWait(state.Phase)) status.Text = "נמצאה הפעלה קודמת. לאחר דקה ללא מתקינים אפשר לאשר ניסיון נוסף באשף, או לשחזר כדי לשנות סימונים.";
                    else if (state.Phase == "Applied") status.Text = "נמצאה הכנה קיימת. אפשר לאשר התקנה ולהפעיל את אשף Microsoft, או לשחזר את ההגדרות.";
                    else if (state.Phase != "Restored") status.Text = "נמצא גיבוי של הכנה שלא הושלמה או שחזור חלקי. בדוק ביומן ושחזר לפני הכנה חדשה.";
                }
                MachineReport report = await Task.Run(() => Machine.Inspect()); ShowReport(report);
                lastSeen = DateTime.UtcNow; installerObserved = Machine.UpgradeProcesses().Count > 0; timer.Start();
            }
            catch (Exception ex) { Fail("טעינת מצב", ex); prepare.Visible = launch.Visible = restore.Visible = false; }
            finally { busy = false; UpdateButtons(); }
        }
        private void ShowReport(MachineReport report)
        {
            machine.Text = report.Summary + "\r\n" + String.Join("\r\n", report.Blocks.Concat(report.Warnings)); log.Write("PREFLIGHT " + report.Summary);
            foreach (string line in report.Blocks) log.Write("BLOCK " + line);
            foreach (string line in report.Warnings) log.Write("WARNING " + line);
        }
        private void RequireIdle()
        {
            List<string> processes = Machine.UpgradeProcesses();
            if (processes.Count > 0) throw new InvalidOperationException("כבר פועל מתקין. אין לשנות הגדרות בזמן פעילותו: " + String.Join(", ", processes));
            if (Machine.PendingSetup().SetupInProgress) throw new InvalidOperationException("Windows מסמן שהתקנת מערכת עדיין פעילה. אין לשנות את הגדרות ההכנה או לפתוח מתקין נוסף.");
        }
        private async Task Prepare()
        {
            busy = true; UpdateButtons(); bool transactionStarted = false;
            try
            {
                RequireIdle();
                MachineReport report = await Task.Run(() => Machine.Inspect()); ShowReport(report);
                if (report.Blocks.Count > 0) throw new InvalidOperationException(String.Join("\r\n", report.Blocks));
                if (report.Warnings.Count > 0 && Ask(String.Join("\r\n", report.Warnings) + "\r\nלהמשיך להכנה?") != DialogResult.Yes) return;
                status.Text = "מוריד את המעדכן ממיקרוסופט ומאמת את חתימתו…";
                downloadCancellation = new CancellationTokenSource(); UpdateButtons();
                string assistant;
                try
                {
                    assistant = await AssistantCache.Acquire(root, reuseAssistant.Checked, log.Write,
                        p => { if (!IsDisposed) progress.Value = p; }, downloadCancellation.Token,
                        message => Task.FromResult(Ask(message) == DialogResult.Yes));
                    downloadCancellation.Token.ThrowIfCancellationRequested();
                }
                finally { downloadCancellation.Dispose(); downloadCancellation = null; UpdateButtons(); }
                RequireIdle(); // A separate installer may have started during the download.
                if (state != null && state.Phase == "Restored") File.Copy(StatePath, Path.Combine(root, "archive-" + state.Id + "-" + Guid.NewGuid().ToString("N") + ".json"), false);
                state = BypassPlan.Create(registry, originalSid, WindowsIdentity.GetCurrent().User.Value, cache.Checked, lab.Checked, log.Write);
                state.SkipCompatCheck = skipCompat.Checked;
                state.AssistantHash = SignatureVerifier.Hash(assistant);
                JsonFile.Save(Path.Combine(root, "backup-" + state.Id + ".json"), state);
                transactionStarted = true;
                new RegistryTransaction(registry, log.Write).Apply(state, StatePath);
                status.Text = "ההכנה הושלמה והגיבוי נשמר. אשר התקנה ואתחולים כדי להפעיל את האשף.";
                log.Write("PREPARATION COMPLETE; installer has NOT been started");
            }
            catch (OperationCanceledException)
            {
                log.Write("PREPARATION CANCELLED before eligibility writes; Microsoft installer was not stopped");
                status.Text = "ההכנה בוטלה לפני שינוי הגדרות זכאות. אשף Microsoft לא הופעל ולא נעצר.";
            }
            catch (Exception ex)
            {
                Fail("הכנה", ex);
                if (!transactionStarted) log.Write("PREPARATION STOPPED before new eligibility registry writes");
                if (transactionStarted)
                {
                    try
                    {
                        // Do not race a newly started third-party installer during rollback.
                        RequireIdle();
                        List<string> conflicts = new RegistryTransaction(registry, log.Write).Restore(state, StatePath);
                        log.Write("PREPARATION ROLLBACK conflicts=" + conflicts.Count);
                        status.Text = conflicts.Count == 0 ? "ההכנה נכשלה. השינויים שוחזרו." : "השחזור חלקי. פירוט ההתנגשויות ביומן.";
                    }
                    catch (Exception restoreError) { Fail("שחזור לאחר כשל; הגיבוי נשמר", restoreError); }
                }
            }
            finally { busy = false; UpdateButtons(); }
        }
        private async Task LaunchAssistant()
        {
            if (state == null || !restartConsent.Checked || !SessionRules.CanLaunch(state.Phase, installerObserved,
                RestoreGuard.RemainingSeconds(state.Phase, lastSeen, DateTime.UtcNow))) return;
            busy = true; UpdateButtons();
            try
            {
                RequireIdle(); MachineReport report = await Task.Run(() => Machine.Inspect()); ShowReport(report);
                if (report.Blocks.Count > 0) throw new InvalidOperationException(String.Join("\r\n", report.Blocks));
                PendingSetupStatus pending = Machine.PendingSetup(); log.Write("SETUP READINESS " + pending.Summary);
                if (pending.SetupInProgress) throw new InvalidOperationException("Windows מדווח שהתקנת מערכת עדיין פעילה. אין להפעיל עוד אשף או לשנות את דגל ההתקנה ידנית.");
                bool retry = RestoreGuard.RequiresWait(state.Phase);
                if (retry)
                {
                    if (Ask("ניסיון קודם הופסק או הסתיים. האם בדקת שחלון Microsoft סגור ושהתקנת Windows אינה עדיין פעילה?\r\nהכלי יאסוף ראיות ויפתח שוב את האשף המקורי, בלי למחוק קובצי התקנה או לאבד את גיבוי ההגדרות.\r\nהאשף יחליט אם להמשיך או להוריד מחדש; אין הבטחה שימשיך מ־70%.") != DialogResult.Yes) return;
                    await Task.Run(() => Evidence.Collect(root, state, log.Write));
                }
                if (pending.RestartPending && Ask("Windows מסמן שאתחול ממתין: " + pending.Summary + "\r\nייתכן שצריך להשלים פעולה קודמת לפני ניסיון נוסף. אם אינך בטוח, בחר 'לא'.\r\nלהמשיך לפתיחת אשף Microsoft? הכלי לא יאתחל בעצמו.") != DialogResult.Yes) return;
                foreach (RegChange change in state.Changes)
                    if (!registry.Read(change.Hive, change.Path, change.Name).Same(change.After))
                    {
                        if (!change.After.Exists) { log.Write("CACHE REBUILT " + change.Label + "; left unchanged"); continue; }
                        throw new InvalidOperationException("הגדרת הכנה השתנתה מאז הגיבוי: " + change.Label + ". יש לשחזר ולהכין מחדש.");
                    }
                string assistant = Path.Combine(root, "Windows11InstallationAssistant.exe");
                using (FileStream hold = new FileStream(assistant, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    log.Write("PRELAUNCH SIGNATURE " + await Task.Run(() => SignatureVerifier.VerifyMicrosoft(assistant)));
                    if (SignatureVerifier.Hash(assistant) != state.AssistantHash) throw new InvalidDataException("Assistant changed after preparation; restore and prepare again");
                    if (Ask("להפעיל כעת את אשף Microsoft?\r\n\r\nהאשף עשוי להוריד ולהתקין מערכת ולהפעיל מחדש את המחשב בהמשך. לחיצה על 'כן' מאשרת גם את האתחולים האלה.\r\nשמור עבודה פתוחה וחבר לחשמל. אשר את תנאי Microsoft בחלון האשף.\r\nאם מוצעת אפשרות שאינה שומרת קבצים ויישומים, בטל את האשף.\r\n\r\nאין כאן מתג מאומת שמבטיח עצירה לפני אתחול.") != DialogResult.Yes) return;
                    RequireIdle();
                    string previousPhase = state.Phase; string previousLaunch = state.LaunchUtc;
                    state.Phase = "LaunchRequested"; state.LaunchUtc = DateTime.UtcNow.ToString("o"); JsonFile.Save(StatePath, state);
                    string arguments = AssistantDownload.LaunchArguments(state.SkipCompatCheck);
                    log.Write("USER CONSENT installation and assistant-managed restarts; visible Microsoft UI; arguments=" + arguments);
                    Process process;
                    try { process = Process.Start(new ProcessStartInfo(assistant, arguments) { UseShellExecute = false, WorkingDirectory = root }); }
                    catch { state.Phase = previousPhase; state.LaunchUtc = previousLaunch; JsonFile.Save(StatePath, state); throw; }
                    log.Write("ASSISTANT LAUNCHED PID=" + process.Id + " retry=" + retry); ObserveBootstrap(process);
                    state.Phase = "Launched"; JsonFile.Save(StatePath, state); lastSeen = DateTime.UtcNow; evidenceCaptured = false;
                }
                status.Text = "אשף Microsoft הופעל. עקוב אחר חלונו; יציאת תהליך אינה מוכיחה שהעדכון הושלם.";
            }
            catch (Exception ex) { Fail("הפעלת האשף", ex); }
            finally { busy = false; UpdateButtons(); }
        }
        private async Task Restore()
        {
            if (state == null) return; busy = true; UpdateButtons();
            try
            {
                RequireIdle();
                if (state.Phase == "Launched" || state.Phase == "LaunchRequested")
                {
                    int remaining = RestoreGuard.RemainingSeconds(state.Phase, lastSeen, DateTime.UtcNow);
                    if (remaining > 0)
                    {
                        status.Text = "המתנה רגילה: עוד " + remaining + " שניות ללא מתקינים לפני שחזור. השאר את החלון פתוח.";
                        log.Write("INFO RESTORE_WAIT remainingSeconds=" + remaining); return;
                    }
                    if (Ask("לא נצפה תהליך התקנה פעיל. האם בדקת שאשף Microsoft וההתקנה סיימו או בוטלו?\r\nאין לשחזר בזמן התקנה או בזמן המתנה להשלמתה.\r\nשחזור זה מחזיר הגדרות Registry בלבד; הוא אינו מבטל שדרוג מערכת.") != DialogResult.Yes) return;
                }
                else if (Ask("לשחזר כעת את הגדרות הזכאות ששונו? גיבוי ויומן יישמרו.\r\nערכים ששונו בינתיים על ידי גורם אחר לא יידרסו.") != DialogResult.Yes) return;
                RequireIdle(); // A separate installer may have started while the confirmation dialog was open.
                List<string> conflicts = await Task.Run(() => new RegistryTransaction(registry, log.Write).Restore(state, StatePath));
                if (conflicts.Count == 0) restartConsent.Checked = false;
                status.Text = conflicts.Count == 0 ? "ההגדרות שוחזרו והסימונים נפתחו להכנה חדשה. גיבוי ויומן נשמרו." : "שחזור חלקי: " + conflicts.Count + " התנגשויות/שגיאות. בדוק ביומן.";
                if (conflicts.Count > 0) MessageBox.Show(this, String.Join("\r\n", conflicts.Take(15)), "שחזור חלקי", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex) { Fail("שחזור", ex); }
            finally { busy = false; UpdateButtons(); }
        }
        private async Task Collect()
        {
            string export;
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "ZIP archive (*.zip)|*.zip", FileName = "Windows11-evidence-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return; export = dialog.FileName;
            }
            busy = true; UpdateButtons();
            try
            {
                string folder = await Task.Run(() => Evidence.Collect(root, state, log.Write));
                await Task.Run(() => Evidence.Export(folder, export)); log.Write("EVIDENCE EXPORT " + export);
                status.Text = "ראיות נשמרו ל־ZIP. הן עשויות להכיל נתיבים ושמות יישומים פרטיים; בדוק לפני שיתוף.";
            }
            catch (Exception ex) { Fail("איסוף ראיות", ex); }
            finally { busy = false; UpdateButtons(); }
        }
        private async Task CheckConnection()
        {
            busy = true; UpdateButtons(); status.Text = "בודק DNS וחיבור HTTPS לשרתי Microsoft, ללא שינוי הגדרות רשת…";
            try
            {
                string report = await ConnectionCheck.Run(); File.WriteAllText(Path.Combine(root, "connection-check.txt"), report, System.Text.Encoding.UTF8);
                log.Write("CONNECTION CHECK\r\n" + report); status.Text = "בדיקת החיבור הסתיימה. התוצאות ביומן ובקובץ connection-check.txt.";
                MessageBox.Show(this, report, "בדיקת חיבור", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Fail("בדיקת חיבור", ex); }
            finally { busy = false; UpdateButtons(); }
        }
        private async Task ImportAssistant()
        {
            string source;
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "Microsoft Installation Assistant (*.exe)|*.exe", Title = "בחר אשף Microsoft מקורי שהורד מהאתר הרשמי" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return; source = dialog.FileName;
            }
            busy = true; UpdateButtons();
            try
            {
                RequireIdle(); await AssistantCache.Import(source, root, log.Write); reuseAssistant.Checked = true;
                status.Text = "האשף המקומי הועתק וחתימת Microsoft אומתה. לחץ '1. הורדה והכנה'; להורדת Windows עדיין נדרש אינטרנט.";
            }
            catch (Exception ex) { Fail("אימות אשף מקומי", ex); }
            finally { busy = false; UpdateButtons(); }
        }
        private void ObserveBootstrap(Process process)
        {
            Task.Run(() =>
            {
                try
                {
                    process.WaitForExit(); int code = process.ExitCode;
                    PostProcessMessage("ASSISTANT BOOTSTRAP EXIT code=" + code + "; handoff possible; not proof of completion");
                }
                catch (Exception ex) { PostProcessMessage("ASSISTANT EXIT OBSERVATION ERROR " + ex.Message); }
                finally { process.Dispose(); }
            });
        }
        private void PostProcessMessage(string message)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) log.Write(message); })); }
            catch (InvalidOperationException) { } // Form was closed while the bootstrap process exited.
        }
        private void Monitor()
        {
            if (busy || preview) return;
            try
            {
                string processes = String.Join(", ", Machine.UpgradeProcesses());
                installerObserved = processes.Length > 0;
                if (processes.Length > 0) lastSeen = DateTime.UtcNow;
                UpdateButtons();
                if (processes != lastProcesses) { log.Write("PROCESS OBSERVATION " + (processes.Length == 0 ? "none; not proof of completion" : processes)); lastProcesses = processes; }
                if (state != null && RestoreGuard.RequiresWait(state.Phase))
                {
                    int remaining = RestoreGuard.RemainingSeconds(state.Phase, lastSeen, DateTime.UtcNow);
                    if (installerObserved) status.Text = "נצפה מתקין פעיל. אין לשחזר הגדרות בזמן התקנה.";
                    else if (remaining > 0) status.Text = "עוד " + remaining + " שניות ללא מתקינים לפני שחזור. השאר את החלון פתוח; שחזור אינו מבצע עדכון.";
                }
                if (state != null && (state.Phase == "Launched" || state.Phase == "LaunchRequested") && processes.Length == 0 && (DateTime.UtcNow - lastSeen).TotalSeconds >= 60 && !evidenceCaptured)
                {
                    evidenceCaptured = true; string folder = Evidence.Collect(root, state, log.Write);
                    status.Text = "לא נצפה מתקין במשך דקה. אם ההתקנה הסתיימה/בוטלה, אפשר לאשר ניסיון נוסף באשף בלי לאבד את הגיבוי.\r\nלשינוי סימונים: 'שחזור הגדרות'. ראיות: " + Path.GetFileName(folder);
                }
            }
            catch (Exception ex) { log.Write("MONITOR ERROR " + ex.Message); }
        }
        private DialogResult Ask(string text) { return MessageBox.Show(this, text, "מעדכן Windows 11 — אישור", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign); }
        private void Fail(string stage, Exception ex)
        {
            log.Write("ERROR stage=" + stage + " " + ex); status.Text = "נעצר בשלב: " + stage + ". " + NetworkFailure.Describe(ex);
            MessageBox.Show(this, status.Text, "הפעולה נעצרה", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }
        private void HandleClosing(object sender, FormClosingEventArgs e)
        {
            if (preview) return;
            if (busy) { e.Cancel = true; return; }
            if (state != null && state.Phase != "Restored" && Ask("לסגור את כלי ההכנה?\r\nהגדרות ההכנה והגיבוי יישמרו. אפשר לפתוח שוב ולשחזר.\r\nאם אשף Microsoft פעיל, סגירת כלי זה אינה עוצרת אותו או את האתחולים שאישרת.") != DialogResult.Yes) e.Cancel = true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); if (log != null) log.Added -= OnLog; } base.Dispose(disposing);
        }
    }
    public static class RestoreGuard
    {
        public static bool RequiresWait(string phase) { return phase == "Launched" || phase == "LaunchRequested"; }
        public static int RemainingSeconds(string phase, DateTime lastSeenUtc, DateTime nowUtc)
        {
            if (!RequiresWait(phase)) return 0;
            // A fresh observation starts a fresh minute, also when recovering a past launch.
            return (int)Math.Ceiling(Math.Max(0, Math.Min(60, 60 - (nowUtc - lastSeenUtc).TotalSeconds)));
        }
    }
    public static class SessionRules
    {
        public static bool CanLaunch(string phase, bool installerRunning, int waitSeconds)
        {
            return !installerRunning && (phase == "Applied" || RestoreGuard.RequiresWait(phase) && waitSeconds == 0);
        }
    }
}
