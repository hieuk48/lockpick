using Lockpick.Core.State;

namespace Lockpick.Core.Maps;

/// <summary>Shape of a collision record in room coordinates (X right, Z up on the map).</summary>
public enum MapShapeKind
{
    Rectangle,
    Circle,
}

/// <summary>A collision record: walls and furniture block movement; soft zones (floors, steps) don't.</summary>
public sealed record MapShape(MapShapeKind Kind, int X0, int Z0, int X1, int Z1, bool Blocks);

/// <summary>A door zone in this room and the room it leads to (adapter-specific id, e.g. "106").</summary>
public sealed record MapDoor(int X, int Z, int Width, int Depth, string Target);

/// <summary>An item pickup zone placed by the room's own data for this character; <paramref name="Flag"/> is the
/// adapter's pickup flag (−1 when none).</summary>
public sealed record MapItem(int X, int Z, int Width, int Depth, int ItemId, string Name, int Flag = -1);

/// <summary>A spot where an item is used and stays (e.g. an emblem slot): the item shows there once it's in place.</summary>
public sealed record MapSocket(int X, int Z, int Width, int Depth, int ItemId, string Name);

/// <summary>A save-room fixture the player uses.</summary>
public enum FixtureKind
{
    ItemBox,
    Typewriter,
}

/// <summary>Where the player uses an item box or a typewriter (the zone the action key tests).</summary>
public sealed record MapFixture(int X, int Z, int Width, int Depth, FixtureKind Kind);

/// <summary>A fixed camera: where it stands and the point it looks at.</summary>
public sealed record MapCamera(int Id, int X, int Z, int TargetX, int TargetZ);

/// <summary>
/// One room's static layout, read from the player's own game files by the adapter. Game-agnostic: coordinates are the
/// game's room units, X to the right and Z up on a top-down map.
/// </summary>
public sealed record RoomMap(
    string RoomId,
    IReadOnlyList<MapShape> Shapes,
    IReadOnlyList<MapDoor> Doors,
    IReadOnlyList<MapItem> Items,
    IReadOnlyList<MapCamera> Cameras,
    IReadOnlyList<MapSocket> Sockets,
    IReadOnlyList<MapFixture> Fixtures)
{
    /// <summary>Bounds of the walls and doors (very large shapes, e.g. whole-room floor zones, are ignored).</summary>
    public (int X0, int Z0, int X1, int Z1) Bounds
    {
        get
        {
            int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
            void Add(int ax, int az, int bx, int bz)
            {
                x0 = Math.Min(x0, ax);
                z0 = Math.Min(z0, az);
                x1 = Math.Max(x1, bx);
                z1 = Math.Max(z1, bz);
            }

            foreach (var s in Shapes)
            {
                if (s.X1 - s.X0 < 60000 && s.Z1 - s.Z0 < 60000)
                {
                    Add(s.X0, s.Z0, s.X1, s.Z1);
                }
            }

            foreach (var d in Doors)
            {
                Add(d.X, d.Z, d.X + d.Width, d.Z + d.Depth);
            }

            return x0 == int.MaxValue ? (0, 0, 1, 1) : (x0, z0, x1, z1);
        }
    }
}

/// <summary>Loads room maps from the player's install; implemented by game adapters that can read their room files.</summary>
public interface IRoomMapSource
{
    /// <summary>The map of <paramref name="room"/> as <paramref name="character"/> sees it, or null when unavailable.</summary>
    RoomMap? Load(RoomLocation room, PlayableCharacter character);

    /// <summary>True when the player has already taken <paramref name="item"/> in this game; null when unknown.</summary>
    bool? IsTaken(MapItem item, GameSnapshot snapshot) => null;

    /// <summary>True when the socket's item has been put in place in this game; null when unknown.</summary>
    bool? IsFilled(RoomMap map, MapSocket socket, GameSnapshot snapshot) => null;
}
