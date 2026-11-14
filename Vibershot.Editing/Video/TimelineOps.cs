using System.Collections.Immutable;
using System.Globalization;
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
        var added = new Track { Name = (kind == TrackKind.Visual ? "V" : "A") + (count + 1).ToString(CultureInfo.InvariantCulture), Kind = kind };
        trackId = added.Id;
        int lastOfKind = project.Tracks.FindLastIndex(track => track.Kind == kind);
        int index = lastOfKind >= 0 ? lastOfKind + 1 : kind == TrackKind.Visual ? 0 : project.Tracks.Count;
        return project with { Tracks = project.Tracks.Insert(index, added) };
    }

    /// <summary>Removes a track and its clips. The last track of a kind stays.</summary>
    public static VideoProject RemoveTrack(VideoProject project, Guid trackId)
    {
        var track = project.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        if (track == null || project.Tracks.Count(candidate => candidate.Kind == track.Kind) <= 1) return project;
        return project with { Tracks = project.Tracks.Remove(track) };
    }

    /// <summary>Whole frames of the source left after <paramref name="sourceIn"/> seconds.</summary>
    private static long SourceFrames(VideoProject project, MediaSource source, double sourceIn) =>
        Math.Max(1, (long)Math.Floor((source.Duration - sourceIn) * project.Rate.Value + 1e-6));

    /// <summary>Places a source at the end of the bottom video track.</summary>
    public static VideoProject Append(VideoProject project, MediaSource source)
    {
        var kind = source.HasVideo ? TrackKind.Visual : TrackKind.Audio;
        var first = project.Tracks.FirstOrDefault(track => track.Kind == kind);
        return Insert(project, source, first?.Id, first == null ? 0 : EndOf(first), out _);
    }

    /// <summary>
    /// Places a source at <paramref name="start"/>: its picture on the given visual track (or the
    /// lowest free one, or a new one on top) and its sound, linked, on the first free audio track.
    /// </summary>
    public static VideoProject Insert(VideoProject project, MediaSource source, Guid? trackId, long start, out Guid clipId)
    {
        project = AddSource(project, source);
        start = Math.Max(0, start);
        long duration = SourceFrames(project, source, 0);
        Guid? link = source.HasVideo && source.HasAudio ? Guid.NewGuid() : null;
        clipId = Guid.Empty;
        if (source.HasVideo)
        {
            var clip = new VideoClip { SourceId = source.Id, Start = start, Duration = duration, LinkId = link };
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
                long sourceRoom = (long)Math.Floor(clip.SourceIn * project.Rate.Value + 1e-6);
                min = Math.Max(min, Math.Max(previousEnd - clip.Start, -sourceRoom));
                max = Math.Min(max, clip.Duration - 1);
            }
        }
        delta = Math.Clamp(delta, min, Math.Max(min, max));
        if (delta == 0) return project;
        return Map(project, group, clip => clip with { Start = clip.Start + delta, Duration = clip.Duration - delta, SourceIn = Math.Max(0, clip.SourceIn + project.Seconds(delta)) });
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
                long available = project.Source(clip.SourceId) is { } source ? SourceFrames(project, source, clip.SourceIn) : clip.Duration;
                max = Math.Min(max, Math.Min(nextStart - clip.End, available - clip.Duration));
                min = Math.Max(min, 1 - clip.Duration);
            }
        }
        delta = Math.Clamp(delta, Math.Min(min, max), max);
        if (delta == 0) return project;
        return Map(project, group, clip => clip with { Duration = clip.Duration + delta });
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
                result.Add(clip with { Duration = leftLength });
                result.Add(clip with { Id = Guid.NewGuid(), Start = frame, Duration = clip.Duration - leftLength, SourceIn = clip.SourceIn + project.Seconds(leftLength), LinkId = rightLink });
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

    /// <summary>The video clips showing at a frame, bottom layer first, skipping hidden tracks.</summary>
    public static List<VideoClip> VisibleAt(VideoProject project, long frame)
    {
        var result = new List<VideoClip>();
        foreach (var track in project.Tracks)
        {
            if (track.Kind != TrackKind.Visual || track.Hidden) continue;
            if (track.Clips.FirstOrDefault(clip => clip.Start <= frame && frame < clip.End) is VideoClip clip) result.Add(clip);
        }
        return result;
    }

    /// <summary>
    /// Flattens the project for ffmpeg. <paramref name="resolvePath"/> can swap a source for a
    /// stand-in (a proxy); by default the linked original is used.
    /// </summary>
    public static ExportPlan ToPlan(VideoProject project, Func<MediaSource, string>? resolvePath = null)
    {
        string PathOf(MediaSource source) => resolvePath?.Invoke(source) ?? source.Path;
        var video = new List<ExportVideoClip>();
        var audio = new List<ExportAudioClip>();
        foreach (var track in project.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (project.Source(clip.SourceId) is not { } source) continue;
                if (clip is VideoClip && track.Kind == TrackKind.Visual && !track.Hidden)
                    video.Add(new ExportVideoClip(PathOf(source), clip.SourceIn, project.Seconds(clip.Duration), project.Seconds(clip.Start)));
                else if (clip is AudioClip { Muted: false } sound && track.Kind == TrackKind.Audio && !track.Muted && sound.Volume > 0)
                    audio.Add(new ExportAudioClip(source.Path, clip.SourceIn, project.Seconds(clip.Duration), project.Seconds(clip.Start), sound.Volume));
            }
        }
        var c = project.Background;
        return new ExportPlan
        {
            Width = project.Width,
            Height = project.Height,
            FrameRate = project.Rate,
            Duration = project.Seconds(project.Duration),
            Background = $"0x{c.R:X2}{c.G:X2}{c.B:X2}",
            Video = video,
            Audio = audio,
        };
    }
}
