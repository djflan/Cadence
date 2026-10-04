using Avalonia.Media;

namespace Cadence.Desktop.Theme;

/// <summary>Colours used by custom-drawn controls. Kept in step with Theme/Cadence.axaml.</summary>
internal static class Palette
{
    public static readonly Color Base = Color.Parse("#0E1014");
    public static readonly Color Panel = Color.Parse("#151820");
    public static readonly Color LaneAlt = Color.Parse("#181C25");
    public static readonly Color LaneSelected = Color.Parse("#1F2533");
    public static readonly Color GridBar = Color.Parse("#2C3240");
    public static readonly Color GridBeat = Color.Parse("#1F2430");
    public static readonly Color TextSecondary = Color.Parse("#A3ABBA");
    public static readonly Color TextMuted = Color.Parse("#6D7586");
    public static readonly Color Accent = Color.Parse("#3DD6C4");
    public static readonly Color Loop = Color.Parse("#3DD6C4");

    /// <summary>Track colours; status is never conveyed by these alone.</summary>
    public static readonly Color[] Tracks =
    [
        Color.Parse("#3DD6C4"),
        Color.Parse("#7C9CFF"),
        Color.Parse("#F7A35C"),
        Color.Parse("#E879B9"),
        Color.Parse("#9ED36A"),
        Color.Parse("#5CC8F7"),
        Color.Parse("#F2D45C"),
        Color.Parse("#B794F6"),
    ];

    public static Color Track(int index) => Tracks[((index % Tracks.Length) + Tracks.Length) % Tracks.Length];
}
