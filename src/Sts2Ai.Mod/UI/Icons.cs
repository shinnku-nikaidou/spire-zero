using Godot;

namespace Sts2Ai.Mod.UI;

/// <summary>
/// The panel's icons, drawn here into textures so they need no file and no font: the toolbar's play, pause,
/// step (an arrow over a dot) and finish (an arrow into a bar), and the head's opacity and close. The shapes are laid out on an 18-unit square and drawn at
/// three times that, then shown at <see cref="Shown"/>: the game scales its 1080p canvas up to the window, and a small
/// texture would blur. Each pixel is the share of 4×4 samples inside the shape.
/// </summary>
internal static class Icons
{
    private const int Units = 18, Scale = 3, Size = Units * Scale;

    /// <summary>How wide a toolbar button draws its icon, in the canvas's pixels.</summary>
    public const int Shown = 15;

    public static readonly Color Green = Color.FromHtml("#5fd18a");
    public static readonly Color Amber = Color.FromHtml("#f5b544");
    public static readonly Color Blue = Color.FromHtml("#8ab4ff");

    public static Texture2D Play { get; } = Draw(Green, (x, y) => Inside(x, y, (5, 3), (5, 15), (15, 9)));

    public static Texture2D Pause { get; } = Draw(Amber, (x, y) => (x is >= 4 and <= 7.5 || x is >= 10.5 and <= 14) && y is >= 3 and <= 15);

    /// <summary>An arc over a dot, ending in an arrowhead on its right.</summary>
    public static Texture2D Step { get; } = Draw(Blue, (x, y) =>
    {
        double r = Math.Sqrt((x - 9) * (x - 9) + (y - 10) * (y - 10));
        bool arc = y <= 10 && r is >= 4.6 and <= 6.6 && x <= 13.5;
        return arc || Inside(x, y, (11.5, 8), (17, 8), (14.25, 12)) || (x - 9) * (x - 9) + (y - 14.5) * (y - 14.5) <= 3.2;
    });

    /// <summary>An arrow to the right, into a bar.</summary>
    public static Texture2D Finish { get; } = Draw(Blue, (x, y) =>
        (y is >= 8 and <= 10 && x is >= 2 and <= 10) || Inside(x, y, (9, 4.5), (9, 13.5), (13, 9)) || (x is >= 14 and <= 16 && y is >= 3 and <= 15));

    /// <summary>The panel's close button: a cross, in white for the button to tint.</summary>
    public static Texture2D Close { get; } = Draw(Colors.White, (x, y) =>
        x is >= 4.5 and <= 13.5 && y is >= 4.5 and <= 13.5 && (Math.Abs(x - y) <= 1.1 || Math.Abs(x + y - 18) <= 1.1));

    /// <summary>The panel's opacity button: a ring with its left half filled, in white for the button to tint.</summary>
    public static Texture2D Opacity { get; } = Draw(Colors.White, (x, y) =>
    {
        double r = Math.Sqrt((x - 9) * (x - 9) + (y - 9) * (y - 9));
        return r <= 6.6 && (r >= 5.2 || x <= 9);
    });

    private static ImageTexture Draw(Color color, Func<double, double, bool> inside)
    {
        var data = new byte[Size * Size * 4];
        for (int py = 0; py < Size; py++)
            for (int px = 0; px < Size; px++)
            {
                int hits = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                        if (inside((px + (sx + 0.5) / 4) / Scale, (py + (sy + 0.5) / 4) / Scale)) hits++;
                int at = (py * Size + px) * 4;
                data[at] = (byte)color.R8;
                data[at + 1] = (byte)color.G8;
                data[at + 2] = (byte)color.B8;
                data[at + 3] = (byte)(255 * hits / 16);
            }
        return ImageTexture.CreateFromImage(Image.CreateFromData(Size, Size, false, Image.Format.Rgba8, data));
    }

    /// <summary>Whether (x, y) is inside the triangle a, b, c.</summary>
    private static bool Inside(double x, double y, (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        double Side((double X, double Y) p, (double X, double Y) q) => (q.X - p.X) * (y - p.Y) - (q.Y - p.Y) * (x - p.X);
        double ab = Side(a, b), bc = Side(b, c), ca = Side(c, a);
        return (ab >= 0 && bc >= 0 && ca >= 0) || (ab <= 0 && bc <= 0 && ca <= 0);
    }
}
