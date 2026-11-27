using PointBlank.Sim;
using PointBlank.Sim.Org;
using PointBlank.Sim.Orders;

namespace PointBlank.Presentation;

/// <summary>What a right-click orders.</summary>
public enum OrderMode
{
    Move,
    MoveFast,
    Sneak,
    FallBack,
    Defend,
    Fire,
    CallFire,
}

/// <summary>Everything a button or key can do. The first seven pick the <see cref="OrderMode"/> of the same name.</summary>
public enum Command
{
    Move,
    MoveFast,
    Sneak,
    FallBack,
    Defend,
    Fire,
    CallFire,
    Hold,
    CycleRoe,
    TogglePause,
    Speed1,
    Speed2,
    Speed4,
}

/// <summary>The debug overlays (item 2.6), each on its own key and independent of the others.</summary>
[Flags]
public enum Overlay
{
    None = 0,
    Sight = 1,
    Values = 2,
    Truth = 4,
    Intent = 8,
}

public sealed record UiButton(Command Command, string Label, Area Area);

/// <summary>
/// The player's side of a battle: selection, orders, camera and clock. It reads only the battle's <see cref="SideView"/>,
/// and orders go through <see cref="SideView.Issue"/>, so the chain of command delays them as it should. They can be
/// given while paused.
/// </summary>
public sealed class GameController
{
    private const float PickPixels = 9;
    private readonly SortedSet<int> _selected = [];
    private readonly Dictionary<int, (long Tick, OrderKind Kind)> _queued = [];
    private readonly MapView _map;
    private readonly SightOverlay _sight = new();

    public GameController(PlayerBattle battle, Layout? layout = null)
    {
        Battle = battle;
        Layout = layout ?? new Layout();
        _map = battle.View.Map ?? throw new InvalidOperationException("The battle has no map.");
        Camera = new Camera(Layout.Map);
        Camera.Fit(_map);
    }

    public PlayerBattle Battle { get; }

    public Layout Layout { get; }

    public Camera Camera { get; }

    public SimClock Clock { get; } = new();

    public OrderMode Mode { get; set; } = OrderMode.Move;

    /// <summary>The selected own squads, by id.</summary>
    public IReadOnlyList<int> Selected => _selected.ToArray();

    /// <summary>The selection box being dragged, if any.</summary>
    public Area? DragBox { get; set; }

    /// <summary>The overlays that are on. All are off at the start.</summary>
    public Overlay Overlays { get; private set; }

    public void Toggle(Overlay overlay) => Overlays ^= overlay;

    /// <summary>Whether the sight overlay has finished its pass (it works a slice per frame).</summary>
    public bool SightComplete => _sight.Complete;

    /// <summary>Finishes the overlays' work at once, for a screenshot.</summary>
    public void FinishOverlays()
    {
        if (Overlays.HasFlag(Overlay.Sight))
        {
            _sight.Update(this, int.MaxValue);
        }
    }

    internal SightOverlay Sight => _sight;

    public void Step() => Battle.Step();

    /// <summary>Runs the ticks that a frame of this length earns. Returns how many.</summary>
    public int Advance(double seconds)
    {
        var ticks = Clock.Advance(seconds);
        for (var i = 0; i < ticks; i++)
        {
            Step();
        }

        return ticks;
    }

    /// <summary>The ground, under everything else. It changes only with the camera.</summary>
    public IReadOnlyList<Prim> Terrain() => TerrainLayer.Build(_map, Camera);

    /// <summary>Units, contacts, orders and the interface.</summary>
    public IReadOnlyList<Prim> Frame() => FrameBuilder.Build(this);

    public IReadOnlyList<UiButton> Buttons()
    {
        (Command, string, float)[] all =
        [
            (Command.Move, "Move [M]", 82), (Command.MoveFast, "Fast [F]", 76), (Command.Sneak, "Sneak [S]", 82),
            (Command.FallBack, "Fall back [B]", 108), (Command.Defend, "Defend [D]", 92), (Command.Fire, "Fire [A]", 76),
            (Command.CallFire, "Call fire [C]", 104), (Command.Hold, "Hold [H]", 76), (Command.CycleRoe, "RoE [R]", 70),
            (Command.TogglePause, "Pause [Space]", 118), (Command.Speed1, "1x [1]", 54), (Command.Speed2, "2x [2]", 54),
            (Command.Speed4, "4x [4]", 54),
        ];
        var list = new List<UiButton>();
        var x = 6f;
        foreach (var (command, label, width) in all)
        {
            // A wider gap before the immediate orders and before the clock.
            x += command is Command.Hold or Command.TogglePause ? 16 : 0;
            list.Add(new UiButton(command, label, new Area(x, Layout.Bottom.Y + 7, width, 28)));
            x += width + 5;
        }

        return list;
    }

    public bool IsActive(Command command) => command switch
    {
        <= Command.CallFire => (int)command == (int)Mode,
        Command.TogglePause => Clock.Paused,
        Command.Speed1 => Clock.Speed == 1,
        Command.Speed2 => Clock.Speed == 2,
        Command.Speed4 => Clock.Speed == 4,
        _ => false,
    };

    public void Press(Command command)
    {
        switch (command)
        {
            case <= Command.CallFire:
                Mode = (OrderMode)(int)command;
                break;
            case Command.Hold:
                foreach (var id in _selected)
                {
                    Issue(id, new Order(OrderKind.Hold, Fixed.Zero, Fixed.Zero));
                }

                break;
            case Command.CycleRoe:
                // The whole selection follows the first squad's next setting.
                if (_selected.Count > 0 && Battle.View.RoeOf(_selected.Min) is { } roe)
                {
                    var next = (RoeMode)(((int)roe + 1) % 3);
                    foreach (var id in _selected)
                    {
                        Issue(id, new Order(OrderKind.SetRoe, Fixed.Zero, Fixed.Zero, (int)next));
                    }
                }

                break;
            case Command.TogglePause:
                Clock.Paused = !Clock.Paused;
                break;
            default:
                Clock.Speed = command == Command.Speed1 ? 1 : command == Command.Speed2 ? 2 : 4;
                break;
        }
    }

    /// <summary>A left click: a button, or the squad whose man is nearest the pointer. <paramref name="add"/> toggles.</summary>
    public void Click(Pt at, bool add)
    {
        if (Buttons().FirstOrDefault(b => b.Area.Contains(at)) is { } button)
        {
            Press(button.Command);
            return;
        }

        if (!Layout.Map.Contains(at))
        {
            return;
        }

        var best = Battle.View.Squads
            .SelectMany(q => q.Soldiers.Select(s => (q.Id, D: Distance(Camera.ToScreen(s.X.F(), s.Y.F()), at))))
            .Where(h => h.D <= PickPixels)
            .OrderBy(h => h.D)
            .Select(h => (int?)h.Id)
            .FirstOrDefault();
        if (!add)
        {
            _selected.Clear();
        }

        if (best is { } id && !_selected.Add(id))
        {
            _selected.Remove(id);
        }
    }

    /// <summary>Selects every own squad with a man inside the box.</summary>
    public void BoxSelect(Pt a, Pt b, bool add)
    {
        var box = new Area(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Abs(a.X - b.X), MathF.Abs(a.Y - b.Y));
        if (!add)
        {
            _selected.Clear();
        }

        foreach (var q in Battle.View.Squads)
        {
            if (q.Soldiers.Any(s => box.Contains(Camera.ToScreen(s.X.F(), s.Y.F()))))
            {
                _selected.Add(q.Id);
            }
        }
    }

    public void Select(IEnumerable<int> squads)
    {
        _selected.Clear();
        _selected.UnionWith(squads.Where(id => Battle.View.Squad(id) != null));
    }

    /// <summary>
    /// Gives the current order at the pointer. A group move keeps the squads' places relative to each other; Fire aims at a
    /// contact under the pointer, else at the ground; a fire mission is called by one squad (an HQ or radio if selected).
    /// </summary>
    public void RightClick(Pt at)
    {
        if (!Layout.Map.Contains(at) || _selected.Count == 0)
        {
            return;
        }

        var (x, y) = Camera.ToWorld(at);
        var squads = _selected.Select(id => Battle.View.Squad(id)).OfType<SquadView>().ToArray();
        var kind = Mode switch
        {
            OrderMode.Move => OrderKind.Move,
            OrderMode.MoveFast => OrderKind.MoveFast,
            OrderMode.Sneak => OrderKind.Sneak,
            OrderMode.FallBack => OrderKind.FallBack,
            OrderMode.Defend => OrderKind.Defend,
            OrderMode.Fire => OrderKind.Fire,
            _ => OrderKind.CallFire,
        };

        if (kind == OrderKind.CallFire)
        {
            var caller = squads.OrderBy(q => q.IsHq || q.HasRadio ? 0 : 1).First();
            Issue(caller.Id, OrderAt(kind, x, y));
            return;
        }

        if (kind == OrderKind.Fire)
        {
            var target = Battle.View.KnownEnemies
                .Select(c => (Contact: c, D: Distance(Camera.ToScreen(c.X.F(), c.Y.F()), at)))
                .Where(h => h.D <= PickPixels)
                .OrderBy(h => h.D)
                .Select(h => (PointBlank.Sim.Spotting.ContactView?)h.Contact)
                .FirstOrDefault();
            var order = target is { } t ? new Order(kind, t.X, t.Y, t.EnemyId) : OrderAt(kind, x, y);
            foreach (var q in squads)
            {
                Issue(q.Id, order);
            }

            return;
        }

        var centres = squads.Select(Centre).ToArray();
        var midX = centres.Average(c => c.X);
        var midY = centres.Average(c => c.Y);
        for (var i = 0; i < squads.Length; i++)
        {
            // A click on the map keeps every offset destination on the map.
            var inside = x >= 0 && y >= 0 && x < Width && y < Height;
            var tx = x + centres[i].X - midX;
            var ty = y + centres[i].Y - midY;
            if (inside)
            {
                tx = Math.Clamp(tx, 0, Width - 0.01f);
                ty = Math.Clamp(ty, 0, Height - 0.01f);
            }

            Issue(squads[i].Id, OrderAt(kind, tx, ty));
        }
    }

    /// <summary>An order given but not yet taken up by the sim (it is, on the next tick).</summary>
    internal OrderKind? Queued(int squad) =>
        _queued.TryGetValue(squad, out var q) && q.Tick == Battle.View.Tick ? q.Kind : null;

    /// <summary>The middle of a squad's able men (of all its men if none is able), in metres.</summary>
    internal static (float X, float Y) Centre(SquadView q)
    {
        var men = q.Soldiers.Where(s => s.Health is HealthState.Ok or HealthState.Wounded).ToArray();
        if (men.Length == 0)
        {
            men = q.Soldiers.ToArray();
        }

        return (men.Average(s => s.X.F()), men.Average(s => s.Y.F()));
    }

    internal MapView Map => _map;

    private float Width => _map.Width * _map.TileSize.F();

    private float Height => _map.Height * _map.TileSize.F();

    private static Order OrderAt(OrderKind kind, float x, float y) => new(kind, Num.ToFixed(x), Num.ToFixed(y));

    private static float Distance(Pt a, Pt b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private void Issue(int squad, Order order)
    {
        if (Battle.View.Issue(squad, order) == IssueResult.Accepted)
        {
            _queued[squad] = (Battle.View.Tick, order.Kind);
        }
    }
}
