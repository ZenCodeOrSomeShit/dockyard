using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dockyard.Interop
{
    /// <summary>
    /// Pulls the real, high-resolution icon out of an executable, shortcut, folder or document.
    ///
    /// Preferred path is IShellItemImageFactory, which hands back the same 256px jumbo bitmap
    /// Explorer uses. If that fails for any reason we drop to SHGetFileInfo (48px, always works),
    /// and finally to a plain image loader for .png/.ico overrides.
    /// </summary>
    internal static class IconExtractor
    {
        // ---- IShellItemImageFactory --------------------------------------

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [Flags]
        private enum SIIGBF
        {
            ResizeToFit = 0x00,
            BiggerSizeOk = 0x01,
            MemoryOnly = 0x02,
            IconOnly = 0x04,
            ThumbnailOnly = 0x08,
            InCacheOnly = 0x10,
            ScaleUp = 0x100
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        // ---- GDI ----------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
        private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
            byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        private const int BI_RGB = 0;
        private const uint DIB_RGB_COLORS = 0;

        // ---- SHGetFileInfo fallback ---------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // ===================================================================

        /// <summary>Get the best available icon for a path. Never throws; returns null if nothing worked.</summary>
        public static ImageSource Load(string path, int size = 128)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // Plain image files: just decode them.
            string ext = "";
            try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch { }
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif")
            {
                ImageSource direct = LoadImageFile(path);
                if (direct != null) return direct;
            }
            if (ext == ".ico")
            {
                // Multi-size .ico: pick the biggest frame ourselves. The default decoder grabs
                // whichever frame it feels like, which is often the 16px one, and a 16px frame
                // scaled up to a tile looks exactly like the "shrunken icon" bug.
                ImageSource ico = LoadIcoLargest(path);
                if (ico != null) return ico;
            }

            ImageSource shell = FromShellItem(path, size);
            if (shell != null) return shell;

            return FromShGetFileInfo(path);
        }

        private static ImageSource LoadIcoLargest(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                IconBitmapDecoder decoder = new IconBitmapDecoder(
                    new Uri(path, UriKind.Absolute),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);            // don't keep a file lock
                BitmapFrame best = null;
                foreach (BitmapFrame frame in decoder.Frames)
                {
                    if (best == null ||
                        frame.PixelWidth * frame.PixelHeight > best.PixelWidth * best.PixelHeight)
                    {
                        best = frame;
                    }
                }
                if (best == null) return null;
                best.Freeze();
                return best;
            }
            catch { return null; }
        }

        private static ImageSource LoadImageFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;          // don't keep a file lock
                bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static ImageSource FromShellItem(string path, int size)
        {
            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                IShellItemImageFactory factory;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
                if (factory == null) return null;

                SIZE sz = new SIZE { cx = size, cy = size };
                factory.GetImage(sz, SIIGBF.IconOnly | SIIGBF.BiggerSizeOk, out hBitmap);
                Marshal.ReleaseComObject(factory);

                if (hBitmap == IntPtr.Zero) return null;
                return FromHBitmap(hBitmap);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            }
        }

        /// <summary>
        /// Turns the shell's HBITMAP into a WPF image, keeping the premultiplied alpha that
        /// CreateBitmapSourceFromHBitmap would flatten.
        ///
        /// The bits are read through GetDIBits with a negative biHeight rather than straight off
        /// bmBits. A DIB can be stored either top-down or bottom-up and GetObject reports a positive
        /// height for both, so there is no way to tell them apart from the header — reading bmBits
        /// directly gets it right half the time and hands back an upside-down icon the rest. Asking
        /// GDI for a top-down copy makes the orientation explicit instead of assumed.
        /// </summary>
        private static ImageSource FromHBitmap(IntPtr hBitmap)
        {
            IntPtr hdc = IntPtr.Zero;
            try
            {
                BITMAP bm;
                if (GetObject(hBitmap, Marshal.SizeOf(typeof(BITMAP)), out bm) == 0) return null;
                if (bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;

                int w = bm.bmWidth;
                int h = bm.bmHeight;
                int stride = w * 4;
                byte[] pixels = new byte[stride * h];

                BITMAPINFOHEADER bi = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                    biWidth = w,
                    biHeight = -h,          // negative = give me the rows top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                    biSizeImage = stride * h
                };

                hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return null;

                int copied = GetDIBits(hdc, hBitmap, 0, (uint)h, pixels, ref bi, DIB_RGB_COLORS);
                if (copied == 0) return null;

                // An all-zero alpha channel means the source carried no alpha at all. Left as-is it
                // would render as an invisible rectangle, so make it opaque.
                bool anyAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0) { anyAlpha = true; break; }
                }
                if (!anyAlpha)
                {
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                }

                BitmapSource src = BitmapSource.Create(
                    w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally
            {
                if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        private static ImageSource FromShGetFileInfo(string path)
        {
            SHFILEINFO info = new SHFILEINFO();
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                uint flags = SHGFI_ICON | SHGFI_LARGEICON;
                if (!File.Exists(path) && !Directory.Exists(path)) flags |= SHGFI_USEFILEATTRIBUTES;

                SHGetFileInfoW(path, 0x80 /* FILE_ATTRIBUTE_NORMAL */, ref info,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);

                hIcon = info.hIcon;
                if (hIcon == IntPtr.Zero) return null;

                ImageSource src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally
            {
                if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            }
        }
    }
}
