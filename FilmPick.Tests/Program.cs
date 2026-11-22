using System.Diagnostics;
using System.Security.Cryptography;
using FilmPick;

if (args is ["--interrupt-copy", var interruptedRoot, var interruptedState, var phase])
{
    var catalog = Catalog.Scan(interruptedRoot, true);
    var service = new Archive(interruptedRoot, new Store(interruptedRoot, interruptedState));
    service.Checkpoint = point => { if (point == phase) Environment.Exit(83); };
    service.Run(service.Plan(catalog, MarkIt(catalog, ("Clip.mov", Mark.Pick))));
    return 3;
}
if (args.Length != 1) throw new ArgumentException("Pass a fixture parent path.");
var area = Path.Combine(Path.GetFullPath(args[0]), "copy-test-" + Guid.NewGuid().ToString("N"));
var root = Path.Combine(area, "素材");
var state = Path.Combine(area, "state");
Put(root, "A.NEF", "raw");
File.WriteAllText(Path.Combine(root, "A.NEF") + ":FilmPickTest", "alternate data");
Put(root, "A.JPG", "photo");
Put(root, "A.NEF.xmp", "raw metadata");
Put(root, "A.XML", "shared metadata");
Put(root, "sub/B.MOV", "reject video");
Put(root, "sub/B.LRF", "proxy");
Put(root, "notes/info.camera", "manual cross-folder");
Put(root, "C.NEF", "ambiguous raw");
Put(root, "C.DNG", "ambiguous raw");
Put(root, "C.JPEG", "ambiguous jpeg");
Put(root, "C.XMP", "ambiguous sidecar");
Put(root, "unmarked.PNG", "keep here");
Paths.Root(root);
var store = new Store(root, state);
var archive = new Archive(root, store);
var data = Catalog.Scan(root, true);
Check(data.Ordered.Count == 6 && data.GroupOf("A.NEF") == "A.JPG" && data.GroupOf("C.NEF") == "C.NEF", "RAW grouping");
Check(data.Automatic["A.NEF.xmp"] == "A.JPG" && data.Automatic["A.XML"] == "A.JPG" &&
      !data.Automatic.ContainsKey("C.XMP") && Catalog.Scan(root, false).Ordered.Count == 5, "sidecars and recursion");
var session = MarkIt(data, ("A.JPG", Mark.Pick), (@"sub\B.MOV", Mark.Reject));
session.PhotoPosition = "A.JPG";
session.Links[@"notes\info.camera"] = new("A.JPG", data.Files["A.JPG"].Stamp, data.Files[@"notes\info.camera"].Stamp);
store.Write("session", session);
session = store.Read<Session>("session");
data.Reconcile(session);
Check(session.Marks.Count == 2 && session.Links.Count == 1, "old JPEG marking");
var groups = archive.Plan(data, session);
var sources = data.Files.Values.ToDictionary(file => Paths.Below(root, file.Relative),
    file => (file.Stamp, Hash(Paths.Below(root, file.Relative))));
Put(Paths.TargetRoot(root, Mark.Pick), "A.JPG", "photo");
var done = archive.Run(groups);
Check(done.Completed == 2 && done.Issues.Count == 0, "copy and fill missing: " + string.Join("; ", done.Issues));
Check(session.Marks.Count == 2, "marks remain");
foreach (var group in groups)
foreach (var member in group.Members)
{
    var source = Paths.Below(root, member.Relative);
    var target = archive.Destination(group.Mark, member.Relative);
    Check(Hash(target) == sources[source].Item2 && NativeFiles.Inspect(target)?.IdLow != member.SourceStamp.IdLow, "independent copy and bytes");
}
Check(archive.Run(groups).Unchanged == 2, "idempotent retry");
Check(File.ReadAllText(archive.Destination(Mark.Pick, "A.NEF") + ":FilmPickTest") == "alternate data", "named stream intact");
Check(!File.Exists(archive.Destination(Mark.Pick, "unmarked.PNG")), "unmarked left alone");
var reject = groups.Single(group => group.Mark == Mark.Reject);
var rejectedVideo = archive.Destination(reject.Mark, reject.Main);
var rejectedProxy = archive.Destination(reject.Mark, @"sub\B.LRF");
File.Move(rejectedVideo, rejectedVideo + ".old");
File.AppendAllText(rejectedProxy, "foreign");
done = archive.Run([reject]);
Check(done.Conflicts == 1 && !File.Exists(rejectedVideo) && File.ReadAllText(rejectedProxy) == "proxyforeign", "conflict skips group");
using (File.Open(Path.Combine(root, "sub", "B.LRF"), FileMode.Open, FileAccess.Read, FileShare.None))
    Check(archive.Run([reject]).Issues.Count == 1 && !File.Exists(rejectedVideo), "locked source");
foreach (var (path, (stamp, digest)) in sources)
    Check(NativeFiles.Inspect(path) == stamp && Hash(path) == digest, "source identity, last write, bytes intact");

foreach (var checkpoint in new[] { "stage", "verified", "publish" })
{
    var crashRoot = Path.Combine(area, "强制退出-" + checkpoint);
    Put(crashRoot, "Clip.mov", "crash video");
    Put(crashRoot, "Clip.SRT", "crash subtitle");
    var crashStore = new Store(crashRoot, state);
    var crash = new Archive(crashRoot, crashStore);
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
    foreach (var value in new[] { "--interrupt-copy", crashRoot, state, checkpoint }) start.ArgumentList.Add(value);
    using (var child = Process.Start(start)!)
    {
        child.WaitForExit();
        Check(child.ExitCode == 83, "crash injection " + checkpoint);
    }
    Check(crash.ReadJournal().Pending != null, "durable journal");
    if (checkpoint == "stage")
    {
        Check(crash.Recover().RecoveryNeeded, "unverified staged file retained");
        var parent = Paths.TargetRoot(crashRoot, Mark.Pick);
        var pending = Directory.EnumerateFiles(Directory.GetDirectories(parent, ".filmpick-copy-*").Single()).Single();
        File.Move(pending, Path.Combine(parent, "retained.partial"));
    }
    Check(!crash.Recover().RecoveryNeeded && !crash.Recover().RecoveryNeeded, "recover twice");
    Check(File.ReadAllText(Path.Combine(crashRoot, "Clip.mov")) == "crash video" &&
          !File.Exists(crash.Destination(Mark.Pick, "Clip.mov")), "recover only outputs");
}
store.Write("journal", new { Pending = new { Group = new { Members = new[] {
    new { Source = Path.Combine(root, "A.JPG"), Target = archive.Destination(Mark.Pick, "A.JPG") } } } } });
Check(archive.LegacyWarning().Length == 1 && archive.Run(groups).Completed == 0 && store.Read<LegacyJournal>("journal").Pending != null,
    "old move fenced and retained");
Console.WriteLine("PASS: RAW grouping, copy and sidecars, no overwrite, retries, source integrity, interruption, legacy journals.");
LutTests.Run(area);
BrowseTests.Run(area, data);
var thumbnail = new PixelSize(570, 375);
Check(!new PreviewBox(450, 450).NeedsRaw(thumbnail) && new PreviewBox(1000, 650).NeedsRaw(thumbnail), "dynamic RAW threshold");
Check(!new PreviewBox(1600, 280).NeedsRaw(thumbnail) && new PreviewBox(1600, 600).NeedsRaw(thumbnail), "landscape letterboxing");
Check(!new PreviewBox(300, 1000).NeedsRaw(new(375, 570)) && new PreviewBox(600, 1000).NeedsRaw(new(375, 570)), "portrait letterboxing");
Check(PreviewBox.FromLayout(480, 310, 1.5, 1.5).NeedsRaw(thumbnail), "DPI scaling");
Check(new PreviewBox(8192, 8192).Fit(new(6000, 4000)) == new PixelSize(4096, 2731), "4096 px limit");
Check(PreviewBox.Neighbors(12, 24, -1).SequenceEqual(Enumerable.Range(2, 10).Reverse()) &&
      PreviewBox.Neighbors(22, 24, 1).SequenceEqual([23]), "direction and maximum lookahead");
Console.WriteLine("PASS: RAW preview size, letterboxing, DPI, cap and lookahead.");
return 0;

static Session MarkIt(Catalog data, params (string Path, Mark Mark)[] choices) =>
    new() { Recursive = true, Marks = choices.ToDictionary(item => item.Path, item => new SavedMark(item.Mark, data.Files[item.Path].Stamp), Paths.Comparer) };
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
static void Put(string directory, string file, string contents)
{
    var target = Path.Combine(directory, file);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllText(target, contents);
}
