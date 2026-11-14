using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Vibershot.Editing;
using Vibershot.Editing.Video;
using Vibershot.UI;

/// <summary>Checks for working on many clips at once: group moves, deletes, copy and paste, and the timeline's selection gestures.</summary>
static class VideoSelectChecks
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }

    static MediaSource Source(string path, double seconds, bool audio = true) =>
        new() { Path = path, Duration = seconds, Width = 1920, Height = 1080, FrameRate = "30", HasAudio = audio };

    public static void Run()
    {
        Operations();
        Gestures();
        Console.WriteLine("PASS: video editor group move, delete and ripple of a set, copy/paste/duplicate with links and new tracks, ctrl/shift click, marquee, group drag.");
    }

    /// <summary>The pure edits behind the gestures.</summary>
    static void Operations()
    {
        // V1: A 0..300 (with sound on A1), B 300..420, C 420..510. V2: a graphic 0..150. C1: a caption 30..120.
        var a = Source(@"C:\media\a.mp4", 10); var b = Source(@"C:\media\b.mp4", 4, audio: false); var c = Source(@"C:\media\c.mp4", 3, audio: false);
        var p = TimelineOps.Append(TimelineOps.Append(TimelineOps.ForSource(a), b), c);
        p = TimelineOps.AddTrack(p, TrackKind.Visual, out var v2);
        p = TimelineOps.InsertGraphic(p, new RectItem { Bounds = new Rect(10, 10, 100, 100), Fill = Colors.Red }, v2, 0, out var g);
        p = TimelineOps.AddCaption(p, null, 30, "Hi", out var cap);
        var v1 = p.Tracks[0].Id; var a1 = p.Tracks.Single(t => t.Kind == TrackKind.Audio).Id; var c1 = p.Tracks.Single(t => t.Kind == TrackKind.Caption).Id;
        Guid A = p.Tracks[0].Clips[0].Id, B = p.Tracks[0].Clips[1].Id, C = p.Tracks[0].Clips[2].Id, sound = p.Tracks.Single(t => t.Kind == TrackKind.Audio).Clips[0].Id;
        Check(p.Tracks.Count == 4 && p.Tracks[1].Id == v2 && TimelineOps.Find(p, g)!.Value.Track.Id == v2 && TimelineOps.Find(p, cap)!.Value.Clip.End == 120, "the starting layout");
        long StartOf(VideoProject project, Guid id) => TimelineOps.Find(project, id)!.Value.Clip.Start;
        Guid TrackOf(VideoProject project, Guid id) => TimelineOps.Find(project, id)!.Value.Track.Id;

        // Group move along the timeline.
        var moved = TimelineOps.MoveMany(p, new[] { B, C }, 60);
        Check(moved != null && StartOf(moved, B) == 360 && StartOf(moved, C) == 480 && StartOf(moved, A) == 0 && TrackOf(moved, B) == v1, "two clips move by the same frames");
        Check(StartOf(p, B) == 300, "the project handed in is untouched");
        Check(TimelineOps.MoveMany(p, new[] { B, C }, -30) == null, "a group move onto a clip outside the set is refused");
        Check(TimelineOps.MoveMany(p, new[] { A, B }, 120) == null, "refused when only the last of the set would overlap");
        Check(TimelineOps.MoveMany(p, new[] { A, B, C }, -30) == null, "a group cannot leave the start of the timeline");
        moved = TimelineOps.MoveMany(p, new[] { A, B, C }, 120);
        Check(moved != null && StartOf(moved, A) == 120 && StartOf(moved, B) == 420 && StartOf(moved, C) == 540 && StartOf(moved, sound) == 120, "clips of the set may pass through each other's old places, and linked sound comes along");
        Check(ReferenceEquals(TimelineOps.MoveMany(p, new[] { B }, 0), p) && TimelineOps.MoveMany(p, new[] { Guid.NewGuid() }, 10) == null, "no move returns the same project; unknown clips move nothing");

        // Group move across tracks: each clip stays among tracks of its own kind.
        moved = TimelineOps.MoveMany(p, new[] { B, C }, 0, 1);
        Check(moved != null && TrackOf(moved, B) == v2 && TrackOf(moved, C) == v2 && StartOf(moved, B) == 300 && moved.Tracks[0].Clips.Count == 1 && moved.Tracks[1].Clips.Count == 3, "two clips go up one layer together");
        Check(TimelineOps.MoveMany(p, new[] { B, C }, -200, 1) == null, "refused when the upper layer is taken there");
        Check(TimelineOps.MoveMany(p, new[] { B, g }, 0, 1) == null && TimelineOps.MoveMany(p, new[] { B }, 0, -1) == null, "refused when a clip would run out of layers");
        Check(TimelineOps.MoveMany(p, new[] { B, cap }, 0, 1) == null && TimelineOps.MoveMany(p, new[] { A }, 200, 1) == null, "every kind needs a track to go to");
        moved = TimelineOps.MoveMany(p, new[] { B, cap }, 15, 1, TrackKind.Visual);
        Check(moved != null && TrackOf(moved, B) == v2 && TrackOf(moved, cap) == c1 && StartOf(moved, cap) == 45 && StartOf(moved, B) == 315, "with one kind named, the others only shift in time");
        moved = TimelineOps.MoveMany(p, new[] { A }, 200, 1, TrackKind.Visual);
        Check(moved != null && TrackOf(moved, A) == v2 && TrackOf(moved, sound) == a1 && StartOf(moved, sound) == 200, "a video changes layer and its sound stays on its lane");
        var lockedUp = TimelineOps.SetTrack(p, p.Tracks[1] with { Locked = true });
        Check(TimelineOps.MoveMany(lockedUp, new[] { B }, 0, 1) == null && TimelineOps.MoveMany(lockedUp, new[] { g }, 300) == null, "locked tracks neither take nor give clips");

        // Delete and ripple delete of a set.
        var cut = TimelineOps.DeleteMany(p, new[] { A, C });
        Check(cut.Tracks[0].Clips.Single().Id == B && StartOf(cut, B) == 300 && cut.Tracks.Single(t => t.Kind == TrackKind.Audio).Clips.Count == 0 && TimelineOps.Find(cut, g) != null, "deleting a set leaves the gaps and takes linked sound");
        cut = TimelineOps.RippleDeleteMany(p, new[] { A, C });
        Check(cut.Tracks[0].Clips.Single().Id == B && StartOf(cut, B) == 0, "ripple delete closes the gap before what is left");
        cut = TimelineOps.RippleDeleteMany(p, new[] { B, cap });
        Check(StartOf(cut, C) == 300 && StartOf(cut, A) == 0 && TimelineOps.Find(cut, cap) == null && StartOf(cut, g) == 0, "ripple delete across tracks slides only what came after on each track");

        // Copy and paste: new ids, new links, same offsets, same tracks.
        var copied = TimelineOps.Copy(p, new[] { A })!;
        Check(copied.Clips.Count == 2 && copied.Sources.Single().Id == a.Id && copied.Start == 0 && copied.End == 300 && TimelineOps.Copy(p, new[] { Guid.NewGuid() }) == null, "copy takes the linked sound and the source");
        var pasted = TimelineOps.Paste(p, copied, 600, out var ids);
        var picture = TimelineOps.Find(pasted, ids[0])!.Value; var noise = TimelineOps.Find(pasted, ids[1])!.Value;
        var oldLink = TimelineOps.Find(p, A)!.Value.Clip.LinkId;
        Check(ids.Count == 2 && !ids.Contains(A) && !ids.Contains(sound) && picture.Track.Id == v1 && noise.Track.Id == a1 && picture.Clip.Start == 600 && noise.Clip.Start == 600 && picture.Clip.Duration == 300, "pasted clips are new and land on their own tracks");
        Check(picture.Clip.LinkId != null && picture.Clip.LinkId == noise.Clip.LinkId && picture.Clip.LinkId != oldLink && TimelineOps.Find(pasted, A)!.Value.Clip.LinkId == oldLink, "the pasted pair is linked to each other, not to the original");
        Check(pasted.Tracks.Count == 4 && pasted.Sources.Count == 3 && p.Tracks[0].Clips.Count == 3, "room on the original tracks: no track is added");

        copied = TimelineOps.Copy(p, new[] { B, C, cap })!;
        pasted = TimelineOps.Paste(p, copied, 1000, out ids);
        Check(ids.Count == 3 && StartOf(pasted, ids[0]) == 1270 && StartOf(pasted, ids[1]) == 1390 && StartOf(pasted, ids[2]) == 1000 && TrackOf(pasted, ids[0]) == v1 && TrackOf(pasted, ids[2]) == c1, "the earliest clip lands at the paste frame and the rest keep their offsets");
        Check(TimelineOps.Find(pasted, ids[0])!.Value.Clip.LinkId == null && TimelineOps.Find(pasted, ids[2])!.Value.Clip is CaptionClip { Text: "Hi" }, "unlinked stays unlinked; a caption keeps its words");

        // No room on the original track: a new track of the same kind.
        pasted = TimelineOps.Paste(p, TimelineOps.Copy(p, new[] { B })!, 100, out ids);
        var landed = TimelineOps.Find(pasted, ids[0])!.Value;
        Check(pasted.Tracks.Count == 5 && landed.Track.Kind == TrackKind.Visual && landed.Track.Name == "V3" && pasted.Tracks[2].Id == landed.Track.Id && landed.Clip.Start == 100 && pasted.Tracks[0].Clips.Count == 3, "no room on the track: pasted onto a new layer on top");
        pasted = TimelineOps.Paste(p, TimelineOps.Copy(p, new[] { A })!, 150, out ids);
        picture = TimelineOps.Find(pasted, ids[0])!.Value; noise = TimelineOps.Find(pasted, ids[1])!.Value;
        Check(pasted.Tracks.Count == 6 && picture.Track.Name == "V3" && noise.Track.Name == "A2" && noise.Track.Kind == TrackKind.Audio && picture.Clip.LinkId == noise.Clip.LinkId && picture.Clip.LinkId != null, "picture and sound each get a new track of their kind and stay linked");
        pasted = TimelineOps.Paste(p, TimelineOps.Copy(p, new[] { B, C })!, 0, out ids);
        Check(pasted.Tracks.Count == 5 && TrackOf(pasted, ids[0]) == TrackOf(pasted, ids[1]) && TrackOf(pasted, ids[0]) != v1 && StartOf(pasted, ids[0]) == 0 && StartOf(pasted, ids[1]) == 120, "clips from one track stay together on the new one");
        var lockedDown = TimelineOps.SetTrack(p, p.Tracks[0] with { Locked = true });
        pasted = TimelineOps.Paste(lockedDown, TimelineOps.Copy(p, new[] { B })!, 2000, out ids);
        Check(pasted.Tracks.Count == 5 && TrackOf(pasted, ids[0]) != v1, "a locked track takes no paste");
        var styled = TimelineOps.SetTrack(p, p.Tracks.Single(t => t.Kind == TrackKind.Caption) with { CaptionStyle = new CaptionStyle { AtTop = true } });
        pasted = TimelineOps.Paste(styled, TimelineOps.Copy(styled, new[] { cap })!, 60, out ids);
        landed = TimelineOps.Find(pasted, ids[0])!.Value;
        Check(landed.Track.Kind == TrackKind.Caption && landed.Track.Id != c1 && landed.Track.CaptionStyle is { AtTop: true } && pasted.Tracks.Count(t => t.Kind == TrackKind.Caption) == 2, "captions with no room get a caption track that looks the same");

        // Into a project that never had the tracks or the source.
        pasted = TimelineOps.Paste(TimelineOps.Empty(), TimelineOps.Copy(p, new[] { A })!, 0, out ids);
        Check(pasted.Tracks.Count == 2 && pasted.Sources.Single().Id == a.Id && pasted.Tracks[0].Clips.Single().Id == ids[0] && pasted.Tracks[1].Clips.Single().Id == ids[1], "a paste brings its source and uses tracks of the right kind");

        // Duplicate: copies go straight after the last of the set.
        var twice = TimelineOps.Duplicate(p, new[] { B, C }, out ids);
        Check(ids.Count == 2 && StartOf(twice, ids[0]) == 510 && StartOf(twice, ids[1]) == 630 && TrackOf(twice, ids[1]) == v1 && twice.Tracks.Count == 4 && twice.Tracks[0].Clips.Count == 5, "duplicate lands after the set on the same track");
        Check(ReferenceEquals(TimelineOps.Duplicate(p, Array.Empty<Guid>(), out ids), p) && ids.Count == 0, "nothing selected, nothing duplicated");
    }

    /// <summary>Drives the timeline control through its pointer methods: the same code the mouse reaches.</summary>
    static void Gestures()
    {
        var a = Source(@"C:\media\a.mp4", 4); var b = Source(@"C:\media\b.mp4", 2, audio: false);
        var c = Source(@"C:\media\c.mp4", 2, audio: false); var d = Source(@"C:\media\d.mp4", 2, audio: false);
        var history = new History<VideoProject>(TimelineOps.Append(TimelineOps.Append(TimelineOps.Append(TimelineOps.ForSource(a), b), c), d));
        TimelineView.Clipboard = null;
        var view = new TimelineView { History = history };
        view.Measure(new Size(1440, 300)); view.Arrange(new Rect(0, 0, 1440, 300));
        view.SetZoom(100); view.ScrollX = 0;
        // 100 px per second at 30 fps. V1 (y 24..76): A 0..400 px, B 400..600, C 600..800, D 800..1000. A's sound is on A1 (y 76..116).
        const double h = TimelineView.HeaderWidth; const double v1 = 50, a1 = 96;
        Guid A = history.Current.Tracks[0].Clips[0].Id, B = history.Current.Tracks[0].Clips[1].Id, C = history.Current.Tracks[0].Clips[2].Id, D = history.Current.Tracks[0].Clips[3].Id;
        Guid sound = history.Current.Tracks[1].Clips[0].Id;
        long StartOf(Guid id) => TimelineOps.Find(history.Current, id)!.Value.Clip.Start;
        string TrackOf(Guid id) => TimelineOps.Find(history.Current, id)!.Value.Track.Name;
        bool Selected(params Guid[] ids) => view.Selection.SetEquals(ids);
        void Click(double x, double y, ModifierKeys keys = ModifierKeys.None) { view.PointerDown(new Point(h + x, y), keys); view.PointerUp(); }
        int changes = 0; view.SelectionChanged += () => changes++;

        // Click, Ctrl+click and Shift+click.
        Click(500, v1);
        Check(Selected(B) && view.Primary == B, "a click selects one clip and makes it the primary");
        Click(900, v1, ModifierKeys.Control);
        Check(Selected(B, D) && view.Primary == D, "ctrl+click adds a clip, which becomes the primary");
        Click(900, v1, ModifierKeys.Control);
        Check(Selected(B) && view.Primary == B, "ctrl+click again takes it out");
        Click(900, v1, ModifierKeys.Shift);
        Check(Selected(B, C, D) && view.Primary == B, "shift+click adds the run from the primary clip to the clicked one");
        Click(200, v1);
        Check(Selected(A, sound) && view.Primary == A, "a plain click on another clip starts over, with its linked sound");
        Click(700, v1, ModifierKeys.Shift);
        Check(Selected(A, sound, B, C), "shift+click runs forward from a linked clip");
        Click(200, a1, ModifierKeys.Shift);
        Check(Selected(A, sound, B, C) && view.Primary == A, "shift+click on another track only adds the clicked clip");
        view.PointerDown(new Point(h + 500, v1));
        Check(Selected(A, sound, B, C) && view.Primary == B, "pressing one of several selected clips keeps the set");
        view.PointerUp();
        Check(Selected(B), "and releasing without a drag leaves just that clip");
        int depth = history.UndoDepth;
        Click(1100, v1);
        Check(view.Selection.Count == 0 && view.Primary == null && view.Marquee == null && view.Playhead == 300 && history.UndoDepth == depth, "a click on empty lane clears the selection and parks the playhead");

        // Marquee: every clip the box touches, linked clips included; it follows the pointer both ways.
        Check(view.PointerDown(new Point(h + 1050, 30)), "a press on empty lane starts a drag");
        view.PointerMove(new Point(h + 1049, 31));
        Check(view.Marquee == null, "a twitch is not a marquee");
        view.PointerMove(new Point(h + 700, 60));
        Check(view.Marquee == new Rect(h + 700, 30, 350, 30) && Selected(C, D), "the marquee selects what it touches: " + view.Marquee);
        view.PointerMove(new Point(h + 300, 100));
        Check(Selected(A, sound, B, C, D), "dragged further it reaches more clips and the sound lane");
        view.PointerMove(new Point(h + 900, 60));
        Check(Selected(D), "dragged back it lets go again");
        view.PointerUp();
        Check(view.Marquee == null && Selected(D) && history.UndoDepth == depth, "releasing keeps the selection and is not an edit");
        Click(500, v1);
        view.PointerDown(new Point(h + 1050, 30), ModifierKeys.Control); view.PointerMove(new Point(h + 850, 60));
        Check(Selected(B, D), "ctrl+marquee adds to the selection");
        view.PointerUp();
        view.PointerDown(new Point(h + 1050, 80)); view.PointerMove(new Point(h + 100, 110)); view.PointerUp();
        Check(Selected(A, sound), "a marquee over the sound lane takes the linked picture too");
        view.SelectAll();
        Check(view.Selection.Count == 5, "select all");
        view.ClearSelection();
        Check(view.Selection.Count == 0 && view.Primary == null, "select none");

        // Group drag: C and D move right together, one undo step.
        view.Playhead = 0;
        Click(700, v1); Click(900, v1, ModifierKeys.Control);
        depth = history.UndoDepth;
        view.PointerDown(new Point(h + 900, v1));
        for (int x = 910; x <= 1100; x += 10) view.PointerMove(new Point(h + x, v1));
        view.PointerUp();
        Check(StartOf(C) == 240 && StartOf(D) == 300 && StartOf(B) == 120 && history.UndoDepth == depth + 1 && Selected(C, D), $"dragging D 200 px right moves C and D 60 frames in one step, got {StartOf(C)} and {StartOf(D)}");
        history.Undo();
        Check(StartOf(C) == 180 && StartOf(D) == 240 && Selected(C, D), "undo puts both back");

        // The far edge of the set snaps to the playhead.
        view.Playhead = 450;
        view.PointerDown(new Point(h + 900, v1)); view.PointerMove(new Point(h + 1200, v1)); view.PointerMove(new Point(h + 1393, v1)); view.PointerUp();
        Check(StartOf(C) == 330 && StartOf(D) == 390, $"the end of the set snaps to the playhead, got {StartOf(C)} and {StartOf(D)}");
        history.Undo(); view.Playhead = 0;

        // Refused: onto B. Nothing changes, and a drag that went somewhere first snaps back without leaving an undo step.
        var before = history.Current; depth = history.UndoDepth;
        view.PointerDown(new Point(h + 900, v1)); view.PointerMove(new Point(h + 850, v1)); view.PointerMove(new Point(h + 800, v1)); view.PointerUp();
        Check(ReferenceEquals(history.Current, before) && history.UndoDepth == depth, "a group move onto another clip is refused");
        view.PointerDown(new Point(h + 900, v1)); view.PointerMove(new Point(h + 1000, v1));
        Check(StartOf(C) == 210, "the set follows the pointer first");
        view.PointerMove(new Point(h + 800, v1)); view.PointerUp();
        Check(ReferenceEquals(history.Current, before) && history.UndoDepth == depth && Selected(C, D), "then snaps back when the move is refused");
        view.SelectAll();
        view.PointerDown(new Point(h + 500, v1)); view.PointerMove(new Point(h + 300, v1)); view.PointerMove(new Point(h + 100, v1)); view.PointerUp();
        Check(ReferenceEquals(history.Current, before) && history.UndoDepth == depth, "the set stops at the start of the timeline");

        // Trim still acts on the one clip under the pointer.
        Click(700, v1); Click(900, v1, ModifierKeys.Control);
        view.PointerDown(new Point(h + 603, v1)); view.PointerMove(new Point(h + 650, v1)); view.PointerMove(new Point(h + 700, v1)); view.PointerUp();
        var trimmed = TimelineOps.Find(history.Current, C)!.Value.Clip;
        Check(trimmed.Start == 210 && trimmed.Duration == 30 && StartOf(D) == 240 && Selected(C, D), $"trimming one of two selected clips leaves the other, got {trimmed.Start} for {trimmed.Duration}");
        history.Undo();

        // Across layers: with V2 on top (y 24..76), V1 is the lane at 76..128 and A1 at 128..168.
        history.Push(TimelineOps.AddTrack(history.Current, TrackKind.Visual, out _));
        const double upper = 50, lower = 102;
        depth = history.UndoDepth;
        view.PointerDown(new Point(h + 900, lower)); view.PointerMove(new Point(h + 900, 80)); view.PointerMove(new Point(h + 900, upper)); view.PointerUp();
        Check(TrackOf(C) == "V2" && TrackOf(D) == "V2" && StartOf(C) == 180 && StartOf(D) == 240 && TrackOf(B) == "V1" && history.UndoDepth == depth + 1, $"dragging straight up takes both clips a layer up, got {TrackOf(C)} and {TrackOf(D)}");
        // A (with its sound) and B go up next to them: the pictures change layer, the sound stays on its lane.
        Click(200, lower); Click(500, lower, ModifierKeys.Control);
        view.PointerDown(new Point(h + 200, lower)); view.PointerMove(new Point(h + 200, 80)); view.PointerMove(new Point(h + 200, upper)); view.PointerUp();
        Check(TrackOf(A) == "V2" && TrackOf(B) == "V2" && TrackOf(sound) == "A1" && StartOf(A) == 0 && StartOf(sound) == 0, "pictures change layer together and the linked sound stays on its lane");
        // Back down where the lower layer is taken by nothing: fine; then C and D down onto A and B is refused.
        view.PointerDown(new Point(h + 200, upper)); view.PointerMove(new Point(h + 200, 80)); view.PointerMove(new Point(h + 200, lower)); view.PointerUp();
        Check(TrackOf(A) == "V1" && TrackOf(B) == "V1", "and come back down");
        Click(700, upper); Click(900, upper, ModifierKeys.Control);
        before = history.Current;
        view.PointerDown(new Point(h + 900, upper)); view.PointerMove(new Point(h + 800, upper)); view.PointerMove(new Point(h + 500, lower)); view.PointerUp();
        Check(StartOf(C) == 60 && StartOf(D) == 120 && TrackOf(C) == "V2", $"a lane change that would overlap falls back to the set's own lanes, got {StartOf(C)} on {TrackOf(C)}");
        history.Undo();
        Check(ReferenceEquals(history.Current, before), "still one undo step");
        // Past the top lane the set gets a new layer.
        view.PointerDown(new Point(h + 900, upper)); view.PointerMove(new Point(h + 900, 30)); view.PointerMove(new Point(h + 900, 8)); view.PointerMove(new Point(h + 930, 8)); view.PointerUp();
        Check(history.Current.Tracks.Count(t => t.Kind == TrackKind.Visual) == 3 && TrackOf(C) == "V3" && TrackOf(D) == "V3" && StartOf(D) == 249, $"dragged above the top lane the set gets a new layer, got {TrackOf(C)} at {StartOf(D)}");
        history.Undo();
        Check(history.Current.Tracks.Count(t => t.Kind == TrackKind.Visual) == 2 && TrackOf(D) == "V2", "undo takes the new layer away again");

        // Copy, paste at the playhead, cut, duplicate and delete act on the whole selection.
        Check(!view.Paste(), "nothing to paste before a copy");
        Check(view.CopySelection() && TimelineView.Clipboard!.Clips.Count == 2, "copy takes the selected clips");
        view.Playhead = 600; depth = history.UndoDepth;
        Check(view.Paste() && history.UndoDepth == depth + 1 && view.Selection.Count == 2 && !view.Selection.Contains(C) && !view.Selection.Contains(D), "paste is one step and selects the copies");
        var copies = history.Current.Tracks.Single(t => t.Name == "V2").Clips.Where(clip => view.Selection.Contains(clip.Id)).ToList();
        Check(copies.Count == 2 && copies[0].Start == 600 && copies[1].Start == 660 && view.Primary == copies[0].Id, "the copies land at the playhead on the same layer, offsets kept");
        view.Playhead = 630;
        Check(view.Paste() && history.Current.Tracks.Count(t => t.Kind == TrackKind.Visual) == 3 && view.Selection.All(id => TrackOf(id) == "V3"), "pasting where the layer is taken makes a new layer");
        history.Undo();
        Check(view.Selection.Count == 0, "undoing a paste drops the copies from the selection");
        Click(200, lower);
        depth = history.UndoDepth;
        Check(view.CutSelection() && history.UndoDepth == depth + 1 && TimelineOps.Find(history.Current, A) == null && TimelineOps.Find(history.Current, sound) == null && TimelineView.Clipboard!.Clips.Count == 2, "cut removes the clip and its sound and keeps them for pasting");
        view.Playhead = 0;
        Check(view.Paste() && view.Selection.Count == 2 && view.Selection.All(id => StartOf(id) == 0) && view.Selection.Select(TrackOf).OrderBy(name => name).SequenceEqual(new[] { "A1", "V1" }), "the cut pair pastes back onto its own tracks");
        var pair = history.Current.AllClips.Where(clip => view.Selection.Contains(clip.Id)).ToList();
        Check(pair[0].LinkId != null && pair[0].LinkId == pair[1].LinkId, "and is linked");
        Click(500, lower);
        Check(view.DuplicateSelection() && view.Selection.Count == 1 && StartOf(view.Selection.Single()) == 180 && TrackOf(view.Selection.Single()) == "V1" && TimelineView.Clipboard!.Clips.Count == 2, "duplicate puts a copy straight after the clip and leaves the clipboard alone");
        view.SelectAll(); int all = view.Selection.Count;
        Click(500, lower); Click(700, upper, ModifierKeys.Control);
        Check(view.DeleteSelection() && history.Current.AllClips.Count() == all - 2 && view.Selection.Count == 0 && StartOf(D) == 240, "delete removes every selected clip and leaves the gaps");
        history.Undo();
        Click(500, lower); Click(700, upper, ModifierKeys.Control);
        Check(view.DeleteSelection(ripple: true) && StartOf(D) == 180 && history.Current.Tracks[0].Clips.First(clip => clip.Start > 0).Start == 120, "ripple delete closes the gap on each track");
        Check(!view.DeleteSelection() && !view.CopySelection() && !view.DuplicateSelection(), "with nothing selected the commands do nothing");
        Check(changes > 0, "selection changes are announced");
        TimelineView.Clipboard = null;
    }
}
