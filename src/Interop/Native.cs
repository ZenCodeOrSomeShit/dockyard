using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Dockyard.Interop
{
    // ----------------------------------------------------------------------
    //  Undocumented-but-universal DWM accent API. This is what gives the dock
    //  a real system blur instead of a fake translucent rectangle.
    // ----------------------------------------------------------------------
    internal enum AccentState
    {
        Disabled = 0,
        EnableGradient = 1,
        EnableTransparentGradient = 2,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
        EnableHostBackdrop = 5,
        InvalidState = 6
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;   // 0xAABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        public int Attribute;       // 19 = WCA_ACCENT_POLICY
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    internal static class Native
    {
        private const int WCA_ACCENT_POLICY = 19;

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);


        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        internal const uint MONITOR_DEFAULTTONEAREST = 2;

        internal const int WM_SIZE = 0x0005;
        internal const int WM_WINDOWPOSCHANGING = 0x0046;
        internal const int WM_SYSCOMMAND = 0x0112;

        internal const int SC_MINIMIZE = 0xF020;
        internal const int SIZE_MINIMIZED = 1;

        internal static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_HIDEWINDOW = 0x0080;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        /// <summary>
        /// Stops the dock taking focus when clicked. Needed for the desktop z-order mode, otherwise
        /// clicking a tile activates the dock and Windows immediately raises it above everything.
        /// </summary>
        internal static void SetNoActivate(IntPtr hwnd, bool on)
        {
            if (hwnd == IntPtr.Zero) return;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            int updated = on ? (ex | WS_EX_NOACTIVATE) : (ex & ~WS_EX_NOACTIVATE);
            if (updated != ex) SetWindowLong(hwnd, GWL_EXSTYLE, updated);
        }

        /// <summary>
        /// Turn the system blur on (or off) behind a window.
        /// mode: "acrylic" | "blur" | anything else = off.
        /// tint is #AARRGGBB; for acrylic it is the colour mixed into the blur.
        /// </summary>
        internal static void ApplyBackdrop(IntPtr hwnd, string mode, byte a, byte r, byte g, byte b)
        {
            if (hwnd == IntPtr.Zero) return;

            AccentState state;
            switch ((mode ?? "").ToLowerInvariant())
            {
                case "acrylic": state = AccentState.EnableAcrylicBlurBehind; break;
                case "blur": state = AccentState.EnableBlurBehind; break;
                default: state = AccentState.Disabled; break;
            }

            AccentPolicy policy = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = 2,                                  // draw all borders
                GradientColor = (a << 24) | (b << 16) | (g << 8) | r,   // ABGR, not ARGB
                AnimationId = 0
            };

            int size = Marshal.SizeOf(typeof(AccentPolicy));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                WindowCompositionAttributeData data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    SizeOfData = size,
                    Data = ptr
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            catch { /* older builds simply won't blur; the tint still reads as glass */ }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        internal const int CORNER_DEFAULT = 0;
        internal const int CORNER_DONOTROUND = 1;
        internal const int CORNER_ROUND = 2;
        internal const int CORNER_ROUNDSMALL = 3;

        /// <summary>
        /// The DWM blur fills the whole window rectangle, so a rounded slab otherwise sits on four
        /// square blurred corners.
        ///
        /// SetWindowRgn looks like the fix but is silently ignored on layered windows, which is what
        /// AllowsTransparency makes this one. DWM's own corner rounding does apply, so that is what
        /// gets used — at the cost of DWM picking the radius, not us.
        /// </summary>
        internal static void SetCornerPreference(IntPtr hwnd, int preference)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int value = preference;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
            }
            catch { /* pre-Windows 11: nothing to do */ }
        }

        /// <summary>
        /// Keep the dock out of Alt+Tab and off the taskbar.
        ///
        /// Setting WS_EX_TOOLWINDOW is only half of it: WS_EX_APPWINDOW forces a taskbar button
        /// regardless, and WPF sets it whenever ShowInTaskbar is true — which this window needs, so
        /// that WPF doesn't build a hidden owner window and break reparenting. So the app flag has
        /// to come off at the same time.
        /// </summary>
        internal static void MakeToolWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            int updated = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
            if (updated == ex) return;

            SetWindowLong(hwnd, GWL_EXSTYLE, updated);
        }

        internal static bool IsInTaskbar(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            return (ex & WS_EX_APPWINDOW) != 0 || (ex & WS_EX_TOOLWINDOW) == 0;
        }

        // ------------------------------------------------------------------
        //  Gluing a window into the desktop itself
        // ------------------------------------------------------------------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string win);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder cls, int max);

        internal static string ClassNameOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "(none)";
            StringBuilder sb = new StringBuilder(64);
            GetClassNameW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        internal const int SW_HIDE = 0;
        internal const int SW_SHOWNA = 8;

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        private const int DWMWA_CLOAKED = 14;

        /// <summary>
        /// Whether DWM is hiding this window without minimising or hiding it in any way the window
        /// itself can see. 0 = visible, 1 = the app cloaked it, 2 = the shell did, 4 = inherited
        /// from an owner.
        ///
        /// This matters because a cloaked window still reports WindowState.Normal and IsVisible
        /// true — every ordinary check says it is fine while nothing is drawn on screen.
        /// </summary>
        internal static int GetCloaked(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            try
            {
                int value;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out value, sizeof(int)) == 0) return value;
            }
            catch { }
            return 0;
        }

        internal static string DescribeCloak(int cloak)
        {
            switch (cloak)
            {
                case 0: return "not cloaked";
                case 1: return "cloaked by the app";
                case 2: return "cloaked by the shell";
                case 4: return "cloaked via owner";
                default: return "cloaked (" + cloak + ")";
            }
        }

        [DllImport("user32.dll")]
        internal static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterWindowMessageW(string message);

        internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_FRAMECHANGED = 0x0020;

        private const int GWL_STYLE = -16;
        private const int GWL_HWNDPARENT = -8;
        private const uint WS_CHILD = 0x40000000;
        private const uint WS_POPUP = 0x80000000;

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        private static void SetWindowHandleValue(IntPtr hwnd, int index, IntPtr value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, index, value);
            else SetWindowLong32(hwnd, index, value.ToInt32());
        }

        /// <summary>
        /// Drops the window's owner.
        ///
        /// WPF gives any window with ShowInTaskbar="False" a hidden owner window of its own — the
        /// HwndWrapper you see if you ask GetParent — so that it stays off the taskbar. That
        /// ownership is stored in the same slot the system uses for a popup's parent, so it
        /// overwrites SetParent and the dock quietly springs back to being top-level. Clearing it
        /// first is what makes the reparent stick.
        /// </summary>
        internal static void ClearOwner(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            SetWindowHandleValue(hwnd, GWL_HWNDPARENT, IntPtr.Zero);
        }

        /// <summary>
        /// Swaps the window between WS_POPUP and WS_CHILD.
        ///
        /// SetParent alone is not enough. A WS_POPUP window whose parent has been changed is still
        /// styled as a top-level window, and the shell keeps treating it as one — which is why
        /// Show Desktop could still minimise it. Only WS_CHILD makes it genuinely part of the
        /// parent, and it has to be set before SetParent for the change to take cleanly.
        /// </summary>
        internal static void SetChildStyle(IntPtr hwnd, bool asChild)
        {
            if (hwnd == IntPtr.Zero) return;

            uint style = unchecked((uint)GetWindowLong(hwnd, GWL_STYLE));

            uint updated = asChild
                ? (style & ~WS_POPUP) | WS_CHILD
                : (style & ~WS_CHILD) | WS_POPUP;

            if (updated == style) return;

            SetWindowLong(hwnd, GWL_STYLE, unchecked((int)updated));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        /// <summary>
        /// The desktop window that actually hosts the icon view.
        ///
        /// Normally that is Progman. But once anything has asked Progman to spawn a WorkerW — a
        /// wallpaper slideshow, another widget app — the icon view gets moved out into that WorkerW
        /// and Progman is left behind it. Parenting to Progman in that state would bury the dock, so
        /// the host is found by looking for whoever currently owns SHELLDLL_DefView rather than
        /// assuming.
        ///
        /// Deliberately does NOT send the 0x052C message that spawns a WorkerW: that would rearrange
        /// the user's desktop as a side effect of us wanting to sit on it.
        /// </summary>
        internal static IntPtr FindDesktopIconHost()
        {
            IntPtr progman = FindWindowW("Progman", null);

            if (progman != IntPtr.Zero &&
                FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                return progman;
            }

            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, lp) =>
            {
                StringBuilder cls = new StringBuilder(32);
                GetClassNameW(hwnd, cls, cls.Capacity);
                if (cls.ToString() != "WorkerW") return true;

                if (FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    found = hwnd;
                    return false;   // stop enumerating
                }
                return true;
            }, IntPtr.Zero);

            return found != IntPtr.Zero ? found : progman;
        }

        /// <summary>Raise a child to the top of its parent's stack, so it draws over the icon view.</summary>
        internal static void RaiseWithinParent(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>Work area (excludes the taskbar) of the monitor the window is on, in device pixels.</summary>
        internal static RECT GetWorkArea(IntPtr hwnd)
        {
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (GetMonitorInfoW(mon, ref mi)) return mi.rcWork;

            // Fallback: primary screen guess.
            return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        }
    }
}
