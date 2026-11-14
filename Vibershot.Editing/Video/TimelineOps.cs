using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using Vibershot.Media;

namespace Vibershot.Editing.Video;

/// <summary>
/// Pure edits on a <see cref="VideoProject"/>. Every method returns a new project (or null when
/// the edit is not possible) and leaves its argument untouched, so the result can go straight
/// onto the undo history.
/// </summary>
public static class TimelineOps
{
    /// <summary>An empty project with one video and one audio track.</summary>
    public static VideoProject Empty(int width = 1920, int height = 1080, string frameRate = "30") => new()
    {
        Width = width,
        Height = height,
        FrameRate = frameRate,
        Tracks = ImmutableList.Create(
            new Track { Name = "V1", Kind = TrackKind.Visual },
            new Track { Name = "A1", Kind = TrackKind.Audio }),
    };

    /// <summary>A project sized and timed like its first clip (capped at 3840 on the long side), with that clip placed at the start.</summary>
    public static VideoProject ForSource(MediaSource source)
    {
        if (source.IsImage) return Append(Empty(), source);
        int width = source.Width > 0 ? source.Width : 1920, height = source.Height > 0 ? source.Height : 1080;
        double scale = Math.Min(1, (double)VideoProject.MaxDimension / Math.Max(width, height));
        var project = Empty(FrameReader.Even((int)Math.Round(width * scale)), FrameReader.Even((int)Math.Round(height * scale)), source.FrameRate ?? "30");
        return Append(project, source);
    }

    public static VideoProject AddSource(VideoProject project, MediaSource source) =>
        project.Sources.Any(existing => existing.Id == source.Id) ? project : project with { Sources = project.Sources.Add(source) };

    public static (Track Track, Clip Clip)? Find(VideoProject project, Guid clipId)
    {
        foreach (var track in project.Tracks)
        {
            var clip = track.Clips.FirstOrDefault(candidate => candidate.Id == clipId);
            if (clip != null) return (track, clip);
        }
        return null;
    }

    /// <summary>The clip ids plus everything linked to them.</summary>
    public static HashSet<Guid> WithLinked(VideoProject project, IEnumerable<Guid> clipIds)
    {
        var ids = new HashSet<Guid>(clipIds);
        var links = project.AllClips.Where(clip => ids.Contains(clip.Id) && clip.LinkId != null).Select(clip => clip.LinkId).ToHashSet();
        foreach (var clip in project.AllClips)
            if (clip.LinkId != null && links.Contains(clip.LinkId)) ids.Add(clip.Id);
        return ids;
    }

    public static bool IsFree(Track track, long start, long duration, ISet<Guid>? ignore = null) =>
        start >= 0 && duration > 0 && !track.Clips.Any(clip => (ignore == null || !ignore.Contains(clip.Id)) && clip.Start < start + duration && start < clip.End);

    public static long EndOf(Track track) => track.Clips.Count == 0 ? 0 : track.Clips.Max(clip => clip.End);

    private static Track WithClips(Track track, IEnumerable<Clip> clips) => track with { Clips = clips.OrderBy(clip => clip.Start).ToImmutableList() };

    public static VideoProject SetTrack(VideoProject project, Track track)
    {
        int index = project.Tracks.FindIndex(existing => existing.Id == track.Id);
        return index < 0 ? project : project with { Tracks = project.Tracks.SetItem(index, track) };
    }

    /// <summary>Adds a track: a visual one goes on top of the other visual tracks, an audio one at the bottom.</summary>
    public static VideoProject AddTrack(VideoProject project, TrackKind kind, out Guid trackId)
    {
        int count = project.Tracks.Count(track => track.Kind == kind);
        var added = new Track { Name = (kind == TrackKind.Visual ? "V" : kind == TrackKind.Caption ? "C" : "A") + (count + 1).ToString(CultureInfo.InvariantCulture), Kind = kind };
        trackId = added.Id;
        int lastOfKind = project.Tracks.FindLastIndex(track => track.Kind == kind);
        // Stored order is visual layers, then caption tracks, then audio lanes.
        int firstAudio = project.Tracks.FindIndex(track => track.Kind == TrackKind.Audio);
        int index = lastOfKind >= 0 ? lastOfKind + 1 : kind == TrackKind.Visual ? 0 : kind == TrackKind.Caption && firstAudio >= 0 ? firstAudio : project.Tracks.Count;
        return project with { Tracks = project.Tracks.Insert(index, added) };
    }

    /// <summary>Removes a track and its clips. The last video and the last audio track stay; caption tracks can all go.</summary>
    public static VideoProject RemoveTrack(VideoProject project, Guid trackId)
    {
        var track = project.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        if (track == null || (track.Kind != TrackKind.Caption && project.Tracks.Count(candidate => candidate.Kind == track.Kind) <= 1)) return project;
        return project with { Tracks = project.Tracks.Remove(track) };
    }

    /// <summary>Whole frames of the source left after <paramref name="sourceIn"/> seconds.</summary>
    private static long SourceFrames(VideoProject project, MediaSource source, double sourceIn) =>
        source.IsImage ? long.MaxValue / 4 : Math.Max(1, (long)Math.Floor((source.Duration - sourceIn) * project.Rate.Value + 1e-6));

    /// <summary>How long a still picture or a graphic stays on screen when it is first placed.</summary>
    public const double ImageSeconds = 5;

    /// <summary>How long a new caption lasts.</summary>
    public const double CaptionSeconds = 3;

    /// <summary>True for clips with no source length: stills, graphics and captions can be any length.</summary>
    public static bool Unbounded(Clip clip) => clip is ImageClip or GraphicClip or CaptionClip;

    /// <summary>Places a source at the end of the bottom video track.</summary>
    public static VideoProject Append(VideoProject project, MediaSource source)
    {
        var kind = source.HasPicture ? TrackKind.Visual : TrackKind.Audio;
        var first = project.Tracks.FirstOrDefault(track => track.Kind == kind);
        return Insert(project, source, first?.Id, first == null ? 0 : EndOf(first), out _);
    }

    /// <summary>
    /// Places a source at <paramref name="start"/>: its picture on the given visual track (or the
    /// lowest free one, or a new one on top) and its sound, linked, on the first free audio track.
    /// </summary>
    public static VideoProject Insert(VideoProject project, MediaSource source, Guid? trackId, long start, out Guid clipId, Placement? placement = null)
    {
        project = AddSource(project, source);
        start = Math.Max(0, start);
        long duration = source.IsImage ? Math.Max(1, project.Frames(ImageSeconds)) : SourceFrames(project, source, 0);
        // A small still (a logo) keeps its own size instead of being blown up to the canvas.
        if (source.IsImage && placement == null && source.Width <= project.Width && source.Height <= project.Height) placement = Natural(project, source.Width, source.Height);
        Guid? link = source.HasVideo && source.HasAudio ? Guid.NewGuid() : null;
        clipId = Guid.Empty;
        if (source.HasPicture)
        {
            VisualClip clip = source.IsImage
                ? new ImageClip { SourceId = source.Id, Start = start, Duration = duration, Placement = placement }
                : new VideoClip { SourceId = source.Id, Start = start, Duration = duration, LinkId = link, Placement = placement };
            project = Place(project, clip, TrackKind.Visual, trackId);
            clipId = clip.Id;
        }
        if (source.HasAudio)
        {
            var clip = new AudioClip { SourceId = source.Id, Start = start, Duration = duration, LinkId = link };
            project = Place(project, clip, TrackKind.Audio, source.HasVideo ? null : trackId);
            if (clipId == Guid.Empty) clipId = clip.Id;
        }
        return project;
    }

    /// <summary>
    /// Places a source at <paramref name="start"/> above everything showing during its span: on
    /// the first free layer over the highest one in use there, or on a new top layer.
    /// </summary>
    public static VideoProject InsertOnTop(VideoProject project, MediaSource source, long start, out Guid clipId, Placement? placement = null)
    {
        project = AddSource(project, source);
        start = Math.Max(0, start);
        long duration = source.IsImage ? Math.Max(1, project.Frames(ImageSeconds)) : SourceFrames(project, source, 0);
        var visual = project.Tracks.Where(track => track.Kind == TrackKind.Visual).ToList();
        int highestBusy = visual.FindLastIndex(track => !IsFree(track, start, duration));
        var target = visual.Skip(highestBusy + 1).FirstOrDefault(track => !track.Locked);
        Guid? trackId = target?.Id;
        if (target == null && source.HasPicture) { project = AddTrack(project, TrackKind.Visual, out var added); trackId = added; }
        return Insert(project, source, trackId, start, out clipId, placement);
    }

    /// <summary>Places only a source's sound, unlinked, on the first free audio track.</summary>
    public static VideoProject InsertAudioOnly(VideoProject project, MediaSource source, long start, out Guid clipId)
    {
        clipId = Guid.Empty;
        if (!source.HasAudio) return project;
        project = AddSource(project, source);
        var clip = new AudioClip { SourceId = source.Id, Start = Math.Max(0, start), Duration = SourceFrames(project, source, 0) };
        clipId = clip.Id;
        return Place(project, clip, TrackKind.Audio, null);
    }

    /// <summary>The placement that fits a picture of this size inside the canvas, centred, on even pixels.</summary>
    public static Placement Fit(VideoProject project, int sourceWidth, int sourceHeight)
    {
        var (w, h) = FrameReader.Fit(sourceWidth, sourceHeight, project.Width, project.Height);
        return new Placement { X = (project.Width - w) / 2.0, Y = (project.Height - h) / 2.0, Width = w, Height = h };
    }

    /// <summary>A picture at its own size when that fits the canvas, otherwise fitted; centred either way.</summary>
    public static Placement Natural(VideoProject project, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || sourceWidth > project.Width || sourceHeight > project.Height) return Fit(project, sourceWidth, sourceHeight);
        return new Placement { X = Math.Round((project.Width - sourceWidth) / 2.0), Y = Math.Round((project.Height - sourceHeight) / 2.0), Width = sourceWidth, Height = sourceHeight };
    }

    /// <summary>The placement that covers the whole canvas, cropping what overhangs.</summary>
    public static Placement Fill(VideoProject project, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return new Placement { Width = project.Width, Height = project.Height };
        double canvas = (double)project.Width / project.Height, picture = (double)sourceWidth / sourceHeight;
        // A wider picture loses its sides, a taller one its top and bottom.
        double cropX = picture > canvas ? (1 - canvas / picture) / 2 : 0, cropY = picture < canvas ? (1 - picture / canvas) / 2 : 0;
        return new Placement { Width = project.Width, Height = project.Height, CropLeft = cropX, CropRight = cropX, CropTop = cropY, CropBottom = cropY };
    }

    /// <summary>A clip's placement: its own, or the fitted default.</summary>
    public static Placement PlacementOf(VideoProject project, VisualClip clip)
    {
        if (clip.Placement is { } own) return own;
        var source = project.Source(clip.SourceId);
        return Fit(project, source?.Width ?? project.Width, source?.Height ?? project.Height);
    }

    public static VideoProject SetPlacement(VideoProject project, Guid clipId, Placement? placement) =>
        Update(project, clipId, clip => clip is VisualClip visual ? visual with { Placement = placement } : clip);

    /// <summary>
    /// Places a graphic (text, callout, shape, arrow, stamp) for five seconds from
    /// <paramref name="start"/>: on the given visual track when it is free there, otherwise above
    /// everything showing during its span.
    /// </summary>
    public static VideoProject InsertGraphic(VideoProject project, Item item, Guid? trackId, long start, out Guid clipId)
    {
        start = Math.Max(0, start);
        long duration = Math.Max(1, project.Frames(ImageSeconds));
        var clip = new GraphicClip { Item = item, Start = start, Duration = duration };
        clipId = clip.Id;
        var chosen = project.Tracks.FirstOrDefault(track => track.Id == trackId && track.Kind == TrackKind.Visual && !track.Locked && IsFree(track, start, duration));
        if (chosen == null)
        {
            var visual = project.Tracks.Where(track => track.Kind == TrackKind.Visual).ToList();
            int highestBusy = visual.FindLastIndex(track => !IsFree(track, start, duration));
            chosen = visual.Skip(highestBusy + 1).FirstOrDefault(track => !track.Locked);
        }
        if (chosen == null)
        {
            project = AddTrack(project, TrackKind.Visual, out var added);
            chosen = project.Tracks.First(track => track.Id == added);
        }
        return SetTrack(project, WithClips(chosen, chosen.Clips.Add(clip)));
    }

    /// <summary>Changes the item a graphic clip draws. The function must keep the kind of item sensible for its caller.</summary>
    public static VideoProject UpdateItem(VideoProject project, Guid clipId, Func<Item, Item> change) =>
        Update(project, clipId, clip => clip is GraphicClip graphic ? graphic with { Item = change(graphic.Item) } : clip);

    /// <summary>
    /// Adds a caption at <paramref name="start"/> on the given caption track, the first one, or a
    /// new one. It lasts three seconds or until the next caption, whichever is shorter. Nothing
    /// changes (and <paramref name="clipId"/> is empty) when a caption already covers that frame.
    /// </summary>
    public static VideoProject AddCaption(VideoProject project, Guid? trackId, long start, string text, out Guid clipId)
    {
        clipId = Guid.Empty;
        start = Math.Max(0, start);
        var track = project.Tracks.FirstOrDefault(candidate => candidate.Id == trackId && candidate.Kind == TrackKind.Caption)
            ?? project.Tracks.FirstOrDefault(candidate => candidate.Kind == TrackKind.Caption);
        if (track == null)
        {
            project = AddTrack(project, TrackKind.Caption, out var added);
            track = project.Tracks.First(candidate => candidate.Id == added);
        }
        if (track.Locked || track.Clips.Any(clip => clip.Start <= start && start < clip.End)) return project;
        long next = track.Clips.Where(clip => clip.Start > start).Select(clip => clip.Start).DefaultIfEmpty(long.MaxValue).Min();
        long duration = Math.Min(Math.Max(1, project.Frames(CaptionSeconds)), next - start);
        var caption = new CaptionClip { Start = start, Duration = duration, Text = text };
        clipId = caption.Id;
        return SetTrack(project, WithClips(track, track.Clips.Add(caption)));
    }

    /// <summary>Puts subtitle cues on a new caption track. Cues that overlap the one before start where it ends.</summary>
    public static VideoProject ImportCaptions(VideoProject project, IEnumerable<SrtCue> cues, out Guid trackId)
    {
        project = AddTrack(project, TrackKind.Caption, out trackId);
        var id = trackId;
        var track = project.Tracks.First(candidate => candidate.Id == id);
        var clips = new List<Clip>();
        long previousEnd = 0;
        foreach (var cue in cues.OrderBy(cue => cue.Start))
        {
            long start = Math.Max(previousEnd, project.Frames(cue.Start)), end = project.Frames(cue.End);
            if (end <= start || string.IsNullOrWhiteSpace(cue.Text)) continue;
            clips.Add(new CaptionClip { Start = start, Duration = end - start, Text = cue.Text });
            previousEnd = end;
        }
        return SetTrack(project, WithClips(track, clips));
    }

    /// <summary>Every caption on the visible caption tracks as subtitle cues, in time order.</summary>
    public static List<SrtCue> CaptionCues(VideoProject project) =>
        project.Tracks.Where(track => track.Kind == TrackKind.Caption && !track.Hidden)
            .SelectMany(track => track.Clips.OfType<CaptionClip>())
            .Where(clip => !string.IsNullOrWhiteSpace(clip.Text))
            .OrderBy(clip => clip.Start)
            .Select(clip => new SrtCue(project.Seconds(clip.Start), project.Seconds(clip.End), clip.Text)).ToList();

    /// <summary>A caption track's style: its own, or the default with a font sized to the canvas.</summary>
    public static CaptionStyle CaptionStyleOf(VideoProject project, Track track) =>
        track.CaptionStyle ?? new CaptionStyle { FontSize = Math.Max(12, Math.Round(project.Height * 0.05)) };

    /// <summary>The outline width letters of a size get when an outline is first switched on.</summary>
    public static double DefaultOutlineWidth(double fontSize) => Math.Max(2, Math.Round(fontSize / 14));

    /// <summary>
    /// The text box a caption is drawn as: centred, at most four fifths of the canvas wide, a
    /// twentieth of the canvas height in from the bottom (or the top).
    /// </summary>
    public static TextItem CaptionItem(VideoProject project, Track track, CaptionClip clip)
    {
        var style = CaptionStyleOf(project, track);
        var item = new TextItem
        {
            Id = clip.Id, Text = clip.Text, FontFamily = style.FontFamily, FontSize = style.FontSize, Bold = style.Bold, Color = style.Color,
            Background = style.Background, Alignment = TextAlign.Center, Padding = Math.Round(style.FontSize * 0.3),
            OutlineColor = style.OutlineColor, OutlineWidth = Math.Max(0, style.OutlineWidth),
            Bounds = new Rect(0, 0, Math.Round(project.Width * 0.8), 10),
        };
        var size = DocumentRenderer.MeasureText(item);
        double width = Math.Min(size.Width, project.Width * 0.8), margin = Math.Round(project.Height * 0.05);
        // Measure again at the final width: a narrower box can wrap differently.
        double height = DocumentRenderer.MeasureText(item with { Bounds = new Rect(0, 0, width, 10) }).Height;
        return item with { Bounds = new Rect(Math.Round((project.Width - width) / 2), style.AtTop ? margin : project.Height - margin - height, width, height) };
    }

    /// <summary>
    /// Everything drawn at a frame, bottom first: the picture or graphic on each visible layer,
    /// then the captions, which always sit on top.
    /// </summary>
    public static List<(Track Track, Clip Clip)> DrawnAt(VideoProject project, long frame)
    {
        var result = new List<(Track, Clip)>();
        foreach (var kind in new[] { TrackKind.Visual, TrackKind.Caption })
            foreach (var track in project.Tracks)
            {
                if (track.Kind != kind || track.Hidden) continue;
                if (track.Clips.FirstOrDefault(clip => clip.Start <= frame && frame < clip.End) is { } clip) result.Add((track, clip));
            }
        return result;
    }

    private static VideoProject Place(VideoProject project, Clip clip, TrackKind kind, Guid? preferred)
    {
        var target = project.Tracks.FirstOrDefault(track => track.Id == preferred && track.Kind == kind && IsFree(track, clip.Start, clip.Duration))
            ?? project.Tracks.FirstOrDefault(track => track.Kind == kind && !track.Locked && IsFree(track, clip.Start, clip.Duration));
        if (target == null)
        {
            project = AddTrack(project, kind, out var id);
            target = project.Tracks.First(track => track.Id == id);
        }
        return SetTrack(project, WithClips(target, target.Clips.Add(clip)));
    }

    /// <summary>
    /// Moves a clip to a track and start frame; clips linked to it shift by the same amount on
    /// their own tracks. Null when the target is the wrong kind or anything would overlap.
    /// </summary>
    public static VideoProject? Move(VideoProject project, Guid clipId, Guid targetTrackId, long newStart)
    {
        if (Find(project, clipId) is not var (fromTrack, clip)) return null;
        var target = project.Tracks.FirstOrDefault(track => track.Id == targetTrackId);
        if (target == null || target.Kind != fromTrack.Kind || target.Locked || fromTrack.Locked) return null;
        long delta = newStart - clip.Start;
        var moving = WithLinked(project, new[] { clipId });
        var tracks = project.Tracks;
        foreach (var id in moving)
        {
            var current = tracks.SelectMany(track => track.Clips.Select(c => (track, c))).First(pair => pair.c.Id == id);
            var destination = id == clipId ? tracks.First(track => track.Id == targetTrackId) : current.track;
            var moved = current.c with { Start = current.c.Start + delta };
            if (!IsFree(destination, moved.Start, moved.Duration, moving)) return null;
            if (destination.Id == current.track.Id)
            {
                tracks = tracks.Replace(current.track, WithClips(current.track, current.track.Clips.Replace(current.c, moved)));
            }
            else
            {
                tracks = tracks.Replace(current.track, current.track with { Clips = current.track.Clips.Remove(current.c) });
                var fresh = tracks.First(track => track.Id == destination.Id);
                tracks = tracks.Replace(fresh, WithClips(fresh, fresh.Clips.Add(moved)));
            }
        }
        return project with { Tracks = tracks };
    }

    /// <summary>The start frame nearest to <paramref name="desired"/> where the clip fits on the track without overlapping.</summary>
    public static long NearestFree(VideoProject project, Guid clipId, Guid trackId, long desired)
    {
        if (Find(project, clipId) is not var (_, clip)) return desired;
        var track = project.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        if (track == null) return desired;
        var ignore = WithLinked(project, new[] { clipId });
        desired = Math.Max(0, desired);
        if (IsFree(track, desired, clip.Duration, ignore)) return desired;
        long best = -1, bestDistance = long.MaxValue;
        // Candidate slots sit flush against an existing clip's edge.
        foreach (var other in track.Clips.Where(other => !ignore.Contains(other.Id)))
        {
            foreach (long candidate in new[] { other.End, other.Start - clip.Duration })
            {
                if (candidate < 0 || !IsFree(track, candidate, clip.Duration, ignore)) continue;
                long distance = Math.Abs(candidate - desired);
                if (distance < bestDistance) { best = candidate; bestDistance = distance; }
            }
        }
        return best >= 0 ? best : desired;
    }

    /// <summary>Drags a clip's left edge to <paramref name="newStart"/>, clamped to its source, its neighbours and one frame of length.</summary>
    public static VideoProject TrimStart(VideoProject project, Guid clipId, long newStart)
    {
        if (Find(project, clipId) is not var (_, primary)) return project;
        var group = WithLinked(project, new[] { clipId });
        long delta = newStart - primary.Start, min = long.MinValue, max = long.MaxValue;
        foreach (var track in project.Tracks)
        {
            foreach (var clip in track.Clips.Where(clip => group.Contains(clip.Id)))
            {
                long previousEnd = track.Clips.Where(other => other.End <= clip.Start && !group.Contains(other.Id)).Select(other => other.End).DefaultIfEmpty(0).Max();
                long sourceRoom = Unbounded(clip) ? long.MaxValue / 4 : (long)Math.Floor(clip.SourceIn * project.Rate.Value + 1e-6);
                min = Math.Max(min, Math.Max(previousEnd - clip.Start, -sourceRoom));
                max = Math.Min(max, clip.Duration - 1);
            }
        }
        delta = Math.Clamp(delta, min, Math.Max(min, max));
        if (delta == 0) return project;
        return Map(project, group, clip => ClampFades(clip with { Start = clip.Start + delta, Duration = clip.Duration - delta, SourceIn = Unbounded(clip) ? 0 : Math.Max(0, clip.SourceIn + project.Seconds(delta)) }));
    }

    /// <summary>Drags a clip's right edge to <paramref name="newEnd"/>, clamped to its source, its neighbours and one frame of length.</summary>
    public static VideoProject TrimEnd(VideoProject project, Guid clipId, long newEnd)
    {
        if (Find(project, clipId) is not var (_, primary)) return project;
        var group = WithLinked(project, new[] { clipId });
        long delta = newEnd - primary.End, min = long.MinValue, max = long.MaxValue;
        foreach (var track in project.Tracks)
        {
            foreach (var clip in track.Clips.Where(clip => group.Contains(clip.Id)))
            {
                long nextStart = track.Clips.Where(other => other.Start >= clip.End && !group.Contains(other.Id)).Select(other => other.Start).DefaultIfEmpty(long.MaxValue).Min();
                long available = Unbounded(clip) ? long.MaxValue / 4 : project.Source(clip.SourceId) is { } source ? SourceFrames(project, source, clip.SourceIn) : clip.Duration;
                max = Math.Min(max, Math.Min(nextStart - clip.End, available - clip.Duration));
                min = Math.Max(min, 1 - clip.Duration);
            }
        }
        delta = Math.Clamp(delta, Math.Min(min, max), max);
        if (delta == 0) return project;
        return Map(project, group, clip => ClampFades(clip with { Duration = clip.Duration + delta }));
    }

    /// <summary>True for clips that can fade: pictures, graphics and sounds, but not captions.</summary>
    public static bool CanFade(Clip clip) => clip is not CaptionClip;

    /// <summary>
    /// How much of a clip shows (or sounds) at a timeline frame, from 0 to 1: it climbs evenly
    /// from nothing on the clip's first frame to full after <see cref="Clip.FadeIn"/> frames, and
    /// falls so that the frame after its last would be nothing. Zero outside the clip. The preview
    /// multiplies a layer's opacity by this, and the export's fade filters follow the same line.
    /// </summary>
    public static double FadeAt(Clip clip, long frame)
    {
        long local = frame - clip.Start, left = clip.Duration - local;
        if (local < 0 || left <= 0) return 0;
        double level = 1;
        if (clip.FadeIn > 0 && local < clip.FadeIn) level = (double)local / clip.FadeIn;
        if (clip.FadeOut > 0 && left < clip.FadeOut) level = Math.Min(level, (double)left / clip.FadeOut);
        return level;
    }

    /// <summary>Shortens a clip's fades so both fit inside it; the fade in keeps its length first.</summary>
    private static Clip ClampFades(Clip clip)
    {
        long fadeIn = Math.Clamp(clip.FadeIn, 0, Math.Max(0, clip.Duration)), fadeOut = Math.Clamp(clip.FadeOut, 0, Math.Max(0, clip.Duration - fadeIn));
        return fadeIn == clip.FadeIn && fadeOut == clip.FadeOut ? clip : clip with { FadeIn = fadeIn, FadeOut = fadeOut };
    }

    /// <summary>
    /// Sets a clip's fade lengths in frames (null leaves one as it is); clips linked to it get the
    /// same. A fade set on its own stops where the other begins; set together, the fade in is
    /// fitted first. Returns the same project when nothing changes.
    /// </summary>
    public static VideoProject SetFade(VideoProject project, Guid clipId, long? fadeIn = null, long? fadeOut = null)
    {
        if (Find(project, clipId) is not var (_, primary) || !CanFade(primary)) return project;
        bool changed = false;
        var result = Map(project, WithLinked(project, new[] { clipId }), clip =>
        {
            if (!CanFade(clip)) return clip;
            long newIn = Math.Max(0, fadeIn ?? clip.FadeIn), newOut = Math.Max(0, fadeOut ?? clip.FadeOut);
            if (fadeIn != null && fadeOut == null) newIn = Math.Min(newIn, Math.Max(0, clip.Duration - newOut));
            var faded = ClampFades(clip with { FadeIn = newIn, FadeOut = newOut });
            changed |= faded.FadeIn != clip.FadeIn || faded.FadeOut != clip.FadeOut;
            return faded;
        });
        return changed ? result : project;
    }

    private static VideoProject Map(VideoProject project, ISet<Guid> ids, Func<Clip, Clip> change) =>
        project with { Tracks = project.Tracks.Select(track => track.Clips.Any(clip => ids.Contains(clip.Id)) ? WithClips(track, track.Clips.Select(clip => ids.Contains(clip.Id) ? change(clip) : clip)) : track).ToImmutableList() };

    /// <summary>Changes one clip in place (volume, mute). The function must keep the clip's id.</summary>
    public static VideoProject Update(VideoProject project, Guid clipId, Func<Clip, Clip> change) => Map(project, new HashSet<Guid> { clipId }, change);

    /// <summary>
    /// Cuts clips in two at <paramref name="frame"/>. With <paramref name="clipIds"/> only those
    /// clips (and what is linked to them) are cut; without, every unlocked clip under the frame.
    /// </summary>
    public static VideoProject Split(VideoProject project, long frame, IEnumerable<Guid>? clipIds = null)
    {
        var only = clipIds == null ? null : WithLinked(project, clipIds);
        var newLinks = new Dictionary<Guid, Guid>();
        bool changed = false;
        var tracks = project.Tracks.Select(track =>
        {
            if (track.Locked) return track;
            var result = new List<Clip>();
            foreach (var clip in track.Clips)
            {
                bool cut = clip.Start < frame && frame < clip.End && (only == null || only.Contains(clip.Id));
                if (!cut) { result.Add(clip); continue; }
                changed = true;
                Guid? rightLink = null;
                if (clip.LinkId is { } link)
                {
                    if (!newLinks.TryGetValue(link, out var mapped)) newLinks[link] = mapped = Guid.NewGuid();
                    rightLink = mapped;
                }
                long leftLength = frame - clip.Start;
                // The left part keeps the fade in, the right part the fade out.
                result.Add(ClampFades(clip with { Duration = leftLength, FadeOut = 0 }));
                result.Add(ClampFades(clip with { Id = Guid.NewGuid(), Start = frame, Duration = clip.Duration - leftLength, SourceIn = Unbounded(clip) ? 0 : clip.SourceIn + project.Seconds(leftLength), LinkId = rightLink, FadeIn = 0 }));
            }
            return WithClips(track, result);
        }).ToImmutableList();
        return changed ? project with { Tracks = tracks } : project;
    }

    /// <summary>
    /// Removes clips and what is linked to them. With <paramref name="ripple"/>, later clips on
    /// each affected track slide left to close the gap.
    /// </summary>
    public static VideoProject Delete(VideoProject project, IEnumerable<Guid> clipIds, bool ripple = false)
    {
        var ids = WithLinked(project, clipIds);
        if (ids.Count == 0) return project;
        var tracks = project.Tracks.Select(track =>
        {
            var removed = track.Clips.Where(clip => ids.Contains(clip.Id)).ToList();
            if (removed.Count == 0 || track.Locked) return track;
            var kept = track.Clips.Where(clip => !ids.Contains(clip.Id));
            if (ripple) kept = kept.Select(clip => clip with { Start = clip.Start - removed.Where(gone => gone.End <= clip.Start).Sum(gone => gone.Duration) });
            return WithClips(track, kept);
        }).ToImmutableList();
        return project with { Tracks = tracks };
    }

    /// <summary>Breaks the link between a clip and its partners so each can be edited alone.</summary>
    public static VideoProject Unlink(VideoProject project, Guid clipId) =>
        Map(project, WithLinked(project, new[] { clipId }), clip => clip with { LinkId = null });

    /// <summary>Frames worth snapping to: 0 and every clip edge not in <paramref name="ignore"/>.</summary>
    public static List<long> SnapPoints(VideoProject project, ISet<Guid>? ignore = null)
    {
        var points = new SortedSet<long> { 0 };
        foreach (var clip in project.AllClips)
        {
            if (ignore != null && ignore.Contains(clip.Id)) continue;
            points.Add(clip.Start);
            points.Add(clip.End);
        }
        return points.ToList();
    }

    /// <summary>The pictures showing at a frame, bottom layer first, skipping hidden tracks.</summary>
    public static List<VisualClip> VisibleAt(VideoProject project, long frame)
    {
        var result = new List<VisualClip>();
        foreach (var track in project.Tracks)
        {
            if (track.Kind != TrackKind.Visual || track.Hidden) continue;
            if (track.Clips.FirstOrDefault(clip => clip.Start <= frame && frame < clip.End) is VisualClip clip) result.Add(clip);
        }
        return result;
    }

    /// <summary>
    /// Flattens the project for ffmpeg. <paramref name="resolvePath"/> can swap a source for a
    /// stand-in (a proxy); by default the linked original is used.
    /// </summary>
    public static ExportPlan ToPlan(VideoProject project, Func<MediaSource, string>? resolvePath = null, int? width = null, int? height = null, string? graphicsFolder = null)
    {
        string PathOf(MediaSource source) => resolvePath?.Invoke(source) ?? source.Path;
        int outWidth = width ?? project.Width, outHeight = height ?? project.Height;
        // Placements are in canvas pixels; a smaller or larger output scales them with the canvas.
        double sx = (double)outWidth / project.Width, sy = (double)outHeight / project.Height;
        var video = new List<ExportVideoClip>();
        var audio = new List<ExportAudioClip>();
        var overlays = new List<ExportOverlayStream>();
        foreach (var track in project.Tracks)
        {
            // Graphics and captions are drawn by WPF into PNGs, so they need somewhere to go; a
            // plan made without a folder (the audio mix, a dry run) leaves them out.
            if (graphicsFolder != null && track.Kind == TrackKind.Caption && !track.Hidden
                && GraphicExport.Captions(project, track, sx, sy, graphicsFolder) is { } stream) overlays.Add(stream);
            foreach (var clip in track.Clips)
            {
                if (clip is GraphicClip graphic)
                {
                    if (graphicsFolder != null && track.Kind == TrackKind.Visual && !track.Hidden
                        && GraphicExport.Graphic(project, graphic, sx, sy, graphicsFolder) is { } drawn) video.Add(drawn);
                    continue;
                }
                if (project.Source(clip.SourceId) is not { } source) continue;
                if (clip is VisualClip visual && track.Kind == TrackKind.Visual && !track.Hidden)
                {
                    var p = PlacementOf(project, visual);
                    if (p.Opacity <= 0 || p.Width < 1 || p.Height < 1) continue;
                    video.Add(new ExportVideoClip(source.IsImage ? source.Path : PathOf(source), clip.SourceIn, project.Seconds(clip.Duration), project.Seconds(clip.Start),
                        p.X * sx, p.Y * sy, Math.Max(2, (int)Math.Round(p.Width * sx)), Math.Max(2, (int)Math.Round(p.Height * sy)))
                    {
                        Rotation = p.Rotation, Opacity = Math.Min(1, p.Opacity), IsImage = source.IsImage,
                        FadeIn = project.Seconds(clip.FadeIn), FadeOut = project.Seconds(clip.FadeOut),
                        CropLeft = p.CropLeft, CropTop = p.CropTop, CropRight = p.CropRight, CropBottom = p.CropBottom,
                    });
                }
                else if (clip is AudioClip { Muted: false } sound && track.Kind == TrackKind.Audio && !track.Muted && sound.Volume > 0)
                    audio.Add(new ExportAudioClip(source.Path, clip.SourceIn, project.Seconds(clip.Duration), project.Seconds(clip.Start), sound.Volume) { FadeIn = project.Seconds(clip.FadeIn), FadeOut = project.Seconds(clip.FadeOut) });
            }
        }
        var c = project.Background;
        return new ExportPlan
        {
            Width = outWidth,
            Height = outHeight,
            FrameRate = project.Rate,
            Duration = project.Seconds(project.Duration),
            Background = $"0x{c.R:X2}{c.G:X2}{c.B:X2}",
            Video = video,
            Overlays = overlays,
            Audio = audio,
        };
    }
}
