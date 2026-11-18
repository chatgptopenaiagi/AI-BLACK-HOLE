using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AIBlackHole;

static class RecoveryAcceptance
{
    public static async Task<object> Run(ApplicationService app, string file)
    {
        var checks = new List<object>(); bool failed = false;
        void Check(string name, bool pass, object? detail = null) { checks.Add(new { name, status = pass ? "VERIFIED" : "FAILED", detail }); failed |= !pass; }
        await app.Discover(); var model = app.Models.Add(file); await app.Models.Verify(model, null, CancellationToken.None);
        if (model.Bytes > 512 * 1024 * 1024) throw new ArgumentException("Recovery acceptance is bounded to a test model below 512 MiB.");
        var report = await Calibrator.Run(app, model, new Job(), CancellationToken.None, [Profile.Safe, Profile.Safe with { Name = "deliberately invalid candidate", GpuLayers = -1 }]);
        Check("Invalid candidate rejected; verified profile restored", report.Candidates[0].Status == "VERIFIED" && report.Candidates[1].Status == "FAILED" && app.Engine.Ready && app.Engine.Active == Profile.Safe, report);
        var original = Json.Read<CalibrationReport>(Calibrator.ReportPath(app, model)); Json.Save(Calibrator.ReportPath(app, model), original with { HardwareFingerprint = "changed-hardware-fixture" });
        bool rejected = false; try { Calibrator.Select(app, model, "BALANCED"); } catch (ArgumentException) { rejected = true; } finally { Json.Save(Calibrator.ReportPath(app, model), original); }
        Check("Material hardware change invalidates measured profiles", rejected);
        int pid = app.Engine.Pid;
        using (var owned = Process.GetProcessById(pid)) { owned.Kill(); await owned.WaitForExitAsync(); }
        app.Engine.SampleMemory(); Check("Owned engine crash detected without exiting ABH", !app.Engine.Ready && Environment.ProcessId != pid);
        await app.Engine.Restart(); var response = await app.Generate("Say hello.", 16, CancellationToken.None);
        Check("Owned engine restarts after crash and returns inference", app.Engine.Ready && response["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Length > 0);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(750)); bool cancelled = false;
        try { await Calibrator.Run(app, model, new Job(), cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check("Calibration cancellation releases owned process and retains measurements", cancelled && app.Engine.Pid == 0 && File.Exists(app.Paths.Data("calibration/" + model.Id + ".attempt.json")));
        await app.Engine.Start(model, app.Engines.First(), Profile.Safe); await app.Engine.Stop(); var stop = JsonNode.Parse(File.ReadAllText(app.Paths.Data("last-stop.json")));
        Check("Recovery ends with clean engine shutdown", stop?["graceful"]?.GetValue<bool>() == true, stop);
        return new { status = failed ? "FAILED" : "VERIFIED", timestamp = DateTimeOffset.UtcNow, modelSha256 = model.Sha256, checks, scope = "Real small local model; deliberate invalid configuration, owned-process termination, cancellation and recovery. No original model bytes modified." };
    }
}
