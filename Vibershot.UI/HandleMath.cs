using System.Windows.Media;

namespace Vibershot.UI;

/// <summary>Which edges of a box a resize handle drags.</summary>
[Flags]
public enum BoxEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>
/// Geometry shared by the image editor and the video editor for boxes that are moved, resized
/// and turned with handles.
/// </summary>
public static class HandleMath
{
    /// <summary>
    /// Moves the given edges of a rectangle by a vector. With <paramref name="keepAspect"/> a
    /// corner drag keeps the rectangle's shape, following the horizontal movement.
    /// </summary>
    public static Rect Resize(Rect r, BoxEdges edges, Vector d, bool keepAspect)
    {
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        bool l = edges.HasFlag(BoxEdges.Left), rt = edges.HasFlag(BoxEdges.Right), t = edges.HasFlag(BoxEdges.Top), b = edges.HasFlag(BoxEdges.Bottom);
        if (l) left += d.X;
        if (rt) right += d.X;
        if (t) top += d.Y;
        if (b) bottom += d.Y;
        if (keepAspect && r.Width > 0 && r.Height > 0 && (l || rt) && (t || b))
        {
            double height = Math.Abs(right - left) / (r.Width / r.Height) * (bottom >= top ? 1 : -1);
            if (t) top = bottom - height; else bottom = top + height;
        }
        return new Rect(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top));
    }

    /// <summary>
    /// Resizes a box that is turned about its centre, in its own frame, so it grows along its
    /// edges and the opposite edge stays where it is on screen.
    /// </summary>
    public static Rect ResizeRotated(Rect bounds, double rotation, BoxEdges edges, Point start, Point current, bool keepAspect)
    {
        var c = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var inverse = new RotateTransform(-rotation, c.X, c.Y);
        var local = Resize(bounds, edges, inverse.Transform(current) - inverse.Transform(start), keepAspect);
        if (rotation == 0) return local;
        var centre = new RotateTransform(rotation, c.X, c.Y).Transform(new Point(local.X + local.Width / 2, local.Y + local.Height / 2));
        return new Rect(centre.X - local.Width / 2, centre.Y - local.Height / 2, local.Width, local.Height);
    }

    /// <summary>The angle, in degrees clockwise from straight up, of a pointer around a centre; snapped to 15° steps on request.</summary>
    public static double AngleTo(Point centre, Point pointer, bool snap)
    {
        double angle = Math.Atan2(pointer.Y - centre.Y, pointer.X - centre.X) * 180 / Math.PI + 90;
        if (snap) angle = Math.Round(angle / 15) * 15;
        angle %= 360;
        if (angle > 180) angle -= 360;
        if (angle <= -180) angle += 360;
        return angle;
    }
}
