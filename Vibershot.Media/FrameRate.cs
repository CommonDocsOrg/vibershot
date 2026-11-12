using System.Globalization;

namespace Vibershot.Media;

/// <summary>
/// A video frame rate as an exact rational (30000/1001 for 29.97) so NTSC rates survive
/// a round trip through settings and ffmpeg arguments without drifting.
/// </summary>
public readonly record struct FrameRate
{
    public int Numerator { get; }
    public int Denominator { get; }

    public FrameRate(int numerator, int denominator)
    {
        if (numerator <= 0) throw new ArgumentOutOfRangeException(nameof(numerator), "Frame rate must be positive.");
        if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator), "Denominator must be positive.");
        int g = Gcd(numerator, denominator);
        Numerator = numerator / g;
        Denominator = denominator / g;
    }

    public static readonly FrameRate Fps24 = new(24, 1);
    public static readonly FrameRate Fps25 = new(25, 1);
    public static readonly FrameRate Ntsc30 = new(30000, 1001);
    public static readonly FrameRate Fps30 = new(30, 1);
    public static readonly FrameRate Fps50 = new(50, 1);
    public static readonly FrameRate Ntsc60 = new(60000, 1001);
    public static readonly FrameRate Fps60 = new(60, 1);

    /// <summary>The rates offered in Settings, in display order.</summary>
    public static IReadOnlyList<FrameRate> Presets { get; } = new[] { Fps24, Fps25, Ntsc30, Fps30, Fps50, Ntsc60, Fps60 };

    public double Value => (double)Numerator / Denominator;

    /// <summary>Whole frames per second, for keyframe intervals and UI rounding (29.97 → 30).</summary>
    public int Rounded => (int)Math.Round(Value, MidpointRounding.AwayFromZero);

    public TimeSpan FrameDuration => TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond * (double)Denominator / Numerator));

    /// <summary>Number of frames that fit in <paramref name="duration"/> at this rate.</summary>
    public long FramesIn(TimeSpan duration) => (long)Math.Floor(duration.TotalSeconds * Value + 1e-9);

    /// <summary>Exact form for ffmpeg: "30" or "30000/1001".</summary>
    public string ToFfmpegString() => Denominator == 1
        ? Numerator.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");

    /// <summary>Short human form: "30", "29.97", "59.94", "23.98".</summary>
    public override string ToString() => Denominator == 1
        ? Numerator.ToString(CultureInfo.InvariantCulture)
        : Value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Accepts "30", "30000/1001", "29.97", "59.94", "23.976" and other decimals. Decimal input
    /// snaps to the matching NTSC preset when within 0.01 fps; anything else becomes an exact
    /// rational over 1000.
    /// </summary>
    public static bool TryParse(string? text, out FrameRate rate)
    {
        rate = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        int slash = text.IndexOf('/');
        if (slash > 0)
        {
            if (int.TryParse(text[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) &&
                int.TryParse(text[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) &&
                n > 0 && d > 0)
            {
                rate = new FrameRate(n, d);
                return true;
            }
            return false;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0 || value > 1000)
        {
            return false;
        }
        foreach (var preset in Presets)
        {
            if (Math.Abs(preset.Value - value) < 0.01)
            {
                rate = preset;
                return true;
            }
        }
        if (Math.Abs(value - Math.Round(value)) < 1e-9)
        {
            rate = new FrameRate((int)Math.Round(value), 1);
            return true;
        }
        rate = new FrameRate((int)Math.Round(value * 1000), 1000);
        return true;
    }

    public static FrameRate Parse(string text) =>
        TryParse(text, out var rate) ? rate : throw new FormatException($"'{text}' is not a frame rate.");

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
