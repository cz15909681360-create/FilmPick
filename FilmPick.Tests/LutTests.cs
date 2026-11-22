using System.IO.Compression;
using FilmPick;

static class LutTests
{
    public static void Run(string area)
    {
        var source = new LutLibrary(Path.Combine(area, "first"));
        var path = Put("camera", "Look", "0 0 1");
        var equivalent = Put("copy", "Equivalent", "0.0  -0.0  1e0", "# formatting is not identity\nDOMAIN_MIN 0 0 0\n");
        var other = Put("style", "Look", "1 0 0");
        var invalid = Put("invalid", "Oops", "NaN 0 0");
        var result = source.Import([path, equivalent, other, invalid]);
        Check(result.Added == 2 && result.Duplicate == 1 && result.Errors.Length == 1, "batch reports");
        var first = source.Index.Entries[0];
        var second = source.Index.Entries[1];
        Check(second.Name == "Look (2)", "same name different content");
        source.AddTag("Camera");
        source.AddTag("Vivid");
        source.AddTag("Unused");
        source.Edit(first.Hash, "Night", ["Camera", "Vivid"]);
        source.Edit(second.Hash, "Day", ["Camera"]);
        source.Favorite(first.Hash);
        source.Select(first.Hash);
        var matches = LutLibrary.Filter(source.Index, "night", ["camera", "Vivid"], true).ToArray();
        Check(matches.Length == 1 && matches[0].Hash == first.Hash, "tags intersection and search");
        Check(!LutLibrary.Filter(source.Index, "day", ["Camera", "Vivid"], false).Any(), "not OR");
        source.ChangeTag("Vivid", "Saturated");
        source.ChangeTag("Unused", null);
        File.Move(path, path + ".moved");
        var restarted = new LutLibrary(Path.Combine(area, "first"));
        restarted.Load();
        Check(restarted.Index.Selected == first.Hash && restarted.Read(first.Hash).Size == 2, "original moved, selection restored");

        var backup = Path.Combine(area, "library.zip");
        restarted.Backup(backup);
        var empty = new LutLibrary(Path.Combine(area, "empty"));
        result = empty.Merge(backup);
        Check(result.Added == 2 && empty.Index.Selected == null && empty.Index.Entries[0].Favorite &&
              empty.Index.Entries[0].Tags.Contains("Saturated"), "backup into empty and no preview state");
        var restored = new LutLibrary(Path.Combine(area, "restored"));
        restored.Import([equivalent, Put("different", "Day", "0 1 0")]);
        restored.Edit(first.Hash, "Local name", []);
        result = restored.Merge(backup);
        Check(result.Added == 1 && result.Duplicate == 1, "merge existing");
        Check(restored.Index.Entries.First(item => item.Hash == first.Hash) is { Name: "Local name", Favorite: false } local &&
              local.Tags.Length == 0 && restored.Index.Entries.Any(item => item.Name == "Day (2)"), "keep metadata and number collision");
        var repeated = restored.Merge(backup);
        Check(repeated.Added == 0 && repeated.Duplicate == 2, "repeat import");
        var corrupt = Path.Combine(area, "corrupt.zip");
        File.Copy(backup, corrupt);
        using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update))
        {
            var selected = zip.GetEntry("luts/" + second.Hash + ".cube")!;
            var entryName = selected.FullName;
            selected.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write(File.ReadAllText(equivalent));
        }
        var previous = restored.Index;
        Reject(() => restored.Merge(corrupt), "bad checksum");
        Check(ReferenceEquals(previous, restored.Index), "failed merge leaves index");
        var corruptEmpty = new LutLibrary(Path.Combine(area, "corrupt-empty"));
        Reject(() => corruptEmpty.Merge(corrupt), "bad checksum into empty");
        Check(corruptEmpty.Index.Entries.Length == 0 && !File.Exists(corruptEmpty.FileFor(first.Hash)), "no partial import");
        Reject(() => restarted.Backup(backup), "backup cannot overwrite");

        var trialPath = Put("trial", "Try", "1 1 0", "LUT_3D_INPUT_RANGE 0 2\n");
        var trial = Cube.Read(trialPath);
        File.Move(trialPath, trialPath + ".moved");
        result = restarted.SaveTrial("Trial", trial);
        Check(result.Added == 1 && restarted.Read(result.First!).Maximum.X == 2, "save from trial after source moved");
        restarted.Remove(first.Hash);
        Check(restarted.Index.Selected == null && !File.Exists(restarted.FileFor(first.Hash)) &&
              File.Exists(path + ".moved"), "remove only managed copy");
        Check(restarted.Merge(backup).Added == 1, "reimport removed");
        Console.WriteLine("PASS: LUT import, content deduplication, tags, persistence, portable ZIP merge, checksum, no overwrite, removal and reimport.");

        string Put(string folder, string name, string values, string extra = "")
        {
            var target = Path.Combine(area, folder, name + ".cube");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "LUT_3D_SIZE 2\n" + extra + string.Concat(Enumerable.Repeat(values + "\n", 8)));
            return target;
        }
    }

    private static void Check(bool condition, string test)
    {
        if (!condition) throw new Exception("FAIL LUT: " + test);
    }

    private static void Reject(Action action, string test)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new Exception("FAIL LUT accepted: " + test);
    }
}
