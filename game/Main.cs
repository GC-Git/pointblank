using System;
using System.Threading.Tasks;
using Godot;
using PointBlank.Presentation;
using PointBlank.Sim;

namespace PointBlank.Game;

/// <summary>
/// The game's one node: loads a battle, passes input to the <see cref="GameController"/> and paints what it returns.
/// Options (after Godot's <c>--</c>) are in <see cref="LaunchOptions"/>.
/// </summary>
public partial class Main : Node2D
{
    private const float DragPixels = 5;
    private const float PanPixelsPerSecond = 600;
    private const float ZoomStep = 1.2f;

    private readonly PrimLayer _terrain = new();
    private readonly PrimLayer _frame = new();
    private GameController? _game;
    private (float, float, float) _drawnCamera;
    private Pt? _dragFrom;
    private bool _screenshot;

    public override void _Ready()
    {
        try
        {
            var options = LaunchOptions.Parse(OS.GetCmdlineUserArgs());
            _screenshot = options.Screenshot;
            _game = new GameController(PlayerBattle.Create(options.Scenario, options.Seed, options.Scripted));
            _game.Select(options.Select);
            if (options.Zoom != null || options.Center != null)
            {
                var (x, y) = options.Center ?? (_game.Camera.CentreX, _game.Camera.CentreY);
                _game.Camera.Set(x, y, options.Zoom ?? _game.Camera.Scale);
            }

            if (_screenshot)
            {
                _ = CaptureAsync(options);
            }
            else
            {
                AddChild(_terrain);
                AddChild(_frame);
                Redraw();
            }
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    public override void _Process(double delta)
    {
        if (_game == null || _screenshot)
        {
            return;
        }

        var pan = (float)delta * PanPixelsPerSecond;
        _game.Camera.Pan(
            pan * ((Input.IsKeyPressed(Key.Left) ? 1 : 0) - (Input.IsKeyPressed(Key.Right) ? 1 : 0)),
            pan * ((Input.IsKeyPressed(Key.Up) ? 1 : 0) - (Input.IsKeyPressed(Key.Down) ? 1 : 0)));
        _game.Advance(delta);
        Redraw();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_game == null || _screenshot)
        {
            return;
        }

        switch (@event)
        {
            case InputEventMouseButton button:
                MouseButtonInput(_game, button);
                break;
            case InputEventMouseMotion motion when (motion.ButtonMask & MouseButtonMask.Middle) != 0:
                _game.Camera.Pan(motion.Relative.X, motion.Relative.Y);
                break;
            case InputEventMouseMotion motion when _dragFrom is { } from:
                var to = At(motion.Position);
                _game.DragBox = new Area(
                    MathF.Min(from.X, to.X), MathF.Min(from.Y, to.Y), MathF.Abs(from.X - to.X), MathF.Abs(from.Y - to.Y));
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                KeyInput(_game, key.Keycode);
                break;
        }
    }

    private static Pt At(Vector2 v) => new(v.X, v.Y);

    private void MouseButtonInput(GameController game, InputEventMouseButton e)
    {
        var at = At(e.Position);
        switch (e.ButtonIndex)
        {
            case MouseButton.Left when e.Pressed:
                _dragFrom = at;
                break;
            case MouseButton.Left when _dragFrom is { } from:
                if (game.Layout.Map.Contains(from) && (MathF.Abs(at.X - from.X) > DragPixels || MathF.Abs(at.Y - from.Y) > DragPixels))
                {
                    game.BoxSelect(from, at, e.ShiftPressed);
                }
                else
                {
                    game.Click(at, e.ShiftPressed);
                }

                _dragFrom = null;
                game.DragBox = null;
                break;
            case MouseButton.Right when e.Pressed:
                // Shift and Ctrl order a fast move or a sneak without changing the chosen order.
                var mode = game.Mode;
                game.Mode = e.ShiftPressed ? OrderMode.MoveFast : e.CtrlPressed ? OrderMode.Sneak : mode;
                game.RightClick(at);
                game.Mode = mode;
                break;
            case MouseButton.WheelUp when e.Pressed:
                game.Camera.Zoom(at, ZoomStep);
                break;
            case MouseButton.WheelDown when e.Pressed:
                game.Camera.Zoom(at, 1 / ZoomStep);
                break;
        }
    }

    private static void KeyInput(GameController game, Key key)
    {
        Command? command = key switch
        {
            Key.M => Command.Move,
            Key.F => Command.MoveFast,
            Key.S => Command.Sneak,
            Key.B => Command.FallBack,
            Key.D => Command.Defend,
            Key.A => Command.Fire,
            Key.C => Command.CallFire,
            Key.H => Command.Hold,
            Key.R => Command.CycleRoe,
            Key.Space => Command.TogglePause,
            Key.Key1 => Command.Speed1,
            Key.Key2 => Command.Speed2,
            Key.Key4 => Command.Speed4,
            _ => null,
        };
        if (command is { } c)
        {
            game.Press(c);
        }
        else if (key == Key.Escape)
        {
            game.Select([]);
        }
    }

    private void Redraw()
    {
        var game = _game!;
        var camera = (game.Camera.CentreX, game.Camera.CentreY, game.Camera.Scale);
        if (camera != _drawnCamera)
        {
            _drawnCamera = camera;
            _terrain.Show(game.Terrain());
        }

        _frame.Show(game.Frame());
    }

    /// <summary>
    /// Screenshot mode: steps to each asked tick, paints into an off-screen viewport of the layout's size (so the window's
    /// size and display scaling do not matter), saves a PNG per tick and quits. It needs a real renderer.
    /// </summary>
    private async Task CaptureAsync(LaunchOptions options)
    {
        try
        {
            if (DisplayServer.GetName() == "headless")
            {
                throw new InvalidOperationException("Screenshot mode needs a real renderer: run it without --headless.");
            }

            var game = _game!;
            var viewport = new SubViewport
            {
                Size = new Vector2I(game.Layout.Width, game.Layout.Height),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            };
            viewport.AddChild(_terrain);
            viewport.AddChild(_frame);
            AddChild(viewport);
            var directory = System.IO.Path.GetFullPath(options.Out);
            System.IO.Directory.CreateDirectory(directory);

            foreach (var tick in options.Ticks)
            {
                while (game.Battle.View.Tick < tick)
                {
                    game.Step();
                }

                Redraw();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var path = System.IO.Path.Combine(directory, $"{options.Scenario}-seed{options.Seed}-tick{tick}.png");
                var error = viewport.GetTexture().GetImage().SavePng(path);
                if (error != Error.Ok)
                {
                    throw new InvalidOperationException($"Could not save {path}: {error}.");
                }

                GD.Print($"screenshot saved {path}");
            }

            GetTree().Quit();
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private void Fail(Exception e)
    {
        GD.PrintErr($"ERROR: {e.Message}");
        GetTree().Quit(1);
    }
}
