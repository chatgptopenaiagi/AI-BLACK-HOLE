using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AIBlackHole;

sealed class MainForm : Form
{
    readonly ApplicationService app; readonly Gateway gateway;
    readonly ListBox models = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
    readonly ComboBox profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, ForeColor = Color.Black, BackColor = Color.White };
    readonly Label state = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new(18) };
    readonly Label hardware = new() { Dock = DockStyle.Fill, Padding = new(18), AutoEllipsis = true };
    readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Maximum = 100 };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    bool closing; string modelSignature = "";
    public string? TestOutput { get; set; }
    public bool TestFailed { get; private set; }
    public MainForm(ApplicationService app, Gateway gateway)
    {
        this.app = app; this.gateway = gateway; Text = "AI BLACK HOLE · ABH 2.0.0-beta.2"; Width = 1200; Height = 850; MinimumSize = new(900, 680); StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; Font = new("Segoe UI", 10); BackColor = Color.FromArgb(12, 18, 28); ForeColor = Color.FromArgb(229, 237, 244);
        var menu = new MenuStrip { BackColor = Color.FromArgb(24, 32, 46), ForeColor = ForeColor, Padding = new(10, 8, 10, 8) };
        void Menu(string name, params (string label, Action action)[] items) { var group = new ToolStripMenuItem(name); foreach (var (label, action) in items) { var item = new ToolStripMenuItem(label); item.Click += (_, _) => Run(action); group.DropDownItems.Add(item); } menu.Items.Add(group); }
        Menu("&File", ("Open GGUF…", Add), ("Add folder…", Scan), ("Import model profile…", Import), ("Export model profile…", Export), ("Open portable folder", () => Open(app.Paths.Root)), ("Exit", Close));
        Menu("&Models", ("Discover existing models", FindExisting), ("Scan selected drive…", DriveScan), ("Find USB models", Usb), ("Hugging Face Explorer", () => Web("models")), ("Model registry", () => Web("models")), ("Model inspector", Inspect), ("Verify model", Verify), ("Remove from registry", Remove), ("Reconnect model…", Reconnect));
        Menu("&Engine", ("Discover engines", () => Async(async () => await app.Discover())), ("Select runtime…", Runtime), ("Start model", StartModel), ("Stop model", () => Async(async () => await app.Stop())), ("Restart model", () => Async(async () => { await app.Stop(); StartModel(); })), ("Runtime logs", () => Web("diagnostics")));
        Menu("&Calibration", ("Auto calibrate", Calibrate), ("Safe profile", () => profiles.SelectedItem = "SAFE"), ("Balanced profile", () => profiles.SelectedItem = "BALANCED"), ("Fastest tested profile", () => profiles.SelectedItem = "FASTEST TESTED"), ("Custom profile…", Custom), ("Benchmark results", () => Web("calibration")), ("Hardware limits", () => Web("hardware")));
        Menu("AI &Chat", ("Open browser chat", () => Web("chat")), ("New conversation", () => Web("chat&action=new")), ("Export conversation", () => Web("chat&action=export")));
        Menu("&Agents", ("Codex", () => Web("agents")), ("OpenAI / Claude / Grok", () => Web("agents")), ("DRAGONHYDRA / ARX", () => Web("agents")), ("MCP / generic local provider", () => Web("agents")));
        Menu("&Workflows", ("New workflow / compare responses", () => Web("workflows")), ("History", () => Open(app.Paths.Data("workflows"))));
        Menu("API &Connections", ("Connection center", () => Web("api")), ("Generate / rotate / revoke key", () => Web("api")), ("Test connection", () => Async(async () => { using var client = new HttpClient(); using var response = await client.GetAsync(gateway.ApiUrl + "/models"); ShowText("Authentication test", "Unauthenticated request: HTTP " + (int)response.StatusCode + " (expected 401).\r\n" + gateway.ApiUrl); })));
        Menu("&Tools", ("Hardware discovery", () => Web("hardware")), ("Logs / diagnostics", () => Web("diagnostics")), ("Backup registry…", Backup), ("Settings", () => Web("settings")));
        Menu("&Help", ("Getting started", Help), ("GGUF / USB / calibration / recovery", Help)); Menu("A&bout", ("AI BLACK HOLE", About), ("GitHub repository", () => Open("https://github.com/chatgptopenaiagi/AI-BLACK-HOLE")));
        MainMenuStrip = menu;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 8, ColumnCount = 1, Padding = new(20) };
        foreach (var size in new[] { 90, 66, 74, 56, 170, 68, 25 }) layout.RowStyles.Add(new(SizeType.Absolute, size)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        var title = new Label { Text = "AI BLACK HOLE\nOne universal wrench. Many AI models.", Font = new("Segoe UI", 24, FontStyle.Bold), Dock = DockStyle.Fill, ForeColor = Color.FromArgb(103, 230, 205) };
        layout.Controls.Add(title, 0, 0); layout.Controls.Add(hardware, 0, 1); layout.Controls.Add(state, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        Button Button(string text, Action click) { var b = new Button { Text = text, AutoSize = true, Height = 37, Padding = new(9, 4, 9, 4), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(34, 48, 65), ForeColor = ForeColor }; b.Click += (_, _) => Run(click); return b; }
        buttons.Controls.Add(Button("ADD MODEL", Add)); buttons.Controls.Add(Button("SCAN FOLDER", Scan)); buttons.Controls.Add(Button("SCAN USB", Usb)); buttons.Controls.Add(Button("HUGGING FACE", () => Web("models"))); buttons.Controls.Add(Button("REFRESH REGISTRY", () => { app.Models.Refresh(); RefreshModels(); })); layout.Controls.Add(buttons, 0, 3);
        models.BackColor = Color.FromArgb(20, 29, 42); models.ForeColor = ForeColor; models.SelectedIndexChanged += (_, _) => { if (models.SelectedItem is ModelRecord m) details.Text = Json.Text(new { identity = m.Info?.Name, architecture = m.Info?.Architecture, quantization = m.Info?.Quantization, m.Bytes, m.Sha256, m.State, m.Capabilities }); }; layout.Controls.Add(models, 0, 4);
        profiles.Items.AddRange(["SAFE", "BALANCED", "FASTEST TESTED"]); profiles.SelectedIndex = 0;
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(0, 10, 0, 0), AutoScroll = true }; controls.Controls.Add(profiles); controls.Controls.Add(Button("VERIFY FILE", Verify)); controls.Controls.Add(Button("AUTO CALIBRATE", Calibrate)); controls.Controls.Add(Button("START MODEL", StartModel)); controls.Controls.Add(Button("STOP MODEL", () => Async(async () => await app.Stop()))); controls.Controls.Add(Button("OPEN CHAT", () => Web("chat"))); controls.Controls.Add(Button("CANCEL JOB", Cancel)); layout.Controls.Add(controls, 0, 5);
        layout.Controls.Add(progress, 0, 6); details.BackColor = Color.FromArgb(16, 24, 35); details.ForeColor = Color.FromArgb(186, 203, 221); details.BorderStyle = BorderStyle.None; details.Font = new("Consolas", 9); layout.Controls.Add(details, 0, 7);
        Controls.Add(layout); Controls.Add(menu); menu.Dock = DockStyle.Top;
        timer.Tick += (_, _) => RefreshStatus(); Shown += async (_, _) => { timer.Start(); try { await Task.Run(app.Discover); RefreshStatus(); if (TestOutput != null) await TestControls(); } catch (Exception e) { if (TestOutput != null) { TestFailed = true; Json.Save(TestOutput, new { status = "FAILED", error = e.ToString() }); Close(); } else ShowError(e); } };
        FormClosing += async (_, e) => { if (closing) return; e.Cancel = true; timer.Stop(); closing = true; Enabled = false; await gateway.DisposeAsync(); await app.DisposeAsync(); Close(); };
    }
    ModelRecord Selected() => models.SelectedItem as ModelRecord ?? throw new ArgumentException("Select a registered model first.");
    void Run(Action action) { try { action(); } catch (Exception e) { ShowError(e); } }
    async void Async(Func<Task> action) { try { await action(); RefreshModels(); } catch (Exception e) { ShowError(e); } }
    void ShowError(Exception e) => MessageBox.Show(this, e.Message, "AI BLACK HOLE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    void Add() { using var dialog = new OpenFileDialog { Title = "Select a GGUF model — original stays in place", Filter = "GGUF model|*.gguf|All files (including Ollama blobs)|*.*", CheckFileExists = true }; if (dialog.ShowDialog(this) == DialogResult.OK) Async(async () => { await Task.Run(() => app.Models.Add(dialog.FileName, app.Settings.AllowNetworkShares)); RefreshModels(); }); }
    string? Folder(string description) { using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true }; return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null; }
    void Scan() { string? folder = Folder("Select a folder to scan (bounded; links are skipped)"); if (folder != null) app.Scan(folder); }
    void DriveScan() { string? folder = Folder("Select the drive or folder to scan (10,000 entries / depth 8 / two minutes)"); if (folder != null) app.Scan(folder); }
    void Usb() { var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Removable).ToArray(); if (drives.Length == 0) { ShowText("USB discovery", "No ready removable volume was detected. External SSDs reported as fixed disks can be selected with Scan selected drive."); return; } foreach (var drive in drives.Take(3)) app.Scan(drive.RootDirectory.FullName); }
    void FindExisting()
    {
        var roots = app.Settings.DiscoveryRoots ?? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models", "blobs"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models") };
        foreach (string root in roots.Where(Directory.Exists).Take(3)) app.Scan(root, true);
        if (!roots.Any(Directory.Exists)) ShowText("Existing model locations", "No configured model directory exists. Choose Add folder or register a file from its current location. Discovery scope is configurable in Settings.");
    }
    void Verify() => app.Verify(Selected().Id);
    void Calibrate() => app.Calibrate(Selected().Id);
    void StartModel() { var model = Selected(); app.Start(model.Id, Calibrator.Select(app, model, profiles.SelectedItem!.ToString()!)); }
    void Inspect() { var m = Selected(); Async(async () => { await Task.Run(() => app.Models.Add(m.Path, app.Settings.AllowNetworkShares)); details.Text = Json.Text(m); }); }
    void Remove() { var m = Selected(); if (MessageBox.Show(this, "Remove this registry entry? The model file stays in place.", "Remove from registry", MessageBoxButtons.OKCancel) == DialogResult.OK) { app.Models.Remove(m.Id); RefreshModels(); } }
    void Reconnect() { var m = Selected(); using var dialog = new OpenFileDialog { Title = "Select the same model at its current location", Filter = "All model files|*.*" }; if (dialog.ShowDialog(this) == DialogResult.OK) app.Submit("RECONNECT", async (_, ct) => await app.Models.Reconnect(m, dialog.FileName, app.Settings.AllowNetworkShares, ct)); }
    void Runtime() { using var dialog = new OpenFileDialog { Title = "Select an installed llama-server.exe", Filter = "llama.cpp server|llama-server.exe" }; if (dialog.ShowDialog(this) == DialogResult.OK) { app.SaveSettings(app.Settings with { RuntimeExecutables = (app.Settings.RuntimeExecutables ?? []).Append(dialog.FileName).Distinct().ToArray() }); Async(app.Discover); } }
    void Custom() { string? json = Prompt("Custom profile JSON", Json.Text(Profile.Safe)); if (json == null) return; var p = System.Text.Json.JsonSerializer.Deserialize<Profile>(json, Json.Options)!; EngineManager.Validate(p); app.Start(Selected().Id, p); }
    void Export() { var model = Selected(); using var dialog = new SaveFileDialog { FileName = "ABH-model-profile.json", Filter = "JSON|*.json" }; if (dialog.ShowDialog(this) == DialogResult.OK) Json.Save(dialog.FileName, new { schema = "abh-model-profile/1", model }); }
    void Import() { using var dialog = new OpenFileDialog { Filter = "JSON|*.json" }; if (dialog.ShowDialog(this) != DialogResult.OK) return; var profile = JsonNode.Parse(File.ReadAllText(dialog.FileName)); if (profile?["schema"]?.GetValue<string>() != "abh-model-profile/1") throw new ArgumentException("Unknown profile format."); string path = profile["model"]!["path"]!.GetValue<string>(); app.Models.Add(path, app.Settings.AllowNetworkShares); RefreshModels(); }
    void Backup() { using var dialog = new SaveFileDialog { FileName = "ABH-registry-backup.json", Filter = "JSON|*.json" }; if (dialog.ShowDialog(this) == DialogResult.OK) Json.Save(dialog.FileName, new RegistryDocument(1, app.Models.All().ToList())); }
    void Cancel() { foreach (var job in app.Jobs.Values.Where(j => j.Finished == null)) job.Cancel.Cancel(); }
    void Web(string section) => Open(gateway.ChatUrl + "&page=" + section);
    static void Open(string path) { if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(path)) Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    void About() => ShowText("About AI BLACK HOLE", $"AI BLACK HOLE · ABH\r\nVersion {Paths.Version}\r\nUniversal AI Model Engine and Agentic Communication Platform\r\n\r\nEngine adapters: llama.cpp CPU / CUDA, configured local endpoints\r\nCurrent model: {app.Engine.Model?.Info?.Name ?? "None"}\r\nDetected engines: {app.Engines.Count}\r\n\r\nOwner: chatgptopenaiagi\r\nhttps://github.com/chatgptopenaiagi/AI-BLACK-HOLE\r\n\r\nBuilt as an independent evolution of verified QWEN SATELLITE components. Compatibility is established per model, runtime and hardware; it is never inferred from a .gguf suffix alone.");
    void Help() => ShowText("AI BLACK HOLE · Offline help", HelpText);
    public const string HelpText = "GETTING STARTED\r\nAdd a GGUF from its current drive. Inspect metadata, verify its SHA-256 once, then Auto Calibrate or Start with SAFE. Open Chat for streaming local conversations.\r\n\r\nMODEL COMPATIBILITY\r\nA valid header is identification, not an inference test. Projectors are auxiliary files. Embeddings use /v1/embeddings. Split models are recognized but their loading is not verified in this beta.\r\n\r\nCALIBRATION\r\nABH measures a bounded set of CPU/GPU candidates. Fastest tested means fastest in that set. Available RAM/VRAM may change. Cancel stops experiments; Stop unloads the owned engine.\r\n\r\nUSB / EXTERNAL MODELS\r\nKeep the complete application folder together. Models can remain external. Removing a registry entry never deletes a file. Reconnect verifies a full fingerprint after drive-letter changes. Stop before disconnecting drives.\r\n\r\nHUGGING FACE\r\nPaste a repository URL, inspect files, select one quantization and a destination. Cancel pauses a download; selecting the same pinned file resumes its .part file. SHA-256 verification precedes registration.\r\n\r\nAPI KEYS\r\nOpen API Connections, generate a scoped key, copy it once, and store it privately. Rotate or revoke keys here. Listener is loopback only. Cloud providers use separate user-supplied keys in the passphrase vault. ChatGPT subscriptions are not API keys.\r\n\r\nCODEX / MCP\r\nThe Codex App Server bridge uses an isolated local provider profile. It leaves normal Codex configuration and authentication untouched. Namespace function calls are mapped; custom/hosted tools are rejected explicitly. General coding compatibility must be tested per model. MCP exposes registered IDs, never arbitrary filesystem commands.\r\n\r\nRECOVERY\r\nInspect Diagnostics for errors. If CUDA fails, SAFE CPU is available. Recalibrate after changing drivers, engine or model. Back up the registry before transferring settings. Portable data lives in data/; installed data lives in your local application-data folder. No global Python installation is needed.";
    void ShowText(string title, string text) { using var form = new Form { Text = title, Width = 800, Height = 600, StartPosition = FormStartPosition.CenterParent }; form.Controls.Add(new TextBox { Text = text, Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new("Segoe UI", 11) }); form.ShowDialog(this); }
    string? Prompt(string title, string value) { using var form = new Form { Text = title, Width = 650, Height = 480, StartPosition = FormStartPosition.CenterParent }; var box = new TextBox { Text = value, Multiline = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill }; var ok = new Button { Text = "Apply", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK, Height = 40 }; form.Controls.Add(box); form.Controls.Add(ok); return form.ShowDialog(this) == DialogResult.OK ? box.Text : null; }
    void RefreshModels()
    {
        var all = app.Models.All(); string signature = string.Join("|", all.Select(m => m.ToString())); if (signature == modelSignature) return; modelSignature = signature;
        string? id = (models.SelectedItem as ModelRecord)?.Id; models.Items.Clear(); models.Items.AddRange(all); models.SelectedItem = all.FirstOrDefault(m => m.Id == id) ?? all.FirstOrDefault();
    }
    void RefreshStatus()
    {
        RefreshModels(); var memory = Hardware.Memory(); var h = app.Hardware;
        hardware.Text = h == null ? "Discovering local hardware and installed engines…" : $"{h.Cpu}   ·   {h.Threads} threads   ·   RAM available {memory.free / 1073741824.0:F1} GiB\r\n{h.Gpu}   ·   driver {h.Driver}   ·   {app.Engines.Count} engine(s) detected";
        var job = app.Jobs.Values.OrderByDescending(j => j.Started).FirstOrDefault(); state.Text = app.Engine.Status + "\r\n" + (job == null ? "Select an external model to begin. " + gateway.ApiUrl : $"{job.Kind}: {job.Status} · {job.Message}"); progress.Value = Math.Clamp((int)(job?.Progress ?? 0), 0, 100); app.Engine.SampleMemory();
    }
    async Task TestControls()
    {
        var checks = new List<object>();
        void Check(string name, bool pass, object? detail = null) { checks.Add(new { name, status = pass ? "VERIFIED" : "FAILED", detail }); if (!pass) TestFailed = true; Json.Save(TestOutput!, new { status = "RUNNING", checks }); }
        IEnumerable<Control> Descendants(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
        void Click(string label) => Descendants(this).OfType<Button>().Single(b => b.Text == label).PerformClick();
        async Task Wait(Func<bool> ready, int seconds = 180) { var until = DateTime.UtcNow.AddSeconds(seconds); while (!ready()) { if (DateTime.UtcNow > until) throw new TimeoutException("Native UI operation timed out."); await Task.Delay(200); } }
        void Capture(string filename) { using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(app.Paths.Data(filename), System.Drawing.Imaging.ImageFormat.Png); }
        try
        {
            Check("Native menu groups", MainMenuStrip!.Items.Count == 11);
            foreach (var label in new[] { "Getting started", "AI BLACK HOLE" })
            {
                bool opened = false;
                using var closer = new System.Windows.Forms.Timer { Interval = 300 };
                closer.Tick += (_, _) => { foreach (Form dialog in Application.OpenForms.Cast<Form>().Where(f => f != this).ToArray()) { opened = dialog.Controls.OfType<TextBox>().Any(b => b.Text.Length > 100); dialog.Close(); } };
                closer.Start(); MainMenuStrip.Items.OfType<ToolStripMenuItem>().SelectMany(m => m.DropDownItems.OfType<ToolStripMenuItem>()).Single(m => m.Text == label).PerformClick(); closer.Stop(); Check("Native offline dialog: " + label, opened);
            }
            Click("REFRESH REGISTRY"); RefreshModels(); Check("Two-model native selector", models.Items.Count >= 2);
            var identities = app.Models.All().Select(m => new { m.Id, m.Path, m.Bytes, modified = File.GetLastWriteTimeUtc(m.Path), m.Sha256 }).ToArray();
            int previousPid = 0;
            foreach (var id in identities.Select(m => m.Id))
            {
                models.SelectedItem = app.Models.Get(id); profiles.SelectedItem = "FASTEST TESTED";
                if (app.Models.Get(id).Info!.Architecture == "llama")
                {
                    Click("AUTO CALIBRATE"); var job = app.Jobs.Values.OrderByDescending(j => j.Started).First(); await Wait(() => job.Finished != null, 300); Check("Native bounded calibration " + id, job.Status == "COMPLETED", job.Result);
                    await app.Stop();
                }
                Click("START MODEL"); var start = app.Jobs.Values.OrderByDescending(j => j.Started).First(); await Wait(() => start.Finished != null);
                Check("Native Start selected model " + id, start.Status == "COMPLETED" && app.Engine.Model?.Id == id && app.Engine.Ready, app.Engine.GetRuntimeStatus());
                if (!app.Engine.Ready) throw new IOException(start.Message);
                if (previousPid > 0) { bool alive; try { using var p = Process.GetProcessById(previousPid); alive = !p.HasExited; } catch { alive = false; } Check("Previous model process exited during switch", !alive); }
                previousPid = app.Engine.Pid;
                var response = await app.Generate("Write one short sentence about books.", 24, CancellationToken.None);
                Check("Genuine selected-model inference " + id, !string.IsNullOrWhiteSpace(response["choices"]?[0]?["message"]?["content"]?.GetValue<string>()), response);
                var browser = await BrowserTest.Run(app.Paths, gateway); Check("Browser chat " + id, System.Text.Json.JsonSerializer.SerializeToNode(browser)!["status"]?.GetValue<string>() == "PASS", browser);
                File.Copy(app.Paths.Data("browser-acceptance.png"), app.Paths.Data("browser-" + id + ".png"), true);
                RefreshStatus(); Capture("native-" + id + ".png");
            }
            Click("STOP MODEL"); await Wait(() => app.Engine.Pid == 0 && app.Engine.Status.StartsWith("Stopped"));
            var shutdown = JsonNode.Parse(File.ReadAllText(app.Paths.Data("last-stop.json"))); Check("Native Stop clean shutdown", shutdown?["graceful"]?.GetValue<bool>() == true, shutdown);
            foreach (var m in identities) Check("External file identity preserved " + m.Id, File.Exists(m.Path) && new FileInfo(m.Path).Length == m.Bytes && File.GetLastWriteTimeUtc(m.Path) == m.modified && app.Models.Get(m.Id).Sha256 == m.Sha256, new { m.Id, m.Bytes, m.Sha256 });
            Json.Save(TestOutput!, new { status = TestFailed ? "FAILED" : "VERIFIED", timestamp = DateTimeOffset.UtcNow, checks, scope = "Actual WinForms controls, two external registered models, real inference and Edge chat. No model moved or modified." });
        }
        catch (Exception e) { TestFailed = true; Json.Save(TestOutput!, new { status = "FAILED", error = e.ToString(), checks }); }
        finally { await app.Stop(); Close(); }
    }
}
