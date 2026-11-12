namespace Vibershot.Media;

/// <summary>A rectangle in physical screen pixels (virtual desktop coordinates).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public PixelRect Intersect(PixelRect other)
    {
        int x1 = Math.Max(X, other.X), y1 = Math.Max(Y, other.Y);
        int x2 = Math.Min(Right, other.Right), y2 = Math.Min(Bottom, other.Bottom);
        return x2 <= x1 || y2 <= y1 ? new PixelRect(x1, y1, 0, 0) : new PixelRect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>
    /// Rounds width and height down to even numbers, as yuv420p needs. A 1-pixel region
    /// still becomes 2 so the encoder never sees a zero dimension.
    /// </summary>
    public PixelRect ToEven()
    {
        int w = Math.Max(2, Width & ~1), h = Math.Max(2, Height & ~1);
        return new PixelRect(X, Y, w, h);
    }
}
