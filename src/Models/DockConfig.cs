using System.Collections.Generic;

namespace Dockyard.Models
{
    /// <summary>Everything that lives in config.json.</summary>
    public class DockConfig
    {
        // ---- layout ----------------------------------------------------
        /// <summary>"Horizontal" or "Vertical".</summary>
        public string Orientation { get; set; } = "Horizontal";

        /// <summary>Icon edge length in DIPs. Ctrl+scroll over the dock changes this live.</summary>
        public double IconSize { get; set; } = 48;

        /// <summary>Gap between tiles.</summary>
        public double TileSpacing { get; set; } = 14;

        /// <summary>
        /// How many rows a horizontal dock wraps into (columns when vertical). 1 is the classic
        /// single line; 2+ wraps tiles onto a new line when a row is full.
        /// </summary>
        public int Rows { get; set; } = 1;

        /// <summary>Inner padding of the dock slab.</summary>
        public double Padding { get; set; } = 14;

        public double CornerRadius { get; set; } = 20;

        public bool ShowLabels { get; set; } = true;

        public double LabelSize { get; set; } = 10.5;

        // ---- look ------------------------------------------------------
        /// <summary>
        /// Name of the preset currently applied, or "custom" once colours are hand-edited.
        /// Deliberately has no default: a config that arrives without one predates the current
        /// theme set, which is how ConfigService knows to migrate it.
        /// </summary>
        public string Theme { get; set; }

        /// <summary>
        /// "none" (tint + drop shadow) is the default: WPF draws the slab, so corners are exact at
        /// any radius. "acrylic" and "blur" hand the backdrop to DWM, which blurs the whole window
        /// rectangle and forces the corner radius to DWM's own.
        /// </summary>
        public string Backdrop { get; set; } = "none";

        /// <summary>Overall window opacity, 0.2 - 1.0.</summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>Thickness of the dock outline.</summary>
        public double BorderThickness { get; set; } = 1;

        /// <summary>The live palette. Applying a preset overwrites this.</summary>
        public ThemeColors Colors { get; set; } = new ThemeColors();

        /// <summary>
        /// Kept aside so hand-tuned colours survive a trip through the presets — selecting Custom
        /// again brings them back instead of starting from whatever preset was last applied.
        /// </summary>
        public ThemeColors CustomColors { get; set; }

        // ---- motion ----------------------------------------------------
        /// <summary>How big a hovered icon gets. 1.0 disables the effect.</summary>
        public double HoverScale { get; set; } = 1.4;

        /// <summary>macOS-style falloff: neighbours of the hovered icon grow a little too.</summary>
        public bool Magnify { get; set; } = true;

        /// <summary>Width of the magnification falloff, as a multiple of one tile. Bigger = wider ripple.</summary>
        public double MagnifyFalloff { get; set; } = 1.0;

        /// <summary>Seconds the scale animation takes to settle.</summary>
        public double AnimationSpeed { get; set; } = 0.16;

        // ---- behaviour -------------------------------------------------
        /// <summary>
        /// Where the dock sits in the window stack.
        ///   "desktop" - pinned to the bottom, like a Rainmeter skin. Sits on the wallpaper and any
        ///               ordinary window covers it. This is the ricing default.
        ///   "normal"  - behaves like a regular window: raises when clicked.
        ///   "topmost" - floats over everything, including fullscreen apps.
        /// </summary>
        public string ZOrder { get; set; } = "desktop";

        /// <summary>
        /// Wallpaper mode only. Also restyles the window as WS_CHILD, which is what actually stops
        /// the shell treating it as top-level — reparenting alone is not enough. It is the riskier
        /// half of the trick, so it can be turned off independently, and the start-up guard clears
        /// it automatically if the dock ever fails to appear.
        /// </summary>
        public bool GlueChild { get; set; } = true;

        /// <summary>Legacy. Only consulted when ZOrder is missing from an older config.</summary>
        public bool AlwaysOnTop { get; set; } = true;

        public bool SnapToEdges { get; set; } = true;

        public bool AutoHide { get; set; } = false;

        /// <summary>When true the dock can't be dragged around by accident.</summary>
        public bool Locked { get; set; } = false;

        public bool SingleClickLaunch { get; set; } = true;

        // ---- persisted position (DIPs, -1 = centre bottom on first run) --
        public double Left { get; set; } = -1;
        public double Top { get; set; } = -1;

        // ---- contents ---------------------------------------------------
        public List<DockItem> Items { get; set; } = new List<DockItem>();
    }

    /// <summary>All colours are #AARRGGBB or #RRGGBB hex strings.</summary>
    public class ThemeColors
    {
        /// <summary>Slab fill. The alpha byte is what makes it read as glass.</summary>
        public string Background { get; set; } = "#D9101218";

        /// <summary>Hairline outline around the slab.</summary>
        public string Border { get; set; } = "#1FFFFFFF";

        /// <summary>Pip under a hovered icon, and the highlight colour in settings.</summary>
        public string Accent { get; set; } = "#C8CEDA";

        public string Text { get; set; } = "#E9ECF2";

        /// <summary>Rounded plate that fades in behind a hovered icon.</summary>
        public string TileHover { get; set; } = "#1AFFFFFF";

        /// <summary>Only used when Backdrop is "none".</summary>
        public string Shadow { get; set; } = "#B3000000";

        public ThemeColors Clone()
        {
            return new ThemeColors
            {
                Background = Background,
                Border = Border,
                Accent = Accent,
                Text = Text,
                TileHover = TileHover,
                Shadow = Shadow
            };
        }
    }
}
