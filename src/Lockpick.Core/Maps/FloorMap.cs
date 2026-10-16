namespace Lockpick.Core.Maps;

/// <summary>A room placed on its floor's map: its layout, its name, and where its origin sits in floor units.</summary>
public sealed record FloorRoom(RoomMap Map, string? Name, double OffsetX, double OffsetZ)
{
    /// <summary>The room's walls and doors in floor units.</summary>
    public (double X0, double Z0, double X1, double Z1) Bounds
    {
        get
        {
            var (x0, z0, x1, z1) = Map.Bounds;
            return (x0 + OffsetX, z0 + OffsetZ, x1 + OffsetX, z1 + OffsetZ);
        }
    }
}

/// <summary>Every room of a stage, placed together (rooms not linked to the rest sit beside it).</summary>
public sealed record FloorMap(int Stage, IReadOnlyList<FloorRoom> Rooms)
{
    public (double X0, double Z0, double X1, double Z1) Bounds =>
        Rooms.Count == 0
            ? (0, 0, 1, 1)
            : (Rooms.Min(r => r.Bounds.X0), Rooms.Min(r => r.Bounds.Z0), Rooms.Max(r => r.Bounds.X1), Rooms.Max(r => r.Bounds.Z1));

    public FloorRoom? Find(string roomId) => Rooms.FirstOrDefault(r => r.Map.RoomId == roomId);
}

/// <summary>
/// Places a stage's rooms from their doors (tools/rdt/floor_map.py): a door's zone centre in room A and its arrival
/// spot in room B are about the same place, so B's origin sits at zone − arrival in A's units. A breadth-first walk
/// gives a first placement and a least-squares pass (each room moves to the mean of what its links say) spreads the
/// remaining error. Rooms share one orientation; positions are good to about a door step (stage 1's loops agreed to a
/// median of 750 units, 2026-10-14). Groups of rooms with no door between them are laid out side by side.
/// </summary>
public static class FloorLayout
{
    /// <summary>Space between groups of rooms with no door between them.</summary>
    private const double GroupGap = 6000;

    private const int Iterations = 200;

    public static FloorMap Build(int stage, IReadOnlyDictionary<string, RoomMap> rooms, Func<string, string?>? names = null)
    {
        var adjacent = new Dictionary<string, List<(string Room, double X, double Z)>>();
        foreach (var (id, map) in rooms)
        {
            foreach (var door in map.Doors)
            {
                if (door.Target == id || !rooms.ContainsKey(door.Target) || door.ArrivalX is not { } ax || door.ArrivalZ is not { } az)
                {
                    continue;
                }

                // The neighbour's origin relative to this room's.
                var dx = door.X + (door.Width / 2.0) - ax;
                var dz = door.Z + (door.Depth / 2.0) - az;
                Add(adjacent, id, door.Target, dx, dz);
                Add(adjacent, door.Target, id, -dx, -dz);
            }
        }

        var placed = new List<FloorRoom>();
        var remaining = new SortedSet<string>(rooms.Keys, StringComparer.Ordinal);
        var nextX = 0.0;
        while (remaining.Count > 0)
        {
            var group = Solve(remaining.Min!, adjacent);
            var (x0, z0, _, _) = GroupBounds(group, rooms);
            var shiftX = nextX - x0;
            var shiftZ = -z0;
            foreach (var (id, (x, z)) in group)
            {
                placed.Add(new FloorRoom(rooms[id], names?.Invoke(id), x + shiftX, z + shiftZ));
                remaining.Remove(id);
            }

            nextX = placed.Max(r => r.Bounds.X1) + GroupGap;
        }

        return new FloorMap(stage, placed);
    }

    private static void Add(Dictionary<string, List<(string, double, double)>> adjacent, string from, string to, double dx, double dz)
    {
        if (!adjacent.TryGetValue(from, out var list))
        {
            adjacent[from] = list = [];
        }

        list.Add((to, dx, dz));
    }

    private static Dictionary<string, (double X, double Z)> Solve(string root, Dictionary<string, List<(string Room, double X, double Z)>> adjacent)
    {
        var position = new Dictionary<string, (double X, double Z)> { [root] = (0, 0) };
        var queue = new Queue<string>([root]);
        while (queue.Count > 0)
        {
            var room = queue.Dequeue();
            foreach (var (next, dx, dz) in adjacent.GetValueOrDefault(room) ?? [])
            {
                if (position.TryAdd(next, (position[room].X + dx, position[room].Z + dz)))
                {
                    queue.Enqueue(next);
                }
            }
        }

        for (var i = 0; i < Iterations; i++)
        {
            foreach (var room in position.Keys.Where(r => r != root).ToList())
            {
                // Each link says: this room = neighbour − (neighbour relative to this room).
                var votes = (adjacent.GetValueOrDefault(room) ?? [])
                    .Where(l => position.ContainsKey(l.Room))
                    .Select(l => (X: position[l.Room].X - l.X, Z: position[l.Room].Z - l.Z))
                    .ToList();
                if (votes.Count > 0)
                {
                    position[room] = (votes.Average(v => v.X), votes.Average(v => v.Z));
                }
            }
        }

        return position;
    }

    private static (double X0, double Z0, double X1, double Z1) GroupBounds(Dictionary<string, (double X, double Z)> group, IReadOnlyDictionary<string, RoomMap> rooms)
    {
        var boxes = group.Select(g => new FloorRoom(rooms[g.Key], null, g.Value.X, g.Value.Z).Bounds).ToList();
        return (boxes.Min(b => b.X0), boxes.Min(b => b.Z0), boxes.Max(b => b.X1), boxes.Max(b => b.Z1));
    }
}
