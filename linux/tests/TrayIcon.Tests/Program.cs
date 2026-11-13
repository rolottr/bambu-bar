using BambuBar.Core.Model;
using BambuBar.Linux;

var cases = new (string Name, PrinterStatus? Status, bool Online, bool Configured, string Expected)[]
{
    ("No printer", null, false, false, "idle"),
    ("Offline ignores stale telemetry", new() { State = JobState.Printing, Progress = 80 }, false, true, "offline"),
    ("Canceled is idle", new() { State = JobState.Failed, Canceled = true }, true, true, "idle"),
    ("Failed", new() { State = JobState.Failed }, true, true, "failed"),
    ("Pause", new() { State = JobState.Paused }, true, true, "paused"),
    ("Finished fills the chamber", new() { State = JobState.Finished, Progress = 20 }, true, true, "finished"),
    ("Preparing uses progress", new() { State = JobState.Preparing, Progress = 48 }, true, true, "printing-10"),
    ("Lower step", new() { State = JobState.Printing, Progress = 47 }, true, true, "printing-9"),
    ("Bad low telemetry", new() { State = JobState.Printing, Progress = -10 }, true, true, "printing-0"),
    ("Bad high telemetry", new() { State = JobState.Printing, Progress = 120 }, true, true, "printing-20"),
};
var count = 0;
void Check(string name, string actual, string expected)
{
    if (actual != expected) throw new Exception($"{name}: expected {expected}, got {actual}");
    if (args.Length > 0 && !File.Exists(Path.Combine(args[0], TrayIcon.Name(actual) + ".svg"))) throw new Exception($"Missing asset: {actual}");
    count++;
}
foreach (var c in cases) Check(c.Name, TrayIcon.Key(c.Status, c.Online, c.Configured), c.Expected);
foreach (var severity in Enum.GetValues<IssueSeverity>())
{
    var status = new PrinterStatus { Issues = [new() { Id = "test", Severity = severity, Message = "test" }] };
    Check($"Issue {severity}", TrayIcon.Key(status, true, true), severity <= IssueSeverity.Serious ? "idle-alert" : "idle");
    status.State = JobState.Failed;
    Check($"Failure embeds mark ({severity})", TrayIcon.Key(status, true, true), "failed");
}
foreach (var state in Enum.GetValues<JobState>())
foreach (var alert in new[] { false, true })
foreach (var progress in Enumerable.Range(0, 101))
{
    var status = new PrinterStatus { State = state, Progress = progress };
    if (alert) status.Issues.Add(new() { Id = "test", Severity = IssueSeverity.Fatal, Message = "test" });
    var key = TrayIcon.Key(status, true, true);
    Check("All state/progress assets exist", key, key);
}
Console.WriteLine($"{count} tray look and packaged-asset checks passed.");
