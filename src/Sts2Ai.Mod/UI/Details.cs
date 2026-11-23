using System.Globalization;
using Godot;
using Sts2Ai.Protocol;

namespace Sts2Ai.Mod.UI;

/// <summary>
/// The fight's details card beside the panel's: the picked line played out turn by turn (<see cref="FightTable"/>), as
/// a table (the cards played, the HP the enemies lost, the block, the HP lost, the HP left, each enemy's HP) and as a
/// chart of the player's HP and the enemies' total HP over the turns; where the line stops with the fight going on, the
/// combat net's guess after it, dashed. The panel's button opens and closes it (<see cref="Settings.Details"/>). Over
/// the game it is a card beside the panel's; in the panel's desktop window, under the answer (<see cref="Dock"/>). Built
/// again only when the table or the colours change.
/// </summary>
internal sealed class Details
{
    private const int ChartWidth = 440, ChartHeight = 150, Gap = 6;

    private readonly CanvasLayer _layer;
    private readonly PanelContainer _card;
    private readonly StyleBoxFlat _style;
    private readonly VBoxContainer _body;
    private object? _built;
    private bool _docked;

    public Details(CanvasLayer layer)
    {
        _layer = layer;
        _style = Panel.Box(Palette.Current.Bg with { A = 0.92f }, 14, Panel.Rule);
        _style.SetContentMarginAll(12);
        _style.ContentMarginLeft = _style.ContentMarginRight = 14;
        _style.ShadowColor = new Color(0, 0, 0, 0.4f);
        _style.ShadowSize = 12;
        _card = Panel.Quiet(new PanelContainer { Visible = false });
        _card.AddThemeStyleboxOverride("panel", _style);
        _body = Panel.Column(10);
        _card.AddChild(_body);
        layer.AddChild(_card);
    }

    /// <summary>The card into <paramref name="into"/> (the panel's window), laid out there; null: back over the game,
    /// beside the panel's card.</summary>
    public void Dock(Container? into)
    {
        _docked = into != null;
        var parent = into ?? (Node)_layer;
        if (_card.GetParent() == parent) return;
        _card.GetParent()?.RemoveChild(_card);
        parent.AddChild(_card);
        _built = null;
    }

    /// <summary>The card beside <paramref name="beside"/> (the panel's card): on its right, or on its left where the
    /// window has no room; where it is docked, as its container lays it out. Hidden with no table.
    /// <paramref name="coop"/>: a run of more than one player, whose search takes the teammates as dead in SL and as
    /// playing nothing more this turn in NoSL.</summary>
    public void Show(FightTable? table, bool visible, Rect2 beside, float opacity, bool coop = false)
    {
        _card.Visible = visible && table != null;
        if (!_card.Visible) return;
        if (_card.Theme != Fonts.Theme) _card.Theme = Fonts.Theme;
        if (_card.Modulate.A != opacity) _card.Modulate = Colors.White with { A = opacity };
        var key = (table, Settings.Current.Env, coop, L10n.T("Turn"), Palette.Current);
        if (!Equals(key, _built))
        {
            _built = key;
            foreach (var child in _body.GetChildren())
            {
                _body.RemoveChild(child);
                child.QueueFree();
            }
            _style.BgColor = Palette.Current.Bg with { A = 0.92f };
            _style.BorderColor = Panel.Rule;
            Build(table!, coop);
            _card.CallDeferred(Control.MethodName.ResetSize);
        }
        if (_docked) return;
        var window = _card.GetViewportRect().Size;
        float right = beside.End.X + Gap;
        float x = right + _card.Size.X <= window.X ? right : beside.Position.X - Gap - _card.Size.X;
        float y = Math.Clamp(beside.Position.Y, 0, Math.Max(window.Y - _card.Size.Y, 0));
        _card.Position = new Vector2(Math.Max(x, 0), y);
    }

    private void Build(FightTable table, bool coop)
    {
        var head = Panel.Row(10);
        var title = Panel.Text(L10n.T("Best line · by turn"), 15, Panel.Fg);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(title);
        if (table.Status != CombatStatus.InProgress)
            head.AddChild(Panel.Text(L10n.T(table.Status == CombatStatus.Won ? "Won the fight" : "Lost the fight"), 12,
                table.Status == CombatStatus.Won ? Icons.Green : Panel.Error));
        _body.AddChild(head);
        _body.AddChild(Table(table));
        _body.AddChild(Legend(table));
        _body.AddChild(Chart(table));
        bool sl = Settings.Current.Env == PlayEnvironment.SL && (!coop || Assist.CoopSl);
        string foot = table.Status != CombatStatus.InProgress ? sl && coop ? "Played out to the fight's end, your teammates dead." : "Played out to the fight's end."
            : sl ? coop ? "Played out to the end of the plan, your teammates dead; after it, the combat net's guess."
                : "Played out to the end of the plan; after it, the combat net's guess."
            : coop ? "This turn played out in one sampled world, your teammates playing nothing more, to the end of the enemies' turn; after it, the combat net's guess."
            : "This turn played out in one sampled world, to the end of the enemies' turn; after it, the combat net's guess.";
        var note = Panel.Wrapped(Panel.Text(L10n.T(foot), 11, Panel.Muted));
        note.CustomMinimumSize = new Vector2(ChartWidth, 0);
        _body.AddChild(note);
    }

    private static GridContainer Table(FightTable table)
    {
        var grid = Panel.Quiet(new GridContainer { Columns = 6 + table.Enemies.Count });
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 5);
        Cell(grid, L10n.T("Turn"), 11, Panel.Muted);
        Cell(grid, L10n.T("Played"), 11, Panel.Muted, cards: true);
        foreach (var name in new[] { "Dealt", "Block", "Taken", "HP left" }) Cell(grid, L10n.T(name), 11, Panel.Muted, right: true);
        foreach (var enemy in table.Enemies) Cell(grid, Describe.TableEnemy(enemy.Id), 11, Panel.Muted, right: true, enemy: true);
        for (int i = 0; i < table.Turns.Count; i++)
        {
            var row = table.Turns[i];
            var color = i == 0 ? Panel.Lead : Panel.Fg;
            Cell(grid, $"{row.Round}", 13, color);
            Cell(grid, string.Join(" · ", row.Cards.Select(Describe.TableCard)), 12, Panel.Muted, cards: true);
            Cell(grid, $"{row.Dealt}", 13, color, right: true);
            Cell(grid, $"{row.Block}", 13, color, right: true);
            Cell(grid, $"{row.Taken}", 13, row.Taken > 0 ? Panel.Error : color, right: true);
            Cell(grid, $"{row.Hp}", 13, color, right: true);
            foreach (int hp in row.Enemies) Cell(grid, hp > 0 ? $"{hp}" : "—", 13, color, right: true);
        }
        if (table.Win is { } win)
        {
            Cell(grid, L10n.T("After"), 12, Panel.Muted);
            Cell(grid, L10n.T("net's guess · win {0}", Percent(win)), 12, Panel.Muted, cards: true);
            for (int i = 0; i < 3; i++) Cell(grid, "—", 12, Panel.Muted, right: true);
            Cell(grid, win > 0 && table.HpLeft is { } hp ? $"≈{hp:0}" : "—", 12, Panel.Muted, right: true);
            foreach (var _ in table.Enemies) Cell(grid, "—", 12, Panel.Muted, right: true);
        }
        return grid;
    }

    private static void Cell(GridContainer grid, string text, int size, Color color, bool right = false, bool cards = false, bool enemy = false)
    {
        var label = Panel.Text(text, size, color);
        if (cards)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(150, 0);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        if (enemy)
        {
            Panel.Clipped(label);
            label.CustomMinimumSize = new Vector2(64, 0);
        }
        if (right)
        {
            label.HorizontalAlignment = HorizontalAlignment.Right;
            if (!enemy) label.CustomMinimumSize = new Vector2(36, 0);
        }
        grid.AddChild(label);
    }

    private static HBoxContainer Legend(FightTable table)
    {
        var legend = Panel.Row(14);
        void Item(string text, Color color, bool dashed)
        {
            var item = Panel.Row(6);
            var swatch = Panel.Quiet(new Control { CustomMinimumSize = new Vector2(16, 12), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
            swatch.Draw += () =>
            {
                if (dashed) swatch.DrawDashedLine(new Vector2(0, 6), new Vector2(16, 6), color, 2, 4);
                else swatch.DrawLine(new Vector2(0, 6), new Vector2(16, 6), color, 2);
            };
            item.AddChild(swatch);
            item.AddChild(Panel.Text(text, 11, Panel.Muted));
            legend.AddChild(item);
        }
        Item(L10n.T("Your HP"), Panel.Accent, false);
        Item(L10n.T("Enemies' HP"), Panel.Muted, false);
        if (table.Win != null) Item(L10n.T("net's guess · win {0}", Percent(table.Win.Value)), Panel.Accent, true);
        return legend;
    }

    /// <summary>The player's HP and the enemies' total over the turns: a point at the line's start and one at each
    /// turn's end; the net's guess of the HP at a win dashed after the last.</summary>
    private static Control Chart(FightTable table)
    {
        var chart = Panel.Quiet(new Control { CustomMinimumSize = new Vector2(ChartWidth, ChartHeight) });
        int[] hp = [table.Hp, .. table.Turns.Select(t => t.Hp)];
        int[] enemies = [table.Enemies.Sum(e => e.Hp), .. table.Turns.Select(t => t.Enemies.Sum())];
        double? guess = table.Win is > 0 ? table.HpLeft : table.Win is 0 ? 0 : null;
        int points = hp.Length + (guess != null ? 1 : 0);
        double top = Math.Max(10, Math.Ceiling(Math.Max(Math.Max(hp.Max(), enemies.Max()), guess ?? 0) / 10) * 10);
        const float left = 34, right = 14, up = 12, down = 24;
        float X(int i) => left + (points > 1 ? i * (ChartWidth - left - right) / (points - 1) : 0);
        float Y(double v) => (float)(up + (1 - v / top) * (ChartHeight - up - down));
        chart.Draw += () =>
        {
            var font = Fonts.Theme?.DefaultFont ?? ThemeDB.FallbackFont;
            float bottom = Y(0);
            chart.DrawLine(new Vector2(left, up), new Vector2(left, bottom), Panel.Rule, 1);
            chart.DrawLine(new Vector2(left, bottom), new Vector2(ChartWidth - right, bottom), Panel.Rule, 1);
            chart.DrawLine(new Vector2(left, Y(top / 2)), new Vector2(ChartWidth - right, Y(top / 2)), Panel.Track, 1);
            foreach (var v in new[] { top, top / 2, 0 })
                chart.DrawString(font, new Vector2(0, Y(v) + 4), v.ToString("0", CultureInfo.InvariantCulture), HorizontalAlignment.Right, left - 6, 11, Panel.Muted);
            Series(chart, enemies.Select((v, i) => new Vector2(X(i), Y(v))).ToArray(), Panel.Muted, 2, 3);
            var mine = hp.Select((v, i) => new Vector2(X(i), Y(v))).ToArray();
            Series(chart, mine, Panel.Accent, 2.5f, 4);
            var last = mine[^1];
            string end = $"{hp[^1]} HP";
            if (guess is { } g)
            {
                var at = new Vector2(X(points - 1), Y(g));
                chart.DrawDashedLine(last, at, Panel.Accent, 2, 5, true, true);
                chart.DrawCircle(at, 3.5f, Panel.Accent);
                (last, end) = (at, $"≈{g:0} HP");
            }
            chart.DrawString(font, new Vector2(last.X - 120, last.Y - 9), end, HorizontalAlignment.Right, 120, 12, Panel.Fg);
            for (int i = 0; i < points; i++)
            {
                string label = i == 0 ? L10n.T("Start") : i < hp.Length ? $"{table.Turns[i - 1].Round}" : L10n.T("After");
                chart.DrawString(font, new Vector2(X(i) - 40, ChartHeight - 6), label, HorizontalAlignment.Center, 80, 11, Panel.Muted);
            }
        };
        return chart;
    }

    private static void Series(Control chart, Vector2[] points, Color color, float width, float dot)
    {
        if (points.Length > 1) chart.DrawPolyline(points, color, width, true);
        foreach (var p in points) chart.DrawCircle(p, dot, color);
    }

    private static string Percent(double p) => (p * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
