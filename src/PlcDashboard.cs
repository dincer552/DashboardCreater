using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PlcDashboard
{
    class PointEntry
    {
        public string Id;
        public string Name;
        public string JsonId = "";
    }

    class JsonResult
    {
        public string Id = "";
        public bool Present;
        public double Value;
        public string Unit = "";
        public int State;
        public string Error = "";
    }

    // test-kontrol-pa repodaki _c600_json_read ile ayni okuma yolu:
    // GET http://host:port/json.html?fn=Read&pin=..&lng=0&us=2&id=<nokta adi>, Basic auth.
    static class ClimatixJson
    {
        static readonly JavaScriptSerializer js = new JavaScriptSerializer();

        public static string Query(string pin, string pointId)
        {
            return "fn=Read&pin=" + Esc(pin) + "&lng=0&us=2&id=" + Esc(pointId);
        }

        public static string PagesQuery(string pin)
        {
            return "FN=GetPages&pin=" + Esc(pin);
        }

        // Climatix boslugu + degil %20 olarak ister; Uri.EscapeDataString bunu dogru yapar.
        static string Esc(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : Uri.EscapeDataString(s);
        }

        public static string Get(string host, int port, string user, string pass, string query, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://" + host + ":" + port + "/json.html?" + query);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            req.UserAgent = "PlcDashboard/1.0";
            req.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(user + ":" + pass));
            using (WebResponse resp = req.GetResponse())
            using (Stream st = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(st, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        public static JsonResult Parse(string payload)
        {
            JsonResult r = new JsonResult();
            if (string.IsNullOrEmpty(payload)) { r.Error = "bos yanit"; return r; }
            string t = payload.Trim();

            if (t.StartsWith("{", StringComparison.Ordinal))
            {
                Dictionary<string, object> d = null;
                try { d = js.Deserialize<Dictionary<string, object>>(t); } catch { }
                object code;
                if (d != null && d.TryGetValue("JSONError", out code)) r.Error = "JSONError " + Convert.ToString(code, CultureInfo.InvariantCulture);
                else r.Error = "beklenmeyen yanit";
                return r;
            }

            if (!t.StartsWith("[", StringComparison.Ordinal)) { r.Error = "beklenmeyen yanit"; return r; }

            object[] arr;
            try { arr = js.Deserialize<object[]>(t); }
            catch (Exception ex) { r.Error = "json cozulemedi: " + ex.Message; return r; }

            // Repodaki semantik: bos dizi = gecerli ama kullanilmayan/karsiliksiz nokta.
            if (arr == null || arr.Length == 0) return r;

            Dictionary<string, object> item = arr[0] as Dictionary<string, object>;
            if (item == null) { r.Error = "beklenmeyen yanit"; return r; }

            object o;
            if (item.TryGetValue("id", out o) && o != null) r.Id = Convert.ToString(o, CultureInfo.InvariantCulture);
            if (item.TryGetValue("state", out o) && o != null) r.State = Convert.ToInt32(o, CultureInfo.InvariantCulture);
            if (item.TryGetValue("descr", out o) && o != null) r.Unit = Convert.ToString(o, CultureInfo.InvariantCulture);
            if (!item.TryGetValue("value", out o) || o == null) return r;

            try
            {
                r.Value = Convert.ToDouble(o, CultureInfo.InvariantCulture);
                r.Present = true;
            }
            catch { r.Error = "deger sayi degil"; }
            return r;
        }
    }

    // test-kontrol-pa / tcp_tunnel.py icindeki RainbowTcpTunnel yonetiminin karsiligi.
    class TunnelManager
    {
        public string LocalHost = "127.0.0.1";
        public int LocalPort = 4243;
        public int RemotePort = 80;

        public string LastDiagnostics = "";
        public string DetectedDevice = "";

        const int SW_HIDE = 0;
        const int STARTF_USESHOWWINDOW = 0x00000001;
        const int CREATE_NEW_CONSOLE = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit,
                                         int flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        System.Diagnostics.Process proc;

        public bool Running
        {
            get
            {
                if (proc == null) return false;
                try { proc.Refresh(); return !proc.HasExited; }
                catch { return false; }
            }
        }

        public string FindTunnelExe()
        {
            string appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string bundled = Path.Combine(Path.Combine(appDir, "Communication"), "RainbowTcpTunnel.exe");
            if (File.Exists(bundled)) return bundled;

            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathVar.Split(';'))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    string cand = Path.Combine(dir.Trim(), "RainbowTcpTunnel.exe");
                    if (File.Exists(cand)) return cand;
                }
                catch { }
            }

            List<string> found = new List<string>();
            foreach (string root in new string[] {
                @"C:\Program Files\Siemens", @"C:\Program Files (x86)\Siemens",
                @"C:\Program Files\Common Files\Rainbow", @"C:\Program Files (x86)\Common Files\Rainbow" })
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(root, "RainbowTcpTunnel.exe", SearchOption.AllDirectories))
                        found.Add(f);
                }
                catch { }
            }

            List<string> valid = new List<string>();
            foreach (string f in found)
                if (File.Exists(Path.Combine(Path.GetDirectoryName(f), "RainbowTcpTunnelSrv.dll"))
                    && File.Exists(Path.Combine(Path.GetDirectoryName(f), "CommandLine.dll")))
                    valid.Add(f);

            List<string> pick = valid.Count > 0 ? valid : found;
            if (pick.Count == 0) return null;

            string newest = pick[0];
            DateTime best = File.GetLastWriteTime(newest);
            foreach (string f in pick)
            {
                DateTime w = File.GetLastWriteTime(f);
                if (w > best) { best = w; newest = f; }
            }
            return newest;
        }

        string LogDir { get { return Path.Combine(Path.GetTempPath(), "Rainbow"); } }

        public string ReadDiagnostics()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Log dizini: " + LogDir);
            foreach (string name in new string[] { "TunnelError.log", "Tunnel.log" })
            {
                string p = Path.Combine(LogDir, name);
                if (!File.Exists(p)) continue;
                string[] lines;
                try { lines = File.ReadAllLines(p); } catch { continue; }
                List<string> tail = new List<string>();
                for (int i = Math.Max(0, lines.Length - 12); i < lines.Length; i++)
                    if (!string.IsNullOrEmpty(lines[i].Trim())) tail.Add(lines[i].Trim());
                if (tail.Count > 0) sb.Append("\n").Append(name).Append(": ").Append(string.Join(" | ", tail.ToArray()));
            }
            return sb.ToString();
        }

        void DetectUsbDevice()
        {
            string last = "";
            foreach (string name in new string[] { "Error.log", "RainbowDebugLog.log" })
            {
                string p = Path.Combine(LogDir, name);
                if (!File.Exists(p)) continue;
                try
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(p), @"\b(POL\d+_[A-Z0-9]+)\b", RegexOptions.IgnoreCase))
                        last = m.Groups[1].Value.ToUpperInvariant();
                }
                catch { }
            }
            DetectedDevice = last;
        }

        bool WaitForListener(int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                if (proc != null)
                {
                    try { proc.Refresh(); if (proc.HasExited) return false; } catch { }
                }
                try
                {
                    using (TcpClient c = new TcpClient())
                    {
                        IAsyncResult ar = c.BeginConnect(LocalHost, LocalPort, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(400) && c.Connected) { c.EndConnect(ar); return true; }
                    }
                }
                catch { }
                Thread.Sleep(200);
            }
            return false;
        }

        public void Stop()
        {
            System.Diagnostics.Process p = proc;
            proc = null;
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(2000); } }
            catch { }
        }

        public void Start(string host, string targetType, string auth, string interfaceType)
        {
            Stop();
            LastDiagnostics = "";

            string exe = FindTunnelExe();
            if (exe == null) throw new Exception("RainbowTcpTunnel.exe bulunamadi. Siemens SCOPE/Communication kurulumu gerekli.");

            string args = "--Port " + LocalPort + " --RemotePort " + RemotePort
                + " --TargetType " + targetType + " --InterfaceType " + interfaceType
                + " --InterfaceParameters " + host + "," + auth
                + " --IpAddress " + LocalHost + " --TimeoutSeconds 5 --ReconnectTimeoutSeconds 30"
                + " --ReconnectDelaySeconds 1 --Verbose --LogData --LogHeaders";

            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = SW_HIDE;
            PROCESS_INFORMATION pi;

            // CREATE_NEW_CONSOLE + SW_HIDE: tünel CMD'deki gibi gizli konsolla ayakta kalir.
            // CREATE_NO_WINDOW Console.ReadKey'i bozup Siemens sürecini aninda kapatıyor.
            bool ok = CreateProcess(exe, exe + " " + args, IntPtr.Zero, IntPtr.Zero, false,
                                    CREATE_NEW_CONSOLE, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi);
            if (!ok) throw new Exception("RainbowTcpTunnel baslatilamadi: " + Marshal.GetLastWin32Error());

            CloseHandle(pi.hThread);
            proc = System.Diagnostics.Process.GetProcessById(pi.dwProcessId);
            LastDiagnostics = "RainbowTcpTunnel: " + exe;

            if (!WaitForListener(30000))
            {
                string diag = ReadDiagnostics();
                string detail = "RainbowTcpTunnel " + LocalHost + ":" + LocalPort + " portunu acmadi.";
                if (!string.IsNullOrEmpty(diag)) detail += " " + diag;
                Stop();
                throw new Exception(detail);
            }
            if (interfaceType == "USB") DetectUsbDevice();
        }
    }

    class RasterRef
    {
        public Bitmap Image;
        public float X, Y, W, H;
    }

    class SvgDoc
    {
        public List<GraphicsPath> Paths = new List<GraphicsPath>();
        public List<RasterRef> Rasters = new List<RasterRef>();
        public float Width = 565f;
        public float Height = 374f;

        public bool HasContent { get { return Paths.Count > 0 || Rasters.Count > 0; } }

        public static SvgDoc FromDataUrl(string dataUrl)
        {
            int comma = dataUrl.IndexOf(',');
            string head = comma >= 0 ? dataUrl.Substring(0, comma) : "";
            string b64 = comma >= 0 ? dataUrl.Substring(comma + 1) : dataUrl;
            if (head.IndexOf("svg", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Bitmap direct = BitmapFromBase64(b64);
                if (direct != null)
                {
                    SvgDoc rasterDoc = new SvgDoc();
                    rasterDoc.Width = direct.Width;
                    rasterDoc.Height = direct.Height;
                    RasterRef rr = new RasterRef();
                    rr.Image = direct;
                    rr.X = 0; rr.Y = 0;
                    rr.W = direct.Width; rr.H = direct.Height;
                    rasterDoc.Rasters.Add(rr);
                    return rasterDoc;
                }
            }
            string xml = Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim()));
            return Parse(xml);
        }

        static SvgDoc Parse(string xml)
        {
            SvgDoc doc = new SvgDoc();

            Match vb = Regex.Match(xml, "viewBox=\"([^\"]+)\"");
            if (vb.Success)
            {
                string[] p = vb.Groups[1].Value.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 4)
                {
                    doc.Width = F(p[2]);
                    doc.Height = F(p[3]);
                }
            }
            else
            {
                Match sw = Regex.Match(xml, "<svg[^>]*\\swidth=\"([^\"]+)\"");
                Match sh = Regex.Match(xml, "<svg[^>]*\\sheight=\"([^\"]+)\"");
                if (sw.Success && sh.Success)
                {
                    float w = Dim(sw.Groups[1].Value), h = Dim(sh.Groups[1].Value);
                    if (w > 0 && h > 0) { doc.Width = w; doc.Height = h; }
                }
            }

            foreach (Match im in Regex.Matches(xml, "<image[^>]*>", RegexOptions.IgnoreCase))
            {
                string tag = im.Value;
                string href = Attr(tag, "href");
                if (string.IsNullOrEmpty(href)) continue;
                Bitmap bmp = BitmapFromDataUrl(href);
                if (bmp == null) continue;
                RasterRef rr = new RasterRef();
                rr.Image = bmp;
                rr.X = AttrF(tag, "x");
                rr.Y = AttrF(tag, "y");
                rr.W = AttrF(tag, "width");
                rr.H = AttrF(tag, "height");
                if (rr.W <= 0) rr.W = bmp.Width;
                if (rr.H <= 0) rr.H = bmp.Height;
                doc.Rasters.Add(rr);
                if (!vb.Success && rr.W > doc.Width) doc.Width = rr.W;
                if (!vb.Success && rr.H > doc.Height) doc.Height = rr.H;
            }

            foreach (Match gm in Regex.Matches(xml, "<g[^>]*transform=\"([^\"]+)\"[^>]*>"))
            {
                string t = gm.Groups[1].Value;
                float tx = 0, ty = 0, sx = 1, sy = 1;
                Match mTr = Regex.Match(t, "translate\\(\\s*([^,\\)]+)\\s*[,\\s]\\s*([^,\\)]+)\\)");
                if (mTr.Success) { tx = F(mTr.Groups[1].Value); ty = F(mTr.Groups[2].Value); }
                Match mSc = Regex.Match(t, "scale\\(\\s*([^,\\)]+)\\s*[,\\s]\\s*([^,\\)]+)\\)");
                if (mSc.Success) { sx = F(mSc.Groups[1].Value); sy = F(mSc.Groups[2].Value); }

                int bodyStart = gm.Index + gm.Length;
                int bodyEnd = xml.IndexOf("</g>", bodyStart, StringComparison.Ordinal);
                if (bodyEnd < 0) bodyEnd = xml.Length;
                string block = xml.Substring(bodyStart, bodyEnd - bodyStart);

                using (Matrix mx = new Matrix(sx, 0, 0, sy, tx, ty))
                {
                    foreach (Match pd in Regex.Matches(block, "d=\"([^\"]+)\""))
                    {
                        GraphicsPath gp = BuildPath(pd.Groups[1].Value);
                        if (gp == null) continue;
                        gp.Transform(mx);
                        doc.Paths.Add(gp);
                    }
                }
            }
            return doc;
        }

        static string Attr(string tag, string name)
        {
            Match m = Regex.Match(tag, "\\s(?:[A-Za-z0-9]+:)?" + name + "=\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        static float AttrF(string tag, string name)
        {
            return Dim(Attr(tag, name));
        }

        static float Dim(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            Match m = Regex.Match(s.Trim(), "^[0-9.]+");
            if (!m.Success) return 0f;
            return F(m.Value);
        }

        static Bitmap BitmapFromDataUrl(string url)
        {
            int comma = url.IndexOf(',');
            if (comma < 0) return null;
            return BitmapFromBase64(url.Substring(comma + 1));
        }

        static Bitmap BitmapFromBase64(string b64)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(b64.Trim());
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Bitmap raw = new Bitmap(ms))
                    return new Bitmap(raw);
            }
            catch { return null; }
        }

        static float F(string s)
        {
            float v;
            float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return v;
        }

        static List<object> Tokenize(string d)
        {
            List<object> tokens = new List<object>();
            StringBuilder num = new StringBuilder();
            for (int i = 0; i < d.Length; i++)
            {
                char c = d[i];
                if (char.IsLetter(c) || c == ',')
                {
                    if (num.Length > 0) { tokens.Add(ParseNum(num.ToString())); num.Length = 0; }
                    if (char.IsLetter(c)) tokens.Add(c);
                }
                else if (c == '-' || c == '+' || c == '.' || char.IsDigit(c) || c == 'e' || c == 'E')
                {
                    if (c == '-' || c == '+')
                    {
                        if (num.Length > 0 && num[num.Length - 1] != 'e' && num[num.Length - 1] != 'E')
                        {
                            tokens.Add(ParseNum(num.ToString()));
                            num.Length = 0;
                        }
                    }
                    if (num.Length == 0 && c == '.') num.Append("0.");
                    else num.Append(c);
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (num.Length > 0) { tokens.Add(ParseNum(num.ToString())); num.Length = 0; }
                }
            }
            if (num.Length > 0) tokens.Add(ParseNum(num.ToString()));
            return tokens;
        }

        static float ParseNum(string s)
        {
            float v;
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return v;
        }

        public static GraphicsPath BuildPath(string d)
        {
            List<object> tok = Tokenize(d);
            GraphicsPath gp = new GraphicsPath();
            gp.FillMode = FillMode.Winding;

            float cx = 0, cy = 0, sx = 0, sy = 0;
            bool figureOpen = false;
            int i = 0;
            char cmd = ' ';

            while (i < tok.Count)
            {
                if (tok[i] is char) { cmd = (char)tok[i]; i++; if (cmd == ' ') continue; }
                if (cmd == ' ') { i++; continue; }

                bool rel = char.IsLower(cmd);
                char up = char.ToUpperInvariant(cmd);

                if (up == 'M')
                {
                    if (i + 1 >= tok.Count) break;
                    float x = Convert.ToSingle(tok[i]), y = Convert.ToSingle(tok[i + 1]);
                    i += 2;
                    if (rel) { x += cx; y += cy; }
                    cx = x; cy = y; sx = x; sy = y;
                    gp.StartFigure();
                    figureOpen = true;
                    cmd = rel ? 'l' : 'L';
                }
                else if (up == 'L')
                {
                    if (i + 1 >= tok.Count) break;
                    float x = Convert.ToSingle(tok[i]), y = Convert.ToSingle(tok[i + 1]);
                    i += 2;
                    if (rel) { x += cx; y += cy; }
                    gp.AddLine(cx, cy, x, y);
                    cx = x; cy = y;
                }
                else if (up == 'H')
                {
                    if (i >= tok.Count) break;
                    float x = Convert.ToSingle(tok[i]); i++;
                    if (rel) x += cx;
                    gp.AddLine(cx, cy, x, cy);
                    cx = x;
                }
                else if (up == 'V')
                {
                    if (i >= tok.Count) break;
                    float y = Convert.ToSingle(tok[i]); i++;
                    if (rel) y += cy;
                    gp.AddLine(cx, cy, cx, y);
                    cy = y;
                }
                else if (up == 'C')
                {
                    if (i + 5 >= tok.Count) break;
                    float x1 = Convert.ToSingle(tok[i]), y1 = Convert.ToSingle(tok[i + 1]);
                    float x2 = Convert.ToSingle(tok[i + 2]), y2 = Convert.ToSingle(tok[i + 3]);
                    float x = Convert.ToSingle(tok[i + 4]), y = Convert.ToSingle(tok[i + 5]);
                    i += 6;
                    if (rel)
                    {
                        x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy;
                    }
                    gp.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                    cx = x; cy = y;
                }
                else if (up == 'Z')
                {
                    if (figureOpen) gp.CloseFigure();
                    figureOpen = false;
                    cx = sx; cy = sy;
                    i++;
                }
                else
                {
                    i++;
                }
            }

            if (gp.PointCount == 0) { gp.Dispose(); return null; }
            return gp;
        }
    }

    class Tile
    {
        public int Column, Row, Width, Height;
        public string Type = "datapoint";
        public string Title = "";
        public string PointId = "";
        public string PointName = "";
        public string Color = "petrol-background";
        public double BarMax = 100;
        public SvgDoc Svg;
        public TileView View;
        public PointEntry Bound;
        public Dictionary<string, object> Raw;
    }

    class TileView : Control
    {
        public Tile Model;
        public double? Value;
        public string UnitOf = "";
        public string Detail = "";
        public bool Selected;

        // Qoder paleti: beyaz kartlar, acik gri paneller, ada cay yesili (#4B6F5A) vurgu.
        static readonly Color Accent = Color.FromArgb(75, 111, 90);
        static readonly Color CardBorder = Color.FromArgb(229, 229, 229);
        static readonly Color CardFill = Color.White;
        static readonly Color SelFill = Color.FromArgb(240, 244, 242);
        static readonly Color TitleText = Color.FromArgb(131, 130, 128);
        static readonly Color ValueText = Color.FromArgb(8, 8, 7);
        static readonly Color UnitText = Color.FromArgb(150, 150, 148);
        static readonly Color Track = Color.FromArgb(232, 236, 234);

        static readonly Dictionary<string, Color> Accents = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            { "petrol-background", Accent },
            { "natural-gray-light-background", Color.FromArgb(131, 130, 128) },
            { "green-background", Accent },
            { "red-background", Color.FromArgb(236, 91, 86) },
            { "orange-background", Color.FromArgb(217, 142, 56) },
            { "yellow-background", Color.FromArgb(183, 154, 46) },
            { "blue-background", Color.FromArgb(58, 147, 225) },
            { "purple-background", Color.FromArgb(122, 91, 166) }
        };

        public static Color AccentFor(string cls)
        {
            Color c;
            if (!string.IsNullOrEmpty(cls) && Accents.TryGetValue(cls, out c)) return c;
            return Accent;
        }

        public TileView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Color accent = AccentFor(Model.Color);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Round(r))
            {
                using (SolidBrush b = new SolidBrush(Selected ? SelFill : CardFill)) g.FillPath(b, p);
                using (Pen pen = new Pen(Selected ? Accent : CardBorder, Selected ? 2f : 1f)) g.DrawPath(pen, p);
            }
            using (SolidBrush ab = new SolidBrush(accent))
            using (GraphicsPath sp = Round(new Rectangle(3, 3, Math.Max(1, Width - 7), 4)))
                g.FillPath(ab, sp);

            string label = Model.Title;
            if (string.IsNullOrEmpty(label)) label = Model.PointName;

            using (Font lf = new Font("Segoe UI", 8.5f))
            using (Font vf = new Font("Segoe UI", 18f, FontStyle.Bold))
            using (Font uf = new Font("Segoe UI", 8.25f))
            {
                TextRenderer.DrawText(g, label, lf, new Rectangle(10, 8, Width - 20, 18), TitleText,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);

                if (Model.Type == "webpicture")
                {
                    DrawSvg(g);
                    return;
                }

                string valText = Value.HasValue ? Format(Value.Value) : "--";

                if (Model.Type == "bargauge")
                {
                    TextRenderer.DrawText(g, valText + " %", vf, new Rectangle(10, 26, Width - 20, 32), ValueText,
                        TextFormatFlags.Left | TextFormatFlags.Top);
                    Rectangle track = new Rectangle(10, Height - 30, Math.Max(0, Width - 20), 12);
                    if (track.Width > 4)
                    {
                        using (SolidBrush tb = new SolidBrush(Track))
                        using (GraphicsPath tp = Round(track)) g.FillPath(tb, tp);
                        if (Value.HasValue && Model.BarMax > 0)
                        {
                            double frac = Math.Max(0, Math.Min(1, Value.Value / Model.BarMax));
                            Rectangle fill = new Rectangle(track.X, track.Y, (int)(track.Width * frac), track.Height);
                            if (fill.Width > 3)
                                using (SolidBrush fb = new SolidBrush(accent))
                                using (GraphicsPath fp = Round(fill)) g.FillPath(fb, fp);
                        }
                    }
                }
                else
                {
                    TextRenderer.DrawText(g, valText, vf, new Rectangle(10, 28, Width - 20, 32), ValueText,
                        TextFormatFlags.Left | TextFormatFlags.Top);
                    string sub = Value.HasValue ? UnitOf : "esleme yok";
                    TextRenderer.DrawText(g, sub, uf, new Rectangle(10, 62, Width - 20, 16), UnitText,
                        TextFormatFlags.Left | TextFormatFlags.Top);
                }
            }
        }

        static string Format(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 0.005) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        void DrawSvg(Graphics g)
        {
            if (Model.Svg == null || !Model.Svg.HasContent) return;
            float aw = Width - 16f, ah = Height - 30f;
            if (aw <= 0 || ah <= 0) return;
            float k = Math.Min(aw / Model.Svg.Width, ah / Model.Svg.Height);
            if (k <= 0) return;
            float ox = 8 + (aw - Model.Svg.Width * k) / 2f;
            float oy = 24 + (ah - Model.Svg.Height * k) / 2f;

            GraphicsState state = g.Save();
            if (Model.Svg.Rasters.Count > 0)
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.MultiplyTransform(new Matrix(k, 0, 0, k, ox, oy));
            foreach (RasterRef rr in Model.Svg.Rasters)
                g.DrawImage(rr.Image, rr.X, rr.Y, rr.W, rr.H);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(72, 84, 90)))
                foreach (GraphicsPath p in Model.Svg.Paths) g.FillPath(b, p);
            g.Restore(state);
        }

        static GraphicsPath Round(Rectangle r)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 2 || r.Height <= 2) { p.AddRectangle(r); return p; }
            int d = Math.Min(12, Math.Min(r.Width, r.Height) - 1);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Qoder masaustu uygulamasinin paleti: acik gri paneller, beyaz yuzeyler,
    // ada cay yesili (#4B6F5A) vurgu ve yine yesil tonlu hover.
    class QTheme : ProfessionalColorTable
    {
        static readonly Color Panel = Color.FromArgb(246, 246, 246);
        static readonly Color White = Color.White;
        static readonly Color Hover = Color.FromArgb(232, 236, 234);
        static readonly Color Accent = Color.FromArgb(75, 111, 90);
        static readonly Color Border = Color.FromArgb(229, 229, 229);

        public override Color MenuStripGradientBegin { get { return Panel; } }
        public override Color MenuStripGradientEnd { get { return Panel; } }
        public override Color MenuBorder { get { return Border; } }
        public override Color MenuItemSelected { get { return Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Color MenuItemBorder { get { return Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return Hover; } }
        public override Color MenuItemPressedGradientEnd { get { return Hover; } }
        public override Color ToolStripBorder { get { return Border; } }
        public override Color ToolStripGradientBegin { get { return Panel; } }
        public override Color ToolStripGradientMiddle { get { return Panel; } }
        public override Color ToolStripGradientEnd { get { return Panel; } }
        public override Color ToolStripDropDownBackground { get { return White; } }
        public override Color ImageMarginGradientBegin { get { return White; } }
        public override Color ImageMarginGradientMiddle { get { return White; } }
        public override Color ImageMarginGradientEnd { get { return White; } }
        public override Color SeparatorDark { get { return Border; } }
        public override Color SeparatorLight { get { return White; } }
        public override Color ButtonSelectedGradientBegin { get { return Hover; } }
        public override Color ButtonSelectedGradientMiddle { get { return Hover; } }
        public override Color ButtonSelectedGradientEnd { get { return Hover; } }
        public override Color ButtonSelectedBorder { get { return Accent; } }
        public override Color ButtonPressedGradientBegin { get { return Hover; } }
        public override Color ButtonPressedGradientMiddle { get { return Hover; } }
        public override Color ButtonPressedGradientEnd { get { return Hover; } }
        public override Color ButtonPressedBorder { get { return Accent; } }
        public override Color ButtonCheckedGradientBegin { get { return Hover; } }
        public override Color ButtonCheckedGradientMiddle { get { return Hover; } }
        public override Color ButtonCheckedGradientEnd { get { return Hover; } }
        public override Color ButtonCheckedHighlight { get { return Hover; } }
        public override Color CheckBackground { get { return White; } }
        public override Color CheckSelectedBackground { get { return Accent; } }
        public override Color CheckPressedBackground { get { return Hover; } }
        public override Color StatusStripGradientBegin { get { return Panel; } }
        public override Color StatusStripGradientEnd { get { return Panel; } }
        public override Color OverflowButtonGradientBegin { get { return Panel; } }
        public override Color OverflowButtonGradientMiddle { get { return Panel; } }
        public override Color OverflowButtonGradientEnd { get { return Panel; } }
        public override Color GripDark { get { return Border; } }
        public override Color GripLight { get { return White; } }
    }

    public class MainForm : Form
    {
        class DashDef
        {
            public string Title;
            public List<Tile> Tiles = new List<Tile>();
            public Dictionary<string, object> Raw;
        }

        readonly Dictionary<string, PointEntry> byId = new Dictionary<string, PointEntry>();
        readonly Dictionary<string, PointEntry> byName = new Dictionary<string, PointEntry>(StringComparer.OrdinalIgnoreCase);
        readonly List<DashDef> dashes = new List<DashDef>();
        readonly List<PointEntry> pointList = new List<PointEntry>();
        readonly JavaScriptSerializer js = new JavaScriptSerializer();
        readonly ToolTip tips = new ToolTip();

        Panel gridPanel;
        ToolStripComboBox dashBox;
        ToolStripStatusLabel statusLabel;
        System.Windows.Forms.Timer timer;

        Panel editorPanel;
        ToolStripButton editBtn;
        ToolStripButton undoBtn, redoBtn;

        sealed class Snap
        {
            public string Json;
            public string Points;
            public int DashIndex;
            public string TileId;
        }

        readonly List<Snap> undoStack = new List<Snap>();
        readonly List<Snap> redoStack = new List<Snap>();
        bool inUndo;
        string lastUndoField = "";
        DateTime lastUndoAt = DateTime.MinValue;
        TextBox txtDashTitle, txtTileText, txtPointId, txtPointName, txtJsonId;
        ComboBox cboType, cboColor;
        NumericUpDown numCol, numRow, numW, numH, numBarMax;
        Button svgBtn;
        Tile selected;
        bool suppress;
        bool dragging;
        Point dragStart;
        int dragCol, dragRow;
        string currentJsonPath;
        List<object> currentRoot;
        float cellW = 100f, cellH = 60f;
        const int GridPad = 10, GridGap = 8;

        bool polling;
        bool suppressPoll;

        // Tek veri kaynagi: RainbowTcpTunnel uzerinden Climatix HTTP JSON.
        // Tunel kurulamazsa ayni API dogrudan cihaz IP'sinden denenir (repodaki davranis).
        // Kullanici/sifre/pin/port programda kayitlidir; kullanicidan yalnizca IP istenir.
        string deviceHost = "";
        const int jsonHttpPort = 80;
        const string apiPin = "6000";
        const string apiUser = "ADMIN";
        const string apiPass = "SBTAdmin!";
        const int apiScopePort = 4242;
        const int jsonTimeoutMs = 3000;
        bool allowDirectFallback = true;
        TunnelManager tunnel = new TunnelManager();
        bool tunnelUnavailable;

        public MainForm()
        {
            ToolStripManager.Renderer = new ToolStripProfessionalRenderer(new QTheme());
            Text = "PLC Dashboard";
            Width = 1180;
            Height = 620;
            MinimumSize = new Size(720, 440);
            BackColor = Color.FromArgb(246, 246, 246);
            AllowDrop = true;
            KeyPreview = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (!e.Control) return;
                if (e.KeyCode == Keys.Z) { DoUndo(); e.Handled = true; }
                else if (e.KeyCode == Keys.Y) { DoRedo(); e.Handled = true; }
            };

            BuildUi();
            LoadConfig();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += delegate { Poll(); };
            timer.Start();

            // IP yazilida gomulu degil; ilk calistirmada kullanicidan istenir.
            Shown += delegate
            {
                if (string.IsNullOrEmpty(deviceHost)) OpenConnectionDialog();
            };
        }

        void BuildUi()
        {
            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem fileMenu = new ToolStripMenuItem("Dosya");
            fileMenu.DropDownItems.Add("Dashboard JSON ac...", null, delegate { OpenJson(); });
            fileMenu.DropDownItems.Add("Nokta eslemesi (points.json)...", null, delegate { OpenPoints(); });
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("Revize JSON kaydet...", null, delegate { SaveRevisedJson(false); });
            fileMenu.DropDownItems.Add("Revize JSON - acilan dosyanin uzerine yaz", null, delegate { SaveRevisedJson(true); });
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("Cikis", null, delegate { Close(); });
            menu.Items.Add(fileMenu);

            ToolStripMenuItem optMenu = new ToolStripMenuItem("Seckiler");

            ToolStripMenuItem connMenu = new ToolStripMenuItem("Baglanti");
            connMenu.DropDownItems.Add("Baglanti ayarlari (cihaz IP)...", null, delegate { OpenConnectionDialog(); });
            connMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem probeItem = new ToolStripMenuItem("Cihaz API'sini dene (GetPages)");
            probeItem.Click += delegate { ProbeApi(); };
            connMenu.DropDownItems.Add(probeItem);
            ToolStripMenuItem tunelItem = new ToolStripMenuItem("Tuneli yeniden kur");
            tunelItem.Click += delegate { tunnel.Stop(); tunnelUnavailable = false; EnsureTunnelThenPoll(); };
            connMenu.DropDownItems.Add(tunelItem);
            ToolStripMenuItem stopTunelItem = new ToolStripMenuItem("Tuneli durdur");
            stopTunelItem.Click += delegate { tunnel.Stop(); SetStatus("tunel durduruldu"); };
            connMenu.DropDownItems.Add(stopTunelItem);
            optMenu.DropDownItems.Add(connMenu);

            foreach (int sec in new int[] { 1, 2, 5, 10 })
            {
                int captured = sec;
                optMenu.DropDownItems.Add(captured + " sn'de bir yenile", null,
                    delegate { timer.Interval = captured * 1000; });
            }
            menu.Items.Add(optMenu);

            StatusStrip status = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("");
            statusLabel.Spring = true;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.ForeColor = Color.FromArgb(131, 130, 128);
            status.Items.Add(statusLabel);

            ToolStrip tool = new ToolStrip();
            tool.GripStyle = ToolStripGripStyle.Hidden;
            dashBox = new ToolStripComboBox();
            dashBox.AutoSize = false;
            dashBox.Width = 260;
            dashBox.DropDownStyle = ComboBoxStyle.DropDownList;
            dashBox.SelectedIndexChanged += delegate { ShowDash(dashBox.SelectedIndex); };
            ToolStripButton openBtn = new ToolStripButton("JSON sec");
            openBtn.Click += delegate { OpenJson(); };
            editBtn = new ToolStripButton("Duzenle");
            editBtn.CheckOnClick = true;
            editBtn.CheckedChanged += delegate
            {
                editorPanel.Visible = editBtn.Checked;
                if (editBtn.Checked && selected == null && gridPanel.Controls.Count > 0)
                    SelectTile(((TileView)gridPanel.Controls[0]).Model);
                LayoutTiles();
            };
            tool.Items.Add(new ToolStripLabel("Pano:"));
            tool.Items.Add(dashBox);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(openBtn);
            tool.Items.Add(editBtn);
            tool.Items.Add(new ToolStripSeparator());
            undoBtn = new ToolStripButton("Geri Al");
            undoBtn.Enabled = false;
            undoBtn.Click += delegate { DoUndo(); };
            redoBtn = new ToolStripButton("Ileri Al");
            redoBtn.Enabled = false;
            redoBtn.Click += delegate { DoRedo(); };
            tool.Items.Add(undoBtn);
            tool.Items.Add(redoBtn);

            gridPanel = new Panel();
            gridPanel.Dock = DockStyle.Fill;
            gridPanel.BackColor = Color.White;
            gridPanel.Resize += delegate { LayoutTiles(); };

            editorPanel = BuildEditor();

            Controls.Add(gridPanel);
            Controls.Add(editorPanel);
            Controls.Add(tool);
            Controls.Add(status);
            Controls.Add(menu);
            MainMenuStrip = menu;
        }

        static Label Head(string text, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Segoe UI", 8.25f, FontStyle.Bold);
            l.Location = new Point(12, y);
            l.AutoSize = true;
            return l;
        }

        static Control RowField(Panel p, string caption, Control box, ref int y)
        {
            Label l = new Label();
            l.Text = caption;
            l.Location = new Point(12, y + 3);
            l.Size = new Size(74, 16);
            box.Location = new Point(90, y);
            p.Controls.Add(l);
            p.Controls.Add(box);
            y += box.Height + 6;
            return box;
        }

        Panel BuildEditor()
        {
            Panel host = new Panel();
            host.Dock = DockStyle.Right;
            host.Width = 330;
            host.BackColor = Color.FromArgb(246, 246, 246);
            host.BorderStyle = BorderStyle.FixedSingle;
            host.AutoScroll = true;
            host.Visible = false;

            int y = 10;
            host.Controls.Add(Head("PANO", y)); y += 20;

            txtDashTitle = new TextBox();
            txtDashTitle.Width = 214;
            txtDashTitle.TextChanged += delegate { ApplyDashTitle(); };
            RowField(host, "Ad", txtDashTitle, ref y);

            y += 6;
            host.Controls.Add(Head("SECILI KUTU", y)); y += 20;

            txtTileText = new TextBox();
            txtTileText.Width = 214;
            txtTileText.TextChanged += delegate { ApplySelected(); };
            RowField(host, "Metin", txtTileText, ref y);

            cboType = new ComboBox();
            cboType.DropDownStyle = ComboBoxStyle.DropDownList;
            cboType.Width = 214;
            cboType.Items.AddRange(new object[] { "datapoint", "bargauge", "webpicture" });
            cboType.SelectedIndexChanged += delegate { ApplySelected(); };
            RowField(host, "Tip", cboType, ref y);

            cboColor = new ComboBox();
            cboColor.Width = 214;
            cboColor.Items.AddRange(new object[] {
                "petrol-background", "natural-gray-light-background", "green-background",
                "red-background", "orange-background", "yellow-background",
                "blue-background", "purple-background" });
            cboColor.TextChanged += delegate { ApplySelected(); };
            RowField(host, "Renk", cboColor, ref y);

            numCol = Num(0, 40); numCol.ValueChanged += delegate { ApplySelected(); };
            RowField(host, "Kol", numCol, ref y);
            numRow = Num(0, 40); numRow.ValueChanged += delegate { ApplySelected(); };
            RowField(host, "Satir", numRow, ref y);
            numW = Num(1, 12); numW.ValueChanged += delegate { ApplySelected(); };
            RowField(host, "Genislik", numW, ref y);
            numH = Num(1, 12); numH.ValueChanged += delegate { ApplySelected(); };
            RowField(host, "Yukseklik", numH, ref y);

            y += 6;
            host.Controls.Add(Head("BULUT NOKTASI (JSON)", y)); y += 20;

            Label idLbl = new Label();
            idLbl.Text = "datapointId";
            idLbl.Location = new Point(12, y + 3);
            idLbl.Size = new Size(74, 16);
            host.Controls.Add(idLbl);

            Label prefixLbl = new Label();
            prefixLbl.Text = "1!";
            prefixLbl.Location = new Point(90, y + 1);
            prefixLbl.Size = new Size(18, 20);
            prefixLbl.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            prefixLbl.ForeColor = Color.FromArgb(75, 111, 90);
            host.Controls.Add(prefixLbl);

            txtPointId = new TextBox();
            txtPointId.Location = new Point(108, y);
            txtPointId.Width = 196;
            txtPointId.TextChanged += delegate { ApplySelected(); };
            host.Controls.Add(txtPointId);
            y += 24;

            Label prefixHint = new Label();
            prefixHint.Text = "\"1!\" bitisi otomatik eklenir, yazmanaya gerek yok.";
            prefixHint.Location = new Point(90, y);
            prefixHint.Size = new Size(214, 14);
            prefixHint.Font = new Font("Segoe UI", 7.5f);
            prefixHint.ForeColor = Color.FromArgb(131, 130, 128);
            host.Controls.Add(prefixHint);
            y += 20;

            txtPointName = new TextBox();
            txtPointName.Width = 214;
            txtPointName.TextChanged += delegate { ApplySelected(); };
            RowField(host, "Nokta adi", txtPointName, ref y);

            numBarMax = Num(1, 1000000); numBarMax.ValueChanged += delegate { ApplySelected(); };
            RowField(host, "Bar max", numBarMax, ref y);

            svgBtn = new Button();
            svgBtn.Text = "Resim / SVG ekle";
            svgBtn.Location = new Point(90, y);
            svgBtn.Size = new Size(214, 24);
            svgBtn.Click += delegate { LoadSvgIntoSelected(); };
            host.Controls.Add(svgBtn);
            y += 30;

            y += 6;
            host.Controls.Add(Head("Klimatix JSON API (json.html)", y)); y += 20;

            txtJsonId = new TextBox();
            txtJsonId.Width = 214;
            txtJsonId.TextChanged += delegate { ApplySelected(); };
            RowField(host, "Nokta id", txtJsonId, ref y);

            Label jsonHint = new Label();
            jsonHint.Text = "Bulut id degil, kisa nokta adi (ornek: 4-TMPVAL, 1-TMP_SET). Ondalikli deger doner.";
            jsonHint.Location = new Point(90, y);
            jsonHint.Size = new Size(214, 26);
            jsonHint.Font = new Font("Segoe UI", 7.5f);
            jsonHint.ForeColor = Color.FromArgb(131, 130, 128);
            host.Controls.Add(jsonHint);
            y += 30;

            Button testJsonBtn = new Button();
            testJsonBtn.Text = "Bu noktayi tek oku";
            testJsonBtn.Location = new Point(90, y);
            testJsonBtn.Size = new Size(214, 24);
            testJsonBtn.Click += delegate { TestReadSelected(); };
            host.Controls.Add(testJsonBtn);
            y += 34;

            Button addBtn = new Button();
            addBtn.Text = "Kutu ekle";
            addBtn.Location = new Point(12, y);
            addBtn.Size = new Size(150, 26);
            addBtn.Click += delegate { AddTile(); };
            host.Controls.Add(addBtn);

            Button delBtn = new Button();
            delBtn.Text = "Secileni sil";
            delBtn.Location = new Point(168, y);
            delBtn.Size = new Size(136, 26);
            delBtn.Click += delegate { DeleteSelected(); };
            host.Controls.Add(delBtn);
            y += 34;

            Button saveBtn = new Button();
            saveBtn.Text = "Revize JSON kaydet";
            saveBtn.Location = new Point(12, y);
            saveBtn.Size = new Size(150, 26);
            saveBtn.BackColor = Color.FromArgb(232, 236, 234);
            saveBtn.Click += delegate { SaveRevisedJson(false); };
            host.Controls.Add(saveBtn);

            Button overBtn = new Button();
            overBtn.Text = "Uzerine yaz";
            overBtn.Location = new Point(168, y);
            overBtn.Size = new Size(136, 26);
            overBtn.Click += delegate { SaveRevisedJson(true); };
            host.Controls.Add(overBtn);
            y += 34;

            return host;
        }

        static NumericUpDown Num(decimal min, decimal max)
        {
            return Num(min, max, 0);
        }

        static NumericUpDown Num(decimal min, decimal max, int decimals)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.DecimalPlaces = decimals;
            n.Width = 214;
            return n;
        }

        static string AppDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        public void LoadDashboardFromArgs(string path)
        {
            LoadDashboard(path);
        }

        public string LoadFirstFoundDashboard()
        {
            foreach (string f in Directory.GetFiles(AppDir, "*.json"))
            {
                if (Path.GetFileName(f).Equals("points.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string txt = File.ReadAllText(f);
                    if (txt.Contains("\"tiles\"")) { LoadDashboard(f); return f; }
                }
                catch { }
            }
            return null;
        }

        void OpenJson()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Dashboard JSON (*.json)|*.json|Tum dosyalar (*.*)|*.*";
                dlg.InitialDirectory = AppDir;
                if (dlg.ShowDialog(this) == DialogResult.OK) LoadDashboard(dlg.FileName);
            }
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (string f in files)
                if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { LoadDashboard(f); return; }
        }

        void OpenPoints()
        {
            string p = Path.Combine(AppDir, "points.json");
            if (!File.Exists(p)) File.WriteAllText(p, DefaultPointsJson());
            try { System.Diagnostics.Process.Start(p); }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        static string ConfigPath { get { return Path.Combine(AppDir, "config.json"); } }

        // config.json yalnizca cihaz IP'sini tutar; diger parametreler programda sabit.
        void LoadConfig()
        {
            if (File.Exists(ConfigPath))
            {
                try
                {
                    Dictionary<string, object> cfg = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigPath));
                    if (cfg != null)
                    {
                        if (cfg.ContainsKey("deviceHost")) deviceHost = Convert.ToString(cfg["deviceHost"]).Trim();
                        else if (cfg.ContainsKey("host")) deviceHost = Convert.ToString(cfg["host"]).Trim();
                        if (cfg.ContainsKey("localTunnelPort")) tunnel.LocalPort = Convert.ToInt32(cfg["localTunnelPort"]);
                        if (cfg.ContainsKey("directFallback")) allowDirectFallback = Convert.ToString(cfg["directFallback"]) != "false";
                    }
                }
                catch { }
            }

            ReloadPoints();
        }

        void SaveConfig()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"deviceHost\": \"" + JsonEsc(deviceHost) + "\",");
            sb.AppendLine("  \"localTunnelPort\": " + tunnel.LocalPort.ToString(CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"directFallback\": " + (allowDirectFallback ? "true" : "false"));
            sb.AppendLine("}");
            try { File.WriteAllText(ConfigPath, sb.ToString()); }
            catch (Exception ex) { SetStatus("config.json yazilamadi: " + ex.Message); }
        }

        void OpenConnectionDialog()
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "Cihaz IP adresi";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(340, 140);
                dlg.BackColor = Color.FromArgb(246, 246, 246);

                TextBox hostBox = DialField(dlg, "Cihaz IP / host", deviceHost, 14);

                Label note = new Label();
                note.Text = "Okuma RainbowTcpTunnel uzerinden cihaz JSON API'si ile yapilir.\r\n"
                    + "Kullanici, sifre, pin ve portlar programda kayitlidir.";
                note.Location = new Point(14, 46);
                note.Size = new Size(312, 32);
                note.Font = new Font("Segoe UI", 7.75f);
                note.ForeColor = Color.FromArgb(131, 130, 128);
                dlg.Controls.Add(note);

                Button testBtn = new Button();
                testBtn.Text = "Baglantiyi dene";
                testBtn.Location = new Point(14, 92);
                testBtn.Size = new Size(140, 26);
                dlg.Controls.Add(testBtn);

                Button okBtn = new Button();
                okBtn.Text = "Kaydet ve baglan";
                okBtn.Location = new Point(186, 92);
                okBtn.Size = new Size(140, 26);
                okBtn.DialogResult = DialogResult.OK;
                dlg.Controls.Add(okBtn);
                dlg.AcceptButton = okBtn;

                testBtn.Click += delegate { ProbeDirect(hostBox.Text.Trim(), jsonHttpPort, apiPass, apiUser, apiPin); };

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                deviceHost = hostBox.Text.Trim();
                tunnelUnavailable = false;

                if (string.IsNullOrEmpty(deviceHost))
                {
                    SetStatus("cihaz IP girilmedi - Seciler > Baglanti > Baglanti ayarlari");
                    return;
                }

                SaveConfig();
                EnsureTunnelThenPoll();
            }
        }

        static TextBox DialField(Form dlg, string caption, string value, int y)
        {
            Label l = new Label();
            l.Text = caption;
            l.Location = new Point(14, y + 3);
            l.Size = new Size(110, 16);
            dlg.Controls.Add(l);

            TextBox t = new TextBox();
            t.Text = value;
            t.Location = new Point(130, y);
            t.Size = new Size(196, 22);
            dlg.Controls.Add(t);
            return t;
        }

        // Tuneli kur, kurulamazsa (yada kullanici isterse) dogrudan HTTP'e dus; sonra ilk okumayi yap.
        void EnsureTunnelThenPoll()
        {
            if (string.IsNullOrEmpty(deviceHost)) { SetStatus("once cihaz IP girin"); return; }
            SetStatus("baglanti kuruluyor: " + deviceHost);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string msg;
                try
                {
                    tunnel.Start(deviceHost, "0", apiPass, "TCP/IP");
                    msg = "Rainbow tuneli hazir: " + deviceHost + ":" + apiScopePort + " -> 127.0.0.1:" + tunnel.LocalPort + " -> HTTP " + tunnel.RemotePort;
                }
                catch (Exception ex)
                {
                    msg = "Tunel kurulamadi: " + ex.Message;
                    if (!string.IsNullOrEmpty(tunnel.LastDiagnostics) && msg.Length < 400) msg += " | " + tunnel.LastDiagnostics;
                }
                string beginMsg = msg;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (string.IsNullOrEmpty(beginMsg) || beginMsg.StartsWith("Tunel kurulamadi"))
                        {
                            tunnelUnavailable = true;
                            SetStatus(allowDirectFallback
                                ? "Tunel yok, dogrudan HTTP JSON: " + deviceHost + ":" + jsonHttpPort
                                : "Tunel yok ve dogrudan baglanti kapali");
                        }
                        else
                        {
                            tunnelUnavailable = false;
                            SetStatus(beginMsg);
                        }
                        Poll();
                    });
                }
                catch { }
            });
        }

        void ReloadPoints()
        {
            byId.Clear();
            byName.Clear();
            pointList.Clear();

            string p = Path.Combine(AppDir, "points.json");
            if (!File.Exists(p)) File.WriteAllText(p, DefaultPointsJson());
            try
            {
                Dictionary<string, object> root = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(p));
                object arr;
                if (root != null && root.TryGetValue("points", out arr))
                {
                    foreach (object o in AsList(arr))
                    {
                        Dictionary<string, object> d = (Dictionary<string, object>)o;
                        PointEntry pe = new PointEntry();
                        pe.Id = GetStr(d, "id");
                        pe.Name = GetStr(d, "name");
                        pe.JsonId = GetStr(d, "jsonId");
                        if (!string.IsNullOrEmpty(pe.Id)) byId[pe.Id] = pe;
                        if (!string.IsNullOrEmpty(pe.Name)) byName[pe.Name] = pe;
                        pointList.Add(pe);
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus("points.json okunamadi: " + ex.Message);
            }
        }

        static List<object> AsList(object o)
        {
            List<object> list = new List<object>();
            if (o == null) return list;
            System.Collections.ArrayList al = o as System.Collections.ArrayList;
            if (al != null) { foreach (object x in al) list.Add(x); return list; }
            object[] arr = o as object[];
            if (arr != null) { list.AddRange(arr); return list; }
            System.Collections.IEnumerable en = o as System.Collections.IEnumerable;
            if (en != null) { foreach (object x in en) list.Add(x); return list; }
            list.Add(o);
            return list;
        }

        static string GetStr(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : "";
        }

        static int GetInt(Dictionary<string, object> d, string k, int def)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            int r;
            return int.TryParse(Convert.ToString(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        static double GetDouble(Dictionary<string, object> d, string k, double def)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            double r;
            return double.TryParse(Convert.ToString(v), NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        string DefaultPointsJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"points\": [");
            sb.AppendLine("    { \"id\": \"1!6WHH65PYCY08FG\", \"name\": \"Supply Temp\",                  \"jsonId\": \"4-TMPVAL\" },");
            sb.AppendLine("    { \"id\": \"1!6WHH6EYR_THR34\",  \"name\": \"Fresh temp\",                 \"jsonId\": \"5-TMPVAL\" },");
            sb.AppendLine("    { \"id\": \"1!6WHH70BLN95ETS\",  \"name\": \"ShowRoom Temp\",              \"jsonId\": \"3-TMPVAL\" },");
            sb.AppendLine("    { \"id\": \"1!6WHH6KXE-_HWWO\",  \"name\": \"Temp Set ShowRoom\",          \"jsonId\": \"1-TMP_SET\" },");
            sb.AppendLine("    { \"id\": \"1!6WHH73LSF8XLAY\",  \"name\": \"Supply fan speed\",           \"jsonId\": \"POSITION\" },");
            sb.AppendLine("    { \"id\": \"1!6WHH6LAJPGACIU\",  \"name\": \"Retun fan speed\",            \"jsonId\": \"1-POSITION\" },");
            sb.AppendLine("    { \"id\": \"1!6WHH72UJVGELMY\",  \"name\": \"Supply Fan Power monitoring\", \"jsonId\": \"SAF_EBM_POWER\" },");
            sb.AppendLine("    { \"id\": \"1!6WHH6N05EEXPFK\",  \"name\": \"Return Fan Power monitoring\", \"jsonId\": \"EAF_EBM_POWER\" }");
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        void LoadDashboard(string path)
        {
            try
            {
                LoadDashboardFromText(File.ReadAllText(path), path);
            }
            catch (Exception ex)
            {
                MessageBox.Show("JSON yuklenemedi: " + ex.Message, "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void LoadDashboardFromText(string text, string path)
        {
            {
                object root = js.Deserialize<object>(text);
                dashes.Clear();
                selected = null;

                List<object> roots = AsList(root);
                foreach (object o in roots)
                {
                    Dictionary<string, object> dash = o as Dictionary<string, object>;
                    if (dash == null || !dash.ContainsKey("tiles")) continue;
                    DashDef def = new DashDef();
                    def.Raw = dash;
                    def.Title = LocalizedTitle(dash);
                    foreach (object to in AsList(dash["tiles"]))
                    {
                        Dictionary<string, object> td = to as Dictionary<string, object>;
                        if (td != null) def.Tiles.Add(ParseTile(td));
                    }
                    dashes.Add(def);
                }

                if (dashes.Count == 0)
                {
                    SetStatus("json pano tanimi icermiyor (kok tip: " + (root == null ? "null" : root.GetType().Name) + ")");
                    return;
                }

                currentRoot = roots;
                currentJsonPath = path;

                dashBox.Items.Clear();
                foreach (DashDef dd in dashes) dashBox.Items.Add(string.IsNullOrEmpty(dd.Title) ? "(adsiz pano)" : dd.Title);
                dashBox.SelectedIndex = 0;
                Text = "PLC Dashboard - " + Path.GetFileName(path);
                SetStatus(EndpointText() + "  |  " + Path.GetFileName(path));
                Poll();
            }
        }

        static string LocalizedTitle(Dictionary<string, object> dash)
        {
            object t;
            if (!dash.TryGetValue("title", out t)) return "";
            List<object> list = AsList(t);
            if (list.Count == 0) return "";
            foreach (string want in new string[] { "tr-TR", "en-US" })
                foreach (object o in list)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d != null && string.Equals(GetStr(d, "key"), want, StringComparison.OrdinalIgnoreCase))
                        return GetStr(d, "value");
                }
            Dictionary<string, object> first = list[0] as Dictionary<string, object>;
            return first != null ? GetStr(first, "value") : "";
        }

        Tile ParseTile(Dictionary<string, object> d)
        {
            Tile t = new Tile();
            t.Column = GetInt(d, "column", 0);
            t.Row = GetInt(d, "row", 0);
            t.Width = Math.Max(1, GetInt(d, "width", 2));
            t.Height = Math.Max(1, GetInt(d, "height", 1));
            t.Type = GetStr(d, "type");
            t.Title = GetStr(d, "title");
            t.Color = GetStr(d, "color");
            if (string.IsNullOrEmpty(t.Color)) t.Color = "petrol-background";

            object cfgObj;
            if (d.TryGetValue("tileConfig", out cfgObj))
            {
                Dictionary<string, object> c = cfgObj as Dictionary<string, object>;
                if (c != null)
                {
                    t.PointName = GetStr(c, "datapointName");
                    t.PointId = GetStr(c, "datapointId");
                    t.BarMax = GetDouble(c, "maxValue", 100);

                    object dp;
                    if (c.TryGetValue("datapointIds", out dp))
                    {
                        List<object> dpList = AsList(dp);
                        Dictionary<string, object> first = dpList.Count > 0
                            ? dpList[0] as Dictionary<string, object> : null;
                        if (first != null)
                        {
                            if (string.IsNullOrEmpty(t.PointId)) t.PointId = GetStr(first, "datapointId");
                            t.PointName = GetStr(first, "name");
                        }
                    }

                    object emb;
                    if (c.TryGetValue("embededWebpicture", out emb))
                    {
                        Dictionary<string, object> ew = emb as Dictionary<string, object>;
                        string data = ew != null ? GetStr(ew, "data") : "";
                        if (!string.IsNullOrEmpty(data))
                        {
                            try { t.Svg = SvgDoc.FromDataUrl(data); }
                            catch { t.Svg = null; }
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(t.PointName)) t.PointName = GetStr(d, "datapointName");
            if (string.IsNullOrEmpty(t.PointId)) t.PointId = GetStr(d, "datapointId");
            t.Raw = d;
            return t;
        }

        void ShowDash(int index)
        {
            gridPanel.Controls.Clear();
            selected = null;
            if (index < 0 || index >= dashes.Count) return;
            foreach (Tile t in dashes[index].Tiles)
            {
                TileView v = new TileView();
                v.Model = t;
                t.View = v;
                tips.SetToolTip(v, t.Type + " | col " + t.Column + " row " + t.Row);
                v.MouseDown += OnTileMouseDown;
                v.MouseMove += OnTileMouseMove;
                v.MouseUp += OnTileMouseUp;
                gridPanel.Controls.Add(v);
            }
            LayoutTiles();
            FillEditor();
        }

        void OnTileMouseDown(object sender, MouseEventArgs e)
        {
            TileView v = (TileView)sender;
            SelectTile(v.Model);
            if (!editBtn.Checked) return;
            dragging = true;
            PushUndo("move");
            dragStart = Cursor.Position;
            dragCol = v.Model.Column;
            dragRow = v.Model.Row;
        }

        void OnTileMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragging || selected == null) return;
            int dx = Cursor.Position.X - dragStart.X;
            int dy = Cursor.Position.Y - dragStart.Y;
            int nc = dragCol + (int)Math.Round(dx / (double)(cellW + GridGap), MidpointRounding.AwayFromZero);
            int nr = dragRow + (int)Math.Round(dy / (double)(cellH + GridGap), MidpointRounding.AwayFromZero);
            if (nc < 0) nc = 0;
            if (nr < 0) nr = 0;
            if (nc == selected.Column && nr == selected.Row) return;
            suppress = true;
            numCol.Value = Math.Min(numCol.Maximum, nc);
            numRow.Value = Math.Min(numRow.Maximum, nr);
            suppress = false;
            selected.Column = nc;
            selected.Row = nr;
            WriteTileGeometry(selected);
            LayoutTiles();
            selected.View.Invalidate();
        }

        void OnTileMouseUp(object sender, MouseEventArgs e)
        {
            dragging = false;
        }

        void SelectTile(Tile t)
        {
            if (selected != null) selected.View.Selected = false;
            selected = t;
            if (selected != null)
            {
                selected.View.Selected = true;
                selected.View.Invalidate();
            }
            FillEditor();
        }

        void FillEditor()
        {
            suppress = true;
            DashDef dd = CurrentDash();
            txtDashTitle.Text = dd == null ? "" : dd.Title;
            if (selected == null)
            {
                txtTileText.Text = "";
                cboType.Text = "";
                cboColor.Text = "";
                numCol.Value = 0; numRow.Value = 0; numW.Value = 1; numH.Value = 1;
                txtPointId.Text = ""; txtPointName.Text = ""; numBarMax.Value = 100;
                txtJsonId.Text = "";
                svgBtn.Enabled = false;
            }
            else
            {
                PointEntry pe = selected.Bound != null ? selected.Bound : Resolve(selected);
                txtTileText.Text = selected.Title;
                cboType.Text = selected.Type;
                cboColor.Text = selected.Color;
                numCol.Value = Math.Min(numCol.Maximum, Math.Max(numCol.Minimum, selected.Column));
                numRow.Value = Math.Min(numRow.Maximum, Math.Max(numRow.Minimum, selected.Row));
                numW.Value = Math.Min(numW.Maximum, Math.Max(numW.Minimum, selected.Width));
                numH.Value = Math.Min(numH.Maximum, Math.Max(numH.Minimum, selected.Height));
                txtPointId.Text = StripPrefix(selected.PointId);
                txtPointName.Text = selected.PointName;
                numBarMax.Value = Math.Min(numBarMax.Maximum, Math.Max(numBarMax.Minimum, (decimal)selected.BarMax));
                txtJsonId.Text = pe == null ? "" : pe.JsonId;
                svgBtn.Enabled = true;
            }
            suppress = false;
        }

        DashDef CurrentDash()
        {
            if (dashBox.SelectedIndex < 0 || dashBox.SelectedIndex >= dashes.Count) return null;
            return dashes[dashBox.SelectedIndex];
        }

        void LayoutTiles()
        {
            if (dashBox.SelectedIndex < 0 || dashes.Count == 0) return;
            List<Tile> list = dashes[dashBox.SelectedIndex].Tiles;
            if (list.Count == 0) return;

            int cols = 0, rows = 0;
            foreach (Tile t in list)
            {
                cols = Math.Max(cols, t.Column + t.Width);
                rows = Math.Max(rows, t.Row + t.Height);
            }
            int pad = GridPad, gap = GridGap;
            float cw = (gridPanel.ClientSize.Width - pad * 2 - gap * (cols - 1)) / (float)cols;
            float ch = (gridPanel.ClientSize.Height - pad * 2 - gap * (rows - 1)) / (float)rows;
            if (cw < 40) cw = 40;
            if (ch < 40) ch = 40;
            cellW = cw;
            cellH = ch;

            foreach (Tile t in list)
            {
                if (t.View == null) continue;
                t.View.Bounds = new Rectangle(
                    pad + (int)Math.Round(t.Column * (cw + gap)),
                    pad + (int)Math.Round(t.Row * (ch + gap)),
                    (int)Math.Round(t.Width * cw + (t.Width - 1) * gap),
                    (int)Math.Round(t.Height * ch + (t.Height - 1) * gap));
            }
        }

        static string StripPrefix(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            return id.StartsWith("1!", StringComparison.Ordinal) ? id.Substring(2) : id;
        }

        static string EnsurePrefix(string bare)
        {
            if (string.IsNullOrEmpty(bare)) return "";
            return bare.StartsWith("1!", StringComparison.Ordinal) ? bare : "1!" + bare;
        }

        Snap Snapshot()
        {
            if (currentRoot == null) return null;
            Snap s = new Snap();
            StringBuilder snap = new StringBuilder();
            WriteNode(snap, currentRoot, 0);
            s.Json = snap.ToString();
            string pp = Path.Combine(AppDir, "points.json");
            s.Points = File.Exists(pp) ? File.ReadAllText(pp) : "";
            s.DashIndex = dashBox.SelectedIndex;
            s.TileId = selected != null && selected.Raw != null ? GetStr(selected.Raw, "tileId") : null;
            return s;
        }

        void PushUndo(string field)
        {
            if (inUndo || currentRoot == null) return;
            bool burst = field == lastUndoField && (DateTime.Now - lastUndoAt).TotalMilliseconds < 900;
            lastUndoField = field;
            lastUndoAt = DateTime.Now;
            if (burst) return;

            Snap s = Snapshot();
            if (s == null) return;
            undoStack.Add(s);
            while (undoStack.Count > 50) undoStack.RemoveAt(0);
            redoStack.Clear();
            UpdateUndoButtons();
        }

        void DoUndo()
        {
            if (undoStack.Count == 0) { SetStatus("geri alinacak islem yok"); return; }
            Snap current = Snapshot();
            Snap s = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            if (current != null) redoStack.Add(current);
            Restore(s);
            SetStatus("geri alindi (" + undoStack.Count + " adim geri, " + redoStack.Count + " ileri)");
        }

        void DoRedo()
        {
            if (redoStack.Count == 0) { SetStatus("ileri alinacak islem yok"); return; }
            Snap current = Snapshot();
            Snap s = redoStack[redoStack.Count - 1];
            redoStack.RemoveAt(redoStack.Count - 1);
            if (current != null) undoStack.Add(current);
            Restore(s);
            SetStatus("ileri alindi (" + undoStack.Count + " adim geri, " + redoStack.Count + " ileri)");
        }

        void Restore(Snap s)
        {
            inUndo = true;
            lastUndoAt = DateTime.MinValue;
            try
            {
                try { File.WriteAllText(Path.Combine(AppDir, "points.json"), s.Points); } catch { }
                ReloadPoints();
                LoadDashboardFromText(s.Json, currentJsonPath);
                if (s.DashIndex >= 0 && s.DashIndex < dashBox.Items.Count) dashBox.SelectedIndex = s.DashIndex;

                DashDef dd = CurrentDash();
                if (dd != null && !string.IsNullOrEmpty(s.TileId))
                    foreach (Tile t in dd.Tiles)
                        if (string.Equals(GetStr(t.Raw, "tileId"), s.TileId, StringComparison.OrdinalIgnoreCase))
                        {
                            SelectTile(t);
                            break;
                        }
                if (editBtn.Checked && selected != null) FillEditor();
                UpdateUndoButtons();
                Poll();
            }
            finally { inUndo = false; }
        }

        void UpdateUndoButtons()
        {
            undoBtn.Enabled = undoStack.Count > 0;
            redoBtn.Enabled = redoStack.Count > 0;
            undoBtn.Text = "Geri Al" + (undoStack.Count > 0 ? " (" + undoStack.Count + ")" : "");
            redoBtn.Text = "Ileri Al" + (redoStack.Count > 0 ? " (" + redoStack.Count + ")" : "");
        }

        void ApplySelected()
        {
            if (suppress || selected == null) return;
            PushUndo("tile");
            selected.Title = txtTileText.Text;
            selected.Type = cboType.Text;
            if (string.IsNullOrEmpty(selected.Type)) selected.Type = "datapoint";
            selected.Color = cboColor.Text;
            if (string.IsNullOrEmpty(selected.Color)) selected.Color = "petrol-background";
            selected.Column = (int)numCol.Value;
            selected.Row = (int)numRow.Value;
            selected.Width = (int)numW.Value;
            selected.Height = (int)numH.Value;
            selected.PointId = EnsurePrefix(txtPointId.Text.Trim());
            selected.PointName = txtPointName.Text.Trim();
            selected.BarMax = (double)numBarMax.Value;

            WriteTile(selected);
            BindJsonPoint(selected, txtJsonId.Text.Trim());
            SavePoints();

            LayoutTiles();
            if (selected.View != null) selected.View.Invalidate();
            Poll();
        }

        static List<object> Mutable(Dictionary<string, object> d, string key)
        {
            object o;
            List<object> l = d.TryGetValue(key, out o) ? o as List<object> : null;
            if (l == null)
            {
                l = new List<object>();
                foreach (object x in AsList(o)) l.Add(x);
                d[key] = l;
            }
            return l;
        }

        void WriteTileGeometry(Tile t)
        {
            if (t.Raw == null) return;
            t.Raw["column"] = t.Column;
            t.Raw["row"] = t.Row;
            t.Raw["columnOrigin"] = t.Column;
            t.Raw["rowOrigin"] = t.Row;
            Dictionary<string, object> cfg = Cfg(t);
            cfg["column"] = t.Column;
            cfg["row"] = t.Row;
            List<object> positions = Mutable(cfg, "responsivePositions");
            if (positions.Count == 0) positions.Add(new Dictionary<string, object>());
            foreach (object o in positions)
            {
                Dictionary<string, object> rp = o as Dictionary<string, object>;
                if (rp == null) continue;
                rp["column"] = t.Column;
                rp["row"] = t.Row;
                rp["width"] = t.Width;
                rp["height"] = t.Height;
                if (!rp.ContainsKey("gridSize")) rp["gridSize"] = 7;
            }
        }

        static Dictionary<string, object> Cfg(Tile t)
        {
            object existing;
            Dictionary<string, object> cfg = (t.Raw != null && t.Raw.TryGetValue("tileConfig", out existing))
                ? existing as Dictionary<string, object> : null;
            if (cfg == null)
            {
                cfg = new Dictionary<string, object>();
                t.Raw["tileConfig"] = cfg;
            }
            return cfg;
        }

        void WriteTile(Tile t)
        {
            if (t.Raw == null) return;
            Dictionary<string, object> d = t.Raw;
            d["column"] = t.Column;
            d["row"] = t.Row;
            d["width"] = t.Width;
            d["height"] = t.Height;
            d["columnOrigin"] = t.Column;
            d["rowOrigin"] = t.Row;
            d["color"] = t.Color;
            d["type"] = t.Type;
            d["title"] = t.Title;
            d["datapointId"] = t.PointId;
            d["datapointName"] = t.PointName;

            Dictionary<string, object> cfg = Cfg(t);
            cfg["datapointId"] = t.PointId;
            cfg["datapointName"] = t.PointName;

            if (t.Type == "bargauge")
            {
                d["mainType"] = "chart";
                cfg["chartType"] = "bargauge";
                cfg["minValue"] = 0;
                cfg["maxValue"] = (int)t.BarMax;
                cfg["historySeconds"] = 3600;
                cfg["timeWindowValue"] = 1;
                cfg["timeWindowType"] = "1";
                cfg["horizontalAlignment"] = "right";
                cfg["displayLegend"] = true;

                List<object> series = Mutable(cfg, "datapointIds");
                if (series.Count == 0)
                {
                    Dictionary<string, object> s = new Dictionary<string, object>();
                    s["plantId"] = "[SystemPlant1]";
                    s["color"] = "#2882a0";
                    s["multiplier"] = "";
                    s["axis"] = "";
                    s["index"] = 0;
                    series.Add(s);
                }
                foreach (object o in series)
                {
                    Dictionary<string, object> s = o as Dictionary<string, object>;
                    if (s == null) continue;
                    s["datapointId"] = t.PointId;
                    s["name"] = t.PointName;
                }
            }
            else if (t.Type == "webpicture")
            {
                if (!cfg.ContainsKey("embededWebpicture"))
                    cfg["embededWebpicture"] = new Dictionary<string, object>();
            }

            WriteTileGeometry(t);
        }

        void ApplyDashTitle()
        {
            if (suppress) return;
            DashDef dd = CurrentDash();
            if (dd == null) return;
            PushUndo("dashtitle");
            dd.Title = txtDashTitle.Text;

            List<object> list = Mutable(dd.Raw, "title");
            bool touched = false;
            foreach (object o in list)
            {
                Dictionary<string, object> e = o as Dictionary<string, object>;
                if (e == null) continue;
                string key = GetStr(e, "key");
                if (string.Equals(key, "tr-TR", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "en-US", StringComparison.OrdinalIgnoreCase))
                {
                    e["value"] = dd.Title;
                    touched = true;
                }
            }
            if (!touched)
            {
                Dictionary<string, object> e = new Dictionary<string, object>();
                e["key"] = "tr-TR";
                e["language"] = "tr-TR";
                e["value"] = dd.Title;
                list.Add(e);
            }

            int i = dashBox.SelectedIndex;
            if (i >= 0)
            {
                suppress = true;
                dashBox.Items[i] = string.IsNullOrEmpty(dd.Title) ? "(adsiz pano)" : dd.Title;
                suppress = false;
            }
        }

        PointEntry BindJsonPoint(Tile t, string jsonId)
        {
            PointEntry pe = t.Bound;
            if (pe == null) pe = Resolve(t);
            if (pe == null)
            {
                if (string.IsNullOrEmpty(t.PointId) && string.IsNullOrEmpty(t.PointName))
                {
                    SetStatus("esleme icin once datapointId veya nokta adi gir");
                    return null;
                }
                pe = new PointEntry();
                pointList.Add(pe);
            }
            pe.JsonId = jsonId;
            if (!string.IsNullOrEmpty(t.PointName)) pe.Name = t.PointName;
            if (!string.IsNullOrEmpty(t.PointId)) pe.Id = t.PointId;
            if (!string.IsNullOrEmpty(pe.Id)) byId[pe.Id] = pe;
            if (!string.IsNullOrEmpty(pe.Name)) byName[pe.Name] = pe;
            t.Bound = pe;
            return pe;
        }

        void AddTile()
        {
            DashDef dd = CurrentDash();
            if (dd == null) { SetStatus("once bir dashboard json yukle"); return; }
            PushUndo("add");

            int rows = 0;
            foreach (Tile t in dd.Tiles) rows = Math.Max(rows, t.Row + t.Height);

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["column"] = 0;
            d["row"] = rows;
            d["color"] = "petrol-background";
            d["width"] = 2;
            d["height"] = 1;
            d["tileId"] = Guid.NewGuid().ToString();
            d["title"] = "Yeni kutu";
            d["type"] = "datapoint";
            d["isSelected"] = false;
            d["isDeactivated"] = false;
            d["href"] = "";

            Dictionary<string, object> cfg = new Dictionary<string, object>();
            cfg["value"] = "";
            cfg["plantId"] = "[SystemPlant1]";
            cfg["dashboardId"] = "";
            cfg["datapointName"] = "";
            cfg["multiplier"] = "";
            cfg["datapointId"] = "";
            cfg["row"] = rows;
            cfg["column"] = 0;
            System.Collections.ArrayList rps = new System.Collections.ArrayList();
            Dictionary<string, object> rp = new Dictionary<string, object>();
            rp["gridSize"] = 7;
            rp["row"] = rows;
            rp["column"] = 0;
            rp["height"] = 1;
            rp["width"] = 2;
            rps.Add(rp);
            cfg["responsivePositions"] = rps;
            d["tileConfig"] = cfg;
            d["rowOrigin"] = rows;
            d["columnOrigin"] = 0;
            d["isNew"] = true;

            List<object> rawTiles = Mutable(dd.Raw, "tiles");
            rawTiles.Add(d);
            dd.Tiles.Add(ParseTile(d));

            ShowDash(dashBox.SelectedIndex);
            SelectTile(dd.Tiles[dd.Tiles.Count - 1]);
            if (!editBtn.Checked) editBtn.Checked = true;
        }

        void DeleteSelected()
        {
            DashDef dd = CurrentDash();
            if (dd == null || selected == null) return;
            if (MessageBox.Show("Secilen kutu silinsin mi?", "Onay", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes) return;
            PushUndo("delete");
            dd.Tiles.Remove(selected);
            List<object> rawTiles = Mutable(dd.Raw, "tiles");
            if (selected.Raw != null) rawTiles.Remove(selected.Raw);
            ShowDash(dashBox.SelectedIndex);
        }

        void LoadSvgIntoSelected()
        {
            if (selected == null || selected.Raw == null) { SetStatus("once bir kutu sec"); return; }
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Resim veya SVG (*.svg;*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.svg;*.png;*.jpg;*.jpeg;*.gif;*.bmp|Tum dosyalar (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    PushUndo("image");
                    string dataUrl = BuildDataUrl(dlg.FileName);
                    Dictionary<string, object> cfg = Cfg(selected);
                    Dictionary<string, object> emb = cfg["embededWebpicture"] as Dictionary<string, object>;
                    if (emb == null)
                    {
                        emb = new Dictionary<string, object>();
                        cfg["embededWebpicture"] = emb;
                    }
                    emb["data"] = dataUrl;
                    emb["name"] = Path.GetFileName(dlg.FileName);
                    cfg["plantId"] = "";
                    cfg["webPictureFileId"] = "";
                    selected.Svg = SvgDoc.FromDataUrl(dataUrl);

                    if (selected.Type != "webpicture")
                    {
                        selected.Type = "webpicture";
                        cboType.Text = "webpicture";
                        WriteTile(selected);
                    }
                    FillEditor();
                    selected.View.Invalidate();
                    SetStatus("resim gomuldu (SVG kapsayici): " + emb["name"]
                        + (selected.Svg.Rasters.Count > 0 ? "  - raster, SVG icine sarildi" : "  - vektor"));
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Resim yuklenemedi: " + ex.Message);
                }
            }
        }

        static string BuildDataUrl(string file)
        {
            byte[] bytes = File.ReadAllBytes(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();

            if (ext == ".svg")
                return "data:image/svg+xml;base64," + Convert.ToBase64String(bytes);

            int w = 0, h = 0;
            using (Bitmap bmp = new Bitmap(file)) { w = bmp.Width; h = bmp.Height; }
            string mime = ext == ".png" ? "image/png"
                        : (ext == ".gif" ? "image/gif"
                        : (ext == ".bmp" ? "image/bmp" : "image/jpeg"));

            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
            sb.Append(" width=\"").Append(w).Append("\" height=\"").Append(h)
              .Append("\" viewBox=\"0 0 ").Append(w).Append(' ').Append(h).Append("\">\n");
            sb.Append("<image x=\"0\" y=\"0\" width=\"").Append(w).Append("\" height=\"").Append(h).Append("\"");
            sb.Append(" xlink:href=\"data:" + mime + ";base64," + Convert.ToBase64String(bytes) + "\"/>\n");
            sb.Append("</svg>\n");
            return "data:image/svg+xml;base64," +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public void StartInEditMode()
        {
            editBtn.Checked = true;
        }

        public string RunSelfTest(string input, string output)
        {
            StringBuilder rep = new StringBuilder();
            suppressPoll = true;
            LoadDashboard(input);
            DashDef dd = CurrentDash();
            if (dd == null) return "FAIL: dashboard yuklenemedi";
            int before = dd.Tiles.Count;
            rep.AppendLine("tiles before = " + before);

            AddTile();
            if (selected == null) return "FAIL: AddTile sonrasi secim yok";
            rep.AppendLine("tiles after add = " + dd.Tiles.Count);

            suppress = true;
            txtTileText.Text = "Self test kutusu";
            cboType.Text = "bargauge";
            cboColor.Text = "green-background";
            txtPointId.Text = "SELFTEST00001";
            txtPointName.Text = "Self test noktasi";
            numBarMax.Value = 200;
            txtJsonId.Text = "SELFTEST-JSON";
            numCol.Value = 4;
            numRow.Value = 3;
            numW.Value = 2;
            numH.Value = 1;
            suppress = false;
            ApplySelected();
            rep.AppendLine("prefix otomatik: PointId = " + selected.PointId);
            if (selected.PointId != "1!SELFTEST00001") rep.AppendLine("FAIL: 1! on eki yanlis");

            int afterAdd = dd.Tiles.Count;
            string keptId = GetStr(selected.Raw, "tileId");

            DoUndo();
            DashDef u1 = CurrentDash();
            Tile t1 = u1 != null && u1.Tiles.Count > 0 ? u1.Tiles[u1.Tiles.Count - 1] : null;
            rep.AppendLine("undo1: tiles = " + (u1 == null ? -1 : u1.Tiles.Count)
                + " son kutu title = " + (t1 == null ? "?" : t1.Title)
                + " tip = " + (t1 == null ? "?" : t1.Type));
            if (u1 == null || u1.Tiles.Count != afterAdd) rep.AppendLine("FAIL: undo1 kutu sayisini bozdu");
            if (t1 == null || t1.Title != "Yeni kutu") rep.AppendLine("FAIL: undo1 alan duzenlemesini geri almadi");

            DoUndo();
            DashDef u2 = CurrentDash();
            rep.AppendLine("undo2: tiles = " + (u2 == null ? -1 : u2.Tiles.Count) + " (beklenen " + before + ")");
            if (u2 == null || u2.Tiles.Count != before) rep.AppendLine("FAIL: undo2 eklemeyi geri almadi");

            DoRedo();
            DoRedo();
            DashDef dr = CurrentDash();
            PointEntry redone = selected == null ? null : Resolve(selected);
            rep.AppendLine("redo x2: tiles = " + (dr == null ? -1 : dr.Tiles.Count)
                + " secili tileId korundu = " + (selected != null && GetStr(selected.Raw, "tileId") == keptId)
                + " title = " + (selected == null ? "?" : selected.Title)
                + " PointId = " + (selected == null ? "?" : selected.PointId)
                + " jsonId = " + (redone == null ? "?" : redone.JsonId));
            if (dr == null || dr.Tiles.Count != afterAdd) rep.AppendLine("FAIL: redo kutuyu geri getirmedi");
            if (selected == null || selected.Title != "Self test kutusu") rep.AppendLine("FAIL: redo alan duzenlemesini geri getirmedi");
            if (redone == null || redone.JsonId != "SELFTEST-JSON") rep.AppendLine("FAIL: redo points.json baglamini geri getirmedi");
            dd = CurrentDash();

            string pngPath = Path.Combine(Path.GetDirectoryName(output), "_selftest.png");
            using (Bitmap bmp = new Bitmap(140, 90))
            {
                using (Graphics gg = Graphics.FromImage(bmp))
                {
                    gg.Clear(Color.White);
                    gg.FillRectangle(Brushes.RosyBrown, 10, 10, 120, 70);
                }
                bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
            }
            string wrapped = BuildDataUrl(pngPath);
            rep.AppendLine("png->svg sarmalama on ek = " + wrapped.Substring(0, Math.Min(30, wrapped.Length)));
            SvgDoc wrappedDoc = SvgDoc.FromDataUrl(wrapped);
            rep.AppendLine("wrapped rasters = " + wrappedDoc.Rasters.Count
                + " doc = " + wrappedDoc.Width + "x" + wrappedDoc.Height
                + (wrappedDoc.Rasters.Count > 0 ? " img = " + wrappedDoc.Rasters[0].W + "x" + wrappedDoc.Rasters[0].H : ""));
            if (wrappedDoc.Rasters.Count != 1) rep.AppendLine("FAIL: png svg kapsayici icinde cozulemedi");

            SvgDoc direct = SvgDoc.FromDataUrl("data:image/png;base64," +
                Convert.ToBase64String(File.ReadAllBytes(pngPath)));
            rep.AppendLine("dogrudan png data-url rasters = " + direct.Rasters.Count);

            WriteJsonTo(output);
            rep.AppendLine("output bytes = " + new FileInfo(output).Length);

            object root2 = js.Deserialize<object>(File.ReadAllText(output));
            List<object> dashList = AsList(root2);
            Dictionary<string, object> d2 = dashList.Count > 0 ? dashList[0] as Dictionary<string, object> : null;
            if (d2 == null) return "FAIL: cikti json parse edilemedi";

            List<object> tiles2 = AsList(d2["tiles"]);
            rep.AppendLine("reparse tiles = " + tiles2.Count);
            if (tiles2.Count != before + 1) rep.AppendLine("FAIL: kutu sayisi tutmuyor");

            Dictionary<string, object> first = tiles2[0] as Dictionary<string, object>;
            rep.AppendLine("tile0 datapointId = " + (first != null ? GetStr(first, "datapointId") : "?"));

            Dictionary<string, object> last = tiles2[tiles2.Count - 1] as Dictionary<string, object>;
            rep.AppendLine("last: type=" + GetStr(last, "type") + " color=" + GetStr(last, "color")
                + " title=" + GetStr(last, "title") + " col=" + GetInt(last, "column", -99)
                + " row=" + GetInt(last, "row", -99) + " w=" + GetInt(last, "width", -99)
                + " h=" + GetInt(last, "height", -99));
            Dictionary<string, object> cfg2 = last["tileConfig"] as Dictionary<string, object>;
            if (cfg2 == null) return "FAIL: tileConfig yok";
            rep.AppendLine("last cfg: datapointId=" + GetStr(cfg2, "datapointId")
                + " maxValue=" + GetInt(cfg2, "maxValue", -99)
                + " chartType=" + GetStr(cfg2, "chartType"));
            List<object> series = AsList(cfg2["datapointIds"]);
            if (series.Count > 0)
                rep.AppendLine("last series: " + GetStr((Dictionary<string, object>)series[0], "name"));
            List<object> rps = AsList(cfg2["responsivePositions"]);
            if (rps.Count > 0)
            {
                Dictionary<string, object> rp = (Dictionary<string, object>)rps[0];
                rep.AppendLine("last respPos: col=" + GetInt(rp, "column", -99) + " row=" + GetInt(rp, "row", -99));
            }
            rep.AppendLine("points.json icerigi:");
            foreach (PointEntry pe in pointList)
                rep.AppendLine("  " + pe.Id + " | " + pe.Name + " | json=" + pe.JsonId);

            string q = ClimatixJson.Query("6000", "4-TMP VAL");
            rep.AppendLine("query: " + q);
            if (q != "fn=Read&pin=6000&lng=0&us=2&id=4-TMP%20VAL") rep.AppendLine("FAIL: bosluk %20 olarak kodlanmadi");

            JsonResult a = ClimatixJson.Parse("[{\"id\":\"4-TMPVAL\",\"state\":0,\"value\":22.1,\"type\":8,\"rep\":1,\"descr\":\" C\",\"low\":-100,\"high\":100}]");
            JsonResult b = ClimatixJson.Parse("[]");
            JsonResult c = ClimatixJson.Parse("{\"JSONError\":8}");
            rep.AppendLine("parse degerli: present=" + a.Present + " value=" + a.Value.ToString(CultureInfo.InvariantCulture)
                + " unit=" + a.Unit + " state=" + a.State);
            rep.AppendLine("parse bos dizi: present=" + b.Present + " hata='" + b.Error + "'");
            rep.AppendLine("parse hata: present=" + c.Present + " hata='" + c.Error + "'");
            if (!a.Present || Math.Abs(a.Value - 22.1) > 0.0001) rep.AppendLine("FAIL: JSON degeri yanlis ayristirildi");
            if (b.Present || !string.IsNullOrEmpty(b.Error)) rep.AppendLine("FAIL: bos dizi 'kullanilmiyor' olmali");
            if (c.Error != "JSONError 8") rep.AppendLine("FAIL: JSONError ayristirilamadi");

            rep.AppendLine("SELFTEST DONE");
            return rep.ToString();
        }

        void SaveRevisedJson(bool overwrite)
        {
            if (currentRoot == null) { SetStatus("kaydedilecek pano yok"); return; }
            string target;
            if (overwrite)
            {
                target = currentJsonPath;
                if (MessageBox.Show("Mevcut dosyanin uzerine yazilacak: " + Path.GetFileName(target)
                    + "\nOnayliyor musunuz?", "Onay", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                    != DialogResult.Yes) return;
            }
            else
            {
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.Filter = "Dashboard JSON (*.json)|*.json";
                    dlg.InitialDirectory = AppDir;
                    string baseName = currentJsonPath == null ? "dashboard" : Path.GetFileNameWithoutExtension(currentJsonPath);
                    dlg.FileName = baseName + "-revize.json";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    target = dlg.FileName;
                }
            }

            try
            {
                WriteJsonTo(target);
                SetStatus("yazildi: " + Path.GetFileName(target));
                MessageBox.Show("Revize JSON hazir:\n" + target + "\n\nNokta/register eslemeleri points.json icinde kaldi (bulut semasinda adres alani yok).",
                    "Kaydedildi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Yazilamadi: " + ex.Message);
            }
        }

        // Cihazin kendi disari aktardigi bicim: 2 bosluk girinti, LF, UTF-8 (BOM'suz),
        // son satirda yeni satir yok, tek tirnak ve non-ASCII karakterler kaçisirilmaz.
        // Kaynak sirasi korunur: sozlukler eklenme sirasiyla dolasilir.
        public void WriteJsonTo(string target)
        {
            StringBuilder sb = new StringBuilder();
            WriteNode(sb, currentRoot, 0);
            File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false));
        }

        static void WriteNode(StringBuilder sb, object node, int depth)
        {
            if (node == null) { sb.Append("null"); return; }
            string s = node as string;
            if (s != null) { sb.Append('"').Append(JsonEsc(s)).Append('"'); return; }
            if (node is bool) { sb.Append((bool)node ? "true" : "false"); return; }
            if (node is double || node is float || node is decimal)
            {
                sb.Append(((IFormattable)node).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (node is byte || node is sbyte || node is short || node is ushort
                || node is int || node is uint || node is long || node is ulong)
            {
                sb.Append(((IFormattable)node).ToString(null, CultureInfo.InvariantCulture));
                return;
            }
            IDictionary<string, object> d = node as IDictionary<string, object>;
            if (d != null)
            {
                if (d.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (!first) sb.Append(',');
                    PadLine(sb, depth + 1);
                    sb.Append('"').Append(JsonEsc(kv.Key)).Append("\": ");
                    WriteNode(sb, kv.Value, depth + 1);
                    first = false;
                }
                PadLine(sb, depth);
                sb.Append('}');
                return;
            }
            System.Collections.IEnumerable items = node as System.Collections.IEnumerable;
            if (items != null)
            {
                System.Collections.ArrayList list = new System.Collections.ArrayList();
                foreach (object o in items) list.Add(o);
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    PadLine(sb, depth + 1);
                    WriteNode(sb, list[i], depth + 1);
                }
                PadLine(sb, depth);
                sb.Append(']');
                return;
            }
            sb.Append('"').Append(JsonEsc(Convert.ToString(node, CultureInfo.InvariantCulture))).Append('"');
        }

        static void PadLine(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            for (int i = 0; i < depth * 2; i++) sb.Append(' ');
        }

        void SavePoints()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"points\": [");
            for (int i = 0; i < pointList.Count; i++)
            {
                PointEntry pe = pointList[i];
                sb.Append("    { \"id\": \"" + JsonEsc(pe.Id) + "\", \"name\": \"" + JsonEsc(pe.Name)
                        + "\", \"jsonId\": \"" + JsonEsc(pe.JsonId) + "\" }");
                sb.AppendLine(i == pointList.Count - 1 ? "" : ",");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            try { File.WriteAllText(Path.Combine(AppDir, "points.json"), sb.ToString()); }
            catch (Exception ex) { SetStatus("points.json yazilamadi: " + ex.Message); }
        }

        static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 4);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        PointEntry Resolve(Tile t)
        {
            PointEntry pe;
            if (!string.IsNullOrEmpty(t.PointId) && byId.TryGetValue(t.PointId, out pe)) return pe;
            if (!string.IsNullOrEmpty(t.PointName) && byName.TryGetValue(t.PointName, out pe)) return pe;
            if (!string.IsNullOrEmpty(t.Title) && byName.TryGetValue(t.Title, out pe)) return pe;
            return null;
        }

        string[] JsonTarget()
        {
            if (tunnel.Running) return new string[] { tunnel.LocalHost, tunnel.LocalPort.ToString(CultureInfo.InvariantCulture) };
            return new string[] { deviceHost, jsonHttpPort.ToString(CultureInfo.InvariantCulture) };
        }

        string EndpointText()
        {
            if (tunnel.Running) return "tunel " + tunnel.LocalHost + ":" + tunnel.LocalPort.ToString(CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(deviceHost) ? "(cihaz IP girilmedi)" : deviceHost + ":" + jsonHttpPort.ToString(CultureInfo.InvariantCulture);
        }

        void Poll()
        {
            if (suppressPoll || polling || dashes.Count == 0 || dashBox.SelectedIndex < 0) return;
            List<Tile> list = dashes[dashBox.SelectedIndex].Tiles;

            List<string> ids = new List<string>();
            foreach (Tile t in list)
            {
                PointEntry pe = t.Bound != null ? t.Bound : Resolve(t);
                if (pe != null && !string.IsNullOrEmpty(pe.JsonId) && !ids.Contains(pe.JsonId)) ids.Add(pe.JsonId);
            }

            if (ids.Count == 0)
            {
                SetStatus("JSON nokta adi (jsonId) atanmamis - editor panelinden ver");
                return;
            }

            if (string.IsNullOrEmpty(deviceHost)) { SetStatus("once Baglanti ayarlari'ndan cihaz IP girin"); return; }

            // Tunel tek veri tasima yolumuz; Siemens kurulu degilse repodaki gibi dogrudan HTTP'e duser.
            if (!tunnel.Running && !tunnelUnavailable)
            {
                EnsureTunnelThenPoll();
                return;
            }

            string[] target = JsonTarget();
            string host = target[0];
            int port = int.Parse(target[1], CultureInfo.InvariantCulture);
            string pin = apiPin, user = apiUser, pass = apiPass;
            int timeout = jsonTimeoutMs;

            polling = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, JsonResult> found = new Dictionary<string, JsonResult>();
                string err = "";
                foreach (string id in ids)
                {
                    try
                    {
                        string payload = ClimatixJson.Get(host, port, user, pass, ClimatixJson.Query(pin, id), timeout);
                        JsonResult r = ClimatixJson.Parse(payload);
                        found[id] = r;
                        if (!string.IsNullOrEmpty(r.Error)) err = id + ": " + r.Error;
                    }
                    catch (Exception ex)
                    {
                        JsonResult r = new JsonResult();
                        r.Error = ex.Message;
                        found[id] = r;
                        err = id + ": " + ex.Message;
                    }
                }
                try { BeginInvoke((MethodInvoker)delegate { ApplyJsonValues(list, found, err, host + ":" + port); }); }
                catch { }
            });
        }

        void ApplyJsonValues(List<Tile> list, Dictionary<string, JsonResult> found, string err, string endpoint)
        {
            polling = false;
            int ok = 0;
            foreach (Tile t in list)
            {
                if (t.View == null) continue;
                PointEntry pe = t.Bound != null ? t.Bound : Resolve(t);
                JsonResult r = null;
                if (pe != null && !string.IsNullOrEmpty(pe.JsonId)) found.TryGetValue(pe.JsonId, out r);

                if (pe == null)
                {
                    t.View.Value = null;
                    t.View.Detail = "points.json icinde kayit yok";
                }
                else if (string.IsNullOrEmpty(pe.JsonId))
                {
                    t.View.Value = null;
                    t.View.UnitOf = "";
                    t.View.Detail = "JSON nokta adi (jsonId) atanmamis";
                }
                else if (r == null)
                {
                    t.View.Value = null;
                    t.View.UnitOf = "";
                    t.View.Detail = "okunamadi: yanit yok";
                }
                else if (!string.IsNullOrEmpty(r.Error))
                {
                    t.View.Value = null;
                    t.View.UnitOf = "";
                    t.View.Detail = pe.JsonId + " -> " + r.Error;
                }
                else if (!r.Present)
                {
                    t.View.Value = null;
                    t.View.UnitOf = "";
                    t.View.Detail = pe.JsonId + " -> [] (Climatix'ye gore kullanilmiyor)";
                }
                else
                {
                    t.View.Value = r.Value;
                    t.View.UnitOf = string.IsNullOrEmpty(r.Unit) ? t.View.UnitOf : r.Unit;
                    t.View.Detail = "JSON " + pe.JsonId + "  value=" + r.Value.ToString(CultureInfo.InvariantCulture)
                        + "  state=" + r.State + "  (ondalikli, cihaz API'sinden)";
                    ok++;
                }
                tips.SetToolTip(t.View, t.View.Detail);
                t.View.Invalidate();
            }

            if (string.IsNullOrEmpty(err))
                SetStatus(endpoint + " (HTTP JSON)   |   " + DateTime.Now.ToString("HH:mm:ss") + "   |   " + ok + " nokta okundu");
            else
                SetStatus(endpoint + " (HTTP JSON)   |   HATA: " + err);
        }

        void TestReadSelected()
        {
            string id = txtJsonId == null ? "" : txtJsonId.Text.Trim();
            if (string.IsNullOrEmpty(id)) { SetStatus("once JSON nokta id yaz (ornek: 4-TMPVAL)"); return; }

            string[] target = JsonTarget();
            string host = target[0];
            int port = int.Parse(target[1], CultureInfo.InvariantCulture);
            string pin = apiPin, user = apiUser, pass = apiPass;
            string url = "http://" + host + ":" + port + "/json.html?" + ClimatixJson.Query(pin, id);
            SetStatus("tek okuma: " + id);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                try
                {
                    string payload = ClimatixJson.Get(host, port, user, pass, ClimatixJson.Query(pin, id), 5000);
                    JsonResult r = ClimatixJson.Parse(payload);
                    text = "URL: GET " + url
                        + "\n\nHam yanit: " + payload
                        + "\n\nDeger: " + (r.Present ? r.Value.ToString(CultureInfo.InvariantCulture) : "-")
                        + "  Birim: " + r.Unit
                        + "\nstate: " + r.State
                        + (string.IsNullOrEmpty(r.Error) ? "" : "\nHata: " + r.Error);
                }
                catch (Exception ex) { text = "HATA: " + ex.Message + "\n\nURL: GET " + url; }
                finalText(text, id);
            });
        }

        void finalText(string text, string id)
        {
            try { BeginInvoke((MethodInvoker)delegate { MessageBox.Show(this, text, "JSON okuma: " + id, MessageBoxButtons.OK, MessageBoxIcon.Information); }); }
            catch { }
        }

        void ProbeApi()
        {
            if (string.IsNullOrEmpty(deviceHost) && !tunnel.Running)
            {
                SetStatus("once Baglanti ayarlari'ndan cihaz IP girin");
                return;
            }

            string[] target = JsonTarget();
            string host = target[0];
            int port = int.Parse(target[1], CultureInfo.InvariantCulture);
            string pin = apiPin, user = apiUser, pass = apiPass;
            SetStatus("HTTP JSON API deneniyor: " + host + ":" + port);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string msg;
                try
                {
                    string payload = ClimatixJson.Get(host, port, user, pass, ClimatixJson.PagesQuery(pin), 5000);
                    int n = payload.Split('{').Length - 1;
                    msg = "API yanit verdi: " + host + ":" + port + "  |  " + n + " sayfa (GetPages)";
                }
                catch (Exception ex)
                {
                    msg = "API yanit vermadi: " + ex.Message;
                }
                try { BeginInvoke((MethodInvoker)delegate { SetStatus(msg); }); } catch { }
            });
        }

        // Ayarlarda yazilan IP/port ile tunelden bagimsiz dogrudan HTTP JSON denemesi.
        void ProbeDirect(string host, int port, string pass, string user, string pin)
        {
            if (string.IsNullOrEmpty(host))
            {
                MessageBox.Show("Cihaz IP adresi bos.", "Baglanti", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Cursor wait = Cursors.WaitCursor;
            Cursor current = Cursor;
            Cursor = wait;
            string msg;
            try
            {
                string payload = ClimatixJson.Get(host, port, user, pass, ClimatixJson.PagesQuery(pin), 5000);
                int n = payload.Split('{').Length - 1;
                msg = "Cihaz yanit verdi: " + host + ":" + port + "  |  " + n + " sayfa (GetPages)";
            }
            catch (Exception ex)
            {
                msg = "Cihaz yanit vermadi: " + ex.Message;
            }
            Cursor = current;

            MessageBox.Show(msg, "Baglanti testi", MessageBoxButtons.OK,
                msg.StartsWith("Cihaz yanit verdi") ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            SetStatus(msg);
        }

        void SetStatus(string text)
        {
            if (statusLabel != null) statusLabel.Text = text;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MainForm form = new MainForm();

            foreach (string a in args)
            {
                if (!a.Equals("-selftest", StringComparison.OrdinalIgnoreCase)) continue;
                string input = args.Length > 1 ? args[1] : null;
                string output = args.Length > 2 ? args[2] : null;
                if (input == null || output == null)
                {
                    Console.Error.WriteLine("kullanim: PlcDashboard.exe -selftest <girdi.json> <cikti.json>");
                    Environment.Exit(2);
                }
                string report = form.RunSelfTest(input, output);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(output), "selftest-report.txt"), report);
                Environment.Exit(report.StartsWith("FAIL") ? 1 : 0);
            }

            string loaded = null;
            foreach (string a in args)
                if (File.Exists(a) && a.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { loaded = a; break; }
            if (loaded != null) form.LoadDashboardFromArgs(loaded);
            else loaded = form.LoadFirstFoundDashboard();

            bool editMode = false;
            foreach (string a in args)
                if (a.Equals("-edit", StringComparison.OrdinalIgnoreCase)) editMode = true;
            if (editMode) form.Shown += delegate { form.StartInEditMode(); };

            if (loaded == null)
                MessageBox.Show(form,
                    "Dashboard JSON bulunamadi.\n\n" +
                    "SystemDashboardConfiguration json dosyasini bu exe ile ayni klasore koyun\n" +
                    "veya dosyayi programin uzerine suruk-birak edin / Dosya > Dashboard JSON ac ile secin.",
                    "Dashboard yuklenmedi", MessageBoxButtons.OK, MessageBoxIcon.Information);

            Application.Run(form);
        }
    }
}
