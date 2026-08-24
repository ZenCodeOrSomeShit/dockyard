using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Dockyard.Models;
using Dockyard.Services;

namespace Dockyard
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _dock;
        private DockConfig Cfg => _dock.Config;

        private bool _suppress;

        // Colour editor state for the role currently being edited.
        private string _role = "Background";
        private double _h, _s, _v;
        private byte _a = 255;

        private Slider _hue, _sat, _val, _alpha;
        private TextBox _hexField;
        private Border _preview;
        private readonly Dictionary<string, Border> _swatches = new Dictionary<string, Border>();

        private static readonly string[] Roles =
        {
            "Background", "Border", "Accent", "Text", "TileHover", "Shadow"
        };

        private static string RoleLabel(string role)
        {
            switch (role)
            {
                case "Background": return "Slab";
                case "Border": return "Outline";
                case "Accent": return "Accent";
                case "Text": return "Label";
                case "TileHover": return "Hover plate";
                case "Shadow": return "Shadow";
                default: return role;
            }
        }

        private static string RoleHint(string role)
        {
            switch (role)
            {
                case "Background": return "The dock's fill. Its alpha is what makes it read as glass.";
                case "Border": return "Hairline around the slab. Keep it faint.";
                case "Accent": return "The pip under a hovered icon, and this window's highlights.";
                case "Text": return "Labels under the icons.";
                case "TileHover": return "The rounded plate that fades in behind a hovered icon.";
                case "Shadow": return "Only drawn when the backdrop is set to None.";
                default: return "";
            }
        }

        // ==================================================================
        public SettingsWindow(MainWindow dock)
        {
            _dock = dock;
            InitializeComponent();

            CloseBtn.Click += (s, e) => Close();
            TitleBar.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            NavTheme.Checked += (s, e) => ShowPane(BuildThemePane);
            NavColours.Checked += (s, e) => ShowPane(BuildColourPane);
            NavLayout.Checked += (s, e) => ShowPane(BuildLayoutPane);
            NavMotion.Checked += (s, e) => ShowPane(BuildMotionPane);
            NavBehaviour.Checked += (s, e) => ShowPane(BuildBehaviourPane);
            NavApps.Checked += (s, e) => ShowPane(BuildAppsPane);

            SyncAccent();
            ShowPane(BuildThemePane);
        }

        private void ShowPane(Func<UIElement> build)
        {
            PaneHost.Children.Clear();
            PaneHost.Children.Add(build());
            Scroller.ScrollToTop();
        }

        /// <summary>Push the change into the live dock and persist it.</summary>
        private void Apply()
        {
            if (_suppress) return;
            SyncAccent();
            _dock.ApplyLive();
        }

        /// <summary>Make this window's highlight colour follow the dock's accent.</summary>
        private void SyncAccent()
        {
            Color a = ColorUtil.Parse(Cfg.Colors?.Accent, Color.FromRgb(0xC8, 0xCE, 0xDA));
            SolidColorBrush b = new SolidColorBrush(Color.FromRgb(a.R, a.G, a.B));
            b.Freeze();
            Resources["UiAccent"] = b;
        }

        // ==================================================================
        //  Building blocks
        // ==================================================================
        private static Border Card(string heading, params UIElement[] rows)
        {
            StackPanel sp = new StackPanel();

            if (!string.IsNullOrEmpty(heading))
            {
                TextBlock t = new TextBlock { Text = heading.ToUpperInvariant() };
                t.SetResourceReference(StyleProperty, "SectionText");
                sp.Children.Add(t);
            }

            foreach (UIElement row in rows)
            {
                if (row != null) sp.Children.Add(row);
            }

            Border card = new Border { Child = sp };
            card.SetResourceReference(StyleProperty, "Card");
            return card;
        }

        /// <summary>label + hint on the left, control on the right.</summary>
        private static Grid Row(string label, string hint, UIElement control)
        {
            Grid g = new Grid { Margin = new Thickness(0, 7, 0, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock lab = new TextBlock { Text = label };
            lab.SetResourceReference(StyleProperty, "LabelText");
            left.Children.Add(lab);

            if (!string.IsNullOrEmpty(hint))
            {
                TextBlock h = new TextBlock { Text = hint, MaxWidth = 330 };
                h.SetResourceReference(StyleProperty, "HintText");
                left.Children.Add(h);
            }

            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            FrameworkElement fe = control as FrameworkElement;
            if (fe != null)
            {
                fe.VerticalAlignment = VerticalAlignment.Center;
                fe.Margin = new Thickness(18, 0, 0, 0);
            }
            Grid.SetColumn(control, 1);
            g.Children.Add(control);

            return g;
        }

        private Grid SliderRow(string label, string hint, double min, double max, double value,
            string format, Action<double> onChange, double width = 200)
        {
            Slider sl = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, value)),
                Width = width
            };
            sl.SetResourceReference(StyleProperty, "Slim");

            TextBlock readout = new TextBlock { Text = value.ToString(format) };
            readout.SetResourceReference(StyleProperty, "ValueText");

            sl.ValueChanged += (s, e) =>
            {
                readout.Text = sl.Value.ToString(format);
                if (_suppress) return;
                onChange(sl.Value);
                Apply();
            };

            StackPanel host = new StackPanel { Orientation = Orientation.Horizontal };
            host.Children.Add(sl);
            readout.Margin = new Thickness(12, 0, 0, 0);
            host.Children.Add(readout);

            return Row(label, hint, host);
        }

        private Grid SwitchRow(string label, string hint, bool value, Action<bool> onChange)
        {
            ToggleButton tb = new ToggleButton { IsChecked = value };
            tb.SetResourceReference(StyleProperty, "Switch");
            tb.Checked += (s, e) => { if (!_suppress) { onChange(true); Apply(); } };
            tb.Unchecked += (s, e) => { if (!_suppress) { onChange(false); Apply(); } };
            return Row(label, hint, tb);
        }

        private Grid SegmentRow(string label, string hint, string[] values, string[] captions,
            string current, Action<string> onChange)
        {
            StackPanel strip = new StackPanel { Orientation = Orientation.Horizontal };
            string group = "seg" + Guid.NewGuid().ToString("N");

            for (int i = 0; i < values.Length; i++)
            {
                string v = values[i];
                RadioButton rb = new RadioButton
                {
                    Content = captions[i],
                    GroupName = group,
                    IsChecked = string.Equals(v, current, StringComparison.OrdinalIgnoreCase)
                };
                rb.SetResourceReference(StyleProperty, "Segment");
                rb.Checked += (s, e) => { if (!_suppress) { onChange(v); Apply(); } };
                strip.Children.Add(rb);
            }

            Border host = new Border { Child = strip };
            host.SetResourceReference(StyleProperty, "SegmentHost");
            return Row(label, hint, host);
        }

        private static Button TextButton(string caption, RoutedEventHandler click, bool accent = false)
        {
            Button b = new Button { Content = caption };
            b.SetResourceReference(StyleProperty, accent ? "AccentButton" : "GhostButton");
            b.Click += click;
            return b;
        }

        // ==================================================================
        //  Theme pane
        // ==================================================================
        private UIElement BuildThemePane()
        {
            StackPanel root = new StackPanel();

            // --- preset gallery ---
            WrapPanel gallery = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };

            List<string> names = new List<string>(ConfigService.PresetNames);
            names.Add("custom");

            foreach (string name in names)
            {
                gallery.Children.Add(BuildSwatchCard(name));
            }

            root.Children.Add(Card("Presets", gallery));

            // --- backdrop ---
            root.Children.Add(Card("Surface",
                SegmentRow("Backdrop",
                    "None lets WPF draw the slab, so the corners are exact at any radius. " +
                    "The blurred modes hand the surface to Windows, which blurs the whole window " +
                    "rectangle and pins the corners to its own radius.",
                    new[] { "none", "blur", "acrylic" },
                    new[] { "None", "Blur", "Acrylic" },
                    Cfg.Backdrop,
                    v => Cfg.Backdrop = v),

                SliderRow("Corner radius", "Ignored while a blurred backdrop is on.",
                    0, 34, Cfg.CornerRadius, "0", v => Cfg.CornerRadius = Math.Round(v)),

                SliderRow("Outline", "Thickness of the hairline around the slab.",
                    0, 3, Cfg.BorderThickness, "0.0", v => Cfg.BorderThickness = v),

                SliderRow("Opacity", "Overall transparency of the whole dock.",
                    0.3, 1.0, Cfg.Opacity, "0.00", v => Cfg.Opacity = v)));

            return root;
        }

        private Border BuildSwatchCard(string name)
        {
            ThemeColors tc = name == "custom"
                ? (Cfg.CustomColors ?? Cfg.Colors)
                : ConfigService.PresetColors(name);

            if (tc == null) tc = Cfg.Colors;

            Color bg = ColorUtil.Parse(tc.Background, Colors.Black);
            Color ac = ColorUtil.Parse(tc.Accent, Colors.White);
            Color tx = ColorUtil.Parse(tc.Text, Colors.White);
            Color bd = ColorUtil.Parse(tc.Border, Colors.Transparent);

            // Miniature of the dock rather than a flat colour chip.
            Border chip = new Border
            {
                Height = 54,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(bd),
                BorderThickness = new Thickness(1)
            };

            StackPanel dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            for (int i = 0; i < 3; i++)
            {
                dots.Children.Add(new Border
                {
                    Width = 14,
                    Height = 14,
                    Margin = new Thickness(4, 0, 4, 0),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(i == 1 ? ac : tx),
                    Opacity = i == 1 ? 1.0 : 0.4
                });
            }
            chip.Child = dots;

            TextBlock title = new TextBlock
            {
                Text = name == "custom" ? "Custom" : char.ToUpperInvariant(name[0]) + name.Substring(1),
                Margin = new Thickness(2, 9, 0, 0)
            };
            title.SetResourceReference(StyleProperty, "LabelText");

            TextBlock blurb = new TextBlock
            {
                Text = ConfigService.PresetBlurb(name),
                Margin = new Thickness(2, 1, 0, 0)
            };
            blurb.SetResourceReference(StyleProperty, "HintText");

            StackPanel body = new StackPanel { Width = 168 };
            body.Children.Add(chip);
            body.Children.Add(title);
            body.Children.Add(blurb);

            bool selected = string.Equals(Cfg.Theme, name, StringComparison.OrdinalIgnoreCase);

            Border card = new Border
            {
                Child = body,
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 12, 12),
                CornerRadius = new CornerRadius(12),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Background = selected ? new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF))
                                      : Brushes.Transparent,
                BorderBrush = selected ? new SolidColorBrush(ColorUtil.Parse(Cfg.Colors.Accent, Colors.White))
                                       : new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))
            };

            card.MouseLeftButtonUp += (s, e) =>
            {
                ConfigService.ApplyPreset(Cfg, name);
                Apply();
                ShowPane(BuildThemePane);   // repaint selection state
            };

            return card;
        }

        // ==================================================================
        //  Colour pane
        // ==================================================================
        private UIElement BuildColourPane()
        {
            StackPanel root = new StackPanel();

            // --- role chips ---
            WrapPanel chips = new WrapPanel();
            _swatches.Clear();

            foreach (string role in Roles)
            {
                string local = role;
                Border sw = new Border
                {
                    Width = 40,
                    Height = 40,
                    CornerRadius = new CornerRadius(10),
                    Background = ColorUtil.Checkerboard(6),
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Transparent,
                    Cursor = Cursors.Hand
                };

                Border fill = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(ColorOf(local))
                };
                sw.Child = fill;

                StackPanel cell = new StackPanel
                {
                    Width = 96,
                    Margin = new Thickness(0, 0, 10, 12)
                };
                cell.Children.Add(sw);

                TextBlock cap = new TextBlock { Text = RoleLabel(local), Margin = new Thickness(1, 6, 0, 0) };
                cap.SetResourceReference(StyleProperty, "HintText");
                cell.Children.Add(cap);

                sw.MouseLeftButtonUp += (s, e) => SelectRole(local);
                _swatches[local] = sw;
                chips.Children.Add(cell);
            }

            root.Children.Add(Card("What to colour", chips));

            // --- editor ---
            _preview = new Border
            {
                Height = 58,
                CornerRadius = new CornerRadius(10),
                Background = ColorUtil.Checkerboard(8),
                Margin = new Thickness(0, 0, 0, 14)
            };
            Border pv = new Border { CornerRadius = new CornerRadius(10) };
            _preview.Child = pv;

            _hue = MakeRamp(0, 360);
            _sat = MakeRamp(0, 1);
            _val = MakeRamp(0, 1);
            _alpha = MakeRamp(0, 255);

            _hexField = new TextBox { Width = 116 };
            _hexField.SetResourceReference(StyleProperty, "Field");
            _hexField.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter) return;
                Color c = ColorUtil.Parse(_hexField.Text.Trim(), ColorOf(_role));
                SetRoleColor(c);
                LoadRole(_role);
            };
            _hexField.LostFocus += (s, e) =>
            {
                Color c = ColorUtil.Parse(_hexField.Text.Trim(), ColorOf(_role));
                SetRoleColor(c);
                LoadRole(_role);
            };

            StackPanel editor = new StackPanel();
            editor.Children.Add(_preview);
            editor.Children.Add(Row("Hue", null, _hue));
            editor.Children.Add(Row("Saturation", null, _sat));
            editor.Children.Add(Row("Brightness", null, _val));
            editor.Children.Add(Row("Alpha", "0 is invisible, 255 is solid.", _alpha));
            editor.Children.Add(Row("Hex", "#AARRGGBB. Press Enter to apply.", _hexField));

            StackPanel actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            Button revert = TextButton("Revert this colour", (s, e) =>
            {
                ThemeColors preset = ConfigService.PresetColors(Cfg.Theme)
                                     ?? ConfigService.PresetColors("obsidian");
                if (preset != null)
                {
                    SetRoleColor(ColorUtil.Parse(Get(preset, _role), ColorOf(_role)));
                    LoadRole(_role);
                }
            });
            revert.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(revert);

            actions.Children.Add(TextButton("Save as Custom", (s, e) =>
            {
                Cfg.Theme = "custom";
                Cfg.CustomColors = Cfg.Colors.Clone();
                Apply();
            }, true));

            editor.Children.Add(actions);

            root.Children.Add(Card("Editing: " + RoleLabel(_role), editor));

            LoadRole(_role);
            return root;
        }

        private Slider MakeRamp(double min, double max)
        {
            Slider s = new Slider { Minimum = min, Maximum = max, Width = 300 };
            s.SetResourceReference(StyleProperty, "RampSlider");
            s.ValueChanged += (a, b) =>
            {
                if (_suppress) return;
                _h = _hue.Value;
                _s = _sat.Value;
                _v = _val.Value;
                _a = (byte)Math.Round(_alpha.Value);
                SetRoleColor(ColorUtil.FromHsv(_h, _s, _v, _a));
                RefreshEditorVisuals();
            };
            return s;
        }

        private void SelectRole(string role)
        {
            _role = role;
            ShowPane(BuildColourPane);
        }

        private void LoadRole(string role)
        {
            Color c = ColorOf(role);
            ColorUtil.ToHsv(c, out _h, out _s, out _v);
            _a = c.A;

            _suppress = true;
            _hue.Value = _h;
            _sat.Value = _s;
            _val.Value = _v;
            _alpha.Value = _a;
            _hexField.Text = ColorUtil.ToHex(c);
            _suppress = false;

            foreach (KeyValuePair<string, Border> kv in _swatches)
            {
                kv.Value.BorderBrush = kv.Key == role
                    ? new SolidColorBrush(ColorUtil.Parse(Cfg.Colors.Accent, Colors.White))
                    : Brushes.Transparent;
            }

            RefreshEditorVisuals();
        }

        /// <summary>Repaint the ramps so each slider previews the result of moving it.</summary>
        private void RefreshEditorVisuals()
        {
            Color current = ColorUtil.FromHsv(_h, _s, _v, _a);

            if (_preview != null)
            {
                Border inner = _preview.Child as Border;
                if (inner != null) inner.Background = new SolidColorBrush(current);
            }

            if (_hue != null) _hue.Background = ColorUtil.HueRamp();

            if (_sat != null)
                _sat.Background = ColorUtil.Ramp(
                    ColorUtil.FromHsv(_h, 0, _v, 255),
                    ColorUtil.FromHsv(_h, 1, _v, 255));

            if (_val != null)
                _val.Background = ColorUtil.Ramp(
                    ColorUtil.FromHsv(_h, _s, 0, 255),
                    ColorUtil.FromHsv(_h, _s, 1, 255));

            if (_alpha != null)
                _alpha.Background = ColorUtil.Ramp(
                    Color.FromArgb(0, current.R, current.G, current.B),
                    Color.FromArgb(255, current.R, current.G, current.B));

            if (_hexField != null && !_hexField.IsKeyboardFocusWithin)
                _hexField.Text = ColorUtil.ToHex(current);

            Border sw;
            if (_swatches.TryGetValue(_role, out sw))
            {
                Border fill = sw.Child as Border;
                if (fill != null) fill.Background = new SolidColorBrush(current);
            }
        }

        private Color ColorOf(string role)
        {
            return ColorUtil.Parse(Get(Cfg.Colors, role), Colors.Gray);
        }

        private static string Get(ThemeColors t, string role)
        {
            switch (role)
            {
                case "Background": return t.Background;
                case "Border": return t.Border;
                case "Accent": return t.Accent;
                case "Text": return t.Text;
                case "TileHover": return t.TileHover;
                case "Shadow": return t.Shadow;
                default: return t.Background;
            }
        }

        private void SetRoleColor(Color c)
        {
            string hex = ColorUtil.ToHex(c);
            switch (_role)
            {
                case "Background": Cfg.Colors.Background = hex; break;
                case "Border": Cfg.Colors.Border = hex; break;
                case "Accent": Cfg.Colors.Accent = hex; break;
                case "Text": Cfg.Colors.Text = hex; break;
                case "TileHover": Cfg.Colors.TileHover = hex; break;
                case "Shadow": Cfg.Colors.Shadow = hex; break;
            }

            // Hand-editing a colour means you are no longer on a preset.
            Cfg.Theme = "custom";
            Cfg.CustomColors = Cfg.Colors.Clone();
            Apply();
        }

        // ==================================================================
        //  Layout pane
        // ==================================================================
        private UIElement BuildLayoutPane()
        {
            StackPanel root = new StackPanel();

            root.Children.Add(Card("Shape",
                SegmentRow("Direction", "Vertical docks sit nicely against a screen edge.",
                    new[] { "Horizontal", "Vertical" },
                    new[] { "Horizontal", "Vertical" },
                    Cfg.Orientation, v => Cfg.Orientation = v),

                SegmentRow("Rows", "Wrap tiles onto more than one line when a row fills up. " +
                    "Magnification follows the line under the cursor.",
                    new[] { "1", "2", "3", "4" },
                    new[] { "1", "2", "3", "4" },
                    Math.Max(1, Math.Min(4, Cfg.Rows)).ToString(),
                    v => { int r; int.TryParse(v, out r); Cfg.Rows = Math.Max(1, Math.Min(4, r)); }),

                SliderRow("Icon size", "Ctrl+scroll over the dock does this too.",
                    24, 128, Cfg.IconSize, "0", v => Cfg.IconSize = Math.Round(v)),

                SliderRow("Spacing", "Gap between tiles.",
                    0, 48, Cfg.TileSpacing, "0", v => Cfg.TileSpacing = Math.Round(v)),

                SliderRow("Padding", "Breathing room inside the slab.",
                    0, 48, Cfg.Padding, "0", v => Cfg.Padding = Math.Round(v))));

            root.Children.Add(Card("Labels",
                SwitchRow("Show labels", "Names under each icon.",
                    Cfg.ShowLabels, v => Cfg.ShowLabels = v),

                SliderRow("Label size", null,
                    7, 18, Cfg.LabelSize, "0.0", v => Cfg.LabelSize = v)));

            return root;
        }

        // ==================================================================
        //  Motion pane
        // ==================================================================
        private UIElement BuildMotionPane()
        {
            StackPanel root = new StackPanel();

            root.Children.Add(Card("Magnification",
                SwitchRow("Ripple", "Neighbouring icons grow too, and the row spreads apart to " +
                                    "make room. Off means only the icon under the cursor grows.",
                    Cfg.Magnify, v => Cfg.Magnify = v),

                SliderRow("Hover scale", "How big the icon under the cursor gets. 1.0 disables it.",
                    1.0, 2.2, Cfg.HoverScale, "0.00", v => Cfg.HoverScale = v),

                SliderRow("Ripple width", "How many tiles either side get pulled along.",
                    0.3, 3.0, Cfg.MagnifyFalloff, "0.0", v => Cfg.MagnifyFalloff = v),

                SliderRow("Settle time", "Seconds for icons to fall back to rest.",
                    0.03, 0.6, Cfg.AnimationSpeed, "0.00", v => Cfg.AnimationSpeed = v)));

            return root;
        }

        // ==================================================================
        //  Behaviour pane
        // ==================================================================
        private UIElement BuildBehaviourPane()
        {
            StackPanel root = new StackPanel();

            root.Children.Add(Card("Window",
                SegmentRow("Layer",
                    "On desktop pins the dock below every other window: it never floats over your " +
                    "work and won't steal focus. Glued tries to make it a child of the desktop so " +
                    "Show Desktop can't hide it — it doesn't work on every Windows build, and it " +
                    "costs the acrylic backdrop. If it fails the dock falls back to On desktop " +
                    "on its own.",
                    new[] { "desktop", "normal", "topmost", "wallpaper" },
                    new[] { "On desktop", "Normal", "On top", "Glued" },
                    CurrentLayer(), v => { Cfg.ZOrder = v; Cfg.AlwaysOnTop = v == "topmost"; }),

                SwitchRow("Snap to edges", "Snaps to screen edges and the horizontal centre when dropped.",
                    Cfg.SnapToEdges, v => Cfg.SnapToEdges = v),

                SwitchRow("Auto-hide", "Slides off the nearest edge when the cursor leaves.",
                    Cfg.AutoHide, v => Cfg.AutoHide = v),

                SwitchRow("Lock position", "Stops the dock being dragged by accident.",
                    Cfg.Locked, v => Cfg.Locked = v)));

            // Ground truth for the Glued mode, straight from the OS. If this says anything other
            // than "Glued to ...", the reparent did not take and Show Desktop will still hide it.
            StackPanel diag = new StackPanel { Orientation = Orientation.Horizontal };
            Button refresh = TextButton("Refresh", (s, e) => ShowPane(BuildBehaviourPane));
            refresh.Margin = new Thickness(0, 0, 8, 0);
            diag.Children.Add(refresh);
            diag.Children.Add(TextButton("Re-attach", (s, e) =>
            {
                _dock.ReattachToDesktop();
                ShowPane(BuildBehaviourPane);
            }));

            root.Children.Add(Card("Attachment",
                Row("Current state", _dock.AttachmentStatus, diag),
                Row("Live window state", _dock.DiagnosticLine, new TextBlock()),

                SwitchRow("Restyle as a child window",
                    "The half of the glue that actually stops the shell treating the dock as a " +
                    "top-level window. It is also the risky half — if the dock ever fails to " +
                    "appear on startup, this gets switched off automatically and the layer resets.",
                    Cfg.GlueChild,
                    v =>
                    {
                        Cfg.GlueChild = v;
                        _dock.ReattachToDesktop();
                    })));

            root.Children.Add(Card("Startup",
                SwitchRow("Start with Windows",
                    "Registers the dock under your account's startup entries. It reopens exactly " +
                    "where you left it — the position is written the moment you finish dragging, " +
                    "not on exit, so it survives a shutdown that never gave the app a chance to " +
                    "close cleanly.",
                    StartupService.IsEnabled(),
                    v =>
                    {
                        if (!StartupService.SetEnabled(v))
                        {
                            MessageBox.Show(
                                "Couldn't update the startup entry. Something is likely blocking " +
                                "writes to the registry Run key.",
                                "Dockyard", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    })));

            StackPanel fileRow = new StackPanel { Orientation = Orientation.Horizontal };
            Button openCfg = TextButton("Open config.json", (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(ConfigService.ConfigPath)
                        { UseShellExecute = true });
                }
                catch { }
            });
            openCfg.Margin = new Thickness(0, 0, 8, 0);
            fileRow.Children.Add(openCfg);
            fileRow.Children.Add(TextButton("Reset position", (s, e) =>
            {
                Cfg.Left = -1;
                Cfg.Top = -1;
                _dock.ResetPosition();
            }));

            root.Children.Add(Card("Config", Row("Settings file",
                ConfigService.ConfigPath, fileRow)));

            return root;
        }

        private string CurrentLayer()
        {
            string z = (Cfg.ZOrder ?? "").ToLowerInvariant();
            if (z == "desktop" || z == "normal" || z == "topmost" || z == "wallpaper") return z;
            return Cfg.AlwaysOnTop ? "topmost" : "normal";
        }

        // ==================================================================
        //  Apps pane
        // ==================================================================
        private UIElement BuildAppsPane()
        {
            StackPanel root = new StackPanel();
            StackPanel list = new StackPanel();

            if (_dock.Items.Count == 0)
            {
                TextBlock empty = new TextBlock
                {
                    Text = "Nothing on the dock yet. Drag an .exe or a shortcut onto it, " +
                           "or use the button below.",
                    Margin = new Thickness(0, 2, 0, 10)
                };
                empty.SetResourceReference(StyleProperty, "HintText");
                list.Children.Add(empty);
            }

            for (int i = 0; i < _dock.Items.Count; i++)
            {
                DockItem item = _dock.Items[i];
                int index = i;

                Grid row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Image icon = new Image
                {
                    Source = item.Icon,
                    Width = 30,
                    Height = 30,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                Grid.SetColumn(icon, 0);
                row.Children.Add(icon);

                StackPanel text = new StackPanel
                {
                    Margin = new Thickness(12, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                TextBlock name = new TextBlock { Text = item.Name };
                name.SetResourceReference(StyleProperty, "LabelText");
                TextBlock path = new TextBlock
                {
                    Text = item.Path,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap
                };
                path.SetResourceReference(StyleProperty, "HintText");
                text.Children.Add(name);
                text.Children.Add(path);
                Grid.SetColumn(text, 1);
                row.Children.Add(text);

                StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };

                Button up = new Button { Content = "↑" };
                up.SetResourceReference(StyleProperty, "IconButton");
                up.IsEnabled = index > 0;
                up.Click += (s, e) =>
                {
                    if (index > 0) { _dock.Items.Move(index, index - 1); ShowPane(BuildAppsPane); }
                };

                Button down = new Button { Content = "↓" };
                down.SetResourceReference(StyleProperty, "IconButton");
                down.IsEnabled = index < _dock.Items.Count - 1;
                down.Click += (s, e) =>
                {
                    if (index < _dock.Items.Count - 1) { _dock.Items.Move(index, index + 1); ShowPane(BuildAppsPane); }
                };

                Button remove = new Button { Content = "✕" };
                remove.SetResourceReference(StyleProperty, "IconButton");
                remove.Click += (s, e) => { _dock.Items.Remove(item); ShowPane(BuildAppsPane); };

                buttons.Children.Add(up);
                buttons.Children.Add(down);
                buttons.Children.Add(remove);
                Grid.SetColumn(buttons, 2);
                row.Children.Add(buttons);

                list.Children.Add(row);
            }

            Button add = TextButton("Add an app…", (s, e) =>
            {
                Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Add to dock",
                    Filter = "Programs and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd|All files|*.*",
                    Multiselect = true
                };
                if (dlg.ShowDialog() == true)
                {
                    foreach (string f in dlg.FileNames) _dock.AddPath(f);
                    ShowPane(BuildAppsPane);
                }
            }, true);
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0, 12, 0, 0);
            list.Children.Add(add);

            root.Children.Add(Card("On the dock", list));
            return root;
        }
    }
}
