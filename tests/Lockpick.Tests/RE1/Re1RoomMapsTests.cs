using System.Buffers.Binary;
using Lockpick.Core.Maps;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;

namespace Lockpick.Tests.RE1;

/// <summary>Room-file parsing on a hand-built room (no game files in tests): cameras, collision, doors, items.</summary>
public class Re1RoomMapsTests
{
    private static readonly RoomLocation Room105 = new(1, 0x05);

    private static byte[] BuildRoom()
    {
        var data = new byte[0x400];
        data[1] = 1; // one camera
        void I32(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(o), v);
        void U16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(o), (ushort)v);

        // Camera 0 at (1000, 2000) looking at (3000, 4000).
        I32(0x94 + 0x08, 1000);
        I32(0x94 + 0x10, 2000);
        I32(0x94 + 0x14, 3000);
        I32(0x94 + 0x1C, 4000);

        // Collision block at 0x100: two records in the first quadrant.
        I32(0x4C, 0x100);
        I32(0x104, 2);
        var r = 0x118;
        U16(r, 5000); U16(r + 2, 6000); U16(r + 4, 1000); U16(r + 6, 2000); U16(r + 8, 1); U16(r + 10, 0x300); // blocking rectangle
        r += 0x0C;
        U16(r, 900); U16(r + 2, 900); U16(r + 4, 100); U16(r + 6, 100); U16(r + 8, 3); U16(r + 10, 0x200);     // soft circle

        // Init script at 0x200: a door to 106, the emblem pickup twice (both branches of a condition), an
        // event-placed item (16x16 zone) and the gold emblem's use spot. Main script at 0x300: empty.
        I32(0x60, 0x200);
        I32(0x64, 0x300);
        var s = 0x202;
        data[s] = 0x0C;
        U16(s + 2, 31600); U16(s + 4, 6300); U16(s + 6, 2000); U16(s + 8, 4000);
        data[s + 0x0F] = 0x06;
        s += 26;
        for (var copy = 0; copy < 2; copy++)
        {
            data[s] = 0x18;
            U16(s + 2, 2000); U16(s + 4, 7200); U16(s + 6, 1800); U16(s + 8, 1800);
            data[s + 0x0A] = 31;
            s += 26;
        }

        data[s] = 0x18;
        U16(s + 6, 16); U16(s + 8, 16);
        data[s + 0x0A] = 32;
        s += 26;
        data[s] = 0x0D;
        U16(s + 2, 2000); U16(s + 4, 7200); U16(s + 6, 1800); U16(s + 8, 1800);
        data[s + 0x0A] = 7;
        U16(s + 0x0C, 9); U16(s + 0x0E, 32 - 0x1B); U16(s + 0x10, 1);
        s += 18;
        foreach (var (handler, x) in new[] { (8, 6200), (16, 2300) }) // item box, typewriter
        {
            data[s] = 0x0D;
            U16(s + 2, x); U16(s + 4, 2100); U16(s + 6, 2000); U16(s + 8, 1400);
            data[s + 0x0A] = (byte)handler;
            s += 18;
        }
        U16(0x200, s - 0x200); // block length includes its own two bytes
        return data;
    }

    private static RoomMap Parse() =>
        Re1RoomMaps.Parse(BuildRoom(), Room105, Name) ?? throw new InvalidOperationException();

    private static string Name(int id) => id switch { 31 => "Emblem", 32 => "Gold emblem", _ => $"item {id}" };

    private static GameSnapshot Flags(params (string Field, long Value)[] fields) =>
        new() { RawFields = fields.ToDictionary(f => f.Field, f => Field<long>.Valid(f.Value)) };

    [Fact]
    public void Reads_cameras_and_collision()
    {
        var map = Parse();
        Assert.Equal(new MapCamera(0, 1000, 2000, 3000, 4000), Assert.Single(map.Cameras));
        Assert.Equal(2, map.Shapes.Count);
        Assert.Equal(new MapShape(MapShapeKind.Rectangle, 1000, 2000, 5000, 6000, true), map.Shapes[0]);
        Assert.Equal(new MapShape(MapShapeKind.Circle, 100, 100, 900, 900, false), map.Shapes[1]);
    }

    [Fact]
    public void Reads_doors_with_their_target_room()
    {
        var door = Assert.Single(Parse().Doors);
        Assert.Equal(new MapDoor(31600, 6300, 2000, 4000, "106"), door);
    }

    [Fact]
    public void Items_are_named_deduplicated_and_skip_event_placed_ones()
    {
        var item = Assert.Single(Parse().Items);
        Assert.Equal(new MapItem(2000, 7200, 1800, 1800, 31, "Emblem", 0), item);
    }

    [Fact]
    public void An_item_is_taken_once_its_pickup_flag_clears()
    {
        var maps = new Re1RoomMaps(Path.GetTempPath(), Name);
        var emblem = new MapItem(2000, 7200, 1800, 1800, 31, "Emblem", 42); // flag 42: word 1, bit 31 − 10
        Assert.False(maps.IsTaken(emblem, Flags(("flagRoomItems1", 0xFFFFFFFF))));
        Assert.True(maps.IsTaken(emblem, Flags(("flagRoomItems1", 0xFFDFFFFF))));
        Assert.Null(maps.IsTaken(emblem, Flags()));
        Assert.Null(maps.IsTaken(emblem with { Flag = -1 }, Flags(("flagRoomItems1", 0))));
    }

    [Fact]
    public void Use_spots_are_read_and_filled_by_their_confirmed_story_flag()
    {
        var map = Parse();
        var socket = Assert.Single(map.Sockets);
        Assert.Equal(new MapSocket(2000, 7200, 1800, 1800, 32, "Gold emblem"), socket);

        var maps = new Re1RoomMaps(Path.GetTempPath(), Name, Re1Knowledge.LoadEmbedded().Fills);
        Assert.False(maps.IsFilled(map, socket, Flags(("flagScenarioB5", 0))));
        Assert.True(maps.IsFilled(map, socket, Flags(("flagScenarioB5", 0x00100000)))); // B171
        Assert.Null(maps.IsFilled(map, socket with { ItemId = 60 }, Flags(("flagScenarioB5", -1))));
    }

    [Fact]
    public void Item_boxes_and_typewriters_are_read()
    {
        Assert.Equal(
            [new MapFixture(6200, 2100, 2000, 1400, FixtureKind.ItemBox), new MapFixture(2300, 2100, 2000, 1400, FixtureKind.Typewriter)],
            Parse().Fixtures);
    }

    [Fact]
    public void Bounds_cover_walls_and_doors()
    {
        Assert.Equal((100, 100, 33600, 10300), Parse().Bounds);
    }

    [Fact]
    public void Stub_and_damaged_files_give_no_map()
    {
        Assert.Null(Re1RoomMaps.Parse(new byte[4], Room105, _ => ""));
        var damaged = BuildRoom();
        BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(0x4C), 0x3FF); // collision pointer at the very end
        Assert.NotNull(Re1RoomMaps.Parse(damaged, Room105, _ => ""));
    }

    [Fact]
    public void Missing_install_gives_no_map()
    {
        var maps = new Re1Adapter().CreateRoomMaps(Path.Combine(Path.GetTempPath(), "no-such-install"));
        Assert.Null(maps.Load(Room105, PlayableCharacter.Jill));
    }
}
