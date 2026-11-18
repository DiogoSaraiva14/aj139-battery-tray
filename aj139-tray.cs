// AJ139 V2 MC battery + DPI tray indicator (AJAZZ AJ139 V2 MC, 2.4G receiver VID A8A5 PID 2255).
// Read-only: listens to what the receiver sends on its vendor HID channel, never writes to the mouse.
// Build:  build.cmd            (C# 5 compiler that ships with Windows / .NET Framework 4)
// Usage:  AJ139Tray.exe        tray icon
//         AJ139Tray.exe --selftest
//         AJ139Tray.exe --preview <folder>   (PNG renders of icons and panel)
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

// ---------------------------------------------------------------------------------------------
// Protocol + reader
// ---------------------------------------------------------------------------------------------
static class AjBattery {
  public static volatile int Level = -1;
  public static volatile string Status = "Starting";
  public static DateTime LastReport = DateTime.MinValue;
  static int baseLevel = -1; static DateTime baseTime;
  static readonly object lk = new object();

  // FA status messages: [0] report id, [1] 0xAA magic, [2] 0xFA, [3] 0x00 periodic / 0xAE event, [5] sub-type
  // battery: sub-type 0x38, [10] = %. [10] agrees across both message types and read 100 after a full charge;
  // [4] (used before) differs by type and reached 111, so it is some other scale.
  public static int Parse(byte[] b, int n) {
    if (n >= 11 && b[1] == 0xAA && b[2] == 0xFA && b[5] == 0x38 && b[10] <= 100) return b[10];
    return -1;
  }

  // DPI button press: sub-type 0x0A, [10] = new stage, 1-based. Returns 0-based index or -1.
  public static int ParseDpiEvent(byte[] b, int n) {
    if (n >= 11 && b[1] == 0xAA && b[2] == 0xFA && b[5] == 0x0A && b[10] >= 1 && b[10] <= 8) return b[10] - 1;
    return -1;
  }

  public static volatile int Raw = -1;                 // latest reading as sent; Level is the smoothed one
  public static volatile bool ReceiverPresent;
  static readonly int[] recent = new int[5];           // ~5 min of readings; the mouse's value jumps around by a few %
  static int recentCount, recentPos;

  public static void OnLevel(int lvl, DateTime now) {
    lock (lk) {
      if (recentCount > 0 && lvl > Level + 5) { recentCount = 0; recentPos = 0; }   // charged: drop old readings
      recent[recentPos] = lvl; recentPos = (recentPos + 1) % recent.Length;
      if (recentCount < recent.Length) recentCount++;
      int[] s = new int[recentCount]; Array.Copy(recent, s, recentCount); Array.Sort(s);
      int smooth = s[recentCount / 2];                  // median
      if (baseLevel < 0 || smooth > baseLevel + 5) { baseLevel = smooth; baseTime = now; }  // first reading or recharged
      Raw = lvl; Level = smooth; LastReport = now; Status = "OK";
    }
    Log("battery raw " + lvl + " shown " + Level);
  }

  public static void ResetForTest() { lock (lk) { recentCount = 0; recentPos = 0; baseLevel = -1; Level = -1; } }

  // %/hour since the baseline; -1 until there is enough data (the mouse's reading jitters by +/-1%)
  public static double DrainPerHour() {
    lock (lk) {
      double h = (LastReport - baseTime).TotalHours;
      if (baseLevel < 0 || h < 0.5 || baseLevel - Level < 2) return -1;
      return (baseLevel - Level) / h;
    }
  }

  // --- DPI: stage values only arrive when the AJAZZ app asks; we keep the last copy we saw ---
  public static int[] DpiStages = new int[0];
  public static volatile int DpiActive = -1;
  public static DateTime DpiRead = DateTime.MinValue;
  public static DateTime DpiChanged = DateTime.MinValue;   // last DPI button press seen live
  public static string DpiCacheFile;

  // 00 AA 0E A5 xx .. [12]=stage count [13]=active stage (1-based) [14..]=stages, uint16 little-endian
  // [4] changes between replies (seen A1, 90), so it is not part of the header
  public static bool ParseDpi(byte[] b, int n, out int[] stages, out int active) {
    stages = null; active = -1;
    if (n < 16 || b[1] != 0xAA || b[2] != 0x0E || b[3] != 0xA5) return false;
    int count = b[12]; active = b[13] - 1;
    if (count < 1 || count > 8 || active < 0 || active >= count || n < 14 + 2 * count) return false;
    stages = new int[count];
    for (int i = 0; i < count; i++) {
      stages[i] = b[14 + 2 * i] | (b[15 + 2 * i] << 8);
      if (stages[i] < 50 || stages[i] > 50000) return false;
    }
    return true;
  }

  public static void OnDpi(int[] stages, int active, DateTime when) {
    lock (lk) { DpiStages = stages; DpiActive = active; DpiRead = when; }
    SaveDpiCache();
    Log("dpi stages " + string.Join(",", stages) + " active " + (active + 1));
  }

  public static void OnDpiEvent(int active, DateTime when) {
    lock (lk) { DpiActive = active; DpiChanged = when; }
    SaveDpiCache();
    Log("dpi button -> stage " + (active + 1));
  }

  // small rolling log next to the exe, for diagnosing things that happen mid-game
  public static string LogFile;
  static readonly object logLock = new object();
  public static void Log(string msg) {
    if (LogFile == null) return;
    try {
      lock (logLock) {
        FileInfo fi = new FileInfo(LogFile);
        if (fi.Exists && fi.Length > 200000) { File.Copy(LogFile, LogFile + ".old", true); File.Delete(LogFile); }
        File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + "\r\n");
      }
    } catch (Exception) { }
  }

  static void SaveDpiCache() {
    if (DpiCacheFile == null) return;
    try { lock (lk) File.WriteAllText(DpiCacheFile, DpiRead.ToString("o") + "|" + DpiActive + "|" + string.Join(",", DpiStages)); }
    catch (Exception) { }
  }

  public static void LoadDpiCache() {
    try {
      string[] p = File.ReadAllText(DpiCacheFile).Split('|');
      int[] st = Array.ConvertAll(p[2].Split(','), int.Parse);
      lock (lk) { DpiRead = DateTime.Parse(p[0], null, DateTimeStyles.RoundtripKind); DpiActive = int.Parse(p[1]); DpiStages = st; }
    } catch (Exception) { }   // no cache yet
  }

  [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List_Size(out int len, ref Guid g, string id, int flags);
  [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List(ref Guid g, string id, char[] buf, int len, int flags);
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);

  static string FindPath() {
    Guid g = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030"); int len;   // GUID_DEVINTERFACE_HID
    if (CM_Get_Device_Interface_List_Size(out len, ref g, null, 0) != 0 || len < 2) return null;
    char[] buf = new char[len];
    if (CM_Get_Device_Interface_List(ref g, null, buf, len, 0) != 0) return null;
    foreach (string p in new string(buf).Split('\0'))
      if (p.ToLowerInvariant().Contains("vid_a8a5&pid_2255&mi_02")) return p;
    return null;
  }

  static void Loop() {
    byte[] b = new byte[65];
    while (true) {
      try {
        string path = FindPath();
        if (path == null) {
          if (ReceiverPresent) Log("receiver not found (unplugged?)");
          ReceiverPresent = false; Status = "Receiver not plugged in"; Thread.Sleep(5000); continue;
        }
        SafeFileHandle h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /*overlapped*/, IntPtr.Zero);
        if (h.IsInvalid) { Log("open failed, error " + Marshal.GetLastWin32Error()); h.Dispose(); Status = "Can't open the receiver"; Thread.Sleep(5000); continue; }
        if (!ReceiverPresent) Log("receiver opened");
        ReceiverPresent = true;
        if (Level < 0) Status = "Waiting for the first report";
        using (FileStream fs = new FileStream(h, FileAccess.Read, 65, true)) {
          while (true) {
            int n = fs.ReadAsync(b, 0, 65).Result;
            // raw status bytes 3..11 for the battery study (bytes 9-10 may be battery voltage)
            if (n >= 12 && b[1] == 0xAA && b[2] == 0xFA) Log("FA " + BitConverter.ToString(b, 3, 9).Replace("-", " "));
            int lvl = Parse(b, n);
            if (lvl >= 0) OnLevel(lvl, DateTime.Now);
            int[] st; int act;
            if (ParseDpi(b, n, out st, out act)) OnDpi(st, act, DateTime.Now);
            int ev = ParseDpiEvent(b, n);
            if (ev >= 0) OnDpiEvent(ev, DateTime.Now);
          }
        }
      } catch (Exception ex) {
        // a read error alone isn't a disconnect: reopen; the next FindPath decides whether the receiver is gone
        Log("read error: " + ex.GetBaseException().Message);
        Thread.Sleep(1000);
      }
    }
  }

  public static void Start() { Thread t = new Thread(Loop); t.IsBackground = true; t.Start(); }
}

// ---------------------------------------------------------------------------------------------
// State snapshot + theme
// ---------------------------------------------------------------------------------------------
sealed class State {
  public int Level; public DateTime Last; public string Status; public double Drain; public bool Stale;
  public int[] Dpi; public int DpiActive; public DateTime DpiRead, DpiChanged;

  public static State Now() {
    State s = new State();
    s.Level = AjBattery.Level; s.Last = AjBattery.LastReport; s.Status = AjBattery.Status; s.Drain = AjBattery.DrainPerHour();
    s.Stale = s.Level >= 0 && (DateTime.Now - s.Last).TotalMinutes > 15;  // reports can pause for minutes while gaming
    s.Dpi = AjBattery.DpiStages; s.DpiActive = AjBattery.DpiActive; s.DpiRead = AjBattery.DpiRead; s.DpiChanged = AjBattery.DpiChanged;
    return s;
  }
}

sealed class Theme {
  public bool Light; public Color Bg, Fg, Sub, Track, Border, Low, Accent;

  public static Theme Make(bool light, Color accent) {
    Theme t = new Theme(); t.Light = light; t.Accent = accent;
    if (light) { t.Bg = Color.FromArgb(249, 249, 249); t.Fg = Color.FromArgb(26, 26, 26); t.Sub = Color.FromArgb(96, 96, 96); t.Track = Color.FromArgb(40, 0, 0, 0); t.Border = Color.FromArgb(229, 229, 229); t.Low = Color.FromArgb(196, 43, 28); }
    else       { t.Bg = Color.FromArgb(44, 44, 44); t.Fg = Color.White; t.Sub = Color.FromArgb(200, 200, 200); t.Track = Color.FromArgb(45, 255, 255, 255); t.Border = Color.FromArgb(70, 70, 70); t.Low = Color.FromArgb(255, 99, 71); }
    return t;
  }

  public static Theme Read() {
    bool light = false;
    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
      object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
      light = v is int && (int)v == 1;
    }
    // Windows 11 uses a lighter accent shade on dark surfaces and a darker one on light surfaces
    Color accent = light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(76, 194, 255);
    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent")) {
      byte[] pal = k == null ? null : k.GetValue("AccentPalette") as byte[];
      int i = light ? 4 : 1;
      if (pal != null && pal.Length >= 32) accent = Color.FromArgb(pal[4 * i], pal[4 * i + 1], pal[4 * i + 2]);
    }
    return Make(light, accent);
  }
}

// ---------------------------------------------------------------------------------------------
// Drawing: tray icon + panel. Fonts are created once and reused.
// ---------------------------------------------------------------------------------------------
sealed class Painter : IDisposable {
  public const int FlyW = 320, FlyH = 248;
  public readonly float S;   // display scale
  public static readonly string IconFont = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");
  public static readonly string TextFont = Pick("Segoe UI Variable Text", "Segoe UI");
  public static readonly string DisplayFont = Pick("Segoe UI Variable Display", "Segoe UI");
  readonly Font fIcon, fTitle, fSmall, fVerdict, fBig, fLabel, fChip;

  public Painter(float scale) {
    S = scale;
    fIcon = new Font(IconFont, 16 * S, FontStyle.Regular, GraphicsUnit.Pixel);
    fTitle = new Font(TextFont, 10.5f, FontStyle.Bold);
    fSmall = new Font(TextFont, 9f);
    fVerdict = new Font(TextFont, 10f);
    fBig = new Font(DisplayFont, 26f, FontStyle.Bold);
    fLabel = new Font(TextFont, 9.5f, FontStyle.Bold);
    fChip = new Font(TextFont, 9f);
  }
  public void Dispose() { fIcon.Dispose(); fTitle.Dispose(); fSmall.Dispose(); fVerdict.Dispose(); fBig.Dispose(); fLabel.Dispose(); fChip.Dispose(); }
  public float Px(float v) { return v * S; }

  static string Pick(params string[] names) {
    foreach (string n in names) { try { using (new FontFamily(n)) return n; } catch (ArgumentException) { } }
    return "Segoe UI";
  }

  // --- tray icon: the battery number, readable at a glance ---
  static GraphicsPath TextPath(FontFamily fam, string text) {
    GraphicsPath p = new GraphicsPath();
    p.AddString(text, fam, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
    return p;
  }

  // green (full) -> yellow (half) -> red (20% and below); darker shades so they stay readable on a light taskbar
  public static Color LevelColor(int lvl, bool light) {
    Color full = light ? Color.FromArgb(15, 123, 15) : Color.FromArgb(108, 203, 95);
    Color half = light ? Color.FromArgb(157, 93, 0) : Color.FromArgb(252, 225, 0);
    Color low  = light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 99, 71);
    if (lvl >= 50) return Lerp(half, full, (Math.Min(lvl, 100) - 50) / 50f);
    if (lvl > 20) return Lerp(low, half, (lvl - 20) / 30f);
    return low;
  }

  static Color Lerp(Color a, Color b, float t) {
    return Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
  }

  public static Bitmap PercentIcon(int size, State st, Theme th) {
    Bitmap bmp = new Bitmap(size, size);
    using (Graphics g = Graphics.FromImage(bmp))
    using (FontFamily fam = new FontFamily(DisplayFont))
    using (GraphicsPath refPath = TextPath(fam, "88"))
    using (GraphicsPath p = TextPath(fam, st.Level < 0 ? "--" : st.Level.ToString())) {
      g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.Half;
      Color c = (st.Level < 0 || st.Stale) ? Color.FromArgb(130, th.Fg) : LevelColor(st.Level, th.Light);
      // scale from "88" so every number gets the same digit height; only "100" shrinks to fit the width
      RectangleF rf = refPath.GetBounds(), r = p.GetBounds();
      float k = Math.Min(size / rf.Height, size / r.Width);
      using (Matrix m = new Matrix()) {
        m.Translate((size - r.Width * k) / 2, (size - rf.Height * k) / 2); m.Scale(k, k); m.Translate(-r.X, -rf.Y);
        p.Transform(m);
      }
      using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
    }
    return bmp;
  }

  // --- panel ---
  static GraphicsPath Pill(float x, float y, float w, float h) {
    GraphicsPath p = new GraphicsPath();
    p.AddArc(x, y, h, h, 90, 180); p.AddArc(x + w - h, y, h, h, 270, 180); p.CloseFigure();
    return p;
  }

  static GraphicsPath RoundRect(float x, float y, float w, float h, float r) {
    GraphicsPath p = new GraphicsPath(); float d = 2 * r;
    p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
    p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90); p.CloseFigure();
    return p;
  }

  static string Ago(DateTime t) {
    int sec = (int)(DateTime.Now - t).TotalSeconds;
    if (sec < 60) return sec + " s ago";
    if (sec < 3600) return sec / 60 + " min ago";
    return Math.Round(sec / 3600.0, 1) + " h ago";
  }

  static string Hours(double h) {
    if (h >= 24) return Math.Floor(h / 24) + " d " + Math.Floor(h % 24) + " h";
    return Math.Floor(h) + " h " + Math.Floor((h % 1) * 60).ToString("00") + " min";
  }

  const string Dot = "  ·  ";

  public void Draw(Graphics g, State st, Theme th) {
    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    g.Clear(th.Bg);
    float W = Px(FlyW), pad = Px(20);
    using (SolidBrush fg = new SolidBrush(th.Fg))
    using (SolidBrush sub = new SolidBrush(th.Sub))
    using (SolidBrush track = new SolidBrush(th.Track))
    using (StringFormat far = new StringFormat { Alignment = StringAlignment.Far }) {
      // header: mouse glyph, name, connection
      g.DrawString("", fIcon, fg, pad, Px(18));
      g.DrawString("AJ139 V2 MC", fTitle, fg, pad + Px(24), Px(15));
      string conn = st.Stale ? "No recent update" : st.Level >= 0 ? "2.4G receiver" : st.Status;
      g.DrawString(conn, fSmall, sub, new RectangleF(0, Px(17), W - pad, Px(20)), far);

      // big percentage + verdict
      string pct = st.Level >= 0 ? st.Level + "%" : "--%";
      g.DrawString(pct, fBig, fg, pad - Px(3), Px(42));
      float pw = g.MeasureString(pct, fBig).Width;
      string verdict = st.Level < 0 ? "" : st.Stale ? "Last known" : st.Level <= 20 ? "Low - charge soon" : st.Level <= 50 ? "OK" : "Good";
      g.DrawString(verdict, fVerdict, sub, pad + pw - Px(4), Px(63));

      // bar
      float bx = pad, by = Px(104), bw = W - 2 * pad, bh = Px(4);
      using (GraphicsPath tp = Pill(bx, by, bw, bh)) g.FillPath(track, tp);
      if (st.Level > 0) {
        float fw = Math.Max(bh, bw * st.Level / 100f);
        Color col = st.Level <= 20 ? th.Low : st.Stale ? th.Sub : th.Accent;
        using (GraphicsPath bp = Pill(bx, by, fw, bh)) using (SolidBrush b = new SolidBrush(col)) g.FillPath(b, bp);
      }

      // details
      string l1 = st.Level >= 0 ? "Updated " + st.Last.ToString("HH:mm:ss") + Dot + Ago(st.Last) : "The mouse reports about once a minute";
      string l2 = st.Stale ? "No battery update for " + (int)(DateTime.Now - st.Last).TotalMinutes + " min - mouse asleep or off?"
                : st.Drain > 0 ? "About " + Hours(st.Level / st.Drain) + " left" + Dot + "-" + st.Drain.ToString("0.0") + "%/h"
                : st.Level >= 0 ? "Time left: estimating (after ~30 min of use)" : "";
      g.DrawString(l1, fSmall, sub, pad, Px(122));
      g.DrawString(l2, fSmall, sub, pad, Px(144));

      // DPI stages
      g.FillRectangle(track, pad, Px(176), W - 2 * pad, Math.Max(1f, S));
      g.DrawString("DPI", fLabel, fg, pad, Px(186));
      string src = st.DpiChanged > st.DpiRead ? "changed on mouse " + st.DpiChanged.ToString("HH:mm")
                 : st.Dpi.Length > 0 ? "from AJAZZ app, " + st.DpiRead.ToString("dd MMM HH:mm") : "";
      g.DrawString(src, fSmall, sub, new RectangleF(0, Px(187), W - pad, Px(20)), far);
      if (st.Dpi.Length == 0) {
        string msg = st.DpiActive >= 0 ? "Stage " + (st.DpiActive + 1) + Dot + "open the AJAZZ app once to see the values"
                                       : "Open the AJAZZ app once and the stages show up here";
        g.DrawString(msg, fSmall, sub, pad, Px(212));
        return;
      }
      using (StringFormat cf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
      using (SolidBrush accent = new SolidBrush(th.Accent))
      using (SolidBrush onAccent = new SolidBrush(th.Light ? Color.White : Color.Black)) {
        float x = pad, y = Px(210), h = Px(24);
        for (int i = 0; i < st.Dpi.Length; i++) {
          string t = st.Dpi[i].ToString();
          float w = g.MeasureString(t, fChip).Width + Px(12);
          bool active = i == st.DpiActive;
          using (GraphicsPath cp = RoundRect(x, y, w, h, Px(4))) g.FillPath(active ? accent : track, cp);
          g.DrawString(t, fChip, active ? onAccent : fg, new RectangleF(x, y, w, h), cf);
          x += w + Px(6);
        }
      }
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Windows plumbing
// ---------------------------------------------------------------------------------------------
static class Native {
  [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
  [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
}

sealed class Flyout : Form {
  readonly Painter painter;
  public DateTime HiddenAt = DateTime.MinValue;

  public Flyout(Painter p) {
    painter = p;
    FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
    StartPosition = FormStartPosition.Manual; KeyPreview = true; DoubleBuffered = true;
    ClientSize = new Size((int)p.Px(Painter.FlyW), (int)p.Px(Painter.FlyH));
  }

  protected override CreateParams CreateParams {
    get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; /* CS_DROPSHADOW */ cp.ExStyle |= 0x80; /* WS_EX_TOOLWINDOW */ return cp; }
  }

  protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    int v = 2; Native.DwmSetWindowAttribute(Handle, 33, ref v, 4);   // Windows 11 rounded corners
    ApplyBorder();
  }

  public void ApplyBorder() {
    if (!IsHandleCreated) return;
    Color c = TrayContext.Theme.Border; int v = c.R | (c.G << 8) | (c.B << 16);
    Native.DwmSetWindowAttribute(Handle, 34, ref v, 4);
  }

  protected override void OnPaint(PaintEventArgs e) { painter.Draw(e.Graphics, State.Now(), TrayContext.Theme); }
  protected override void OnPaintBackground(PaintEventArgs e) { }   // Draw() clears; avoids flicker
  protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Hide(); HiddenAt = DateTime.Now; }
  protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); base.OnKeyDown(e); }

  public void ShowNearCursor() {
    Point pos = Cursor.Position;
    Rectangle wa = Screen.FromPoint(pos).WorkingArea; int m = (int)painter.Px(12);
    Left = Math.Min(Math.Max(pos.X - Width / 2, wa.Left + m), wa.Right - Width - m);
    Top = wa.Bottom - Height - m;
    Show(); Activate(); Invalidate();
  }
}

sealed class TrayContext : ApplicationContext {
  public static Theme Theme = Theme.Read();
  readonly NotifyIcon tray = new NotifyIcon();
  readonly Painter painter;
  readonly Flyout fly;
  readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
  string shownKey;
  IntPtr hIcon = IntPtr.Zero;

  public TrayContext(float scale) {
    painter = new Painter(scale);
    fly = new Flyout(painter);
    ContextMenuStrip menu = new ContextMenuStrip();
    menu.Items.Add("Battery details", null, delegate { fly.ShowNearCursor(); });
    menu.Items.Add("-");
    menu.Items.Add("Exit", null, delegate { Quit(); });
    tray.ContextMenuStrip = menu;
    tray.MouseClick += delegate(object s, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left) return;
      if (fly.Visible) { fly.Hide(); return; }
      if ((DateTime.Now - fly.HiddenAt).TotalMilliseconds < 300) return;   // this click just closed it
      fly.ShowNearCursor();
    };
    SystemEvents.UserPreferenceChanged += OnPreferenceChanged;   // theme / accent changes, no polling
    AjBattery.Start();
    Refresh();   // the icon only becomes visible once the receiver is found
    timer.Interval = 2000;
    timer.Tick += delegate { Refresh(); };
    timer.Start();
  }

  void OnPreferenceChanged(object s, UserPreferenceChangedEventArgs e) {
    if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color) return;
    Theme = Theme.Read(); shownKey = null; fly.ApplyBorder(); Refresh();
  }

  void Refresh() {
    bool present = AjBattery.ReceiverPresent;   // show the icon only while the AJAZZ receiver is plugged in
    if (tray.Visible != present) { tray.Visible = present; if (!present && fly.Visible) fly.Hide(); }
    if (!present) return;
    State st = State.Now();
    string key = st.Level + "|" + st.Stale + "|" + Theme.Light + "|" + Theme.Fg.ToArgb();
    if (key != shownKey) {
      using (Bitmap bmp = Painter.PercentIcon(SystemInformation.SmallIconSize.Width, st, Theme)) {
        IntPtr h = bmp.GetHicon();
        Icon old = tray.Icon;
        tray.Icon = Icon.FromHandle(h);
        if (old != null) old.Dispose();
        if (hIcon != IntPtr.Zero) Native.DestroyIcon(hIcon);
        hIcon = h;
      }
      shownKey = key;
    }
    string tip = st.Level < 0 ? "AJ139 V2 MC\n" + st.Status : "AJ139 V2 MC: " + st.Level + "%\nUpdated " + (int)(DateTime.Now - st.Last).TotalMinutes + " min ago";
    tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;   // NotifyIcon limit
    if (fly.Visible) fly.Invalidate();
  }

  void Quit() {
    timer.Stop();
    SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    tray.Visible = false; tray.Dispose();
    if (hIcon != IntPtr.Zero) Native.DestroyIcon(hIcon);
    fly.Dispose(); painter.Dispose();
    ExitThread();
  }
}

// ---------------------------------------------------------------------------------------------
// Entry point, self-test, preview
// ---------------------------------------------------------------------------------------------
static class Program {
  [STAThread]
  static int Main(string[] args) {
    if (args.Length > 0 && args[0] == "--selftest") return SelfTest();
    if (args.Length > 0 && args[0] == "--watch") return Watch(args.Length > 1 ? int.Parse(args[1]) : 120);
    Native.SetProcessDPIAware();
    float scale;
    using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
    if (args.Length > 1 && args[0] == "--preview") { Preview(args[1], scale); return 0; }

    bool created;
    using (Mutex mutex = new Mutex(true, @"Local\AJ139BatteryTray", out created)) {
      if (!created) return 0;   // already running
      // portable: data lives next to the exe (sandboxed launchers redirect AppData writes, which hid the log before)
      string dir = Path.GetDirectoryName(Application.ExecutablePath);
      AjBattery.DpiCacheFile = Path.Combine(dir, "dpi.txt");
      AjBattery.LogFile = Path.Combine(dir, "log.txt");
      AjBattery.LoadDpiCache();
      AjBattery.Log("started");
      Application.EnableVisualStyles();
      Application.Run(new TrayContext(scale));
    }
    return 0;
  }

  static byte[] B(params int[] v) { return Array.ConvertAll(v, x => (byte)x); }

  // --watch [seconds]: print every state change to the console, for troubleshooting without the tray
  static int Watch(int seconds) {
    Native.AttachConsole(-1);
    AjBattery.Start();
    string last = null; DateTime end = DateTime.Now.AddSeconds(seconds);
    while (DateTime.Now < end) {
      string now = "battery=" + AjBattery.Level + " raw=" + AjBattery.Raw + " receiver=" + AjBattery.ReceiverPresent + " status=\"" + AjBattery.Status + "\" dpiStage=" + (AjBattery.DpiActive + 1);
      if (now != last) { Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + now); last = now; }
      Thread.Sleep(250);
    }
    return 0;
  }

  static int SelfTest() {
    Native.AttachConsole(-1);   // print to the calling console
    bool ok = true;
    // real packets: periodic (yesterday, 81%) and event (today after a full charge, 100%)
    ok &= AjBattery.Parse(B(0, 0xAA, 0xFA, 0, 0x59, 0x38, 0, 0, 0, 0xD0, 0x51), 11) == 81;
    ok &= AjBattery.Parse(B(0, 0xAA, 0xFA, 0xAE, 0x6F, 0x38, 1, 1, 1, 0xD0, 0x64), 11) == 100;
    ok &= AjBattery.Parse(B(0, 0xAA, 0xFA, 0, 0x59, 0x38, 0, 0, 0, 0xD0, 0xFF), 11) == -1;  // out of range
    ok &= AjBattery.Parse(B(0, 0xAA, 0x08, 0xA5, 0x07, 0x20, 0, 0, 0, 0x20, 0x01), 11) == -1;
    ok &= AjBattery.Parse(B(0, 0xAA, 0xFA, 0, 0x59, 0x38), 6) == -1;                     // too short
    // real DPI button press (stage 5 of 6) must not read as battery 38%
    byte[] press = B(0, 0xAA, 0xFA, 0xAE, 0x26, 0x0A, 1, 1, 1, 0x10, 5, 4);
    ok &= AjBattery.Parse(press, 12) == -1 && AjBattery.ParseDpiEvent(press, 12) == 4;
    ok &= AjBattery.ParseDpiEvent(B(0, 0xAA, 0xFA, 0, 0x59, 0x38, 0, 0, 0, 0xD0, 0x51), 11) == -1;
    // smoothing: the real 89 / 86 / 89 jitter shows as 89
    DateTime t0 = new DateTime(2026, 1, 1, 12, 0, 0);
    AjBattery.ResetForTest();
    AjBattery.OnLevel(89, t0); AjBattery.OnLevel(86, t0); AjBattery.OnLevel(89, t0);
    ok &= AjBattery.Level == 89 && AjBattery.Raw == 89;
    // drain estimate on the smoothed value
    AjBattery.ResetForTest();
    AjBattery.OnLevel(90, t0); ok &= AjBattery.DrainPerHour() == -1;                    // no history yet
    for (int i = 0; i < 3; i++) AjBattery.OnLevel(86, t0.AddHours(2));
    ok &= AjBattery.Level == 86 && AjBattery.DrainPerHour() == 2;                       // 4% in 2 h
    AjBattery.OnLevel(100, t0.AddHours(3));
    ok &= AjBattery.Level == 100 && AjBattery.DrainPerHour() == -1;                     // recharged: shows at once, estimate resets
    // real DPI reply captured from the AJAZZ app: 6 stages, stage 2 (1-based) active = index 1
    byte[] pkt = Array.ConvertAll("00 AA 0E A5 A1 2F 00 00 00 00 00 04 06 02 90 01 84 03 B0 04 40 06 60 09 80 0C 00 00 00 00".Split(' '), x => Convert.ToByte(x, 16));
    int[] st; int act;
    ok &= AjBattery.ParseDpi(pkt, pkt.Length, out st, out act) && act == 1 && string.Join(",", st) == "400,900,1200,1600,2400,3200";
    // icon colour gradient: ends hit the stops, the middle is in between, below 20 stays red
    ok &= Painter.LevelColor(100, false) == Color.FromArgb(108, 203, 95) && Painter.LevelColor(50, false) == Color.FromArgb(252, 225, 0);
    ok &= Painter.LevelColor(20, false) == Color.FromArgb(255, 99, 71) && Painter.LevelColor(5, true) == Color.FromArgb(196, 43, 28);
    Color mid = Painter.LevelColor(75, false); ok &= mid.R > 108 && mid.R < 252;
    // second real reply (19:13): byte 4 differs (90 instead of A1) and must still parse
    byte[] pkt2 = Array.ConvertAll("00 AA 0E A5 90 2F 00 00 00 00 00 04 06 02 90 01 84 03 B0 04 40 06 60 09 80 0C 00 00 00 00".Split(' '), x => Convert.ToByte(x, 16));
    ok &= AjBattery.ParseDpi(pkt2, pkt2.Length, out st, out act) && act == 1 && st.Length == 6;
    pkt[12] = 9; ok &= !AjBattery.ParseDpi(pkt, pkt.Length, out st, out act);           // bad stage count
    Console.WriteLine(ok ? "self-test OK" : "self-test FAILED");
    return ok ? 0 : 1;
  }

  static void Preview(string dir, float scale) {
    Directory.CreateDirectory(dir);
    int[] levels = { 100, 75, 50, 35, 15, -1 };
    foreach (bool light in new[] { false, true }) {
      Theme th = Theme.Make(light, light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(76, 194, 255));
      string name = light ? "light" : "dark";
      const int z = 8, cell = 24 * z + 24;
      using (Bitmap sheet = new Bitmap(levels.Length * cell + 24, 2 * cell + 24))
      using (Graphics sg = Graphics.FromImage(sheet)) {
        sg.Clear(light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(32, 32, 32));
        sg.InterpolationMode = InterpolationMode.NearestNeighbor; sg.PixelOffsetMode = PixelOffsetMode.Half;
        int row = 0;
        foreach (int sz in new[] { 16, 24 }) {
          for (int col = 0; col < levels.Length; col++) {
            State s = new State(); s.Level = levels[col];
            using (Bitmap img = Painter.PercentIcon(sz, s, th)) {
              sg.DrawImage(img, 24 + col * cell, 24 + row * cell, sz * z, sz * z);
              sg.DrawImage(img, 24 + col * cell + 24 * z - sz, 24 + row * cell + 24 * z - sz + 16, sz, sz);
            }
          }
          row++;
        }
        sheet.Save(Path.Combine(dir, "icons-" + name + ".png"));
      }
      using (Painter p = new Painter(scale)) {
        foreach (string c in new[] { "normal", "low", "waiting" }) {
          State s = new State();
          s.Level = c == "normal" ? 72 : c == "low" ? 14 : -1;
          s.Last = c == "waiting" ? DateTime.MinValue : DateTime.Now.AddSeconds(-34);
          s.Status = c == "waiting" ? "Waiting for the first report" : "OK";
          s.Drain = c == "normal" ? 2.3 : -1;
          s.Dpi = c == "waiting" ? new int[0] : new[] { 400, 900, 1200, 1600, 2400, 3200 };
          s.DpiActive = c == "waiting" ? -1 : 1;
          s.DpiRead = DateTime.Now.AddMinutes(-25);
          s.DpiChanged = c == "waiting" ? DateTime.MinValue : DateTime.Now.AddMinutes(-3);
          using (Bitmap bmp = new Bitmap((int)p.Px(Painter.FlyW), (int)p.Px(Painter.FlyH)))
          using (Graphics g = Graphics.FromImage(bmp)) {
            p.Draw(g, s, th);
            bmp.Save(Path.Combine(dir, "flyout-" + name + "-" + c + ".png"));
          }
        }
      }
    }
  }
}
