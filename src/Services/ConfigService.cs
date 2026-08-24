using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dockyard.Models;

namespace Dockyard.Services
{
    public static class ConfigService
    {
        private static string _resolvedPath;

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>
        /// Portable first: a config.json sitting next to the exe wins, so the whole thing can live
        /// on a USB stick. Otherwise %APPDATA%\Dockyard\config.json.
        /// </summary>
        public static string ConfigPath
        {
            get
            {
                if (_resolvedPath != null) return _resolvedPath;

                try
                {
                    string exeDir = AppContext.BaseDirectory;
                    string portable = Path.Combine(exeDir, "config.json");
                    if (File.Exists(portable))
                    {
                        _resolvedPath = portable;
                        return _resolvedPath;
                    }
                }
                catch { /* fall through to AppData */ }

                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Dockyard");
                Directory.CreateDirectory(dir);
                _resolvedPath = Path.Combine(dir, "config.json");
                return _resolvedPath;
            }
        }

        // ------------------------------------------------------------------
        //  Start-up crash guard
        //
        //  Settings that change how the window itself is created can stop the dock appearing at
        //  all — and because they are saved, every subsequent launch fails the same way, with no
        //  UI left to turn them off. A marker file written at startup and cleared once the dock is
        //  safely on screen makes that recoverable: if the marker is still there next time, the
        //  last run never got that far, so the risky settings get reset before we try again.
        // ------------------------------------------------------------------
        private static string FlagPath
        {
            get
            {
                try { return Path.Combine(Path.GetDirectoryName(ConfigPath) ?? ".", "starting.flag"); }
                catch { return null; }
            }
        }

        public static bool LastStartFailed()
        {
            try { return FlagPath != null && File.Exists(FlagPath); }
            catch { return false; }
        }

        public static void MarkStarting()
        {
            try { if (FlagPath != null) File.WriteAllText(FlagPath, DateTime.Now.ToString("O")); }
            catch { }
        }

        public static void MarkStarted()
        {
            try { if (FlagPath != null && File.Exists(FlagPath)) File.Delete(FlagPath); }
            catch { }
        }

        /// <summary>Back off every setting that can prevent the window from showing.</summary>
        public static void FallBackToSafeWindow(DockConfig cfg)
        {
            cfg.ZOrder = "desktop";
            cfg.GlueChild = false;
            cfg.Backdrop = "none";
            cfg.Opacity = 1.0;
            Save(cfg);
        }

        public static DockConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    DockConfig cfg = JsonSerializer.Deserialize<DockConfig>(json, Options);
                    if (cfg != null)
                    {
                        if (cfg.Items == null) cfg.Items = new List<DockItem>();
                        if (cfg.Colors == null) cfg.Colors = new ThemeColors();
                        Migrate(cfg);
                        return cfg;
                    }
                }
            }
            catch { /* corrupt config: fall back to defaults rather than refusing to start */ }

            DockConfig fresh = new DockConfig();
            ApplyPreset(fresh, "obsidian");
            Save(fresh);
            return fresh;
        }

        /// <summary>
        /// Configs written by the first cut name themes that no longer exist, and default to an
        /// acrylic backdrop whose blur bleeds out past the rounded corners. Move those forward once,
        /// quietly, without touching anything the user has deliberately set since.
        /// </summary>
        private static void Migrate(DockConfig cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.Theme)) return;   // already on the new schema

            ApplyPreset(cfg, "obsidian");
            cfg.Backdrop = "none";
            if (cfg.CornerRadius > 34) cfg.CornerRadius = 20;
            Save(cfg);
        }

        public static void Save(DockConfig cfg)
        {
            try
            {
                string json = JsonSerializer.Serialize(cfg, Options);
                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, json);
                // Atomic-ish replace so a crash mid-write can't wipe the dock.
                if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
                else File.Move(tmp, ConfigPath);
            }
            catch { /* not worth interrupting the user over */ }
        }

        // ------------------------------------------------------------------
        //  Built-in presets. Applied over the current config; layout and items
        //  are left alone so switching themes never loses your tiles.
        // ------------------------------------------------------------------
        /// <summary>
        /// Deliberately restrained. Every one of these is a near-neutral slab with a single quiet
        /// accent — the dock should sit under your wallpaper's colours, not compete with them.
        /// "custom" is not listed here; it is whatever the colour editor last produced.
        /// </summary>
        public static readonly string[] PresetNames =
        {
            "obsidian", "graphite", "frost", "nord", "dune", "ink", "moss", "porcelain"
        };

        /// <summary>Human-readable one-liners, shown under each swatch in the settings window.</summary>
        public static string PresetBlurb(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "obsidian": return "Near-black, barely-there edge";
                case "graphite": return "Warm grey, soft and matte";
                case "frost": return "Pale glass, cool highlight";
                case "nord": return "Polar night, muted blue";
                case "dune": return "Warm sand on charcoal";
                case "ink": return "Deep indigo, dusk violet";
                case "moss": return "Forest grey, sage accent";
                case "porcelain": return "Light, for pale wallpapers";
                case "custom": return "Your own palette";
                default: return "";
            }
        }

        public static void ApplyPreset(DockConfig cfg, string name)
        {
            string key = (name ?? "").ToLowerInvariant();

            // Custom restores the saved hand-tuned palette rather than defining one.
            if (key == "custom")
            {
                cfg.Theme = "custom";
                if (cfg.CustomColors != null) cfg.Colors = cfg.CustomColors.Clone();
                return;
            }

            ThemeColors c = PresetColors(key);
            if (c == null) return;

            cfg.Theme = key;
            cfg.Colors = c;
            cfg.BorderThickness = 1;
        }

        public static ThemeColors PresetColors(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "obsidian":
                    return new ThemeColors
                    {
                        Background = "#D9101218",
                        Border = "#1FFFFFFF",
                        Accent = "#C8CEDA",
                        Text = "#E9ECF2",
                        TileHover = "#1AFFFFFF",
                        Shadow = "#B3000000"
                    };

                case "graphite":
                    return new ThemeColors
                    {
                        Background = "#D92A2724",
                        Border = "#1AF2E9E0",
                        Accent = "#D8CFC4",
                        Text = "#EDE7E0",
                        TileHover = "#1FF2E9E0",
                        Shadow = "#A6000000"
                    };

                case "frost":
                    return new ThemeColors
                    {
                        Background = "#B8202833",
                        Border = "#33BFD4E6",
                        Accent = "#9EC5DE",
                        Text = "#E8F0F6",
                        TileHover = "#22BFD4E6",
                        Shadow = "#99060A10"
                    };

                case "nord":
                    return new ThemeColors
                    {
                        Background = "#D92E3440",
                        Border = "#334C566A",
                        Accent = "#88C0D0",
                        Text = "#ECEFF4",
                        TileHover = "#2E4C566A",
                        Shadow = "#A60B0E14"
                    };

                case "dune":
                    return new ThemeColors
                    {
                        Background = "#D91C1917",
                        Border = "#26D9B48F",
                        Accent = "#D9B48F",
                        Text = "#EFE6DA",
                        TileHover = "#1FD9B48F",
                        Shadow = "#A6100D0A"
                    };

                case "ink":
                    return new ThemeColors
                    {
                        Background = "#D9141626",
                        Border = "#2699A0D9",
                        Accent = "#A9A5E0",
                        Text = "#DFE2F2",
                        TileHover = "#1F99A0D9",
                        Shadow = "#B30A0B14"
                    };

                case "moss":
                    return new ThemeColors
                    {
                        Background = "#D9161A17",
                        Border = "#26A7C0A8",
                        Accent = "#A7C0A8",
                        Text = "#E4EBE4",
                        TileHover = "#1FA7C0A8",
                        Shadow = "#A6080B09"
                    };

                case "porcelain":
                    return new ThemeColors
                    {
                        Background = "#D9F2F1EE",
                        Border = "#1F000000",
                        Accent = "#5A6472",
                        Text = "#23262C",
                        TileHover = "#14000000",
                        Shadow = "#4D000000"
                    };
            }

            return null;
        }
    }
}
