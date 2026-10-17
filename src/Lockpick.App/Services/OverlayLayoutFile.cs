using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lockpick.App.Services;

/// <summary>
/// An overlay layout to save and share (Settings → Overlay → Layout): each widget's corner or spot and size, the look,
/// and the map's options — nothing else from the settings (no paths, tokens or accounts). Loading one checks every
/// value: unknown widgets, corners and views are dropped and numbers are kept in range.
/// </summary>
public static class OverlayLayoutFile
{
    public const string Extension = ".lockpick-layout.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(string path, AppSettings settings)
    {
        var layout = new LayoutData(
            1,
            OverlayLayout.Widgets.ToDictionary(w => w.Id, w => OverlayLayout.Corner(settings, w.Id)),
            settings.OverlayWidgetSpots.ToDictionary(p => p.Key, p => new SpotData(p.Value.X, p.Value.Y)),
            OverlayLayout.Widgets.ToDictionary(w => w.Id, w => settings.OverlayWidgetSizes.GetValueOrDefault(w.Id, 100)),
            settings.OverlayLook,
            settings.MapOverlay);
        File.WriteAllText(path, JsonSerializer.Serialize(layout, Json));
    }

    /// <summary>The settings with the layout in <paramref name="path"/> applied. Throws InvalidDataException for a bad file.</summary>
    public static AppSettings Apply(string path, AppSettings settings)
    {
        LayoutData? layout;
        try
        {
            layout = JsonSerializer.Deserialize<LayoutData>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a Lockpick layout ({ex.Message}).", ex);
        }

        if (layout is not { Version: 1 })
        {
            throw new InvalidDataException("Not a Lockpick layout (or one from a newer Lockpick).");
        }

        var widgets = OverlayLayout.Widgets.Select(w => w.Id).ToHashSet();
        var corners = (layout.Corners ?? []).Where(p => widgets.Contains(p.Key) && OverlayLayout.Choices.Contains(p.Value))
            .ToDictionary(p => p.Key, p => p.Value);
        var spots = (layout.Spots ?? []).Where(p => widgets.Contains(p.Key))
            .ToDictionary(p => p.Key, p => new WidgetSpot(Math.Clamp(p.Value.X, 0, 1), Math.Clamp(p.Value.Y, 0, 1)));
        var sizes = (layout.Sizes ?? []).Where(p => widgets.Contains(p.Key))
            .ToDictionary(p => p.Key, p => Math.Clamp(p.Value, 25, 400));
        var look = layout.Look is { } l
            ? l with
            {
                BackgroundOpacity = Math.Clamp(l.BackgroundOpacity, 0, 100),
                TextColor = OverlayLook.TextColors.Any(c => c.Id == l.TextColor) ? l.TextColor : "default",
                Font = OverlayLook.Fonts.Any(f => f.Id == l.Font) ? l.Font : "default",
            }
            : settings.OverlayLook;
        var map = layout.Map is { } m
            ? m with
            {
                Views = [.. (m.Views ?? []).Where(v => MapViews.All.Any(a => a.Id == v))],
                Reach = Math.Clamp(m.Reach, 1000, 30000),
                Size = Math.Clamp(m.Size, 60, 400),
                SeeThroughOpacity = Math.Clamp(m.SeeThroughOpacity, 10, 100),
            }
            : settings.MapOverlay;
        return settings with
        {
            OverlayWidgetCorners = corners,
            OverlayWidgetSpots = spots,
            OverlayWidgetSizes = sizes,
            OverlayLook = look,
            MapOverlay = map,
        };
    }

    private sealed record SpotData(double X, double Y);

    private sealed record LayoutData(
        int Version,
        Dictionary<string, string>? Corners,
        Dictionary<string, SpotData>? Spots,
        Dictionary<string, int>? Sizes,
        OverlayLook? Look,
        MapOverlaySettings? Map);
}
