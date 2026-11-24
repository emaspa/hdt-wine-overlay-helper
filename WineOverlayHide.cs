// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Emanuele Sparvoli
//
// Companion program for Hearthstone Deck Tracker under Wine/Proton: really hide
// the overlay when Hearthstone loses focus.
//
// Stock HDT doesn't hide the overlay when Hearthstone goes to the background.
// It sends it to the bottom of the z-order. Under Wine the overlay is an
// override-redirect X window, which the compositor always draws above managed
// windows, so "send to back" does nothing and the overlay floats over every
// other app.
//
// This program runs next to HDT in the same Wine prefix and only looks at
// windows. It never touches the Hearthstone or HDT processes. While Hearthstone
// is running but is not the foreground window, it hides the overlay window,
// which makes Wine unmap it, and shows it again when Hearthstone regains focus.
// It follows HDT's "Hide in background" option, read from HDT's config.xml.
//
// It also keeps the overlay on top of Hearthstone's client area. HDT follows
// the game window through a location-change WinEvent hook, and under Wine that
// event doesn't arrive when the window manager moves or resizes the window, so
// the overlay would stay where it was.
// (HDT's z-order state can't be used as the signal: Wine keeps WS_EX_TOPMOST
// set on a window that was moved to HWND_BOTTOM.)
//
// Versions up to 1.0.0 were an HDT plugin. HDT removed plugin support on
// 2026-11-30, hence the standalone program.
//
// C# 5: compiled with the .NET Framework csc.exe inside the Wine prefix.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

[assembly: System.Reflection.AssemblyTitle("WineOverlayHide")]
[assembly: System.Reflection.AssemblyDescription("Hides the Hearthstone Deck Tracker overlay while Hearthstone is in the background under Wine/Proton")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.0.0")]

namespace WineOverlayHide
{
    internal static class Program
    {
        private const string OverlayTitle = "HearthstoneOverlay";
        private const string WpfClassPrefix = "HwndWrapper[";
        private const string HearthstoneClass = "UnityWndClass";
        private const string HdtProcessName = "HearthstoneDeckTracker";
        private const string UpdaterProcessName = "Update";

        private const int PollMs = 100;
        // How long to wait for HDT to come up, and how long it may be gone
        // (self-update restarts it) before this program exits too.
        private const int StartupGraceMs = 180000;
        private const int GoneGraceMs = 15000;
        private const int AdoptMs = 5000;

        private const int SwHide = 0;
        private const int SwShowNa = 8;

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const uint SwpAsyncWindowPos = 0x4000;
        // Don't repeat the same move more often than this while HDT applies it.
        private const int MoveRetryMs = 1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X, Y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        // Async: never block on HDT's UI thread.
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private static StreamWriter _log;
        private static string _lastForeground;
        private static Rect _lastMove;
        private static readonly Stopwatch MoveAge = new Stopwatch();

        // HDT's "Hide in background" option. HDT defaults it to on.
        private static bool _hideInBackground = true;
        private static string _configPath;
        private static DateTime _configStamp;
        private static readonly Stopwatch ConfigAge = new Stopwatch();

        [STAThread]
        private static int Main(string[] args)
        {
            bool launch = false;
            string logPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--launch") launch = true;
                else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
                else if (args[i] == "--log") logPath = Path.Combine(Path.GetTempPath(), "WineOverlayHide.log");
            }

            // Work in real pixels, like HDT and Hearthstone do.
            try { SetProcessDPIAware(); } catch (Exception) { }

            if (launch) LaunchHdt();

            bool first;
            using (Mutex single = new Mutex(true, "WineOverlayHide-watcher", out first))
            {
                if (!first) return 0; // another instance already watches this session

                if (logPath != null)
                {
                    try
                    {
                        _log = new StreamWriter(logPath, true);
                        _log.AutoFlush = true;
                    }
                    catch (Exception) { }
                }
                Log("started");
                Watch();
                Log("HDT is gone, exiting");
            }
            return 0;
        }

        private static void Watch()
        {
            IntPtr overlay = IntPtr.Zero;
            bool weHid = false;
            bool seenHdt = false;
            Stopwatch absent = Stopwatch.StartNew();
            Stopwatch sinceStart = Stopwatch.StartNew();

            while (true)
            {
                if (overlay == IntPtr.Zero || !IsWindow(overlay))
                {
                    overlay = FindOverlay();
                    weHid = false;
                    if (overlay != IntPtr.Zero)
                    {
                        Log("overlay window found");
                        // HDT keeps the overlay window shown while Hearthstone
                        // runs. If it is hidden right as this program starts, an
                        // earlier instance hid it and died: take that over, or
                        // nothing would ever show it again.
                        if (sinceStart.ElapsedMilliseconds < AdoptMs && !IsWindowVisible(overlay)
                            && FindWindow(HearthstoneClass, null) != IntPtr.Zero)
                        {
                            weHid = true;
                            Log("adopted hidden overlay");
                        }
                    }
                }

                if (overlay == IntPtr.Zero)
                {
                    // No overlay window: HDT is starting, restarting for an
                    // update, or gone. Poll slowly and decide whether to quit.
                    if (IsHdtProcessAlive())
                    {
                        seenHdt = true;
                        absent.Restart();
                    }
                    else if (absent.ElapsedMilliseconds > (seenHdt ? GoneGraceMs : StartupGraceMs))
                    {
                        return;
                    }
                    Thread.Sleep(1000);
                    continue;
                }

                seenHdt = true;
                absent.Restart();
                Tick(overlay, ref weHid);
                Thread.Sleep(PollMs);
            }
        }

        private static void Tick(IntPtr overlay, ref bool weHid)
        {
            IntPtr hearthstone = FindWindow(HearthstoneClass, null);
            if (hearthstone == IntPtr.Zero)
            {
                // Hearthstone is closed: HDT hides the overlay on its own and
                // shows it again on the next start. Nothing left to restore.
                weHid = false;
                return;
            }

            RefreshConfig();
            FollowHearthstone(overlay, hearthstone);
            string foreground = ClassOf(GetForegroundWindow());
            bool hsForeground = foreground == HearthstoneClass;
            bool visible = IsWindowVisible(overlay);
            if (_log != null && foreground != _lastForeground)
            {
                _lastForeground = foreground;
                Log("foreground: " + (foreground.Length > 0 ? foreground : "(none)"));
            }

            if (!hsForeground && _hideInBackground)
            {
                // HDT only sends the overlay to the back here, which does
                // nothing under Wine. Hide it for real.
                if (visible)
                {
                    ShowWindowAsync(overlay, SwHide);
                    Log(weHid ? "hide again" : "hide");
                    weHid = true;
                }
            }
            else if (weHid)
            {
                if (!visible) ShowWindowAsync(overlay, SwShowNa);
                Log("show");
                weHid = false;
            }
        }

        // Put the overlay where HDT's own UpdatePosition would put it: on
        // Hearthstone's client area, one pixel short on the right and bottom.
        private static void FollowHearthstone(IntPtr overlay, IntPtr hearthstone)
        {
            if (IsIconic(hearthstone)) return;
            Rect client, current;
            if (!GetClientRect(hearthstone, out client) || !GetWindowRect(overlay, out current)) return;

            Point topLeft = new Point();
            topLeft.X = client.Left;
            topLeft.Y = client.Top;
            Point bottomRight = new Point();
            bottomRight.X = Math.Max(client.Left, client.Right - 1);
            bottomRight.Y = Math.Max(client.Top, client.Bottom - 1);
            if (!ClientToScreen(hearthstone, ref topLeft) || !ClientToScreen(hearthstone, ref bottomRight)) return;

            Rect want = new Rect();
            want.Left = topLeft.X;
            want.Top = topLeft.Y;
            want.Right = bottomRight.X;
            want.Bottom = bottomRight.Y;
            int width = want.Right - want.Left;
            int height = want.Bottom - want.Top;
            // A window that just launched has no height yet.
            if (width <= 0 || height <= 0) return;

            bool moved = current.Left != want.Left || current.Top != want.Top;
            bool resized = current.Right - current.Left != width || current.Bottom - current.Top != height;
            if (!moved && !resized) return;

            bool sameAsLast = want.Left == _lastMove.Left && want.Top == _lastMove.Top
                && want.Right == _lastMove.Right && want.Bottom == _lastMove.Bottom;
            if (sameAsLast && MoveAge.IsRunning && MoveAge.ElapsedMilliseconds < MoveRetryMs) return;
            _lastMove = want;
            MoveAge.Restart();

            uint flags = SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate | SwpAsyncWindowPos;
            if (!resized) flags |= SwpNoSize;
            SetWindowPos(overlay, IntPtr.Zero, want.Left, want.Top, width, height, flags);
            Log((resized ? "move and resize to " : "move to ") + want.Left + "," + want.Top + " " + width + "x" + height
                + (sameAsLast ? " (again)" : ""));
        }

        // Re-read HDT's config.xml when it changes, at most once a second.
        private static void RefreshConfig()
        {
            if (ConfigAge.IsRunning && ConfigAge.ElapsedMilliseconds < 1000) return;
            ConfigAge.Restart();
            try
            {
                if (_configPath == null)
                    _configPath = Path.Combine(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "HearthstoneDeckTracker"), "config.xml");
                DateTime stamp = File.GetLastWriteTimeUtc(_configPath);
                if (stamp == _configStamp) return;
                string xml;
                using (FileStream fs = new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(fs))
                    xml = reader.ReadToEnd();
                // A half-written file has neither value: keep the old setting
                // and the old stamp, and look again next time.
                bool on = xml.Contains("<HideInBackground>true</HideInBackground>");
                bool off = xml.Contains("<HideInBackground>false</HideInBackground>");
                if (!on && !off) return;
                _configStamp = stamp;
                if (on != _hideInBackground) Log("HideInBackground=" + on);
                _hideInBackground = on;
            }
            catch (Exception)
            {
                // Missing or locked: keep the current setting.
            }
        }

        private static IntPtr FindOverlay()
        {
            IntPtr hwnd = FindWindow(null, OverlayTitle);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            // HDT's overlay is a WPF window. Ignore anything else with that title.
            return ClassOf(hwnd).StartsWith(WpfClassPrefix, StringComparison.Ordinal) ? hwnd : IntPtr.Zero;
        }

        private static string ClassOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(256);
            return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        private static bool IsHdtProcessAlive()
        {
            try
            {
                return Process.GetProcessesByName(HdtProcessName).Length > 0
                    || Process.GetProcessesByName(UpdaterProcessName).Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void LaunchHdt()
        {
            if (FindOverlay() != IntPtr.Zero) return; // already running

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HearthstoneDeckTracker");
            string updater = Path.Combine(dir, "Update.exe");
            string direct = Path.Combine(Path.Combine(dir, "current"), HdtProcessName + ".exe");
            try
            {
                ProcessStartInfo psi = File.Exists(updater)
                    ? new ProcessStartInfo(updater, "--processStart " + HdtProcessName + ".exe")
                    : new ProcessStartInfo(direct);
                psi.WorkingDirectory = dir;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception)
            {
                // Nothing to report to: without HDT the watcher exits after the
                // startup grace period.
            }
        }

        private static void Log(string what)
        {
            if (_log == null) return;
            try { _log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + what); }
            catch (Exception) { }
        }
    }
}
