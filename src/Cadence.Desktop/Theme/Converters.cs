using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Cadence.Desktop.Theme;

/// <summary>Maps a track's colour index to its palette brush.</summary>
public sealed class TrackBrushConverter : IValueConverter
{
    public static readonly TrackBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(Palette.Track(value is int index ? index : 0));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"TRACK" or "TRACKS" for the inspector header.</summary>
public sealed class SelectionHeaderConverter : IValueConverter
{
    public static readonly SelectionHeaderConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "TRACKS" : "TRACK";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DeleteLabelConverter : IValueConverter
{
    public static readonly DeleteLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "Delete Tracks" : "Delete Track";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A track colour mixed into the panel background, for icon tiles and subtle fills.</summary>
public sealed class TrackTintConverter : IValueConverter
{
    public static readonly TrackTintConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var amount = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 0.3;
        return new SolidColorBrush(Palette.Mix(Palette.Panel, Palette.Track(value is int index ? index : 0), amount));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when an enum value equals the parameter (by name), e.g. for tabs bound to an enum.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
