using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vibershot.Editing;
using Vibershot.Editing.Video;
using Vibershot.UI;

/// <summary>Checks for outlined and shadowed text, outlined captions, and picking captions in the preview.</summary>
static class VideoTextChecks
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }
    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;

    public static void Run()
    {
        Json();
        Pixels();
        Extents();
        Captions();
        Picking();
        Console.WriteLine("PASS: text outline and shadow (JSON, pixels, defaults unchanged, export extent), caption outline, caption picking and in-place edit request.");
    }

    // ---- Pictures ----

    /// <summary>A rendered region as premultiplied BGRA bytes.</summary>
    sealed record Picture(byte[] Bytes, int Width, int Height)
    {
        public (byte B, byte G, byte R, byte A) At(int x, int y) { int i = (y * Width + x) * 4; return (Bytes[i], Bytes[i + 1], Bytes[i + 2], Bytes[i + 3]); }
        public int Count(Func<(byte B, byte G, byte R, byte A), bool> test)
        {
            int n = 0;
            for (int i = 0; i < Bytes.Length; i += 4) if (test((Bytes[i], Bytes[i + 1], Bytes[i + 2], Bytes[i + 3]))) n++;
            return n;
        }
        /// <summary>The smallest box holding every pixel that is not fully transparent.</summary>
        public Int32Rect Inked()
        {
            int l = Width, t = Height, r = -1, b = -1;
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) if (At(x, y).A > 0) { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y); }
            return r < 0 ? Int32Rect.Empty : new Int32Rect(l, t, r - l + 1, b - t + 1);
        }
    }

    static Picture Read(BitmapSource bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return new Picture(bytes, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    static Picture Draw(Item item, Rect region) => Read(GraphicExport.Render(new[] { item }, region, (int)region.Width, (int)region.Height));

    static bool Red((byte B, byte G, byte R, byte A) p) => p.A > 200 && p.R > 200 && p.G < 60 && p.B < 60;
    /// <summary>Clearly darker than white ink of the same coverage (the bytes are premultiplied).</summary>
    static bool Dark((byte B, byte G, byte R, byte A) p) => p.A > 20 && p.R * 3 < p.A;
    static bool White((byte B, byte G, byte R, byte A) p) => p.A == 255 && p.R == 255 && p.G == 255 && p.B == 255;

    static TextItem Sample() => new() { Text = "HIM", FontSize = 90, Bold = true, Color = Colors.White, Padding = 30, Bounds = new Rect(100, 100, 400, 190) };

    // ---- Checks ----

    static void Json()
    {
        // The new fields survive a save, on text and on a callout.
        Item text = Sample() with { OutlineColor = Colors.Red, OutlineWidth = 4.5, TextShadow = true };
        var back = (TextItem)DocumentJson.Deserialize<Item>(DocumentJson.Serialize(text))!;
        Check(back.OutlineColor == Colors.Red && back.OutlineWidth == 4.5 && back.TextShadow && back.HasOutline && back == (TextItem)text, "text outline and shadow round trip");
        Item callout = new CalloutItem { Text = "Look", OutlineColor = Colors.Blue, OutlineWidth = 2, TextShadow = true, Bounds = new Rect(0, 0, 100, 50) };
        var calloutBack = (CalloutItem)DocumentJson.Deserialize<Item>(DocumentJson.Serialize(callout))!;
        Check(calloutBack.OutlineColor == Colors.Blue && calloutBack.OutlineWidth == 2 && calloutBack.TextShadow, "a callout carries the outline too");

        // Files written before the fields existed load with no outline and no shadow.
        var old = (TextItem)DocumentJson.Deserialize<Item>("{\"type\":\"text\",\"Bounds\":[10,20,300,80],\"Text\":\"Old\",\"FontSize\":40,\"Color\":\"#FFFFFFFF\",\"Padding\":6}")!;
        Check(old.Text == "Old" && old.FontSize == 40 && old.OutlineWidth == 0 && !old.HasOutline && !old.TextShadow && old.OutlineColor == Colors.Black, "old text JSON loads with no outline or shadow");
        Check(!new TextItem().HasOutline && !new TextItem().TextShadow && !new TextItem { OutlineWidth = 3, OutlineColor = Colors.Transparent }.HasOutline, "a new text item has no outline or shadow");

        // The same for a whole project: a caption style and a graphic, with the new fields cut out of the JSON.
        var project = TimelineOps.ForSource(new MediaSource { Path = @"C:\media\a.mp4", Duration = 10, Width = 1920, Height = 1080, FrameRate = "30" });
        project = TimelineOps.InsertGraphic(project, text, null, 0, out var textId);
        project = TimelineOps.AddCaption(project, null, 0, "Hello", out var captionId);
        var lane = project.Tracks.Single(t => t.Kind == TrackKind.Caption);
        project = TimelineOps.SetTrack(project, lane with { CaptionStyle = new CaptionStyle { FontSize = 40, Background = null, OutlineColor = Colors.Navy, OutlineWidth = 3 } });
        string json = DocumentJson.Serialize(project);
        var loaded = DocumentJson.Deserialize<VideoProject>(json)!;
        Check(loaded.Tracks.Single(t => t.Kind == TrackKind.Caption).CaptionStyle is { OutlineWidth: 3, Background: null } style && style.OutlineColor == Colors.Navy, "a caption style's outline, and its missing box, round trip");
        Check(((GraphicClip)TimelineOps.Find(loaded, textId)!.Value.Clip).Item is TextItem { OutlineWidth: 4.5, TextShadow: true }, "a text graphic's outline round trips in a project");
        var node = JsonNode.Parse(json)!;
        int cut = Strip(node);
        var older = DocumentJson.Deserialize<VideoProject>(node.ToJsonString())!;
        var olderStyle = older.Tracks.Single(t => t.Kind == TrackKind.Caption).CaptionStyle!;
        var olderText = (TextItem)((GraphicClip)TimelineOps.Find(older, textId)!.Value.Clip).Item;
        Check(cut == 5 && olderStyle.OutlineWidth == 0 && olderStyle.FontSize == 40 && olderStyle.Background == null && !olderText.HasOutline && !olderText.TextShadow && olderText.Text == "HIM", "a project saved before outlines loads with none: " + cut);
    }

    /// <summary>Removes the outline and shadow properties everywhere in a JSON tree; returns how many were cut.</summary>
    static int Strip(JsonNode? node)
    {
        int cut = 0;
        if (node is JsonObject o)
        {
            foreach (var name in new[] { "OutlineColor", "OutlineWidth", "TextShadow" }) if (o.Remove(name)) cut++;
            foreach (var child in o.ToList()) cut += Strip(child.Value);
        }
        else if (node is JsonArray a) foreach (var child in a) cut += Strip(child);
        return cut;
    }

    static void Pixels()
    {
        var plain = Sample();
        var region = new Rect(60, 60, 480, 270);
        var plainPicture = Draw(plain, region);
        Check(plainPicture.Count(White) > 1500 && plainPicture.Count(Red) == 0 && plainPicture.Count(Dark) == 0 && plainPicture.Count(p => Math.Abs(p.R - p.A) > 2 || p.G != p.R || p.B != p.R) == 0, $"plain white text has only white ink: {plainPicture.Count(White)} white, {plainPicture.Count(Red)} red, {plainPicture.Count(Dark)} dark, {plainPicture.Count(p => Math.Abs(p.R - p.A) > 2 || p.G != p.R || p.B != p.R)} tinted");

        // Unchanged defaults: the renderer draws a plain text item exactly as the code before outlines did.
        foreach (var item in new[] { plain, plain with { Background = Colors.DarkSlateBlue, Stroke = Colors.Orange, StrokeWidth = 3, Shadow = true, Rotation = 12, Opacity = 0.8, Underline = true }, plain with { Text = "wrapped words that run over several lines", FontSize = 30, Alignment = TextAlign.Right, Italic = true } })
        {
            var now = Draw(item, region);
            var then = Read(Reference(item, region));
            Check(now.Bytes.SequenceEqual(then.Bytes), "default text renders byte for byte as before: " + item.Text);
            // A colour or a zero width alone draws nothing.
            Check(Draw(item with { OutlineColor = Colors.Red, OutlineWidth = 0 }, region).Bytes.SequenceEqual(now.Bytes) && Draw(item with { OutlineColor = Colors.Transparent, OutlineWidth = 8 }, region).Bytes.SequenceEqual(now.Bytes), "width 0 or a clear colour draws no outline");
        }

        // An outline: its colour sits just outside the letters, and the letters themselves are untouched.
        var outlined = Draw(plain with { OutlineColor = Colors.Red, OutlineWidth = 5 }, region);
        Check(outlined.Count(Red) > 2000, "the outline colour is drawn: " + outlined.Count(Red));
        int row = -1, first = -1;
        for (int y = plainPicture.Height / 2; y < plainPicture.Height && first < 0; y++)
            for (int x = 3; x < plainPicture.Width; x++)
                if (White(plainPicture.At(x, y))) { row = y; first = x; break; }
        Check(first > 3 && plainPicture.At(first - 3, row).A == 0, "found the left edge of a letter");
        Check(Red(outlined.At(first - 3, row)) && outlined.At(first - 9, row).A == 0, $"outline colour just outside the letter and no further than its width: {outlined.At(first - 3, row)} {outlined.At(first - 9, row)}");
        bool lettersKept = true;
        for (int y = 0; y < plainPicture.Height; y++) for (int x = 0; x < plainPicture.Width; x++) if (White(plainPicture.At(x, y)) && !White(outlined.At(x, y))) lettersKept = false;
        Check(lettersKept, "the letters are drawn over the outline, unchanged");
        Int32Rect ink = plainPicture.Inked(), outlinedInk = outlined.Inked();
        Check(Near(ink.X - outlinedInk.X, 5, 1.5) && Near(ink.Y - outlinedInk.Y, 5, 1.5) && Near(outlinedInk.Width - ink.Width, 10, 2.5) && Near(outlinedInk.Height - ink.Height, 10, 2.5), $"the outline reaches its width beyond the letters on every side: {ink} to {outlinedInk}");

        // A shadow: dark, part clear, down and to the right only.
        var shadowed = Draw(plain with { TextShadow = true }, region);
        var shadowInk = shadowed.Inked();
        double offset = DocumentRenderer.TextShadowOffset(plain);
        Check(Near(offset, 5.4) && DocumentRenderer.TextShadowOffset(plain with { FontSize = 10 }) == 1.5, "the shadow offset follows the letter size");
        Check(shadowed.Count(p => Dark(p) && p.A < 200) > 1500 && shadowed.Count(p => Dark(p) && p.A >= 200) == 0, "a part-clear dark shadow is drawn");
        Check(shadowInk.X + shadowInk.Width > ink.X + ink.Width + 4 && shadowInk.Y + shadowInk.Height > ink.Y + ink.Height + 4 && shadowInk.X >= ink.X - 1 && shadowInk.Y >= ink.Y - 1, $"the shadow falls down and right: {ink} to {shadowInk}");
        Check(shadowed.Count(White) == plainPicture.Count(White), "the letters stay on top of their shadow");
        // Soft: the edge of the shadow is fainter than its middle.
        Check(shadowed.Count(p => Dark(p) && p.A < 80) > 200 && shadowed.Count(p => Dark(p) && p.A > 120) > 200, "the shadow has a faint edge and a darker middle");

        // Both together, and on a callout (whose words are centred in its body).
        var both = Draw(plain with { OutlineColor = Colors.Red, OutlineWidth = 5, TextShadow = true }, region);
        Check(both.Count(Red) > 2000 && both.Count(White) == plainPicture.Count(White) && both.Inked().Width > outlinedInk.Width + 4, "outline and shadow together");
        var callout = new CalloutItem { Text = "HIM", FontSize = 90, Bold = true, Color = Colors.White, Background = Colors.DarkGreen, Padding = 30, Bounds = new Rect(100, 100, 400, 190) };
        Check(Draw(callout, region).Count(Red) == 0 && Draw(callout with { OutlineColor = Colors.Red, OutlineWidth = 5 }, region).Count(Red) > 2000, "a callout's words take the outline");
        Check(Draw(callout with { OutlineColor = Colors.Red }, region).Bytes.SequenceEqual(Draw(callout, region).Bytes), "a callout with no outline width is unchanged");

        // Scaling an item scales its outline with its letters.
        var scaled = (TextItem)VideoElements.Scale(plain with { OutlineWidth = 3 }, 2, new Point(0, 0));
        var scaledCallout = (CalloutItem)VideoElements.Scale(callout with { OutlineWidth = 3 }, 0.5, new Point(0, 0));
        Check(scaled.OutlineWidth == 6 && scaled.FontSize == 180 && scaledCallout.OutlineWidth == 1.5, "scaling an item scales its outline");
    }

    /// <summary>A text item drawn the way the renderer drew it before outlines and shadows existed.</summary>
    static BitmapSource Reference(TextItem item, Rect region)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-region.X, -region.Y));
            dc.PushTransform(new RotateTransform(item.Rotation, item.Center.X, item.Center.Y));
            dc.PushOpacity(Math.Clamp(item.Opacity, 0, 1));
            var bounds = item.Bounds;
            if (item.Shadow) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), null, Rect.Offset(bounds, new Vector(3, 3)));
            if (item.Background is { A: > 0 } background) dc.DrawRectangle(new SolidColorBrush(background), null, bounds);
            if (item.HasStroke) dc.DrawRectangle(null, new Pen(new SolidColorBrush(item.Stroke!.Value), item.StrokeWidth) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, bounds);
            var area = DocumentRenderer.TextArea(item);
            dc.PushClip(new RectangleGeometry(bounds));
            dc.DrawText(DocumentRenderer.FormatText(item, new SolidColorBrush(item.Color)), new Point(area.X, area.Y));
            dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
        }
        var bitmap = new RenderTargetBitmap((int)region.Width, (int)region.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    static void Extents()
    {
        var plain = Sample();
        var was = GraphicExport.Extent(plain, 1920, 1080);
        Check(was == new Rect(88, 88, 424, 214), "a plain text item's extent is what it was: " + was);
        Rect Grown(double by) { var r = was; r.Inflate(by, by); return r; }
        Check(GraphicExport.Extent(plain with { OutlineWidth = 6 }, 1920, 1080) == Grown(6), "the extent grows by the outline width");
        Check(GraphicExport.Extent(plain with { OutlineWidth = 6, OutlineColor = Colors.Transparent }, 1920, 1080) == was, "an outline that is not drawn adds nothing");
        double reach = DocumentRenderer.TextOverhang(plain with { TextShadow = true });
        Check(Near(reach, 8.1) && Near(DocumentRenderer.TextOverhang(plain with { TextShadow = true, OutlineWidth = 6 }), 14.1) && DocumentRenderer.TextOverhang(plain) == 0, "overhang is the outline plus the shadow's offset and soft edge");
        var shadowExtent = GraphicExport.Extent(plain with { TextShadow = true }, 1920, 1080);
        Check(shadowExtent.Contains(Grown(8)) && Grown(9).Contains(shadowExtent), "the extent grows by the shadow: " + shadowExtent);

        // Nothing is clipped even with no padding and a heavy outline: the picture's rim stays clear, and a far larger picture holds no more ink.
        var tight = new TextItem { Text = "MW", FontSize = 120, Bold = true, Color = Colors.White, Padding = 0, OutlineColor = Colors.Red, OutlineWidth = 14, TextShadow = true, Bounds = new Rect(300, 300, 10, 10) };
        tight = tight with { Bounds = new Rect(new Point(300, 300), DocumentRenderer.MeasureText(tight with { Bounds = default })) };
        var extent = GraphicExport.Extent(tight, 1920, 1080);
        var picture = Draw(tight, extent);
        bool rimClear = true;
        for (int x = 0; x < picture.Width; x++) if (picture.At(x, 0).A > 0 || picture.At(x, picture.Height - 1).A > 0) rimClear = false;
        for (int y = 0; y < picture.Height; y++) if (picture.At(0, y).A > 0 || picture.At(picture.Width - 1, y).A > 0) rimClear = false;
        var wide = extent; wide.Inflate(80, 80);
        var ink = picture.Inked();
        var tightPlain = Draw(tight with { OutlineWidth = 0, TextShadow = false }, extent).Inked();
        Check(rimClear && picture.Count(p => p.A > 0) == Draw(tight, wide).Count(p => p.A > 0), "the extent holds the whole outline and shadow");
        Check(ink.X < tightPlain.X - 10 && ink.X + ink.Width > tightPlain.X + tightPlain.Width + 14, $"the outline is drawn past a box with no padding: {tightPlain} to {ink}");
    }

    static void Captions()
    {
        var project = TimelineOps.ForSource(new MediaSource { Path = @"C:\media\a.mp4", Duration = 10, Width = 1920, Height = 1080, FrameRate = "30" });
        project = TimelineOps.AddCaption(project, null, 0, "Hello there", out var id);
        var lane = project.Tracks.Single(t => t.Kind == TrackKind.Caption);
        var clip = (CaptionClip)lane.Clips[0];

        // Existing captions keep their look: no outline unless the track's style asks for one.
        var plain = TimelineOps.CaptionItem(project, lane, clip);
        Check(new CaptionStyle().OutlineWidth == 0 && plain.OutlineWidth == 0 && !plain.HasOutline && !plain.TextShadow, "captions have no outline by default");
        var region = GraphicExport.Extent(plain, 1920, 1080);
        var bare = plain with { OutlineColor = Colors.Black, OutlineWidth = 0, TextShadow = false };
        Check(Draw(plain, region).Bytes.SequenceEqual(Read(Reference(bare, region)).Bytes), "a default caption renders as before");

        // The style's outline reaches the drawn item, without moving or resizing the caption.
        var styled = TimelineOps.SetTrack(project, lane with { CaptionStyle = TimelineOps.CaptionStyleOf(project, lane) with { Background = null, OutlineColor = Colors.Red, OutlineWidth = 5 } });
        var styledLane = styled.Tracks.Single(t => t.Kind == TrackKind.Caption);
        var item = TimelineOps.CaptionItem(styled, styledLane, clip);
        Check(item.HasOutline && item.OutlineColor == Colors.Red && item.OutlineWidth == 5 && item.Bounds == plain.Bounds && item.Background == null, "the track style's outline reaches the caption");
        var outlinedRegion = GraphicExport.Extent(item, 1920, 1080);
        Check(outlinedRegion.Width == region.Width + 10 && outlinedRegion.Height == region.Height + 10, "an outlined caption's export band grows with it");
        var picture = Draw(item, outlinedRegion);
        Check(picture.Count(Red) > 1500 && picture.Count(White) > 1000, "an outlined caption is drawn with its outline");
        Check(TimelineOps.CaptionItem(styled, styledLane with { CaptionStyle = styledLane.CaptionStyle! with { OutlineWidth = -3 } }, clip).OutlineWidth == 0, "a negative width is no outline");
        Check(TimelineOps.DefaultOutlineWidth(54) == 4 && TimelineOps.DefaultOutlineWidth(12) == 2, "the starting outline width suits the letters");
    }

    static void Picking()
    {
        var project = TimelineOps.ForSource(new MediaSource { Path = @"C:\media\a.mp4", Duration = 10, Width = 1920, Height = 1080, FrameRate = "30" });
        var videoId = project.Tracks[0].Clips[0].Id;
        // A box across the lower part of the picture, under where the caption is drawn.
        project = TimelineOps.InsertGraphic(project, new RectItem { Bounds = new Rect(200, 700, 1500, 360), Fill = Colors.Teal }, null, 0, out var boxId);
        project = TimelineOps.AddCaption(project, null, 0, "A caption", out var captionId);
        var history = new History<VideoProject>(project);
        var view = new VideoPreview { History = history };
        view.Measure(new Size(992, 572)); view.Arrange(new Rect(0, 0, 992, 572));
        void Refresh() => view.Show(history.Current, 0, false);
        Point At(double x, double y) => view.CanvasToScreen(new Point(x, y));
        Vibershot.Editing.Video.Track Lane() => history.Current.Tracks.Single(t => t.Kind == TrackKind.Caption);
        Refresh();
        var picks = new List<Guid?>(); view.ClipPicked += id => picks.Add(id);
        var edits = new List<Guid>(); view.TextEditRequested += id => edits.Add(id);
        var box = TimelineOps.CaptionItem(history.Current, Lane(), (CaptionClip)Lane().Clips[0]).Bounds;
        Point inside = At(box.X + box.Width / 2, box.Y + box.Height / 2), above = At(box.X + box.Width / 2, box.Y - 12), beside = At(box.Right + 12, box.Y + box.Height / 2);
        Check(box.Y > 712 && box.Right < 1688 && box.Bottom < 1060, "the caption sits over the box: " + box);
        Check(view.CaptionRect(captionId) is { } shown && Near(shown.X, At(box.X, 0).X, 0.01) && Near(shown.Width, box.Width * (At(1, 0).X - At(0, 0).X), 0.01) && view.CaptionRect(boxId) == null, "the caption's place on screen is known");

        // A click inside the drawn caption picks it; it is selected, with no handles and no drag.
        Check(!view.PointerDown(inside), "a caption is not dragged"); view.PointerMove(new Point(inside.X + 40, inside.Y + 40)); view.PointerUp();
        Check(picks.SequenceEqual(new Guid?[] { captionId }) && view.Selected == captionId && view.HandlePoint(BoxEdges.Right) == null && view.HandlePoint(BoxEdges.None) == null, "a click on a caption picks it, without handles");
        Check(ReferenceEquals(history.Current, project), "pressing and moving on a caption changes nothing");
        view.PointerDown(inside); view.PointerUp();
        Check(picks.Count == 1, "a second click on the selected caption does not pick again");

        // Just outside its box the graphic underneath takes the click; the caption wins only where it is drawn.
        view.PointerDown(above); view.PointerUp();
        Check(picks.Last() == boxId && view.Selected == boxId && view.HandlePoint(BoxEdges.Right) != null, "above the caption, the graphic under it is picked");
        view.PointerDown(beside); view.PointerUp(); view.PointerDown(inside); view.PointerUp();
        Check(picks.Last() == captionId && picks.Count == 3, "beside the caption is still the graphic; on it, the caption again");
        view.PointerDown(At(1000, 300)); view.PointerUp();
        Check(picks.Last() == videoId, "the video is picked where nothing covers it");

        // Double-click: the caption's words are asked for, only when it is the selected thing under the pointer.
        view.PointerDoubleClick(inside);
        Check(edits.Count == 0, "a double-click acts on the selected clip, not on a caption that is not selected");
        view.CropMode = false;
        view.PointerDown(inside); view.PointerUp();
        Check(!view.PointerDoubleClick(above) && edits.Count == 0, "a double-click beside the selected caption asks for nothing");
        Check(view.PointerDoubleClick(inside) && edits.SequenceEqual(new[] { captionId }), "a double-click on the selected caption asks to edit its words");

        // The selection is outlined on screen, hugging the caption's box.
        var selectedPicture = Snapshot(view);
        view.Selected = null;
        var idlePicture = Snapshot(view);
        var rect = view.CaptionRect(captionId)!.Value;
        int edgeX = (int)Math.Round(rect.X) - 1, midY = (int)Math.Round(rect.Y + rect.Height / 2), farX = (int)Math.Round(rect.X) - 8;
        Check(selectedPicture.At(edgeX, midY) != idlePicture.At(edgeX, midY) && selectedPicture.At(farX, midY) == idlePicture.At(farX, midY), "a selected caption is outlined at its edge");
        int insideX = (int)Math.Round(rect.X + rect.Width / 2);
        Check(selectedPicture.At(insideX, midY) == idlePicture.At(insideX, midY), "the outline does not cover the caption");

        // A locked caption track still selects, but its words cannot be edited here; a hidden one is not there to click.
        history.Push(TimelineOps.SetTrack(history.Current, Lane() with { Locked = true })); Refresh();
        view.PointerDown(inside); view.PointerUp();
        Check(view.Selected == captionId && !view.PointerDoubleClick(inside) && edits.Count == 1, "a locked caption is picked but not edited");
        history.Push(TimelineOps.SetTrack(history.Current, Lane() with { Locked = false, Hidden = true })); Refresh();
        view.PointerDown(inside); view.PointerUp();
        Check(view.Selected == boxId && view.CaptionRect(captionId) == null, "a hidden caption lets the click through");

        // Captions at the top are picked where they are drawn, and a caption above another track's caption wins.
        history.Push(TimelineOps.SetTrack(history.Current, Lane() with { Hidden = false, CaptionStyle = TimelineOps.CaptionStyleOf(history.Current, Lane()) with { AtTop = true } })); Refresh();
        view.PointerDown(inside); view.PointerUp();
        Check(view.Selected == boxId, "the caption has moved away from the bottom");
        var topBox = TimelineOps.CaptionItem(history.Current, Lane(), (CaptionClip)Lane().Clips[0]).Bounds;
        view.PointerDown(At(topBox.X + topBox.Width / 2, topBox.Y + topBox.Height / 2)); view.PointerUp();
        Check(view.Selected == captionId, "a caption at the top is picked there");
    }

    /// <summary>The preview as it draws itself, without a window.</summary>
    static Picture Snapshot(VideoPreview view)
    {
        view.InvalidateVisual();
        view.Arrange(new Rect(0, 0, view.ActualWidth, view.ActualHeight));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        return Read(bitmap);
    }
}
