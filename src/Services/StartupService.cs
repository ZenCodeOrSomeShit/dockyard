using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace Dockyard.Services
{
    /// <summary>
    /// Launch-at-login, via the per-user Run key. No scheduled task, no admin rights, and it shows
    /// up in Task Manager's Startup tab where people expect to find it.
    /// </summary>
    public static class StartupService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Dockyard";

        /// <summary>
        /// How Windows should start us. Normally just the exe — but when the app is running through
        /// the shared runtime (because the apphost could not be produced) the command has to be
        /// dotnet plus the dll, or nothing happens at login.
        /// </summary>
        public static string LaunchCommand()
        {
            try
            {
                string host = Process.GetCurrentProcess().MainModule.FileName;

                if (host.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                {
                    Assembly entry = Assembly.GetEntryAssembly();
                    string dll = entry != null ? entry.Location : null;
                    if (!string.IsNullOrEmpty(dll))
                        return "\"" + host + "\" \"" + dll + "\"";
                }

                return "\"" + host + "\"";
            }
            catch
            {
                return null;
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(ValueName);
                    return v != null && !string.IsNullOrWhiteSpace(v.ToString());
                }
            }
            catch { return false; }
        }

        /// <summary>Returns false if the registry refused the write.</summary>
        public static bool SetEnabled(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey, true))
                {
                    if (k == null) return false;

                    if (!on)
                    {
                        k.DeleteValue(ValueName, false);
                        return true;
                    }

                    string cmd = LaunchCommand();
                    if (string.IsNullOrEmpty(cmd)) return false;

                    k.SetValue(ValueName, cmd, RegistryValueKind.String);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
