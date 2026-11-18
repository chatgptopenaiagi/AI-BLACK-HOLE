using System.Text.Json.Nodes;

namespace AIBlackHole;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--engine-host")) return Engine.Host().GetAwaiter().GetResult();
            if (args.Contains("--mcp")) return McpProtocol.Stdio().GetAwaiter().GetResult();
            string? Value(string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
            var paths = new Paths(Value("--state"));
            if (args.Length > 0 && !args.All(x => x is "--state" || x == Value("--state"))) return Command(args, paths, Value).GetAwaiter().GetResult();
            ApplicationConfiguration.Initialize(); var app = new ApplicationService(paths); var gateway = new Gateway(app); gateway.Start().GetAwaiter().GetResult();
            Application.Run(new MainForm(app, gateway)); return 0;
        }
        catch (Exception e)
        {
            if (args.Length > 0) { using var writer = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true }; writer.WriteLine(e.ToString()); }
            else MessageBox.Show("ABH could not start: " + e.Message + "\nIf this folder is read-only, launch with --state and a writable folder.", "AI BLACK HOLE", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    static async Task<int> Command(string[] args, Paths paths, Func<string,string?> value)
    {
        await using var app = new ApplicationService(paths); await using var gateway = new Gateway(app); string? output = value("--output"); object result;
        if (args.Contains("--inspect")) result = GgufInspector.Inspect(value("--inspect")!);
        else if (args.Contains("--register")) result = app.Models.Add(value("--register")!, app.Settings.AllowNetworkShares);
        else if (args.Contains("--verify")) result = new { sha256 = await app.Models.Verify(app.Models.Get(value("--verify")!), null, CancellationToken.None) };
        else if (args.Contains("--download-url"))
        {
            var files = await HuggingFace.List(value("--download-url")!, CancellationToken.None); if (files.Length != 1) throw new ArgumentException("Select an exact single GGUF resolve URL.");
            var job = new Job { Kind = "DOWNLOAD" }; result = await HuggingFace.Download(app, files[0], value("--destination")!, job, job.Cancel.Token);
        }
        else if (args.Contains("--discover")) { await app.Discover(); result = app.Status(); }
        else if (args.Contains("--self-test")) return await Acceptance.SelfTest(app, gateway, output);
        else if (args.Contains("--acceptance")) return await Acceptance.Models(app, gateway, args, value, output);
        else if (args.Contains("--serve"))
        {
            await app.Discover(); await gateway.Start();
            if (value("--model") is { } id) await app.Engine.Start(app.Models.Get(id), app.Engines.First(), Calibrator.Select(app, app.Models.Get(id), value("--profile") ?? "SAFE"));
            string ready = value("--ready") ?? paths.Data("ready.json"); Json.Save(ready, new { pid = Environment.ProcessId, url = gateway.Url, chatUrl = gateway.ChatUrl, runtime = app.Engine.GetRuntimeStatus() });
            string stop = value("--stop-file") ?? paths.Data("stop-request"); var until = DateTime.UtcNow.AddHours(8);
            while (!File.Exists(stop) && DateTime.UtcNow < until) await Task.Delay(500); return 0;
        }
        else throw new ArgumentException("Commands: --inspect FILE, --register FILE, --verify ID, --download-url URL --destination DIR, --discover, --self-test, --acceptance, --serve. Optional --state DIR --output JSON.");
        if (output != null) Json.Save(output, result); else { using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }; await writer.WriteLineAsync(Json.Text(result)); } return 0;
    }
}
