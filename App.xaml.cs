using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace FloatingClipboard
{
    public partial class App : Application
    {
        Mutex mutex;
        WinForms.NotifyIcon tray;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Contains("--selftest"))
            {
                AltHold.SelfTest();
                Console.WriteLine("selftest ok");
                Shutdown(0);
                return;
            }
            mutex = new Mutex(true, "FloatingClipboard.SingleInstance", out bool first);
            if (!first) { Shutdown(); return; }

            var s = Settings.Load();
            s.ApplyAutostart(); // refreshed on every start, so the path follows the exe if it moves
            var win = new MainWindow(s);

            var auto = new WinForms.ToolStripMenuItem("Start with Windows") { Checked = s.Autostart, CheckOnClick = true };
            auto.CheckedChanged += (_, _) => { s.Autostart = auto.Checked; s.Save(); s.ApplyAutostart(); };
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Show / Hide", null, (_, _) => win.Toggle());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(auto);
            menu.Items.Add(Choice("Alt hold time", new[] { 0.3, 0.45, 0.6, 0.8 }, v => $"{v} s", s.HoldSeconds,
                v => { s.HoldSeconds = win.Alt.Hold = v; s.Save(); }));
            menu.Items.Add(Choice("Default position", new[] { "Cursor", "Top-left" }, v => v, s.Position,
                v => { s.Position = v; s.Save(); }));
            menu.Items.Add(Choice("History limit", new[] { 50, 100, 300, 1000 }, v => $"{v} items", s.MaxHistory,
                v => { s.MaxHistory = v; s.Save(); win.TrimHistory(); }));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Clear history", null, (_, _) => win.ClearHistory());
            menu.Items.Add("Clear shelf", null, (_, _) => win.ClearShelf());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => Shutdown());

            using var exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            tray = new WinForms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(exeIcon, WinForms.SystemInformation.SmallIconSize),
                Text = "Floating Clipboard — hold Alt to toggle",
                ContextMenuStrip = menu,
                Visible = true,
            };
            tray.MouseClick += (_, a) => { if (a.Button == WinForms.MouseButtons.Left) win.Toggle(); };
            if (e.Args.Contains("--show")) win.Toggle();
        }

        // Radio-style submenu: checks the current value, switches on click
        static WinForms.ToolStripMenuItem Choice<T>(string title, T[] values, Func<T, string> label, T current, Action<T> set)
        {
            var root = new WinForms.ToolStripMenuItem(title);
            foreach (var v in values)
            {
                var item = new WinForms.ToolStripMenuItem(label(v)) { Checked = Equals(v, current) };
                item.Click += (_, _) =>
                {
                    set(v);
                    foreach (WinForms.ToolStripMenuItem i in root.DropDownItems) i.Checked = i == item;
                };
                root.DropDownItems.Add(item);
            }
            return root;
        }

        protected override void OnExit(ExitEventArgs e) => tray?.Dispose();
    }

    public class Settings
    {
        static readonly string FilePath = Path.Combine(MainWindow.Dir, "settings.json");
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public bool Autostart { get; set; } = true;
        public double HoldSeconds { get; set; } = 0.45;
        public int MaxHistory { get; set; } = 300;
        public string Position { get; set; } = "Cursor"; // "Cursor" or "Top-left"
        public double? PinX { get; set; } // set by Ctrl+P; overrides Position
        public double? PinY { get; set; }

        public static Settings Load()
        {
            try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
            catch (Exception ex) when (ex is IOException or JsonException) { return new Settings(); }
        }

        public void Save()
        {
            Directory.CreateDirectory(MainWindow.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }

        public void ApplyAutostart()
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (Autostart) key.SetValue("FloatingClipboard", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("FloatingClipboard", false);
        }
    }

    /// <summary>Alt held alone for Hold seconds → Poll() returns true, once per press.
    /// Alt tapped alone (released before Hold) → Key() returns true on release.
    /// Any other key while Alt is down cancels both (so Alt+Tab, Alt+F4 never trigger).</summary>
    public class AltHold
    {
        public double Hold { get; set; }
        double? since;
        bool spent;

        public AltHold(double hold = 0.45) => Hold = hold;

        static bool IsAlt(int vk) => vk is 0x12 or 0xA4 or 0xA5;

        public bool Key(int vk, bool down, double t)
        {
            if (IsAlt(vk))
            {
                if (!down)
                {
                    bool tap = since != null && !spent && t - since < Hold;
                    since = null;
                    return tap;
                }
                if (since == null) { since = t; spent = false; }
            }
            else if (down) spent = true;
            return false;
        }

        // altHeld comes from GetAsyncKeyState: recovers when a key-up was missed (UAC prompt, lock screen)
        public bool Poll(double t, bool altHeld)
        {
            if (!altHeld) since = null;
            if (since == null || spent || t - since < Hold) return false;
            spent = true;
            return true;
        }

        public static void SelfTest()
        {
            static void Ok(bool c, string m) { if (!c) throw new Exception("selftest failed: " + m); }
            var a = new AltHold(0.5);
            a.Key(0xA4, true, 0);
            Ok(!a.Poll(0.4, true), "too early");
            Ok(a.Poll(0.5, true), "fires at hold");
            Ok(!a.Poll(0.9, true), "fires once");
            Ok(!a.Key(0xA4, false, 1), "long press release is not a tap");
            a.Key(0xA4, true, 2); a.Key(0x09, true, 2.1);
            Ok(!a.Poll(3, true), "alt+tab cancels");
            a.Key(0x09, false, 3);
            Ok(!a.Key(0xA4, false, 3), "alt+tab is not a tap");
            a.Key(0xA5, true, 4);
            Ok(a.Poll(4.6, true), "right alt after cancelled press");
            Ok(!a.Poll(5, false), "released");
            a.Key(0xA4, true, 6);  // restarts even though the previous key-up was never seen
            Ok(a.Poll(6.5, true), "recovers from missed key-up");
            a.Key(0xA4, false, 7);
            a.Key(0xA4, true, 8); a.Key(0xA4, true, 8.1); // key auto-repeat doesn't matter
            Ok(!a.Poll(8.2, true) && a.Key(0xA4, false, 8.2), "quick press is a tap");
            a.Hold = 0.3;
            a.Key(0xA4, true, 9);
            Ok(a.Poll(9.3, true), "hold is adjustable");
        }
    }
}
