using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AIBlackHole;

static class ArxIntegration
{
    public static async Task<object> Run(ApplicationService app, CancellationToken token)
    {
        string python = app.Settings.ArxExecutable ?? throw new ArgumentException("Select an existing Python executable for the optional ARX integration in Settings.");
        string root = app.Settings.ArxSourceDirectory ?? throw new ArgumentException("Select the verified ARX source directory in Settings.");
        if (!File.Exists(python) || !File.Exists(Path.Combine(root, "src", "arx", "cli.py")) || !File.Exists(Path.Combine(root, "pyproject.toml"))) throw new ArgumentException("Selected Python or ARX source is unavailable.");
        string report = app.Paths.Data("arx-scan-" + Guid.NewGuid().ToString("N") + ".json");
        var info = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = app.Paths.State };
        foreach (string argument in new[] { "-I", "-c", "import sys; sys.path.insert(0, sys.argv.pop(1)); from arx.cli import main; raise SystemExit(main())", Path.Combine(root, "src"), "-o", report, "deep" }) info.ArgumentList.Add(argument);
        // Python -I excludes global PYTHONPATH/user site; no installation or changes to the original ARX application.
        using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); } catch { if (!process.HasExited) process.Kill(true); throw; }
        await stdout; await stderr; if (process.ExitCode != 0 || !File.Exists(report)) throw new IOException("ARX deep scan failed (exit " + process.ExitCode + "). Check the selected optional environment; local inference remains available.");
        var evidence = Connectors.ImportArx(await File.ReadAllTextAsync(report, token)); Json.Save(app.Paths.Data("arx-import.json"), evidence);
        return new { status = "VERIFIED", operation = "Actual isolated ARX CLI deep scan", cliSha256 = await Json.FileHash(Path.Combine(root, "src", "arx", "cli.py")), evidence, limitation = "ARX machine evidence is not a GGUF inference or hardware-acceleration test." };
    }
}
