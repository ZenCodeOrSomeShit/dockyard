using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Dockyard.Interop;
using Dockyard.Models;
using Dockyard.Services;

namespace Dockyard
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        // ------------------------------------------------------------------
        //  State
        // ------------------------------------------------------------------
        public DockConfig Config { get; private set; }
        public ObservableCollection<DockItem> Items { get; } = new ObservableCollection<DockItem>();

        private IntPtr _hwnd = IntPtr.Zero;

        private DockItem _pressItem;
        private Point _pressPoint;
        private bool _reordering;

        private bool _dragging;
        private Point _dragStartScreen;
        private double _dragOriginLeft, _dragOriginTop;

        private readonly DispatcherTimer _saveTimer = new DispatcherTimer();
        private readonly DispatcherTimer _hideTimer = new DispatcherTimer();
        private readonly DispatcherTimer _guardTimer = new DispatcherTimer();

        private bool _closing;
        private bool _shown;
        private bool _recovered;
        private EventWaitHandle _showSignal;
        private int _reattachAttempts;
        private int _lastAttachError;
        private int _uncloakCount;

        /// <summary>Everything worth knowing about the window's current state, in one line.
        /// Shown in Settings so a screenshot is enough to tell what is actually happening.</summary>
        public string DiagnosticLine
        {
            get
            {
                int cloak = Native.GetCloaked(_hwnd);
                return "layer " + ZMode
                     + " · parent " + Native.ClassNameOf(Native.GetParent(_hwnd))
                     + " · " + Native.DescribeCloak(cloak)
                     + " · " + WindowState
                     + (IsVisible ? " · visible" : " · not visible")
                     + (_uncloakCount > 0 ? " · uncloaked " + _uncloakCount + "x" : "");
            }
        }

        private bool _autoHidden;
        private double _shownLeft, _shownTop;   // always screen coordinates
        private bool _userMoved;
        private DispatcherTimer _proximityTimer;

        // Set once the window has been reparented into the desktop. While attached, Left/Top are
        // relative to the host's client area rather than the screen, so everything that cares about
        // position goes through ScreenLeft/ScreenTop/PlaceWindow instead of touching Left/Top.
        private bool _attached;
        private IntPtr _desktopHost = IntPtr.Zero;
        private Vector _hostOffset;
        private uint _taskbarCreatedMsg;

        /// <summary>True when the dock is closing because someone asked it to, not because its
        /// parent window was destroyed underneath it.</summary>
        public bool UserClosed { get; private set; }

        // ------------------------------------------------------------------
        //  Bindable layout values (DataContext is this window)
        // ------------------------------------------------------------------
        public double IconSize => Config.IconSize;
        public double LabelSize => Config.LabelSize;
        public double LabelMaxWidth => Math.Max(48, Config.IconSize * 1.7);
        public CornerRadius TileCorner => new CornerRadius(Math.Max(4, Config.IconSize * 0.24));
        public Visibility LabelVisibility => Config.ShowLabels ? Visibility.Visible : Visibility.Collapsed;
        public Orientation DockOrientation =>
            string.Equals(Config.Orientation, "Vertical", StringComparison.OrdinalIgnoreCase)
                ? Orientation.Vertical : Orientation.Horizontal;

        public Thickness TileMargin
        {
            get
            {
                double h = Config.TileSpacing / 2.0;
                return DockOrientation == Orientation.Horizontal
                    ? new Thickness(h, 0, h, 0)
                    : new Thickness(0, h, 0, h);
            }
        }

        private bool Horizontal => DockOrientation == Orientation.Horizontal;

        /// <summary>Row count (columns when vertical), clamped to something sane.</summary>
        public int Rows => Math.Max(1, Math.Min(4, Config.Rows));

        /// <summary>
        /// Constrain the WrapPanel so tiles wrap onto the next line after N lines' worth.
        ///
        /// The line unit comes from the tiles themselves rather than IconSize + spacing: a tile's
        /// layout slot is as wide as its label (up to 1.7x the icon), so guessing from the config
        /// undershoots and the wrap lands mid-line, scrambling the rows. Measured off the live
        /// containers it is exact, and it keeps tracking label/icon-size changes for free.
        ///
        /// One line means no constraint and the panel behaves like a plain StackPanel.
        /// </summary>
        private void UpdateWrapConstraint()
        {
            double unit = 0;
            for (int i = 0; i < Items.Count; i++)
            {
                ContentPresenter cp = ContainerAt(i);
                if (cp == null) continue;
                unit = Math.Max(unit, Horizontal ? cp.ActualWidth : cp.ActualHeight);
            }
            if (unit <= 0) unit = Config.IconSize + Config.TileSpacing;   // nothing generated yet

            // Half a DIP of slack absorbs layout rounding so a full line of tiles always fits.
            double extent = Rows > 1 ? Rows * unit + 0.5 : double.PositiveInfinity;

            if (Horizontal)
            {
                ItemsHost.MaxWidth = extent;
                ItemsHost.MaxHeight = double.PositiveInfinity;
            }
            else
            {
                ItemsHost.MaxHeight = extent;
                ItemsHost.MaxWidth = double.PositiveInfinity;
            }
        }

        // ==================================================================
        public MainWindow()
        {
            Config = ConfigService.Load();

            // If the previous run never got as far as putting the dock on screen, something in
            // these settings is stopping the window from being created. Back them off rather than
            // failing identically forever with no UI left to fix it from.
            _recovered = ConfigService.LastStartFailed();
            if (_recovered) ConfigService.FallBackToSafeWindow(Config);
            ConfigService.MarkStarting();

            InitializeComponent();
            DataContext = this;
            UpdateWrapConstraint();

            // Tiles get generated, labels toggle, icon size changes — the wrap constraint follows
            // whatever the containers actually measure. Setting the same value is a no-op, so
            // this settles after one extra pass rather than looping.
            ItemsHost.LayoutUpdated += (s, e) => UpdateWrapConstraint();

            foreach (DockItem it in Config.Items)
            {
                it.Icon = LoadIconFor(it);
                Items.Add(it);
            }
            Items.CollectionChanged += (s, e) => { RefreshEmptyHint(); QueueSave(); };

            _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveNow(); };

            _hideTimer.Interval = TimeSpan.FromMilliseconds(200);
            _hideTimer.Tick += HideTimerTick;

            // Belt and braces behind the message handling above: if anything ever does manage to
            // minimise or hide the dock, put it back. One cheap property read per second.
            _guardTimer.Interval = TimeSpan.FromMilliseconds(900);
            try
            {
                _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, App.ShowSignalName);
            }
            catch { _showSignal = null; }

            _guardTimer.Tick += (s, e) =>
            {
                if (_closing) return;

                // Someone tried to launch a second copy. Almost always because this one is invisible.
                if (_showSignal != null && _showSignal.WaitOne(0)) { RecoverToVisible(); return; }

                if (WindowState != WindowState.Normal || !IsVisible) Unminimise();

                // Cheap read, and it catches anything that puts the taskbar button back.
                if (Native.IsInTaskbar(_hwnd)) Native.MakeToolWindow(_hwnd);

                // Show Desktop on Windows 11 doesn't always minimise — it can ask DWM to cloak the
                // window instead. A cloaked window still reports Normal and visible, so nothing
                // above notices; it just stops being drawn. There is no supported way to clear a
                // shell cloak, but hiding and re-showing forces DWM to re-evaluate it.
                if (Native.GetCloaked(_hwnd) != 0)
                {
                    _uncloakCount++;
                    Native.ShowWindow(_hwnd, Native.SW_HIDE);
                    Native.ShowWindow(_hwnd, Native.SW_SHOWNA);
                    ApplyZOrder();
                }

                if (_attached)
                {
                    // WPF re-owns its windows on some operations, which silently undoes the
                    // reparent. Verify against the OS rather than trusting our own flag.
                    if (Native.GetParent(_hwnd) != _desktopHost)
                    {
                        if (_reattachAttempts < 3)
                        {
                            _reattachAttempts++;
                            ReattachInternal();
                            return;
                        }

                        // Out of retries. Wallpaper mode isn't going to work on this machine, and
                        // sitting in a half-attached state buys nothing — fall back to the layer
                        // that does work rather than churning forever.
                        Config.ZOrder = "desktop";
                        Config.GlueChild = false;
                        AttachmentStatus = "Gave up gluing — fell back to On desktop";
                        DetachFromDesktop();
                        ApplyZOrder();
                        SaveNow();
                        return;
                    }

                    // The icon view likes to raise itself back over its siblings, which would bury
                    // a dock that lives beside it. Keep asking to be on top of the pile.
                    Native.RaiseWithinParent(_hwnd);
                }
            };

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            ContentRendered += OnContentRendered;
            SizeChanged += (s, e) => UpdateWindowRegion();
            Closing += (s, e) =>
            {
                _closing = true;
                UserClosed = true;
                _guardTimer.Stop();
                _hideTimer.Stop();
                _proximityTimer.Stop();
                SaveNow();
                DetachFromDesktop();

                if (_settings != null)
                {
                    try { _settings.Close(); } catch { }
                    _settings = null;
                }

                if (_showSignal != null)
                {
                    try { _showSignal.Dispose(); } catch { }
                    _showSignal = null;
                }
            };

            // input
            PreviewMouseLeftButtonDown += OnPreviewLeftDown;
            PreviewMouseLeftButtonUp += OnPreviewLeftUp;
            MouseMove += OnMouseMoveWindow;
            PreviewMouseWheel += OnWheel;
            MouseRightButtonUp += OnRightClick;

            // Magnification reacts to the cursor anywhere near the dock, not only while it is
            // over the window — hovering the desktop beside the dock raises the edge tiles, the
            // way a dock should. MouseMove handles the in-window case directly; this poll feeds
            // the true cursor position from anywhere else, and the falloff does the rest.
            _proximityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _proximityTimer.Tick += (s, e) => UpdateProximity();
            _proximityTimer.Start();

            DragEnter += OnDragEnter;
            DragOver += OnDragOver;
            DragLeave += OnDragLeave;
            Drop += OnDrop;
        }

        // ------------------------------------------------------------------
        //  Startup
        // ------------------------------------------------------------------
        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd);

            // Broadcast to every top-level window when Explorer comes back after a restart.
            _taskbarCreatedMsg = Native.RegisterWindowMessageW("TaskbarCreated");

            HwndSource source = HwndSource.FromHwnd(_hwnd);
            if (source != null) source.AddHook(WndProc);

            ApplyBackdrop();
        }

        private void OnContentRendered(object sender, EventArgs e)
        {
            if (_shown) return;
            _shown = true;

            // WPF re-applies its own extended styles while showing the window, which puts the
            // taskbar button back. Re-assert after the fact, not just before.
            Native.MakeToolWindow(_hwnd);

            // Now that there is definitely a window on screen, it is safe to try the risky part.
            ApplyZOrder();

            // Only clear the crash marker once the dock has survived a few seconds with whatever
            // the config asked for. Clearing it immediately would defeat the guard, because the
            // things it protects against take effect a moment after this point.
            DispatcherTimer ok = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            ok.Tick += (s2, e2) => { ok.Stop(); ConfigService.MarkStarted(); };
            ok.Start();

            if (_recovered)
            {
                MessageBox.Show(
                    "Dockyard didn't finish starting last time, so the window settings have been " +
                    "reset to safe values: layer is back to \"On desktop\" and the backdrop to " +
                    "\"None\".\n\nYour tiles, colours and layout are untouched.",
                    "Dockyard", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyTheme();
            RefreshEmptyHint();
            UpdateLayout();
            RestorePosition();
            ApplyZOrder();
            _hideTimer.IsEnabled = Config.AutoHide;
            _guardTimer.Start();

            // At login the dock can start before the shell has finished laying out the desktop:
            // the taskbar may not be registered yet and a per-monitor DPI change can arrive a beat
            // later, either of which moves the window after it has been placed. Re-assert the saved
            // position once things have settled, unless the user has already grabbed it.
            DispatcherTimer settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            settle.Tick += (s2, e2) =>
            {
                settle.Stop();
                if (_userMoved || _autoHidden) return;
                UpdateLayout();
                RestorePosition();
            };
            settle.Start();
        }

        // ------------------------------------------------------------------
        //  Z-order
        // ------------------------------------------------------------------
        private string ZMode
        {
            get
            {
                string z = (Config.ZOrder ?? "").ToLowerInvariant();
                if (z == "desktop" || z == "normal" || z == "topmost" || z == "wallpaper") return z;
                // Config written before ZOrder existed.
                return Config.AlwaysOnTop ? "topmost" : "normal";
            }
        }

        private void ApplyZOrder()
        {
            string z = ZMode;
            Topmost = z == "topmost";

            // Never touch parenting or window styles while the window is still being created —
            // that is what stopped the dock appearing at all. Wait until it is on screen.
            if (!_shown || _hwnd == IntPtr.Zero) return;

            if (z == "wallpaper") AttachToDesktop();
            else DetachFromDesktop();

            Native.SetNoActivate(_hwnd, z == "desktop" || z == "wallpaper");
        }

        // ------------------------------------------------------------------
        //  Wallpaper mode: become a child of the desktop
        // ------------------------------------------------------------------

        /// <summary>Window position in screen coordinates, whichever mode we are in.</summary>
        public double ScreenLeft => Left - _hostOffset.X;
        public double ScreenTop => Top - _hostOffset.Y;

        /// <summary>Move the window to a screen position, translating if we are parented.</summary>
        private void PlaceWindow(double screenLeft, double screenTop)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = screenLeft + _hostOffset.X;
            Top = screenTop + _hostOffset.Y;
        }

        private Vector DeviceToDip(double x, double y)
        {
            double sx = 1, sy = 1;
            PresentationSource src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null)
            {
                Matrix m = src.CompositionTarget.TransformFromDevice;
                sx = m.M11;
                sy = m.M22;
            }
            return new Vector(x * sx, y * sy);
        }

        private void RecomputeHostOffset()
        {
            _hostOffset = new Vector(0, 0);
            if (!_attached || _desktopHost == IntPtr.Zero) return;

            // Where does screen (0,0) land in the host's client space? That difference is exactly
            // what has to be added to a screen coordinate to get a parent-relative one.
            POINT origin = new POINT { X = 0, Y = 0 };
            if (Native.ScreenToClient(_desktopHost, ref origin))
                _hostOffset = DeviceToDip(origin.X, origin.Y);
        }

        /// <summary>
        /// Makes the dock a child of the window that hosts the desktop icons. Being a child rather
        /// than a top-level window is what makes Show Desktop leave it alone — that feature
        /// minimises top-level windows, and this stops being one.
        ///
        /// Two things are given up. DWM composites blur per top-level window, so acrylic cannot
        /// reach a child; the backdrop is forced to None here. And if Explorer restarts it destroys
        /// the desktop windows along with their children, taking the dock with them — App watches
        /// for that and brings it back.
        /// </summary>
        private void AttachToDesktop()
        {
            if (_hwnd == IntPtr.Zero || _attached) return;

            IntPtr host = Native.FindDesktopIconHost();
            if (host == IntPtr.Zero)
            {
                AttachmentStatus = "Not glued — couldn't find the desktop window";
                return;
            }

            double sl = ScreenLeft, st = ScreenTop;

            Topmost = false;

            // Owner first: while WPF's hidden parking window owns this one, that ownership sits in
            // the same slot as a popup's parent and simply overwrites the SetParent below.
            Native.ClearOwner(_hwnd);

            // Then style, then parent. Reversing the last two leaves the window styled as a popup
            // for a moment, and the shell can latch onto that.
            if (Config.GlueChild) Native.SetChildStyle(_hwnd, true);

            Native.SetParent(_hwnd, host);
            _lastAttachError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();

            _attached = true;
            _desktopHost = host;
            RecomputeHostOffset();

            PlaceWindow(sl, st);
            Native.RaiseWithinParent(_hwnd);

            if (!string.Equals(Config.Backdrop, "none", StringComparison.OrdinalIgnoreCase))
            {
                Config.Backdrop = "none";
                ApplyTheme();
            }

            // Ask the system what actually happened rather than assuming it worked.
            IntPtr actual = Native.GetParent(_hwnd);
            if (actual == host)
            {
                _reattachAttempts = 0;
                AttachmentStatus = "Glued to " + Native.ClassNameOf(host)
                    + " (0x" + host.ToInt64().ToString("X") + ")"
                    + (Config.GlueChild ? ", styled as a child" : ", reparented only");
            }
            else
            {
                AttachmentStatus = "Attach refused — parent is " + Native.ClassNameOf(actual)
                    + (_lastAttachError != 0 ? ", SetParent error " + _lastAttachError : "");
            }
        }

        /// <summary>What the dock is currently attached to. Shown in Settings so this is diagnosable
        /// without guesswork.</summary>
        public string AttachmentStatus { get; private set; } = "Top-level window";

        /// <summary>Drop the current attachment and glue again from scratch. Resets the retry
        /// budget, since this is someone asking on purpose.</summary>
        public void ReattachToDesktop()
        {
            _reattachAttempts = 0;
            ReattachInternal();
        }

        private void ReattachInternal()
        {
            DetachFromDesktop();
            _attached = false;
            _desktopHost = IntPtr.Zero;
            _hostOffset = new Vector(0, 0);
            ApplyZOrder();
            UpdateLayout();
            RestorePosition();
        }

        private void DetachFromDesktop()
        {
            if (!_attached) return;

            double sl = ScreenLeft, st = ScreenTop;

            Native.SetParent(_hwnd, IntPtr.Zero);
            Native.SetChildStyle(_hwnd, false);

            _attached = false;
            _desktopHost = IntPtr.Zero;
            _hostOffset = new Vector(0, 0);

            Native.MakeToolWindow(_hwnd);
            PlaceWindow(sl, st);

            AttachmentStatus = "Top-level window";
        }

        /// <summary>
        /// Desktop mode works by answering every reposition request with "put me at the bottom".
        /// Windows asks before it moves the window, so this pins the dock under all other windows
        /// without a timer fighting the compositor.
        ///
        /// The rest of this guards against Show Desktop. That feature minimises every top-level
        /// window, and the dock is one — so it disappeared along with everything else, which is
        /// backwards: showing the desktop is precisely when you want the dock. A dock has no
        /// business being minimised at all, so the request is refused three ways: the system
        /// command is swallowed, a minimise that slips through is undone, and a hide flag on a
        /// position change is stripped.
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == Native.SC_MINIMIZE)
            {
                handled = true;
                return IntPtr.Zero;
            }

            // Explorer restarted, so the desktop windows we may have attached to are new ones.
            if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg && !_closing)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _attached = false;          // the old host is gone
                    _desktopHost = IntPtr.Zero;
                    _hostOffset = new Vector(0, 0);
                    ApplyZOrder();
                    RestorePosition();
                }), DispatcherPriority.Background);
            }

            if (msg == Native.WM_SIZE && wParam.ToInt64() == Native.SIZE_MINIMIZED && !_closing)
            {
                Dispatcher.BeginInvoke(new Action(Unminimise), DispatcherPriority.Background);
            }

            if (msg == Native.WM_WINDOWPOSCHANGING)
            {
                Native.WINDOWPOS pos = (Native.WINDOWPOS)System.Runtime.InteropServices.Marshal
                    .PtrToStructure(lParam, typeof(Native.WINDOWPOS));

                bool rewrite = false;

                if (ZMode == "desktop")
                {
                    pos.hwndInsertAfter = Native.HWND_BOTTOM;
                    pos.flags &= ~Native.SWP_NOZORDER;
                    rewrite = true;
                }

                // Some shells hide rather than minimise. The dock never hides itself — auto-hide
                // slides it off-screen instead — so this flag is only ever someone else's idea.
                if (!_closing && (pos.flags & Native.SWP_HIDEWINDOW) != 0)
                {
                    pos.flags &= ~Native.SWP_HIDEWINDOW;
                    rewrite = true;
                }

                if (rewrite)
                {
                    System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Get back to a state the user can definitely see and click, whatever the config asked
        /// for. Reached by launching the app a second time, which is the only route left when the
        /// dock is running but invisible.
        /// </summary>
        public void RecoverToVisible()
        {
            if (_closing) return;

            DetachFromDesktop();

            Config.ZOrder = "desktop";
            Config.GlueChild = false;
            Config.Backdrop = "none";
            Config.Opacity = Math.Max(0.6, Config.Opacity);

            ApplyTheme();
            ApplyZOrder();

            WindowState = WindowState.Normal;
            if (!IsVisible) Show();

            UpdateLayout();
            RestorePosition();

            SaveNow();
        }

        private void Unminimise()
        {
            if (_closing) return;

            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            if (!IsVisible) Show();

            ApplyZOrder();
        }

        // ------------------------------------------------------------------
        //  Theming
        // ------------------------------------------------------------------
        private static Color ParseColor(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return fallback; }
        }

        private static SolidColorBrush Frozen(Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public void ApplyTheme()
        {
            ThemeColors c = Config.Colors ?? new ThemeColors();

            Color bg = ParseColor(c.Background, Color.FromArgb(0x8C, 0x11, 0x14, 0x1C));
            Color bd = ParseColor(c.Border, Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            Color ac = ParseColor(c.Accent, Color.FromRgb(0x7A, 0xA2, 0xF7));
            Color tx = ParseColor(c.Text, Color.FromRgb(0xE8, 0xEC, 0xF4));
            Color th = ParseColor(c.TileHover, Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            Color sh = ParseColor(c.Shadow, Color.FromArgb(0xCC, 0x00, 0x00, 0x00));

            Resources["DockBackgroundBrush"] = Frozen(bg);
            Resources["DockBorderBrush"] = Frozen(bd);
            Resources["DockAccentBrush"] = Frozen(ac);
            Resources["DockTextBrush"] = Frozen(tx);
            Resources["DockTileHoverBrush"] = Frozen(th);

            Root.CornerRadius = new CornerRadius(EffectiveCornerRadius);
            Root.BorderThickness = new Thickness(Config.BorderThickness);

            // Leave room for icons to grow and slide without touching the slab edge.
            double bleed = MagnifyBleed;
            double pad = Config.Padding;
            Root.Padding = new Thickness(pad + bleed, pad + bleed, pad + bleed, pad + bleed);

            bool blurred = !string.Equals(Config.Backdrop, "none", StringComparison.OrdinalIgnoreCase);
            if (blurred)
            {
                // A blurred window is blurred edge to edge, so a shadow margin would show as a
                // rectangular halo. Keep the window tight to the slab instead.
                Root.Margin = new Thickness(0);
                Root.Effect = null;
            }
            else
            {
                Root.Margin = new Thickness(18);
                Root.Effect = new DropShadowEffect
                {
                    BlurRadius = 26,
                    ShadowDepth = 5,
                    Direction = 270,
                    Opacity = sh.A / 255.0,
                    Color = Color.FromRgb(sh.R, sh.G, sh.B)
                };
            }

            Opacity = Math.Min(1.0, Math.Max(0.2, Config.Opacity));

            RaiseAll();
            ApplyBackdrop();
            UpdateWindowRegion();
        }

        /// <summary>
        /// With a system backdrop the whole window rectangle is blurred, so the slab's corners must
        /// be DWM's corners or the blur pokes out behind them. That caps the radius at what DWM
        /// offers — which is why "none" is the default backdrop: it renders in WPF and can be
        /// rounded however you like.
        /// </summary>
        private void UpdateWindowRegion()
        {
            if (_hwnd == IntPtr.Zero) return;

            bool blurred = !string.Equals(Config.Backdrop, "none", StringComparison.OrdinalIgnoreCase);
            Native.SetCornerPreference(_hwnd, blurred ? Native.CORNER_ROUND : Native.CORNER_DONOTROUND);
        }

        /// <summary>Radius actually drawn. Blurred modes are pinned to DWM's ~8px so nothing bleeds.</summary>
        private double EffectiveCornerRadius
        {
            get
            {
                bool blurred = !string.Equals(Config.Backdrop, "none", StringComparison.OrdinalIgnoreCase);
                return blurred ? 8.0 : Math.Max(0, Config.CornerRadius);
            }
        }

        private void ApplyBackdrop()
        {
            if (_hwnd == IntPtr.Zero) return;
            Color bg = ParseColor(Config.Colors?.Background, Color.FromArgb(0x8C, 0x11, 0x14, 0x1C));
            Native.ApplyBackdrop(_hwnd, Config.Backdrop, bg.A, bg.R, bg.G, bg.B);
        }

        private void RaiseAll()
        {
            Raise(nameof(IconSize));
            Raise(nameof(LabelSize));
            Raise(nameof(LabelMaxWidth));
            Raise(nameof(TileCorner));
            Raise(nameof(LabelVisibility));
            Raise(nameof(DockOrientation));
            Raise(nameof(TileMargin));
            Raise(nameof(Rows));
            UpdateWrapConstraint();
        }

        private void RefreshEmptyHint()
        {
            EmptyHint.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------------------------
        //  Positioning / snapping / auto-hide
        // ------------------------------------------------------------------
        private Rect WorkAreaDip()
        {
            RECT r = Native.GetWorkArea(_hwnd);
            double sx = 1, sy = 1;
            PresentationSource src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null)
            {
                Matrix m = src.CompositionTarget.TransformFromDevice;
                sx = m.M11;
                sy = m.M22;
            }
            return new Rect(r.Left * sx, r.Top * sy, r.Width * sx, r.Height * sy);
        }

        /// <summary>
        /// A saved position is only rejected if it would leave the dock unreachable. Checking
        /// against the whole virtual screen rather than one monitor's work area matters for
        /// multi-monitor setups, where a perfectly valid position on a left-hand or overhead
        /// monitor has negative coordinates.
        /// </summary>
        private static bool IsReachable(double left, double top, double w, double h)
        {
            Rect virt = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            Rect r = new Rect(left, top, Math.Max(1, w), Math.Max(1, h));
            r.Intersect(virt);
            return r.Width >= 40 && r.Height >= 20;
        }

        private bool HasSavedPosition
        {
            get
            {
                if (double.IsNaN(Config.Left) || double.IsNaN(Config.Top)) return false;
                return !(Config.Left <= -1 && Config.Top <= -1);   // -1,-1 is the "never placed" marker
            }
        }

        private void RestorePosition()
        {
            if (HasSavedPosition && IsReachable(Config.Left, Config.Top, ActualWidth, ActualHeight))
            {
                PlaceWindow(Config.Left, Config.Top);
            }
            else
            {
                Rect wa = WorkAreaDip();
                PlaceWindow(wa.Left + (wa.Width - ActualWidth) / 2.0,
                            wa.Bottom - ActualHeight - 18);
            }

            _shownLeft = ScreenLeft;
            _shownTop = ScreenTop;
        }

        private void SnapToEdges()
        {
            if (!Config.SnapToEdges) return;

            Rect wa = WorkAreaDip();
            const double threshold = 28;
            double l = ScreenLeft, t = ScreenTop, w = ActualWidth, h = ActualHeight;

            if (Math.Abs(l - wa.Left) < threshold) l = wa.Left;
            if (Math.Abs((l + w) - wa.Right) < threshold) l = wa.Right - w;
            if (Math.Abs(t - wa.Top) < threshold) t = wa.Top;
            if (Math.Abs((t + h) - wa.Bottom) < threshold) t = wa.Bottom - h;

            // horizontal centre snap
            double centred = wa.Left + (wa.Width - w) / 2.0;
            if (Math.Abs(l - centred) < threshold) l = centred;

            // keep it reachable
            l = Math.Max(wa.Left - w + 40, Math.Min(l, wa.Right - 40));
            t = Math.Max(wa.Top - h + 40, Math.Min(t, wa.Bottom - 40));

            AnimateWindowTo(l, t);
            _shownLeft = l;
            _shownTop = t;
        }

        /// <summary>Takes screen coordinates; translates them if the dock is parented.</summary>
        private void AnimateWindowTo(double screenLeft, double screenTop)
        {
            double left = screenLeft + _hostOffset.X;
            double top = screenTop + _hostOffset.Y;

            Duration d = new Duration(TimeSpan.FromMilliseconds(160));
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            DoubleAnimation la = new DoubleAnimation(left, d) { EasingFunction = ease };
            DoubleAnimation ta = new DoubleAnimation(top, d) { EasingFunction = ease };

            // Hand the property back once the animation lands, otherwise the held animation value
            // fights DragMove the next time the user grabs the dock.
            la.Completed += (s, e) => { BeginAnimation(LeftProperty, null); Left = left; };
            ta.Completed += (s, e) => { BeginAnimation(TopProperty, null); Top = top; };

            BeginAnimation(LeftProperty, la);
            BeginAnimation(TopProperty, ta);
        }

        private string NearestEdge(Rect wa)
        {
            double dl = Math.Abs(ScreenLeft - wa.Left);
            double dr = Math.Abs(wa.Right - (ScreenLeft + ActualWidth));
            double dt = Math.Abs(ScreenTop - wa.Top);
            double db = Math.Abs(wa.Bottom - (ScreenTop + ActualHeight));
            double min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            if (min == db) return "bottom";
            if (min == dt) return "top";
            if (min == dl) return "left";
            return "right";
        }

        private void HideTimerTick(object sender, EventArgs e)
        {
            if (!Config.AutoHide || !IsLoaded) return;

            POINT p;
            if (!Native.GetCursorPos(out p)) return;

            double sx = 1, sy = 1;
            PresentationSource src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null)
            {
                Matrix m = src.CompositionTarget.TransformFromDevice;
                sx = m.M11; sy = m.M22;
            }
            Point cursor = new Point(p.X * sx, p.Y * sy);

            // Generous hot zone around wherever the dock lives when shown.
            Rect hot = new Rect(_shownLeft - 12, _shownTop - 12, ActualWidth + 24, ActualHeight + 24);
            bool inside = hot.Contains(cursor);

            if (inside && _autoHidden) SetHidden(false);
            else if (!inside && !_autoHidden && !_reordering) SetHidden(true);
        }

        private void SetHidden(bool hide)
        {
            _autoHidden = hide;
            Rect wa = WorkAreaDip();

            if (!hide)
            {
                AnimateWindowTo(_shownLeft, _shownTop);
                return;
            }

            const double peek = 3;
            double l = _shownLeft, t = _shownTop;
            switch (NearestEdge(wa))
            {
                case "bottom": t = wa.Bottom - peek; break;
                case "top": t = wa.Top - ActualHeight + peek; break;
                case "left": l = wa.Left - ActualWidth + peek; break;
                case "right": l = wa.Right - peek; break;
            }
            AnimateWindowTo(l, t);
        }

        // ------------------------------------------------------------------
        //  Magnification
        // ------------------------------------------------------------------
        private ContentPresenter ContainerAt(int index)
        {
            return ItemsHost.ItemContainerGenerator.ContainerFromIndex(index) as ContentPresenter;
        }

        private static Grid TileRootOf(ContentPresenter cp)
        {
            if (cp == null) return null;
            if (VisualTreeHelper.GetChildrenCount(cp) == 0) return null;
            return VisualTreeHelper.GetChild(cp, 0) as Grid;
        }

        private static T TemplatePart<T>(ContentPresenter cp, string name) where T : class
        {
            if (cp == null || cp.ContentTemplate == null) return null;
            try { return cp.ContentTemplate.FindName(name, cp) as T; }
            catch { return null; }
        }

        private static void SetTileTransform(Grid tile, double scale, double offset, bool horizontal,
            bool animated, double seconds)
        {
            TransformGroup tg = tile.RenderTransform as TransformGroup;
            if (tg == null || tg.IsFrozen || tg.Children.Count < 2)
            {
                tg = new TransformGroup();
                tg.Children.Add(new ScaleTransform(1, 1));
                tg.Children.Add(new TranslateTransform(0, 0));
                tile.RenderTransform = tg;
            }

            ScaleTransform st = tg.Children[0] as ScaleTransform;
            TranslateTransform tt = tg.Children[1] as TranslateTransform;
            if (st == null || tt == null) return;

            double tx = horizontal ? offset : 0;
            double ty = horizontal ? 0 : offset;

            if (animated)
            {
                Duration d = new Duration(TimeSpan.FromSeconds(seconds));
                CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, d) { EasingFunction = ease });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, d) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(tx, d) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(ty, d) { EasingFunction = ease });
            }
            else
            {
                // Any running animation holds its value and would win over a direct assignment.
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.BeginAnimation(TranslateTransform.YProperty, null);
                st.ScaleX = scale;
                st.ScaleY = scale;
                tt.X = tx;
                tt.Y = ty;
            }
        }

        /// <summary>
        /// mouse == null resets everything to rest. Otherwise every tile is scaled by how close the
        /// cursor is to it along the dock's axis, and each line re-spreads around the cursor so
        /// grown neighbours slide out of each other's way instead of overlapping. That combination
        /// is what reads as a dock rather than a row of buttons that happen to get bigger.
        ///
        /// Proximity is deliberately global: every line magnifies from the same axis distance, so
        /// hovering the middle of a multi-row dock raises a column of tiles, exactly like the
        /// macOS dock raises one icon per row. The re-spread, though, is computed per line — lines
        /// must never shove each other around. The cursor position may lie outside the dock
        /// entirely; the falloff just fades to zero out there.
        ///
        /// Positions are measured on the ContentPresenter, not on the tile, because the tile carries
        /// the render transform — measuring it would feed its own output back into the input.
        /// </summary>
        private void UpdateMagnification(Point? mouse, bool animated)
        {
            int n = Items.Count;
            if (n == 0) return;

            bool horiz = Horizontal;
            double maxScale = Math.Max(1.0, Config.HoverScale);
            double speed = Math.Max(0.03, Config.AnimationSpeed);
            bool active = mouse.HasValue && maxScale > 1.0;

            ContentPresenter[] cps = new ContentPresenter[n];
            Grid[] tiles = new Grid[n];
            double[] centre = new double[n];
            double[] cross = new double[n];
            double[] extent = new double[n];
            double[] scale = new double[n];
            double[] plate = new double[n];
            double[] offset = new double[n];

            double fallbackUnit = Config.IconSize + Config.TileSpacing;

            for (int i = 0; i < n; i++)
            {
                cps[i] = ContainerAt(i);
                tiles[i] = TileRootOf(cps[i]);
                scale[i] = 1.0;

                if (cps[i] == null) { extent[i] = fallbackUnit; continue; }

                Point c = cps[i].TranslatePoint(
                    new Point(cps[i].ActualWidth / 2.0, cps[i].ActualHeight / 2.0), ItemsHost);

                centre[i] = horiz ? c.X : c.Y;
                cross[i] = horiz ? c.Y : c.X;
                extent[i] = horiz ? cps[i].ActualWidth : cps[i].ActualHeight;
                if (extent[i] <= 1) extent[i] = fallbackUnit;
            }

            // --- how big does each tile get -------------------------------
            if (active)
            {
                double m = horiz ? mouse.Value.X : mouse.Value.Y;
                double mCross = horiz ? mouse.Value.Y : mouse.Value.X;

                // Per-tile 2D distance: a tile responds to how far the cursor is from *it*, not
                // from the dock's axis. Measuring the axis alone made row-mates grow in lockstep —
                // hover one tile and the one beside it swelled too, since both sit at the same
                // axis position. The sigma is narrower than the tile pitch, so the neighbour one
                // slot over stays at rest: tiles respond individually, not as a row.
                double unit = Config.IconSize + Config.TileSpacing;
                double sigma = Math.Max(0.15, Config.MagnifyFalloff) * 0.35 * unit;
                double deadzone = 1.5 * unit;

                for (int i = 0; i < n; i++)
                {
                    if (cps[i] == null) continue;
                    double dAxis = m - centre[i];
                    double dCross = mCross - cross[i];
                    double d2 = dAxis * dAxis + dCross * dCross;

                    if (Config.Magnify)
                    {
                        double f = d2 >= deadzone * deadzone
                            ? 0.0
                            : Math.Exp(-d2 / (2 * sigma * sigma));
                        scale[i] = 1.0 + (maxScale - 1.0) * f;
                        plate[i] = 0.95 * f;
                    }
                    else
                    {
                        bool over = d2 <= (extent[i] * extent[i]) / 4.0;
                        scale[i] = over ? maxScale : 1.0;
                        plate[i] = over ? 0.95 : 0.0;
                    }
                }

                // No re-spread: the classic dock shoved neighbours aside to make room for the
                // grown tile, but with per-tile magnification that slide read as "everything
                // is growing". Tiles scale in place; nothing moves.
            }

            // --- apply -----------------------------------------------------
            for (int i = 0; i < n; i++)
            {
                if (tiles[i] != null)
                    SetTileTransform(tiles[i], scale[i], offset[i], horiz, animated, speed);

                System.Windows.Controls.Border hp = TemplatePart<System.Windows.Controls.Border>(cps[i], "HoverPlate");
                if (hp != null) hp.Opacity = plate[i];

                System.Windows.Controls.Border pip = TemplatePart<System.Windows.Controls.Border>(cps[i], "Pip");
                if (pip != null) pip.Opacity = plate[i] * 0.9;

                if (tiles[i] != null)
                {
                    TextBlock tb = FindDescendant<TextBlock>(tiles[i]);
                    if (tb != null) tb.Opacity = 0.62 + 0.38 * plate[i];
                }
            }
        }

        /// <summary>
        /// Cursor tracking for out-of-window magnification. Maps the global cursor position into
        /// host coordinates and feeds it to the same magnification math the window's own mouse
        /// events use. No distance cut-off here: the falloff in UpdateMagnification fades the
        /// effect to nothing with distance, so feeding it the true position everywhere is what
        /// makes approaching the dock feel gradual instead of switching on at a boundary.
        /// </summary>
        private void UpdateProximity()
        {
            if (_closing || !IsLoaded || !IsVisible || _dragging || _reordering || _autoHidden) return;

            // No IsMouseOver short-circuit here: the shadow margin around the slab is transparent
            // and click-through, so the cursor can be "on the window" without WPF mouse events
            // ever firing. Feeding the position unconditionally covers that gap, and recomputing
            // the same math the mouse path runs is harmless.

            POINT p;
            if (!Native.GetCursorPos(out p)) return;
            Vector v = DeviceToDip(p.X, p.Y);

            Point host;
            try { host = ItemsHost.PointFromScreen(new Point(v.X, v.Y)); }
            catch { return; }                        // window not on screen right now

            UpdateMagnification(host, true);
        }

        /// <summary>
        /// Slack the dock reserves around its contents so magnified tiles never touch the window
        /// edge. Doubles as the clamp on how far the re-spread can push a tile.
        /// </summary>
        private double MagnifyBleed =>
            Math.Max(0, Config.IconSize * (Math.Max(1.0, Config.HoverScale) - 1.0) * 0.6);

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                T hit = child as T;
                if (hit != null) return hit;
                hit = FindDescendant<T>(child);
                if (hit != null) return hit;
            }
            return null;
        }

        // ------------------------------------------------------------------
        //  Mouse
        // ------------------------------------------------------------------
        private DockItem ItemFromSource(object source)
        {
            DependencyObject d = source as DependencyObject;
            int guard = 0;
            while (d != null && guard++ < 64)
            {
                FrameworkElement fe = d as FrameworkElement;
                if (fe != null && fe.DataContext is DockItem) return (DockItem)fe.DataContext;

                // VisualTreeHelper.GetParent throws on anything that isn't a Visual (a Run inside
                // a TextBlock, for instance), so step through the logical tree in that case.
                DependencyObject next = null;
                if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                    next = VisualTreeHelper.GetParent(d);
                if (next == null)
                    next = LogicalTreeHelper.GetParent(d);

                d = next;
            }
            return null;
        }

        private void OnMouseMoveWindow(object sender, MouseEventArgs e)
        {
            if (_dragging)
            {
                if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }

                Point now = CursorScreenDip();
                PlaceWindow(_dragOriginLeft + (now.X - _dragStartScreen.X),
                            _dragOriginTop + (now.Y - _dragStartScreen.Y));
                return;
            }

            Point p = e.GetPosition(ItemsHost);
            UpdateMagnification(p, false);

            if (e.LeftButton == MouseButtonState.Pressed && _pressItem != null)
            {
                Point now = e.GetPosition(this);
                if (!_reordering &&
                    (Math.Abs(now.X - _pressPoint.X) > 8 || Math.Abs(now.Y - _pressPoint.Y) > 8))
                {
                    _reordering = true;
                }

                if (_reordering) DoReorder(p);
            }
        }

        private void DoReorder(Point mouseInHost)
        {
            int from = Items.IndexOf(_pressItem);
            if (from < 0) return;

            // Nearest tile centre, in both axes. On a single line this reduces to the old
            // along-the-axis behaviour; with multiple rows it means the tile you're actually
            // pointing at, wherever it sits.
            int target = from;
            double best = double.MaxValue;
            for (int i = 0; i < Items.Count; i++)
            {
                ContentPresenter cp = ContainerAt(i);
                if (cp == null) continue;
                Point c = cp.TranslatePoint(new Point(cp.ActualWidth / 2.0, cp.ActualHeight / 2.0), ItemsHost);
                double dx = mouseInHost.X - c.X;
                double dy = mouseInHost.Y - c.Y;
                double d = dx * dx + dy * dy;
                if (d < best) { best = d; target = i; }
            }

            if (target != from)
            {
                Items.Move(from, target);
                ItemsHost.UpdateLayout();
            }
        }

        private void OnPreviewLeftDown(object sender, MouseButtonEventArgs e)
        {
            _pressItem = ItemFromSource(e.OriginalSource);
            _pressPoint = e.GetPosition(this);
            _reordering = false;

            if (_pressItem == null && !Config.Locked)
            {
                // Dragged by hand rather than with Window.DragMove: DragMove hands control to the
                // system move loop, which does not behave for a child window — and in wallpaper
                // mode the dock is one. Tracking the cursor ourselves works identically in every
                // mode and doesn't block the message pump.
                _dragging = true;
                _dragStartScreen = CursorScreenDip();
                _dragOriginLeft = ScreenLeft;
                _dragOriginTop = ScreenTop;
                CaptureMouse();
            }
        }

        /// <summary>Cursor position in screen DIPs.</summary>
        private Point CursorScreenDip()
        {
            POINT p;
            if (!Native.GetCursorPos(out p)) return new Point(0, 0);
            Vector v = DeviceToDip(p.X, p.Y);
            return new Point(v.X, v.Y);
        }

        private void EndDrag()
        {
            if (!_dragging) return;

            _dragging = false;
            ReleaseMouseCapture();

            _userMoved = true;
            _shownLeft = ScreenLeft;
            _shownTop = ScreenTop;

            SnapToEdges();      // updates _shownLeft/_shownTop to the snapped spot

            // Written straight through rather than debounced. If the machine is shut down or logged
            // off before a queued save fires, the dock comes back in the wrong place — which is the
            // one thing this is here to prevent.
            SaveNow();
        }

        private void OnPreviewLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragging) { EndDrag(); return; }

            DockItem item = _pressItem;
            bool wasReorder = _reordering;
            _pressItem = null;
            _reordering = false;

            if (item != null && !wasReorder) Launch(item, false);
            else if (wasReorder) QueueSave();
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;

            double step = e.Delta > 0 ? 4 : -4;
            Config.IconSize = Math.Max(24, Math.Min(160, Config.IconSize + step));
            ApplyTheme();
            QueueSave();
        }

        // ------------------------------------------------------------------
        //  Launching
        // ------------------------------------------------------------------
        private void Launch(DockItem item, bool elevated)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;

            try
            {
                string path = Environment.ExpandEnvironmentVariables(item.Path);

                if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                };

                if (!string.IsNullOrWhiteSpace(item.Arguments)) psi.Arguments = item.Arguments;

                if (!string.IsNullOrWhiteSpace(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
                    psi.WorkingDirectory = item.WorkingDirectory;
                else if (File.Exists(path))
                    psi.WorkingDirectory = Path.GetDirectoryName(path);

                if (elevated) psi.Verb = "runas";

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't start " + item.Name + ".\n\n" + ex.Message,
                    "Dockyard", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ------------------------------------------------------------------
        //  Drag & drop
        // ------------------------------------------------------------------
        private void OnDragEnter(object sender, DragEventArgs e) { ShowDropRing(true); }
        private void OnDragLeave(object sender, DragEventArgs e) { ShowDropRing(false); }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
                ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        }

        private void ShowDropRing(bool on)
        {
            DropRing.BeginAnimation(OpacityProperty,
                new DoubleAnimation(on ? 0.85 : 0.0, new Duration(TimeSpan.FromMilliseconds(120))));
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            ShowDropRing(false);

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null)
                {
                    foreach (string p in paths) AddPath(p);
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                string text = (e.Data.GetData(DataFormats.Text) ?? "").ToString().Trim();
                if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    AddItem(HostOf(text), text, "", "", text);
                }
            }

            e.Handled = true;
        }

        private static string HostOf(string url)
        {
            try { return new Uri(url).Host.Replace("www.", ""); }
            catch { return url; }
        }

        /// <summary>Turn a dropped/picked path into a tile, resolving shortcuts along the way.</summary>
        public void AddPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            string ext = "";
            try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch { }

            string name = "";
            try { name = Path.GetFileNameWithoutExtension(path); } catch { }

            if (ext == ".lnk")
            {
                ShortcutTarget t = ShortcutResolver.Resolve(path);
                if (t != null && !string.IsNullOrWhiteSpace(t.Path))
                {
                    string icon = !string.IsNullOrWhiteSpace(t.IconPath) ? t.IconPath : t.Path;
                    AddItem(name, t.Path, t.Arguments, t.WorkingDirectory, icon);
                    return;
                }
                // Unresolvable (Store app shortcuts, mostly): launch the .lnk itself.
                AddItem(name, path, "", "", path);
                return;
            }

            if (ext == ".url")
            {
                // Steam, Xbox and friends hand out .url files whose icon lives in the IconFile=
                // line. Rendering the .url through the shell comes back as a small icon painted
                // on an opaque white square, so go to the .ico directly when there is one.
                UrlTarget u = ShortcutResolver.ResolveUrlFile(path);
                string launch = string.IsNullOrWhiteSpace(u.Url) ? path : u.Url;
                string icon = (!string.IsNullOrWhiteSpace(u.IconFile) &&
                               File.Exists(u.IconFile)) ? u.IconFile : path;
                AddItem(name, launch, "", "", icon);
                return;
            }

            AddItem(name, path, "", "", path);
        }

        private void AddItem(string name, string path, string args, string workdir, string iconSource)
        {
            DockItem item = new DockItem
            {
                Name = string.IsNullOrWhiteSpace(name) ? "App" : name,
                Path = path,
                Arguments = args ?? "",
                WorkingDirectory = workdir ?? "",
                IconSource = iconSource ?? path
            };
            item.Icon = LoadIconFor(item);
            Items.Add(item);
        }

        private static string ResolveIconPath(DockItem item)
        {
            string src = !string.IsNullOrWhiteSpace(item.IconSource) ? item.IconSource : item.Path;
            try { return Environment.ExpandEnvironmentVariables(src ?? ""); }
            catch { return src; }
        }

        /// <summary>Icon for a tile, with a generic system icon standing in for URLs and dead paths.</summary>
        private static ImageSource LoadIconFor(DockItem item)
        {
            ImageSource img = IconExtractor.Load(ResolveIconPath(item));
            if (img != null) return img;

            try
            {
                string generic = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\shell32.dll");
                return IconExtractor.Load(generic);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------
        //  Context menus
        // ------------------------------------------------------------------
        private void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            DockItem item = ItemFromSource(e.OriginalSource);
            OpenMenu(item != null ? BuildTileMenu(item) : BuildDockMenu());
            e.Handled = true;
        }

        private ContextMenu _openMenu;
        // Rooted here on purpose: the native hook holds only a function pointer, so if nothing
        // managed references the delegate the GC collects it and the next mouse click anywhere
        // on the system calls into collected memory — which kills the process (FailFast).
        private Native.LowLevelMouseProc _menuProc;

        private void OpenMenu(ContextMenu menu)
        {
            CloseOpenMenu();
            _openMenu = menu;

            IntPtr hook = IntPtr.Zero;
            _menuProc = (nCode, w, l) =>
            {
                if (nCode >= 0 && _openMenu != null)
                {
                    int msg = w.ToInt32();
                    if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN ||
                        msg == Native.WM_XBUTTONDOWN)
                    {
                        Native.MSLLHOOKSTRUCT info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                            l, typeof(Native.MSLLHOOKSTRUCT));

                        IntPtr hit = Native.WindowAt(info.pt);
                        if (hit == IntPtr.Zero || !Native.IsOwnWindow(hit))
                        {
                            // Off our windows entirely: the menu is done. Deferred to the dispatcher
                            // because a hook must not touch WPF objects synchronously.
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (_openMenu != null) _openMenu.IsOpen = false;
                            }));
                        }
                    }
                }
                return Native.NextMouseHook(hook, nCode, w, l);
            };

            hook = Native.SetMouseHook(_menuProc);
            if (hook == IntPtr.Zero)
            {
                _menuProc = null;
                // No hook available; the menu still opens, it just loses the foreign-click close.
                menu.PlacementTarget = this;
                menu.IsOpen = true;
                return;
            }

            menu.Closed += (s, e) =>
            {
                Native.RemoveMouseHook(hook);
                _menuProc = null;
                if (_openMenu == menu) _openMenu = null;
            };

            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }

        private void CloseOpenMenu()
        {
            ContextMenu m = _openMenu;
            _openMenu = null;
            if (m != null)
            {
                try { m.IsOpen = false; } catch { }
            }
        }

        private static MenuItem Mi(string header, Action action, bool isCheckable = false, bool isChecked = false)
        {
            MenuItem mi = new MenuItem { Header = header, IsCheckable = isCheckable, IsChecked = isChecked };
            if (action != null) mi.Click += (s, e) => action();
            return mi;
        }

        private ContextMenu BuildTileMenu(DockItem item)
        {
            ContextMenu m = new ContextMenu();

            m.Items.Add(Mi("Launch", () => Launch(item, false)));
            m.Items.Add(Mi("Run as administrator", () => Launch(item, true)));
            m.Items.Add(Mi("Open file location", () =>
            {
                try
                {
                    string p = Environment.ExpandEnvironmentVariables(item.Path);
                    if (File.Exists(p))
                        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + p + "\"") { UseShellExecute = true });
                }
                catch { }
            }));

            m.Items.Add(new Separator());

            m.Items.Add(Mi("Rename…", () =>
            {
                string v = Prompt("Rename", "Label shown under the icon", item.Name);
                if (v != null) { item.Name = v; QueueSave(); }
            }));

            m.Items.Add(Mi("Change icon…", () =>
            {
                Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Pick an icon",
                    Filter = "Images and icons|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.exe;*.dll|All files|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    item.IconSource = dlg.FileName;
                    item.Icon = LoadIconFor(item);
                    QueueSave();
                }
            }));

            m.Items.Add(Mi("Reset icon", () =>
            {
                item.IconSource = item.Path;
                item.Icon = LoadIconFor(item);
                QueueSave();
            }));

            m.Items.Add(Mi("Edit arguments…", () =>
            {
                string v = Prompt("Arguments", "Command line arguments", item.Arguments);
                if (v != null) { item.Arguments = v; QueueSave(); }
            }));

            m.Items.Add(new Separator());

            int idx = Items.IndexOf(item);
            MenuItem back = Mi(Horizontal ? "Move left" : "Move up", () =>
            {
                int i = Items.IndexOf(item);
                if (i > 0) Items.Move(i, i - 1);
            });
            back.IsEnabled = idx > 0;
            m.Items.Add(back);

            MenuItem fwd = Mi(Horizontal ? "Move right" : "Move down", () =>
            {
                int i = Items.IndexOf(item);
                if (i >= 0 && i < Items.Count - 1) Items.Move(i, i + 1);
            });
            fwd.IsEnabled = idx >= 0 && idx < Items.Count - 1;
            m.Items.Add(fwd);

            m.Items.Add(new Separator());
            m.Items.Add(Mi("Remove from dock", () => Items.Remove(item)));

            return m;
        }

        private ContextMenu BuildDockMenu()
        {
            ContextMenu m = new ContextMenu();

            // Everything tweakable now lives in the settings window; the menu keeps only the
            // handful of things you actually reach for mid-use.
            m.Items.Add(Mi("Settings…", OpenSettings));

            m.Items.Add(Mi("Add app…", () =>
            {
                Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Add to dock",
                    Filter = "Programs and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd|All files|*.*",
                    Multiselect = true
                };
                if (dlg.ShowDialog() == true)
                {
                    foreach (string f in dlg.FileNames) AddPath(f);
                }
            }));

            m.Items.Add(new Separator());

            MenuItem zorder = new MenuItem { Header = "Layer" };
            zorder.Items.Add(Mi("Glued to desktop", () => SetZOrder("wallpaper"), true, ZMode == "wallpaper"));
            zorder.Items.Add(Mi("On the desktop", () => SetZOrder("desktop"), true, ZMode == "desktop"));
            zorder.Items.Add(Mi("Normal window", () => SetZOrder("normal"), true, ZMode == "normal"));
            zorder.Items.Add(Mi("Always on top", () => SetZOrder("topmost"), true, ZMode == "topmost"));
            m.Items.Add(zorder);

            m.Items.Add(Mi("Snap to edges", () =>
            {
                Config.SnapToEdges = !Config.SnapToEdges;
                QueueSave();
            }, true, Config.SnapToEdges));

            m.Items.Add(Mi("Auto-hide", () =>
            {
                Config.AutoHide = !Config.AutoHide;
                _hideTimer.IsEnabled = Config.AutoHide;
                if (!Config.AutoHide && _autoHidden) SetHidden(false);
                QueueSave();
            }, true, Config.AutoHide));

            m.Items.Add(Mi("Lock position", () =>
            {
                Config.Locked = !Config.Locked;
                QueueSave();
            }, true, Config.Locked));

            m.Items.Add(new Separator());

            m.Items.Add(Mi("Edit config.json…", () =>
            {
                SaveNow();
                try
                {
                    Process.Start(new ProcessStartInfo(ConfigService.ConfigPath) { UseShellExecute = true });
                }
                catch
                {
                    try { Process.Start(new ProcessStartInfo("notepad.exe", ConfigService.ConfigPath) { UseShellExecute = true }); }
                    catch { }
                }
            }));

            m.Items.Add(Mi("Reload config", ReloadConfig));

            m.Items.Add(Mi("Reset position", () =>
            {
                Config.Left = -1;
                Config.Top = -1;
                RestorePosition();
                QueueSave();
            }));

            m.Items.Add(new Separator());
            // Close rather than Shutdown: closing marks this as deliberate, which is what stops App
            // from treating the disappearance as an Explorer restart and putting the dock back.
            m.Items.Add(Mi("Exit", () => { SaveNow(); Close(); }));

            return m;
        }

        private string Prompt(string title, string label, string initial)
        {
            Brush bg = Resources["DockBackgroundBrush"] as Brush ?? Brushes.Black;
            Brush fg = Resources["DockTextBrush"] as Brush ?? Brushes.White;
            Brush ac = Resources["DockAccentBrush"] as Brush ?? Brushes.DodgerBlue;

            // The prompt sits on a solid surface; a translucent dock colour would look broken.
            SolidColorBrush scb = bg as SolidColorBrush;
            if (scb != null)
            {
                Color c = scb.Color;
                bg = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            }

            return PromptWindow.Ask(this, title, label, initial, bg, fg, ac);
        }

        private void ReloadConfig()
        {
            DockConfig fresh = ConfigService.Load();
            Config = fresh;

            Items.Clear();
            foreach (DockItem it in Config.Items)
            {
                it.Icon = LoadIconFor(it);
                Items.Add(it);
            }

            ApplyTheme();
            ApplyZOrder();
            _hideTimer.IsEnabled = Config.AutoHide;
            UpdateLayout();
            RestorePosition();
        }

        private void SetZOrder(string mode)
        {
            Config.ZOrder = mode;
            Config.AlwaysOnTop = mode == "topmost";
            ApplyZOrder();
            QueueSave();
        }

        // ------------------------------------------------------------------
        //  Settings window
        // ------------------------------------------------------------------
        private SettingsWindow _settings;

        private void OpenSettings()
        {
            if (_settings != null)
            {
                try { _settings.Activate(); return; }
                catch { _settings = null; }
            }

            _settings = new SettingsWindow(this);
            _settings.Closed += (s, e) => { _settings = null; SaveNow(); };
            _settings.Show();
        }

        /// <summary>Called by the settings window after any change, to repaint the dock live.</summary>
        public void ApplyLive()
        {
            ApplyTheme();
            ApplyZOrder();

            _hideTimer.IsEnabled = Config.AutoHide;
            if (!Config.AutoHide && _autoHidden) SetHidden(false);

            UpdateLayout();
            UpdateMagnification(null, false);
            QueueSave();
        }

        /// <summary>Recentre the dock. Exposed for the settings window.</summary>
        public void ResetPosition()
        {
            UpdateLayout();
            RestorePosition();
            QueueSave();
        }

        // ------------------------------------------------------------------
        //  Persistence
        // ------------------------------------------------------------------
        private void QueueSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveNow()
        {
            Config.Items.Clear();
            foreach (DockItem it in Items) Config.Items.Add(it);

            if (!_autoHidden && IsLoaded)
            {
                Config.Left = _shownLeft;
                Config.Top = _shownTop;
            }

            ConfigService.Save(Config);
        }

        // ------------------------------------------------------------------
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
