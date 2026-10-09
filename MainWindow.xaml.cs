using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FloatingClipboard
{
    /// <summary>Kind: text / image (Data = PNG path) / files (Data = paths separated by \n)</summary>
    public record Entry(string Kind, string Data)
    {
        public string[] Files() => Data.Split('\n');
    }

    public class Item
    {
        static readonly HashSet<string> ImgExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico" };

        public Entry E { get; }
        public string Label { get; }
        public string Tip { get; }
        public ImageSource Icon { get; }

        public Item(Entry e)
        {
            E = e;
            if (e.Kind == "text")
            {
                var flat = Regex.Replace(e.Data.Trim(), @"\s+", " ");
                Label = flat.Length > 120 ? flat[..120] : flat;
                Tip = e.Data.Length > 1500 ? e.Data[..1500] : e.Data;
                return;
            }
            var p = e.Kind == "image" ? e.Data : e.Files()[0];
            var name = Path.GetFileName(p.TrimEnd('\\'));
            var n = e.Kind == "files" ? e.Files().Length : 1;
            Label = e.Kind == "image" ? "Image" : (name.Length > 0 ? name : p) + (n > 1 ? $"  +{n - 1} more" : "");
            Tip = e.Kind == "image" ? p : e.Data;
            Icon = IconFor(p);
        }

        public static BitmapImage LoadBitmap(string path, int width = 0)
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(path);
            b.CacheOption = BitmapCacheOption.OnLoad; // release the file after loading so it can be deleted later
            if (width > 0) b.DecodePixelWidth = width;
            b.EndInit();
            b.Freeze();
            return b;
        }

        static ImageSource IconFor(string p)
        {
            try
            {
                if (ImgExt.Contains(Path.GetExtension(p)) && File.Exists(p)) return LoadBitmap(p, 80);
                var info = new Native.SHFILEINFO();
                if (Native.SHGetFileInfo(p, 0, ref info, (uint)Marshal.SizeOf(info), 0x100 /*SHGFI_ICON*/) == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                Native.DestroyIcon(info.hIcon);
                src.Freeze();
                return src;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or COMException)
            {
                return null; // broken image or no shell icon: just show none
            }
        }
    }

    public partial class MainWindow : Window
    {
        internal static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FloatingClipboard");
        static readonly string StoreFile = Path.Combine(Dir, "store.json"), ImgDir = Path.Combine(Dir, "images");
        // Password managers etc. mark their clipboard data with these formats: "do not record"
        static readonly string[] Private = { "ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore" };

        class Store
        {
            public List<Entry> History { get; set; } = new();
            public List<Entry> Shelf { get; set; } = new();
        }

        readonly ObservableCollection<Item> historyItems = new(), shelfItems = new();
        public readonly AltHold Alt;
        readonly Settings settings;
        readonly Stopwatch clock = Stopwatch.StartNew();
        Native.HookProc hookProc; // keep a reference, or the GC collects the delegate and the hook crashes
        IntPtr hwnd, prev;
        Point? dragStart;
        ListBox dragSource;

        public MainWindow(Settings settings)
        {
            this.settings = settings;
            Alt = new AltHold(settings.HoldSeconds);
            InitializeComponent();
            Directory.CreateDirectory(ImgDir);
            Load();
            History.ItemsSource = historyItems;
            Shelf.ItemsSource = shelfItems;
            shelfItems.CollectionChanged += (_, _) => UpdateShelfSize();
            UpdateShelfSize();
            SizeChanged += (_, _) => KeepOnScreen(); // growing must never push the panel off screen
            CollectionViewSource.GetDefaultView(historyItems).Filter = o => Matches((Item)o, Search.Text);
            SourceInitialized += (_, _) => Init();
            new WindowInteropHelper(this).EnsureHandle(); // create the HWND now: clipboard updates must arrive before the window is ever shown
        }

        void Init()
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            int dark = 1, round = 2;
            Native.DwmSetWindowAttribute(hwnd, 20, ref dark, 4);  // DWMWA_USE_IMMERSIVE_DARK_MODE
            Native.DwmSetWindowAttribute(hwnd, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE = round
            // Accent acrylic instead of DWMWA_SYSTEMBACKDROP_TYPE: the latter turns solid when the window loses focus.
            // With "Settings > Personalization > Colors > Transparency effects" off, DWM draws acrylic solid, so use a translucent fill.
            var transparency = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1);
            if (transparency is not 0) Native.EnableAcrylic(hwnd, 0x30201A18); // tint, AABBGGRR
            else Root.Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x1C, 0x1C, 0x24));
            Native.SetWindowLong(hwnd, -20, Native.GetWindowLong(hwnd, -20) | 0x80); // WS_EX_TOOLWINDOW: hide from Alt+Tab
            Native.AddClipboardFormatListener(hwnd);

            // ponytail: the hook runs on the UI thread; if the UI stalls past LowLevelHooksTimeout Windows drops it. Move it to its own thread if that ever happens.
            hookProc = OnKey;
            Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, hookProc, Native.GetModuleHandle(null), 0);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) =>
            {
                if (Alt.Poll(clock.Elapsed.TotalSeconds, Native.GetAsyncKeyState(0x12) < 0)) Toggle();
            };
            timer.Start();
        }

        IntPtr OnKey(int code, IntPtr wp, IntPtr lp)
        {
            if (code == 0)
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lp);
                int msg = (int)wp;
                // Ignore injected keys (e.g. our own Ctrl+V). While the window is open, a single Alt tap expands / collapses the history.
                if ((k.flags & 0x10) == 0 && Alt.Key((int)k.vkCode, msg == 0x100 || msg == 0x104, clock.Elapsed.TotalSeconds) && IsVisible)
                    Dispatcher.BeginInvoke(new Action(() => SetExpanded(HistoryPanel.Visibility != Visibility.Visible)));
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, wp, lp);
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x031D) Dispatcher.BeginInvoke(new Action(OnClipboard)); // WM_CLIPBOARDUPDATE
            // A lone Alt tap normally enters system-menu mode and swallows the next keystrokes; block SC_KEYMENU
            if (msg == 0x0112 && ((int)w & 0xFFF0) == 0xF100) handled = true;
            return IntPtr.Zero;
        }

        // ---------- show / hide ----------
        public void Toggle()
        {
            if (IsVisible) { Hide(); return; }
            var fg = Native.GetForegroundWindow();
            if (fg != hwnd) prev = fg;

            Search.Clear();
            SetExpanded(false);
            // Window sizes to content, so measure it to center on the cursor before showing
            Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var dpi = VisualTreeHelper.GetDpi(this);
            var c = System.Windows.Forms.Cursor.Position;
            Left = c.X / dpi.DpiScaleX - Root.DesiredSize.Width / 2;
            Top = c.Y / dpi.DpiScaleY - Root.DesiredSize.Height / 2;
            Show();
            KeepOnScreen(); // ActualWidth is still 0 before the first show, so clamp again afterwards
            Native.ForceForeground(hwnd);
            Activate();
        }

        public void SetExpanded(bool on)
        {
            HistoryPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            UpdateShelfSize();
            UpdateLayout();
            KeepOnScreen();
            if (!on || !IsVisible) return;
            Native.ForceForeground(hwnd);
            Activate();
            Search.Focus();
        }

        // Shelf grows with its items: a small square when (nearly) empty, up to MaxCols x MaxRows cells, then it scrolls
        const int CellW = 82, CellH = 78, MaxCols = 5, MaxRows = 4, HistoryWidth = 340;
        const int ListChrome = 10; // ListBox border (1+1) + template padding (1+1) + Padding (3+3)

        void UpdateShelfSize()
        {
            // ponytail: WPF won't size a window below SystemParameters.MinimumWindowTrackWidth, so that is the smallest square;
            // going smaller needs a native window subclass to rewrite WM_GETMINMAXINFO before WPF reads it
            double min = SystemParameters.MinimumWindowTrackWidth - Root.Padding.Left - Root.Padding.Right
                         - Root.BorderThickness.Left - Root.BorderThickness.Right;
            int n = shelfItems.Count, cols = Math.Clamp(n, 1, MaxCols), rows = Math.Clamp((n + cols - 1) / cols, 1, MaxRows);
            double w = cols * CellW + ListChrome + (n > MaxCols * MaxRows ? SystemParameters.VerticalScrollBarWidth : 0);
            Shelf.Height = Math.Max(rows * CellH + ListChrome, min);
            Shelf.Width = Math.Max(HistoryPanel.Visibility == Visibility.Visible ? Math.Max(w, HistoryWidth) : w, min);
            HistoryPanel.Width = Shelf.Width;
        }

        void KeepOnScreen()
        {
            if (double.IsNaN(Left) || double.IsNaN(Top)) return; // not positioned yet
            var dpi = VisualTreeHelper.GetDpi(this);
            var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
                (int)((Left + ActualWidth / 2) * dpi.DpiScaleX), (int)(Top * dpi.DpiScaleY))).WorkingArea;
            Left = Math.Max(wa.Left / dpi.DpiScaleX, Math.Min(Left, wa.Right / dpi.DpiScaleX - ActualWidth));
            Top = Math.Max(wa.Top / dpi.DpiScaleY, Math.Min(Top, wa.Bottom / dpi.DpiScaleY - ActualHeight));
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true; // Alt+F4 only hides; quit from the tray menu
            Hide();
        }

        void OnBackgroundDown(object s, MouseButtonEventArgs e) => DragMove();

        void OnPreviewKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Hide();
            else if (e.Key == Key.Enter && HistoryPanel.IsVisible && !Shelf.IsKeyboardFocusWithin) Paste(Selected(History));
            else if (e.Key == Key.Down && Search.IsKeyboardFocused)
                (History.ItemContainerGenerator.ContainerFromItem(History.SelectedItem) as ListBoxItem ?? (UIElement)History).Focus();
            else if (e.Key == Key.Delete && !Search.IsKeyboardFocused) DeleteSelected(Shelf.IsKeyboardFocusWithin ? Shelf : History);
            else return;
            e.Handled = true;
        }

        // ---------- clipboard ----------
        void OnClipboard()
        {
            Entry e = null;
            for (int i = 0; i < 5; i++)
            {
                try { e = ReadClipboard(); break; }
                catch (ExternalException) { Thread.Sleep(40); } // another app holds the clipboard; retry shortly
            }
            if (e == null) return;
            Put(historyItems, e, top: true);
            TrimHistory();
            SelectFirst();
        }

        public void TrimHistory()
        {
            while (historyItems.Count > settings.MaxHistory) Remove(historyItems, historyItems[^1]);
            Save();
        }

        static Entry ReadClipboard()
        {
            var d = Clipboard.GetDataObject();
            if (d == null || d.GetFormats().Any(f => Private.Contains(f))) return null;
            if (d.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                return new Entry("files", string.Join("\n", files));
            if (d.GetData(DataFormats.UnicodeText) is string t && !string.IsNullOrWhiteSpace(t))
                return new Entry("text", t);
            byte[] png = null;
            if (d.GetData("PNG") is MemoryStream ms) png = ms.ToArray(); // screenshot tools provide PNG, which keeps transparency
            else if (System.Windows.Forms.Clipboard.GetImage() is System.Drawing.Image img)
                using (img)
                using (var buf = new MemoryStream())
                {
                    img.Save(buf, System.Drawing.Imaging.ImageFormat.Png);
                    png = buf.ToArray();
                }
            if (png == null) return null;
            var path = Path.Combine(ImgDir, Convert.ToHexString(SHA1.HashData(png))[..16] + ".png");
            if (!File.Exists(path)) File.WriteAllBytes(path, png);
            return new Entry("image", path);
        }

        static DataObject MakeData(IEnumerable<Entry> entries, bool drag)
        {
            var d = new DataObject();
            var files = new StringCollection();
            var texts = new List<string>();
            foreach (var e in entries)
            {
                if (e.Kind == "files") files.AddRange(e.Files());
                else if (e.Kind == "text") texts.Add(e.Data);
                else if (File.Exists(e.Data))
                {
                    d.SetImage(Item.LoadBitmap(e.Data));
                    if (drag) files.Add(e.Data); // also offer the PNG file when dragging, so Explorer / chat apps accept it
                }
            }
            if (files.Count > 0) d.SetFileDropList(files);
            if (texts.Count > 0) d.SetText(string.Join("\n", texts));
            return d;
        }

        void Paste(List<Entry> entries)
        {
            if (entries.Count == 0) return;
            try { Clipboard.SetDataObject(MakeData(entries, drag: false), true); }
            catch (ExternalException) { return; }
            Hide();
            if (prev == IntPtr.Zero) return;
            Native.SetForegroundWindow(prev);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            t.Tick += (_, _) => { t.Stop(); Native.SendCtrlV(); };
            t.Start();
        }

        // ---------- lists ----------
        static bool Matches(Item i, string q) =>
            q.Length == 0 || (i.E.Kind == "image" ? "image" : i.E.Data).Contains(q, StringComparison.OrdinalIgnoreCase);

        void Search_TextChanged(object s, TextChangedEventArgs e)
        {
            CollectionViewSource.GetDefaultView(historyItems).Refresh();
            SelectFirst();
        }

        void SelectFirst()
        {
            History.SelectedIndex = History.Items.Count > 0 ? 0 : -1;
            if (History.SelectedItem != null) History.ScrollIntoView(History.SelectedItem);
        }

        static List<Entry> Selected(ListBox lb) =>
            lb.SelectedItems.Cast<Item>().OrderBy(i => lb.Items.IndexOf(i)).Select(i => i.E).ToList();

        static void Put(ObservableCollection<Item> list, Entry e, bool top)
        {
            var old = list.FirstOrDefault(i => i.E == e); // dedupe: an existing entry moves to the top
            if (old == null) list.Insert(top ? 0 : list.Count, new Item(e));
            else if (top) list.Move(list.IndexOf(old), 0);
        }

        static void Remove(ObservableCollection<Item> list, Item it)
        {
            list.Remove(it);
            if (it.E.Kind == "image")
                try { File.Delete(it.E.Data); } catch (IOException) { }
        }

        void DeleteSelected(ListBox lb)
        {
            var list = lb == Shelf ? shelfItems : historyItems;
            foreach (var it in lb.SelectedItems.Cast<Item>().ToList()) Remove(list, it);
            Save();
        }

        public void ClearHistory() => Clear(historyItems);

        public void ClearShelf() => Clear(shelfItems);

        void Clear(ObservableCollection<Item> list)
        {
            foreach (var it in list.ToList()) Remove(list, it);
            Save();
        }

        void Shelf_Remove(object s, RoutedEventArgs e)
        {
            Remove(shelfItems, (Item)((FrameworkElement)s).DataContext); // removes from the shelf only, never touches the file
            Save();
        }

        void History_DoubleClick(object s, MouseButtonEventArgs e)
        {
            if (OnItem(History, e)) Paste(Selected(History));
        }

        void Shelf_DoubleClick(object s, MouseButtonEventArgs e)
        {
            if (!OnItem(Shelf, e)) return;
            foreach (var en in Selected(Shelf))
                try { Process.Start(new ProcessStartInfo(en.Data) { UseShellExecute = true }); }
                catch (Win32Exception) { } // file gone or no associated app
        }

        static bool OnItem(ListBox lb, MouseEventArgs e) =>
            ItemsControl.ContainerFromElement(lb, e.OriginalSource as DependencyObject) is ListBoxItem;

        // ---------- drag & drop ----------
        void List_MouseDown(object s, MouseButtonEventArgs e) =>
            dragStart = OnItem((ListBox)s, e) && !OnButton(e.OriginalSource) ? e.GetPosition(null) : null;

        // A slightly shaky click on the remove button must not start a drag
        static bool OnButton(object src)
        {
            for (var d = src as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is Button) return true;
            return false;
        }

        void List_MouseMove(object s, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || dragStart == null || dragSource != null) return;
            var d = e.GetPosition(null) - dragStart.Value;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var lb = (ListBox)s;
            var entries = Selected(lb);
            if (entries.Count == 0) return;
            dragSource = lb;
            try { DragDrop.DoDragDrop(lb, MakeData(entries, drag: true), DragDropEffects.Copy); } // Copy only: the target can never move the original away
            finally { dragSource = null; dragStart = null; }
        }

        void Shelf_DragOver(object s, DragEventArgs e)
        {
            e.Effects = dragSource != Shelf && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void Shelf_Drop(object s, DragEventArgs e)
        {
            if (dragSource == Shelf || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var f in files) Put(shelfItems, new Entry("files", f), top: false);
            e.Effects = DragDropEffects.Copy; // we only keep the path; never report Move or the source may delete the file
            e.Handled = true;
            Save();
        }

        // ---------- persistence ----------
        void Load()
        {
            Store st;
            try { st = JsonSerializer.Deserialize<Store>(File.ReadAllText(StoreFile)) ?? new Store(); }
            catch (Exception ex) when (ex is IOException or JsonException) { st = new Store(); }
            foreach (var e in st.History)
                if (e.Kind != "image" || File.Exists(e.Data)) historyItems.Add(new Item(e));
            foreach (var e in st.Shelf)
                if (File.Exists(e.Data) || Directory.Exists(e.Data)) shelfItems.Add(new Item(e));
        }

        void Save()
        {
            var st = new Store { History = historyItems.Select(i => i.E).ToList(), Shelf = shelfItems.Select(i => i.E).ToList() };
            var tmp = StoreFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(st));
            File.Move(tmp, StoreFile, true); // write a temp file then replace, so a crash mid-write can't corrupt the store
        }
    }
}
