using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Dockyard.Interop
{
    /// <summary>What a .lnk actually points at.</summary>
    internal class ShortcutTarget
    {
        public string Path = "";
        public string Arguments = "";
        public string WorkingDirectory = "";
        public string IconPath = "";
    }

    /// <summary>What a .url internet shortcut points at.</summary>
    internal class UrlTarget
    {
        public string Url = "";
        public string IconFile = "";
    }

    /// <summary>Reads .lnk files through IShellLinkW so dropped Start-menu shortcuts work properly.</summary>
    internal static class ShortcutResolver
    {
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath,
                out WIN32_FIND_DATAW pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
                int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        private const uint SLGP_RAWPATH = 0x4;
        private const uint SLR_NO_UI = 0x1;
        private const uint SLR_NOUPDATE = 0x8;

        /// <summary>Returns null if the file isn't a resolvable shortcut.</summary>
        public static ShortcutTarget Resolve(string lnkPath)
        {
            object comObject = null;
            try
            {
                comObject = new ShellLink();
                IShellLinkW link = (IShellLinkW)comObject;
                IPersistFile persist = (IPersistFile)comObject;

                persist.Load(lnkPath, 0);
                try { link.Resolve(IntPtr.Zero, SLR_NO_UI | SLR_NOUPDATE); } catch { /* dead target is fine */ }

                StringBuilder sb = new StringBuilder(512);
                WIN32_FIND_DATAW fd;
                link.GetPath(sb, sb.Capacity, out fd, SLGP_RAWPATH);
                string target = sb.ToString();

                sb.Clear();
                link.GetArguments(sb, sb.Capacity);
                string args = sb.ToString();

                sb.Clear();
                link.GetWorkingDirectory(sb, sb.Capacity);
                string wd = sb.ToString();

                sb.Clear();
                int iconIndex;
                link.GetIconLocation(sb, sb.Capacity, out iconIndex);
                string iconPath = sb.ToString();

                if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(iconPath))
                    return null;

                target = Environment.ExpandEnvironmentVariables(target ?? "");
                iconPath = Environment.ExpandEnvironmentVariables(iconPath ?? "");
                wd = Environment.ExpandEnvironmentVariables(wd ?? "");

                return new ShortcutTarget
                {
                    Path = target,
                    Arguments = args ?? "",
                    WorkingDirectory = wd,
                    IconPath = (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath)) ? iconPath : ""
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                if (comObject != null)
                {
                    try { Marshal.ReleaseComObject(comObject); } catch { }
                }
            }
        }

        /// <summary>
        /// Pulls the URL and icon out of an Internet Shortcut (.url) file. Steam, Xbox and a few
        /// launchers hand these out. The IconFile line matters: it names the .ico Explorer shows,
        /// and asking the shell to render the .url itself comes back as a small icon painted on an
        /// opaque white square, which is exactly what the dock must not put on a tile.
        /// </summary>
        public static UrlTarget ResolveUrlFile(string urlPath)
        {
            UrlTarget target = new UrlTarget();
            try
            {
                foreach (string line in File.ReadAllLines(urlPath))
                {
                    string t = line.Trim();
                    if (target.Url == "" && t.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        target.Url = t.Substring(4).Trim();
                    else if (target.IconFile == "" && t.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                        target.IconFile = Environment.ExpandEnvironmentVariables(t.Substring(9).Trim());
                }
            }
            catch { }
            return target;
        }
    }
}
