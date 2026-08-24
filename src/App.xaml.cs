using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Dockyard
{
    public partial class App : Application
    {
        private static Mutex _single;
        private bool _shuttingDown;

        /// <summary>Set by a second launch to tell the running dock to show itself.</summary>
        internal const string ShowSignalName = "Dockyard.ShowYourself.9f2c";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // One dock at a time. But a running instance is not necessarily a visible one — a bad
            // window setting can leave it alive with nothing on screen, still holding this handle
            // and silently swallowing every new launch. So rather than just exiting, poke the
            // instance that is already running and ask it to make itself visible again. Running the
            // app a second time becomes the fix for a dock you cannot see.
            bool isNew;
            _single = new Mutex(true, "Dockyard.SingleInstance.9f2c", out isNew);
            if (!isNew)
            {
                try
                {
                    EventWaitHandle existing;
                    if (EventWaitHandle.TryOpenExisting(ShowSignalName, out existing))
                    {
                        existing.Set();
                        existing.Dispose();
                    }
                }
                catch { }

                _shuttingDown = true;
                Shutdown();
                return;
            }

            // Never let an unexpected exception nuke the dock silently.
            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show(args.Exception.Message, "Dockyard", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };

            Spawn();
        }

        /// <summary>
        /// Creates the dock, and puts it back if it goes away without being asked to.
        ///
        /// That happens for real: in wallpaper mode the dock is a child of one of Explorer's
        /// desktop windows, and destroying a window destroys its children — so an Explorer restart
        /// takes the dock down with it. The window closes without ever raising a close request,
        /// which is exactly the signal used here to tell the two cases apart.
        /// </summary>
        private void Spawn()
        {
            MainWindow dock = new MainWindow();
            MainWindow = dock;

            dock.Closed += (s, e) =>
            {
                if (_shuttingDown) return;

                if (dock.UserClosed)
                {
                    _shuttingDown = true;
                    Shutdown();
                    return;
                }

                // Give Explorer a moment to finish rebuilding the desktop before we go looking
                // for it again.
                DispatcherTimer retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
                retry.Tick += (s2, e2) =>
                {
                    retry.Stop();
                    if (!_shuttingDown) Spawn();
                };
                retry.Start();
            };

            dock.Show();
        }
    }
}
