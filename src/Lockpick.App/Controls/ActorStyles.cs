using Avalonia.Media;
using Lockpick.Core.State;

namespace Lockpick.App.Controls;

/// <summary>Marker shape of an enemy or person on the map.</summary>
public enum ActorGlyph
{
    Circle,
    Triangle,
    Diamond,
    Square,
    Hexagon,
    Star,
}

/// <summary>One look per family of enemy type (RE1 ids; names.json), shared by the map and its legend.</summary>
public static class ActorStyles
{
    public static (ActorGlyph Shape, Color Color, string Letter, string Family) For(LiveActor actor) => actor.Kind switch
    {
        ActorKind.Npc => (ActorGlyph.Square, Color.Parse("#4FA3FF"), actor.Name.Length > 0 ? actor.Name[..1] : "?", "People"),
        ActorKind.Boss => (ActorGlyph.Star, Color.Parse("#E040FB"), "B", "Boss"),
        _ => actor.Type switch
        {
            0 or 1 or 17 => (ActorGlyph.Circle, Color.Parse("#7CB342"), "Z", "Zombie (Z)"),
            2 => (ActorGlyph.Triangle, Color.Parse("#FF8A3D"), "D", "Dog (D)"),
            3 => (ActorGlyph.Hexagon, Color.Parse("#B388FF"), "S", "Spider (S)"),
            5 => (ActorGlyph.Diamond, Color.Parse("#B0BEC5"), "C", "Crow (C)"),
            6 => (ActorGlyph.Triangle, Color.Parse("#FF5252"), "H", "Hunter (H)"),
            7 => (ActorGlyph.Diamond, Color.Parse("#FFD740"), "W", "Wasp (W)"),
            9 => (ActorGlyph.Hexagon, Color.Parse("#26C6DA"), "C", "Chimera (C)"),
            10 => (ActorGlyph.Circle, Color.Parse("#C6FF00"), "S", "Snake (S)"),
            _ => (ActorGlyph.Circle, Color.Parse("#9E9E9E"), "?", "Plants, webs, other"),
        },
    };

    /// <summary>A 12×12 icon of the shape, for the legend.</summary>
    public static Geometry Icon(ActorGlyph glyph) => StreamGeometry.Parse(glyph switch
    {
        ActorGlyph.Triangle => "M6,0 L12,11 L0,11 Z",
        ActorGlyph.Diamond => "M6,0 L12,6 L6,12 L0,6 Z",
        ActorGlyph.Square => "M0,0 L12,0 L12,12 L0,12 Z",
        ActorGlyph.Hexagon => "M3,0 L9,0 L12,6 L9,12 L3,12 L0,6 Z",
        ActorGlyph.Star => "M6,0 L7.4,4.1 L11.7,4.1 L8.2,6.7 L9.5,10.8 L6,8.3 L2.5,10.8 L3.8,6.7 L0.3,4.1 L4.6,4.1 Z",
        _ => "M0,6 A6,6 0 1 1 12,6 A6,6 0 1 1 0,6 Z",
    });
}
