using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace Sts2Ai.Mod.UI;

/// <summary>
/// The game's own font for the mod's text. The game draws at 1080p and scales the canvas to the window,
/// with font oversampling off: its fonts are MSDF and stay sharp at any scale, while the engine's default font would be
/// drawn at 1080p and stretched, blurred. So the mod takes the font the game uses for its language: the game's
/// substitute where it has one (Chinese, Japanese, Korean, Russian, Thai), else Kreon.
/// </summary>
internal static class Fonts
{
    private const string Kreon = "res://themes/kreon_regular_shared.tres";

    private static string? _language;
    private static Theme? _theme;

    /// <summary>A theme with the game's font for its current language, for a control and everything in it.</summary>
    public static Theme? Theme
    {
        get
        {
            string? language = LocManager.Instance?.Language;
            if (_theme != null && language == _language) return _theme;
            _language = language;
            var font = (language != null ? FontManager.GetSubstituteFont(language, FontType.Regular) : null) ?? ResourceLoader.Load<Font>(Kreon);
            _theme = font != null ? new Theme { DefaultFont = font } : null;
            return _theme;
        }
    }
}
