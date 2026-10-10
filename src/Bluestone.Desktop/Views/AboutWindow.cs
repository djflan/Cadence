using System.Reflection;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cadence.Desktop.Views;

/// <summary>About Cadence: version, licence, and the non-affiliation notice.</summary>
internal sealed class AboutWindow : Window
{
    public const string NonAffiliationNotice =
        "Cadence is an independent open-source project and is not affiliated with, authorized, sponsored, or endorsed by Yamaha Corporation. " +
        "Yamaha, XG, QY100, and other product names and trademarks belong to their respective owners and are used solely to describe compatibility.";

    public AboutWindow()
    {
        Title = "About Cadence";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var version = typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development build";
        var close = new Button { Content = "OK", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Classes.Add("primary");
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(28),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Cadence", FontSize = 22, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"Version {version.Split('+')[0]}", Classes = { "secondary" } },
                new TextBlock { Text = "A hardware-oriented MIDI workstation. MIT licensed.", Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = NonAffiliationNotice, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                close,
            },
        };
    }
}
