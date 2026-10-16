using Lockpick.Core.Maps;

namespace Lockpick.Tests.Core;

public sealed class FloorLayoutTests
{
    private static RoomMap Room(string id, params MapDoor[] doors) =>
        new(id, [new MapShape(MapShapeKind.Rectangle, -500, -500, 500, 500, true)], doors, [], [], [], []);

    [Fact]
    public void A_door_places_the_next_room_where_the_player_arrives()
    {
        var rooms = new Dictionary<string, RoomMap>
        {
            // The door's zone centre in 100 (1100, 100) is the arrival spot in 101 (0, 0).
            ["100"] = Room("100", new MapDoor(1000, 0, 200, 200, "101", 0, 0)),
            ["101"] = Room("101", new MapDoor(-100, -100, 200, 200, "100", 1100, 100)),
        };
        var floor = FloorLayout.Build(1, rooms);

        var a = floor.Find("100")!;
        var b = floor.Find("101")!;
        Assert.Equal(1100, b.OffsetX - a.OffsetX, 3);
        Assert.Equal(100, b.OffsetZ - a.OffsetZ, 3);
    }

    [Fact]
    public void Disagreeing_links_meet_in_the_middle()
    {
        var rooms = new Dictionary<string, RoomMap>
        {
            ["100"] = Room("100", new MapDoor(1000, 0, 0, 0, "101", 0, 0)),
            ["101"] = Room("101", new MapDoor(0, 0, 0, 0, "100", 1200, 0)), // says 1200, the other door says 1000
        };
        var floor = FloorLayout.Build(1, rooms);
        Assert.Equal(1100, floor.Find("101")!.OffsetX - floor.Find("100")!.OffsetX, 3);
    }

    [Fact]
    public void Rooms_with_no_door_between_them_sit_side_by_side()
    {
        var rooms = new Dictionary<string, RoomMap>
        {
            ["100"] = Room("100"),
            ["105"] = Room("105"),
        };
        var floor = FloorLayout.Build(1, rooms, id => id == "105" ? "Dining room" : null);

        Assert.Equal(2, floor.Rooms.Count);
        Assert.True(floor.Find("105")!.Bounds.X0 >= floor.Find("100")!.Bounds.X1);
        Assert.Equal("Dining room", floor.Find("105")!.Name);
    }

    [Fact]
    public void Doors_to_other_stages_and_without_an_arrival_are_ignored()
    {
        var rooms = new Dictionary<string, RoomMap>
        {
            ["100"] = Room("100", new MapDoor(0, 0, 10, 10, "200", 5, 5), new MapDoor(0, 0, 10, 10, "101")),
            ["101"] = Room("101"),
        };
        var floor = FloorLayout.Build(1, rooms);
        Assert.Equal(2, floor.Rooms.Count); // placed apart, not linked
    }
}
