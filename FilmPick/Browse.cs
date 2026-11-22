using System.IO;

namespace FilmPick;

enum SortField { Name, Date, Rating, Mark }
enum DateFilter { All, Range, Unknown }
record BrowseOrder(SortField Field = SortField.Name, bool Descending = false);
record BrowsePreferences(BrowseOrder Photo, BrowseOrder Video, bool ShowCapture)
{
    public BrowsePreferences() : this(new(), new(), true) { }
}

record BrowseFilter(string Name = "", DateFilter Date = DateFilter.All, DateOnly? From = null, DateOnly? To = null,
    int Stars = 0, int Marks = 0)
{
    public bool Includes(string name, string? companion, CaptureDate? capture, int? stars, Mark mark, bool pending)
    {
        if (!name.Contains(Name, StringComparison.OrdinalIgnoreCase) &&
            companion?.Contains(Name, StringComparison.OrdinalIgnoreCase) != true) return false;
        if (Marks != 0 && (Marks & (1 << (int)mark)) == 0) return false;
        if (Stars != 0 && (stars is not int rating || (Stars & (1 << rating)) == 0)) return false;
        return Date switch
        {
            DateFilter.Unknown => !pending && capture == null,
            DateFilter.Range => capture is { } value && (From == null || value.Day >= From) && (To == null || value.Day <= To),
            _ => true
        };
    }
}

record BrowseItem(string Relative, string? Companion, Mark Mark, int? Rating, CaptureDate? Date, bool Pending)
{
    public string Name { get; } = Path.GetFileName(Relative);
}

static class Browse
{
    public static int Compare(BrowseItem left, BrowseItem right, BrowseOrder order)
    {
        if (order.Field == SortField.Date && (left.Date == null || right.Date == null))
        {
            var dateState = DateRank(left).CompareTo(DateRank(right));
            return dateState != 0 ? dateState : CompareNames(left, right);
        }
        if (order.Field == SortField.Rating && (left.Rating == null || right.Rating == null))
        {
            var ratingState = (left.Rating == null).CompareTo(right.Rating == null);
            return ratingState != 0 ? ratingState : CompareNames(left, right);
        }
        var result = order.Field switch
        {
            SortField.Date => Nullable.Compare(left.Date?.Clock, right.Date?.Clock),
            SortField.Rating => Nullable.Compare(left.Rating, right.Rating),
            SortField.Mark => MarkRank(left.Mark).CompareTo(MarkRank(right.Mark)),
            _ => NativeFiles.NaturalCompare(left.Name, right.Name)
        };
        return result != 0 ? (order.Descending ? -Math.Sign(result) : result) : CompareNames(left, right);
    }

    public static string? Next(string? current, IReadOnlyList<string> before, HashSet<string> visible, bool advance)
    {
        if (current == null) return null;
        var kept = visible.Contains(current);
        if (kept && !advance) return current;
        var at = -1;
        for (var i = 0; i < before.Count; i++)
            if (Paths.Comparer.Equals(before[i], current)) { at = i; break; }
        if (at < 0) return kept ? current : null;
        for (var i = at + 1; i < before.Count; i++)
            if (visible.Contains(before[i])) return before[i];
        if (kept) return current;
        for (var i = at - 1; i >= 0; i--)
            if (visible.Contains(before[i])) return before[i];
        return null;
    }

    private static int DateRank(BrowseItem item) => item.Date != null ? 0 : item.Pending ? 1 : 2;
    private static int MarkRank(Mark mark) => mark switch { Mark.Reject => 0, Mark.Pick => 2, _ => 1 };

    private static int CompareNames(BrowseItem left, BrowseItem right)
    {
        var name = NativeFiles.NaturalCompare(left.Name, right.Name);
        return name != 0 ? name : Paths.Comparer.Compare(left.Relative, right.Relative);
    }
}
