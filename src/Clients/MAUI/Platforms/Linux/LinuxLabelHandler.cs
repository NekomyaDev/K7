using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace K7.Clients.MAUI.Platforms.Linux;

/// <summary>
/// Label styling on GTK. The labs <c>LabelHandler</c> builds one CSS block per widget from all
/// its mappers. A single value GTK rejects drops the whole block (the subtitle label then
/// renders with the theme's default size and colour), and <c>Shadow</c> becomes a
/// <c>box-shadow</c> halo around the box instead of a glyph shadow. This provider re-emits the
/// text style (colour, font, background) in its own self-contained block, added after the labs
/// one so it wins, and maps <c>Shadow</c> to a <c>text-shadow</c> with a thin outline.
/// </summary>
internal static class LinuxLabelTextShadow
{
    private static readonly ConditionalWeakTable<Gtk.Widget, Gtk.CssProvider> _providers = new();

    public static void Install()
    {
        LabelHandler.Mapper.ReplaceMapping<ILabel, LabelHandler>(nameof(IView.Shadow), (handler, label) => Apply(handler.PlatformView, label));
        LabelHandler.Mapper.AppendToMapping(nameof(ILabel.Font), (handler, label) => Apply(handler.PlatformView, label));
        LabelHandler.Mapper.AppendToMapping(nameof(ILabel.TextColor), (handler, label) => Apply(handler.PlatformView, label));
        LabelHandler.Mapper.AppendToMapping(nameof(IView.Background), (handler, label) => Apply(handler.PlatformView, label));
    }

    private static void Apply(Gtk.Widget? widget, ILabel label)
    {
        if (widget is null)
            return;

        var provider = _providers.GetValue(widget, w =>
        {
            var created = Gtk.CssProvider.New();
            w.GetStyleContext().AddProvider(created, Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION);
            return created;
        });

        var css = new StringBuilder("* { ");
        if (label.TextColor is { } textColor)
            css.Append("color: ").Append(Rgba(textColor, 1)).Append("; ");

        var font = label.Font;
        if (font.Size > 0 && !double.IsNaN(font.Size))
            css.Append(FormattableString.Invariant($"font-size: {font.Size:0.##}px; "));

        var family = FirstFamily(font.Family);
        if (family is not null)
            css.Append("font-family: \"").Append(family).Append("\"; ");

        if (((IView)label).Background is SolidPaint { Color: { } background })
            css.Append("background-color: ").Append(Rgba(background, 1)).Append("; background-image: none; ");

        var shadow = label.Shadow;
        if (shadow is not null && shadow.Opacity > 0 && shadow.Paint is SolidPaint { Color: { } shadowColor })
        {
            var rgba = Rgba(shadowColor, shadow.Opacity);
            var ox = shadow.Offset.X.ToString("0.#", CultureInfo.InvariantCulture);
            var oy = shadow.Offset.Y.ToString("0.#", CultureInfo.InvariantCulture);
            var blur = Math.Max(0, shadow.Radius).ToString("0.#", CultureInfo.InvariantCulture);
            // Offset shadow plus a thin four-way outline: readable on bright and dark video.
            css.Append($"text-shadow: {ox}px {oy}px {blur}px {rgba}, 1px 0 1px {rgba}, -1px 0 1px {rgba}, 0 1px 1px {rgba}, 0 -1px 1px {rgba}; ");
        }

        css.Append('}');
        try
        {
            provider.LoadFromString(css.ToString());
            // The frame MAUI arranged may predate this CSS (first layout with the theme font):
            // ask for a new measure so the text fits the font it is actually drawn with.
            label.InvalidateMeasure();
        }
        catch (Exception ex)
        {
            Playback.VlcPlayerLog.Warn("gtk label css " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static string Rgba(Color color, double opacity) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"rgba({(int)(color.Red * 255)},{(int)(color.Green * 255)},{(int)(color.Blue * 255)},{color.Alpha * opacity:0.###})");

    /// <summary>GTK takes one family per declaration: first entry of a CSS-style list, quotes removed.</summary>
    private static string? FirstFamily(string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
            return null;

        var first = family.Split(',')[0].Trim().Trim('"', '\'');
        return first.Length == 0 ? null : first.Replace("\"", string.Empty);
    }
}
