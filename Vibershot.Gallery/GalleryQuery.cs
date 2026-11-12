namespace Vibershot.Gallery;

public enum GalleryFilter { All, Images, Videos, Projects, Favourites, Tag }

public enum GallerySort { Date, Name, Size }

/// <summary>Pure search, filter, sort and grouping over gallery items. No UI, no IO.</summary>
public static class GalleryQuery
{
    public static List<GalleryItem> Apply(IEnumerable<GalleryItem> items, GalleryFilter filter, string? tag, string? search, GallerySort sort, bool descending = true)
    {
        IEnumerable<GalleryItem> query = items.Where(item => Passes(item, filter, tag));
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(item => Matches(item, search));
        IOrderedEnumerable<GalleryItem> ordered = sort switch
        {
            GallerySort.Name => descending ? query.OrderByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase) : query.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            GallerySort.Size => descending ? query.OrderByDescending(i => i.Size) : query.OrderBy(i => i.Size),
            _ => descending ? query.OrderByDescending(i => i.Created) : query.OrderBy(i => i.Created),
        };
        return ordered.ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool Passes(GalleryItem item, GalleryFilter filter, string? tag) => filter switch
    {
        GalleryFilter.Images => item.Kind == GalleryItemKind.Image,
        GalleryFilter.Videos => item.Kind == GalleryItemKind.Video,
        GalleryFilter.Projects => item.Kind == GalleryItemKind.Project,
        GalleryFilter.Favourites => item.Favourite,
        GalleryFilter.Tag => tag is not null && item.Sidecar.HasTag(tag),
        _ => true,
    };

    /// <summary>Every whitespace-separated word must appear in the file name, source app, window title or a tag.</summary>
    public static bool Matches(GalleryItem item, string search)
    {
        foreach (var word in search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool hit = Contains(item.Name, word) || Contains(item.SourceProcess, word) || Contains(item.SourceTitle, word)
                || item.Tags.Any(t => Contains(t, word));
            if (!hit) return false;
        }
        return true;
    }

    private static bool Contains(string? text, string word) => text is not null && text.Contains(word, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Uppercase section header for an item under the given sort.</summary>
    public static string GroupLabel(GalleryItem item, GallerySort sort, DateTime today)
    {
        switch (sort)
        {
            case GallerySort.Name:
                char first = item.Name.Length > 0 ? char.ToUpperInvariant(item.Name[0]) : '#';
                return char.IsLetter(first) ? first.ToString() : "#";
            case GallerySort.Size:
                return item.Size switch
                {
                    >= 100L << 20 => "OVER 100 MB",
                    >= 10L << 20 => "10 TO 100 MB",
                    >= 1L << 20 => "1 TO 10 MB",
                    >= 100L << 10 => "100 KB TO 1 MB",
                    _ => "UNDER 100 KB",
                };
            default:
                var day = item.Created.Date;
                if (day == today.Date) return "TODAY";
                if (day == today.Date.AddDays(-1)) return "YESTERDAY";
                if (day > today.Date.AddDays(-7)) return day.ToString("dddd d MMMM").ToUpperInvariant();
                return day.ToString(day.Year == today.Year ? "d MMMM" : "d MMMM yyyy").ToUpperInvariant();
        }
    }

    /// <summary>Distinct tags with counts, most used first then alphabetical.</summary>
    public static List<(string Tag, int Count)> Tags(IEnumerable<GalleryItem> items)
    {
        var counts = new Dictionary<string, (string Tag, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            foreach (var tag in item.Tags)
                counts[tag] = counts.TryGetValue(tag, out var c) ? (c.Tag, c.Count + 1) : (tag, 1);
        return counts.Values.OrderByDescending(c => c.Count).ThenBy(c => c.Tag, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
