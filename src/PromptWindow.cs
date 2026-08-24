using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dockyard
{
    /// <summary>
    /// Tiny themed text prompt. WPF ships no InputBox, and dragging in WinForms just for one
    /// dialog isn't worth it.
    /// </summary>
    internal class PromptWindow : Window
    {
        private readonly TextBox _box;

        public string Value => _box.Text;

        public PromptWindow(string title, string label, string initial, Brush background, Brush foreground, Brush accent)
        {
            Title = title;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.Height;
            Width = 380;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Border shell = new Border
            {
                Background = background,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 4,
                    Opacity = 0.55,
                    Color = Colors.Black
                }
            };

            StackPanel stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = foreground,
                FontSize = 12.5,
                Margin = new Thickness(2, 0, 0, 8)
            });

            _box = new TextBox
            {
                Text = initial ?? "",
                Foreground = foreground,
                CaretBrush = accent,
                SelectionBrush = accent,
                Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13
            };
            stack.Children.Add(_box);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            Button cancel = MakeButton("Cancel", foreground, false, accent);
            cancel.Click += (s, e) => { DialogResult = false; };
            cancel.Margin = new Thickness(0, 0, 8, 0);

            Button ok = MakeButton("OK", foreground, true, accent);
            ok.Click += (s, e) => { DialogResult = true; };
            ok.IsDefault = true;

            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            stack.Children.Add(buttons);

            shell.Child = stack;
            Content = shell;
            Margin = new Thickness(0);

            Loaded += (s, e) => { _box.Focus(); _box.SelectAll(); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) DialogResult = false; };
            MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
        }

        private static Button MakeButton(string text, Brush fg, bool primary, Brush accent)
        {
            Button b = new Button
            {
                Content = text,
                Foreground = primary ? Brushes.Black : fg,
                Padding = new Thickness(16, 6, 16, 6),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 12.5
            };
            Brush fill = primary ? accent : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            b.Background = fill;

            // Rounded corners without a full control template file.
            ControlTemplate t = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, fill);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.PaddingProperty, new Thickness(16, 6, 16, 6));
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            t.VisualTree = border;
            b.Template = t;

            return b;
        }

        /// <summary>Shows the prompt and returns the entered text, or null if cancelled.</summary>
        public static string Ask(Window owner, string title, string label, string initial,
            Brush background, Brush foreground, Brush accent)
        {
            PromptWindow w = new PromptWindow(title, label, initial, background, foreground, accent);
            if (owner != null && owner.IsLoaded) w.Owner = owner;
            bool? ok = w.ShowDialog();
            return ok == true ? w.Value : null;
        }
    }
}
