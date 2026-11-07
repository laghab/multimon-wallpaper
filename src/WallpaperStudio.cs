using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WallpaperApp
{
    public class Layer
    {
        public string Name;
        public Image Img;
        public Bitmap Thumb;
        public float Ratio;
        public MemoryStream Stream;
        public RectangleF Src;   // visible part of the image (image pixels)
        public RectangleF Dest;  // where that part sits on the desktop canvas (desktop pixels)
        public int Monitor = -1; // -1 = free, otherwise clipped to that monitor
    }

    public class Canvas : Panel
    {
        public Canvas()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }
    }

    public static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        public static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            try { Application.SetCompatibleTextRenderingDefault(false); } catch { }
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        enum Drag { None, Move, Scale, Crop }

        List<Layer> layers = new List<Layer>();
        Layer sel;
        Screen[] screens;
        Rectangle virt;
        string sig;

        Canvas canvas;
        ListBox list;
        ComboBox monCombo;
        CheckBox cropChk;
        Label status;

        float vs = 1f;
        PointF vo;
        bool updating;

        Drag drag = Drag.None;
        int dragHandle;
        PointF dragStart;
        RectangleF dragDest, dragSrc;

        public MainForm()
        {
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Wallpaper Studio";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(1240, 740);
            StartPosition = FormStartPosition.CenterScreen;

            ReadMonitors();

            canvas = new Canvas();
            canvas.Dock = DockStyle.Fill;
            canvas.Paint += OnCanvasPaint;
            canvas.MouseDown += OnMouseDown;
            canvas.MouseMove += OnMouseMove;
            canvas.MouseUp += OnMouseUp;
            canvas.MouseWheel += OnWheel;
            canvas.MouseEnter += delegate { if (Form.ActiveForm == this) canvas.Focus(); };

            Panel right = new Panel();
            right.Dock = DockStyle.Right;
            right.Width = 260;
            right.Padding = new Padding(8);

            list = new ListBox();
            list.Dock = DockStyle.Fill;
            list.IntegralHeight = false;
            list.SelectedIndexChanged += delegate
            {
                if (updating) return;
                int i = list.SelectedIndex;
                Select(i < 0 ? null : layers[layers.Count - 1 - i]);
            };

            Label lblLayers = new Label();
            lblLayers.Text = "Layers (top first)";
            lblLayers.Dock = DockStyle.Top;
            lblLayers.Height = 24;
            lblLayers.TextAlign = ContentAlignment.BottomLeft;

            monCombo = new ComboBox();
            monCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            monCombo.Dock = DockStyle.Top;
            monCombo.SelectedIndexChanged += delegate
            {
                if (updating || sel == null) return;
                sel.Monitor = monCombo.SelectedIndex - 1;
                ApplyFill(sel, true);
                RefreshList();
                Changed();
            };

            Label lblMon = new Label();
            lblMon.Text = "Target monitor for selected image:";
            lblMon.Dock = DockStyle.Top;
            lblMon.Height = 24;
            lblMon.TextAlign = ContentAlignment.BottomLeft;

            right.Controls.Add(list);
            right.Controls.Add(lblLayers);
            right.Controls.Add(monCombo);
            right.Controls.Add(lblMon);

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.AutoSize = true;
            bar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bar.Padding = new Padding(6, 4, 6, 4);
            bar.WrapContents = true;

            bar.Controls.Add(Btn("Add images...", delegate { AddImages(); }));
            bar.Controls.Add(Btn("Remove", delegate { RemoveSel(); }));
            bar.Controls.Add(Btn("Fill", delegate { ApplyMode("fill"); }));
            bar.Controls.Add(Btn("Fit", delegate { ApplyMode("fit"); }));
            bar.Controls.Add(Btn("Stretch", delegate { ApplyMode("stretch"); }));
            bar.Controls.Add(Btn("Center", delegate { ApplyMode("center"); }));

            cropChk = new CheckBox();
            cropChk.Text = "Crop mode";
            cropChk.Appearance = Appearance.Button;
            cropChk.AutoSize = true;
            cropChk.Margin = new Padding(3);
            cropChk.Padding = new Padding(4, 2, 4, 2);
            cropChk.CheckedChanged += delegate { canvas.Invalidate(); UpdateStatus(); };
            bar.Controls.Add(cropChk);

            bar.Controls.Add(Btn("Reset crop", delegate { ResetCrop(); }));
            bar.Controls.Add(Btn("Bring forward", delegate { MoveLayer(1); }));
            bar.Controls.Add(Btn("Send back", delegate { MoveLayer(-1); }));
            bar.Controls.Add(Btn("Save PNG...", delegate { SavePng(); }));

            Button set = Btn("Set as wallpaper", delegate { SetWallpaper(); });
            set.FlatStyle = FlatStyle.Flat;
            set.BackColor = Color.FromArgb(0, 120, 215);
            set.ForeColor = Color.White;
            set.FlatAppearance.BorderSize = 0;
            bar.Controls.Add(set);

            status = new Label();
            status.Dock = DockStyle.Bottom;
            status.Height = 26;
            status.Padding = new Padding(8, 0, 0, 0);
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.BackColor = Color.FromArgb(235, 235, 238);

            Controls.Add(canvas);
            Controls.Add(right);
            Controls.Add(bar);
            Controls.Add(status);

            RebuildCombo();
            SyncUi();
            UpdateStatus();
        }

        Button Btn(string text, EventHandler h)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Margin = new Padding(3);
            b.Padding = new Padding(4, 1, 4, 1);
            b.Click += h;
            return b;
        }

        // ---------- monitors ----------

        void ReadMonitors()
        {
            screens = Screen.AllScreens;
            Rectangle r = screens[0].Bounds;
            sig = "";
            foreach (Screen s in screens)
            {
                r = Rectangle.Union(r, s.Bounds);
                sig += s.Bounds.ToString();
            }
            virt = r;
        }

        RectangleF MonRect(int i)
        {
            Rectangle b = screens[i].Bounds;
            return new RectangleF(b.X - virt.X, b.Y - virt.Y, b.Width, b.Height);
        }

        void RebuildCombo()
        {
            updating = true;
            monCombo.Items.Clear();
            monCombo.Items.Add("All monitors (span across)");
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle b = screens[i].Bounds;
                monCombo.Items.Add("Monitor " + (i + 1) + "  (" + b.Width + "x" + b.Height + ")" + (screens[i].Primary ? "  primary" : ""));
            }
            monCombo.SelectedIndex = 0;
            updating = false;
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (screens == null) return;
            string old = sig;
            ReadMonitors();
            if (old != sig)
            {
                foreach (Layer l in layers) if (l.Monitor >= screens.Length) l.Monitor = -1;
                RebuildCombo();
                RefreshList();
                SyncUi();
                canvas.Invalidate();
            }
        }

        // ---------- layers ----------

        Layer LoadLayer(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            MemoryStream ms = new MemoryStream(data);
            Image im = Image.FromStream(ms);
            try
            {
                if (Array.IndexOf(im.PropertyIdList, 0x0112) >= 0)
                {
                    int o = im.GetPropertyItem(0x0112).Value[0];
                    if (o == 3) im.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    else if (o == 6) im.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    else if (o == 8) im.RotateFlip(RotateFlipType.Rotate270FlipNone);
                }
            }
            catch { }

            float r = Math.Min(1f, 1600f / Math.Max(im.Width, im.Height));
            int tw = Math.Max(1, (int)(im.Width * r));
            int th = Math.Max(1, (int)(im.Height * r));
            Bitmap t = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(t))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(im, new Rectangle(0, 0, tw, th));
            }

            Layer l = new Layer();
            l.Name = Path.GetFileName(path);
            l.Img = im;
            l.Stream = ms;
            l.Thumb = t;
            l.Ratio = (float)tw / im.Width;
            l.Src = new RectangleF(0, 0, im.Width, im.Height);
            return l;
        }

        void AddImages()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Multiselect = true;
                d.Title = "Choose wallpaper images";
                d.Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Cursor = Cursors.WaitCursor;
                foreach (string p in d.FileNames)
                {
                    try
                    {
                        Layer l = LoadLayer(p);
                        l.Monitor = layers.Count % screens.Length;
                        ApplyFill(l, true);
                        layers.Add(l);
                        sel = l;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Could not open " + p + "\n\n" + ex.Message, Text);
                    }
                }
                Cursor = Cursors.Default;
                RefreshList();
                SyncUi();
                Changed();
            }
        }

        void ApplyFill(Layer l, bool cover)
        {
            RectangleF t = l.Monitor >= 0 ? MonRect(l.Monitor) : new RectangleF(0, 0, virt.Width, virt.Height);
            float iw = l.Img.Width, ih = l.Img.Height;
            l.Src = new RectangleF(0, 0, iw, ih);
            float sc = cover ? Math.Max(t.Width / iw, t.Height / ih) : Math.Min(t.Width / iw, t.Height / ih);
            float w = iw * sc, h = ih * sc;
            l.Dest = new RectangleF(t.X + (t.Width - w) / 2f, t.Y + (t.Height - h) / 2f, w, h);
        }

        void ApplyMode(string mode)
        {
            if (sel == null) return;
            if (mode == "fill") ApplyFill(sel, true);
            else if (mode == "fit") ApplyFill(sel, false);
            else
            {
                RectangleF t = sel.Monitor >= 0 ? MonRect(sel.Monitor) : new RectangleF(0, 0, virt.Width, virt.Height);
                float iw = sel.Img.Width, ih = sel.Img.Height;
                sel.Src = new RectangleF(0, 0, iw, ih);
                if (mode == "stretch") sel.Dest = t;
                else sel.Dest = new RectangleF(t.X + (t.Width - iw) / 2f, t.Y + (t.Height - ih) / 2f, iw, ih);
            }
            Changed();
        }

        void OnMouseUp(object sender, MouseEventArgs e)
        {
            Drag was = drag;
            drag = Drag.None;
            // dropping a monitor-assigned image onto another monitor moves it there
            if (was == Drag.Move && sel != null && sel.Monitor >= 0)
            {
                PointF v = ToV(e.Location);
                for (int i = 0; i < screens.Length; i++)
                {
                    if (i != sel.Monitor && MonRect(i).Contains(v))
                    {
                        sel.Monitor = i;
                        ApplyFill(sel, true);
                        RefreshList();
                        SyncUi();
                        Changed();
                        break;
                    }
                }
            }
        }

        void ResetCrop()
        {
            if (sel == null) return;
            float sc = sel.Dest.Width / sel.Src.Width;
            sel.Dest = new RectangleF(sel.Dest.X - sel.Src.X * sc, sel.Dest.Y - sel.Src.Y * sc, sel.Img.Width * sc, sel.Img.Height * sc);
            sel.Src = new RectangleF(0, 0, sel.Img.Width, sel.Img.Height);
            Changed();
        }

        void RemoveSel()
        {
            if (sel == null) return;
            layers.Remove(sel);
            sel.Img.Dispose();
            sel.Thumb.Dispose();
            sel.Stream.Dispose();
            sel = layers.Count > 0 ? layers[layers.Count - 1] : null;
            RefreshList();
            SyncUi();
            Changed();
        }

        void MoveLayer(int dir)
        {
            if (sel == null) return;
            int i = layers.IndexOf(sel);
            int j = i + dir;
            if (j < 0 || j >= layers.Count) return;
            layers.RemoveAt(i);
            layers.Insert(j, sel);
            RefreshList();
            SyncUi();
            Changed();
        }

        void Select(Layer l)
        {
            sel = l;
            SyncUi();
            Changed();
        }

        void RefreshList()
        {
            updating = true;
            list.Items.Clear();
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                Layer l = layers[i];
                list.Items.Add((i + 1) + ".  " + l.Name + (l.Monitor >= 0 ? "   [Monitor " + (l.Monitor + 1) + "]" : "   [free]"));
            }
            updating = false;
        }

        void SyncUi()
        {
            updating = true;
            list.SelectedIndex = sel == null ? -1 : layers.Count - 1 - layers.IndexOf(sel);
            monCombo.Enabled = sel != null;
            int ci = sel == null ? 0 : sel.Monitor + 1;
            monCombo.SelectedIndex = ci < monCombo.Items.Count ? ci : 0;
            updating = false;
        }

        void Changed()
        {
            canvas.Invalidate();
            UpdateStatus();
        }

        void UpdateStatus()
        {
            string s = "Canvas " + virt.Width + "x" + virt.Height + " px, " + screens.Length + " monitor(s).   ";
            if (sel != null)
                s += sel.Name + " - zoom " + (int)Math.Round(sel.Dest.Width / sel.Src.Width * 100f) + "%.   ";
            if (cropChk.Checked)
                s += "CROP MODE: drag the handles to crop.";
            else
                s += "Drag = move, mouse wheel = zoom, corner handles = resize, Alt = no snapping, Del = remove.";
            status.Text = s;
        }

        // ---------- drawing ----------

        void DrawOne(Graphics g, Layer l, float alpha, bool thumb)
        {
            Image im = thumb ? (Image)l.Thumb : l.Img;
            float k = thumb ? l.Ratio : 1f;
            RectangleF s = new RectangleF(l.Src.X * k, l.Src.Y * k, l.Src.Width * k, l.Src.Height * k);
            PointF[] pts = new PointF[] {
                new PointF(l.Dest.Left, l.Dest.Top),
                new PointF(l.Dest.Right, l.Dest.Top),
                new PointF(l.Dest.Left, l.Dest.Bottom) };
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                if (alpha < 1f)
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = alpha;
                    ia.SetColorMatrix(cm);
                }
                g.DrawImage(im, pts, s, GraphicsUnit.Pixel, ia);
            }
        }

        void DrawClipped(Graphics g, Layer l, bool thumb)
        {
            if (l.Monitor >= 0 && l.Monitor < screens.Length)
            {
                g.SetClip(MonRect(l.Monitor), CombineMode.Replace);
                DrawOne(g, l, 1f, thumb);
            }
            else
            {
                for (int i = 0; i < screens.Length; i++)
                {
                    g.SetClip(MonRect(i), CombineMode.Replace);
                    DrawOne(g, l, 1f, thumb);
                }
            }
            g.ResetClip();
        }

        Bitmap Render()
        {
            Bitmap bmp = new Bitmap(virt.Width, virt.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                foreach (Layer l in layers) DrawClipped(g, l, false);
            }
            return bmp;
        }

        void OnCanvasPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.FromArgb(32, 33, 38));
            float cw = canvas.ClientSize.Width, ch = canvas.ClientSize.Height;
            vs = Math.Max(0.0001f, Math.Min((cw - 60f) / virt.Width, (ch - 60f) / virt.Height));
            vo = new PointF((cw - virt.Width * vs) / 2f, (ch - virt.Height * vs) / 2f);

            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            g.TranslateTransform(vo.X, vo.Y);
            g.ScaleTransform(vs, vs);
            foreach (Layer l in layers) DrawOne(g, l, 0.25f, true);   // ghost of everything outside the monitors
            for (int i = 0; i < screens.Length; i++) g.FillRectangle(Brushes.Black, MonRect(i));
            foreach (Layer l in layers) DrawClipped(g, l, true);
            g.ResetTransform();

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Color.FromArgb(120, 170, 255), 2f))
            using (Font f = new Font("Segoe UI", 10f, FontStyle.Bold))
            {
                for (int i = 0; i < screens.Length; i++)
                {
                    RectangleF r = MonRect(i);
                    g.DrawRectangle(p, vo.X + r.X * vs, vo.Y + r.Y * vs, r.Width * vs, r.Height * vs);
                    string t = (i + 1) + "   " + (int)r.Width + "x" + (int)r.Height + (screens[i].Primary ? "  (primary)" : "");
                    g.DrawString(t, f, Brushes.Black, vo.X + r.X * vs + 9, vo.Y + r.Y * vs + 7);
                    g.DrawString(t, f, Brushes.White, vo.X + r.X * vs + 8, vo.Y + r.Y * vs + 6);
                }
            }

            if (layers.Count == 0)
            {
                using (Font f = new Font("Segoe UI", 12f))
                {
                    string m = "Click \"Add images...\" to begin";
                    SizeF sz = g.MeasureString(m, f);
                    g.DrawString(m, f, Brushes.Gainsboro, (cw - sz.Width) / 2f, vo.Y + virt.Height * vs + 8);
                }
            }

            if (sel != null)
            {
                Color c = cropChk.Checked ? Color.Orange : Color.Gold;
                using (Pen p = new Pen(c, 1.5f))
                {
                    p.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(p, vo.X + sel.Dest.X * vs, vo.Y + sel.Dest.Y * vs, sel.Dest.Width * vs, sel.Dest.Height * vs);
                }
                PointF[] hp = HandlePts(sel);
                using (Pen hb = new Pen(c, 2f))
                {
                    for (int i = 0; i < 8; i++)
                    {
                        if (!cropChk.Checked && i % 2 == 1) continue;
                        g.FillRectangle(Brushes.White, hp[i].X - 5, hp[i].Y - 5, 10, 10);
                        g.DrawRectangle(hb, hp[i].X - 5, hp[i].Y - 5, 10, 10);
                    }
                }
            }
        }

        // ---------- mouse ----------

        PointF ToV(Point p)
        {
            return new PointF((p.X - vo.X) / vs, (p.Y - vo.Y) / vs);
        }

        PointF[] HandlePts(Layer l)
        {
            float x0 = vo.X + l.Dest.Left * vs, x1 = vo.X + l.Dest.Right * vs, xm = (x0 + x1) / 2f;
            float y0 = vo.Y + l.Dest.Top * vs, y1 = vo.Y + l.Dest.Bottom * vs, ym = (y0 + y1) / 2f;
            return new PointF[] {
                new PointF(x0, y0), new PointF(xm, y0), new PointF(x1, y0), new PointF(x1, ym),
                new PointF(x1, y1), new PointF(xm, y1), new PointF(x0, y1), new PointF(x0, ym) };
        }

        int HitHandle(Point p)
        {
            if (sel == null) return -1;
            PointF[] hp = HandlePts(sel);
            for (int i = 0; i < 8; i++)
            {
                if (!cropChk.Checked && i % 2 == 1) continue;
                if (Math.Abs(p.X - hp[i].X) <= 8 && Math.Abs(p.Y - hp[i].Y) <= 8) return i;
            }
            return -1;
        }

        bool Hit(Layer l, PointF v)
        {
            if (!l.Dest.Contains(v)) return false;
            if (l.Monitor >= 0 && l.Monitor < screens.Length) return MonRect(l.Monitor).Contains(v);
            for (int i = 0; i < screens.Length; i++) if (MonRect(i).Contains(v)) return true;
            return false;
        }

        static Cursor HandleCursor(int h)
        {
            if (h == 0 || h == 4) return Cursors.SizeNWSE;
            if (h == 2 || h == 6) return Cursors.SizeNESW;
            if (h == 1 || h == 5) return Cursors.SizeNS;
            return Cursors.SizeWE;
        }

        void OnMouseDown(object sender, MouseEventArgs e)
        {
            canvas.Focus();
            if (e.Button != MouseButtons.Left) return;
            PointF v = ToV(e.Location);
            int h = HitHandle(e.Location);
            if (h >= 0)
            {
                drag = cropChk.Checked ? Drag.Crop : Drag.Scale;
                dragHandle = h;
            }
            else
            {
                Layer hit = null;
                for (int i = layers.Count - 1; i >= 0; i--)
                    if (Hit(layers[i], v)) { hit = layers[i]; break; }
                if (hit == null) { Select(null); return; }
                if (hit != sel) Select(hit);
                drag = Drag.Move;
            }
            dragStart = v;
            dragDest = sel.Dest;
            dragSrc = sel.Src;
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (drag == Drag.None)
            {
                int h = HitHandle(e.Location);
                if (h >= 0) canvas.Cursor = HandleCursor(h);
                else
                {
                    PointF v = ToV(e.Location);
                    bool over = false;
                    for (int i = layers.Count - 1; i >= 0; i--) if (Hit(layers[i], v)) { over = true; break; }
                    canvas.Cursor = over ? Cursors.SizeAll : Cursors.Default;
                }
                return;
            }

            PointF m = ToV(e.Location);
            if (drag == Drag.Move) DoMove(m);
            else if (drag == Drag.Scale) DoScale(m);
            else DoCrop(m);
            Changed();
        }

        void DoMove(PointF v)
        {
            RectangleF d = dragDest;
            d.X += v.X - dragStart.X;
            d.Y += v.Y - dragStart.Y;
            if ((ModifierKeys & Keys.Alt) == 0)
            {
                float th = 10f / vs;
                float bestX = th, adjX = 0, bestY = th, adjY = 0;
                List<float> xs = new List<float>(); List<float> ys = new List<float>();
                xs.Add(0); xs.Add(virt.Width); ys.Add(0); ys.Add(virt.Height);
                for (int i = 0; i < screens.Length; i++)
                {
                    RectangleF r = MonRect(i);
                    xs.Add(r.Left); xs.Add(r.Right); ys.Add(r.Top); ys.Add(r.Bottom);
                }
                foreach (float t in xs)
                {
                    float a = t - d.Left, b = t - d.Right;
                    if (Math.Abs(a) < bestX) { bestX = Math.Abs(a); adjX = a; }
                    if (Math.Abs(b) < bestX) { bestX = Math.Abs(b); adjX = b; }
                }
                foreach (float t in ys)
                {
                    float a = t - d.Top, b = t - d.Bottom;
                    if (Math.Abs(a) < bestY) { bestY = Math.Abs(a); adjY = a; }
                    if (Math.Abs(b) < bestY) { bestY = Math.Abs(b); adjY = b; }
                }
                d.X += adjX;
                d.Y += adjY;
            }
            sel.Dest = d;
        }

        void DoScale(PointF v)
        {
            RectangleF d = dragDest;
            float ratio = d.Height / d.Width;
            float ax, ay, w;
            switch (dragHandle)
            {
                case 0: ax = d.Right; ay = d.Bottom; w = ax - v.X; break;
                case 2: ax = d.Left; ay = d.Bottom; w = v.X - ax; break;
                case 4: ax = d.Left; ay = d.Top; w = v.X - ax; break;
                default: ax = d.Right; ay = d.Top; w = ax - v.X; break;
            }
            w = Math.Max(30f, w);
            float h = w * ratio;
            float x = (dragHandle == 0 || dragHandle == 6) ? ax - w : ax;
            float y = (dragHandle == 0 || dragHandle == 2) ? ay - h : ay;
            sel.Dest = new RectangleF(x, y, w, h);
        }

        static float Clamp(float v, float lo, float hi)
        {
            if (hi < lo) hi = lo;
            return Math.Max(lo, Math.Min(hi, v));
        }

        void DoCrop(PointF v)
        {
            float sc = dragDest.Width / dragSrc.Width;
            float dx = (v.X - dragStart.X) / sc, dy = (v.Y - dragStart.Y) / sc;
            RectangleF s = dragSrc;
            float iw = sel.Img.Width, ih = sel.Img.Height, min = 8f;
            int h = dragHandle;
            float L = s.Left, R = s.Right, T = s.Top, B = s.Bottom;
            if (h == 0 || h == 6 || h == 7) L = Clamp(s.Left + dx, 0, R - min);
            if (h == 2 || h == 3 || h == 4) R = Clamp(s.Right + dx, L + min, iw);
            if (h == 0 || h == 1 || h == 2) T = Clamp(s.Top + dy, 0, B - min);
            if (h == 4 || h == 5 || h == 6) B = Clamp(s.Bottom + dy, T + min, ih);
            sel.Src = new RectangleF(L, T, R - L, B - T);
            sel.Dest = new RectangleF(dragDest.X + (L - s.Left) * sc, dragDest.Y + (T - s.Top) * sc, (R - L) * sc, (B - T) * sc);
        }

        void OnWheel(object sender, MouseEventArgs e)
        {
            if (sel == null) return;
            PointF v = ToV(e.Location);
            RectangleF d = sel.Dest;
            if (!d.Contains(v)) v = new PointF(d.X + d.Width / 2f, d.Y + d.Height / 2f);
            float f = (float)Math.Pow(1.1, e.Delta / 120.0);
            float w = Math.Max(30f, d.Width * f);
            f = w / d.Width;
            sel.Dest = new RectangleF(v.X - (v.X - d.X) * f, v.Y - (v.Y - d.Y) * f, w, d.Height * f);
            Changed();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Delete && sel != null)
            {
                RemoveSel();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------- output ----------

        void SavePng()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "PNG image|*.png";
                d.FileName = "wallpaper.png";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                Cursor = Cursors.WaitCursor;
                using (Bitmap b = Render()) b.Save(d.FileName, ImageFormat.Png);
                Cursor = Cursors.Default;
            }
        }

        void SetWallpaper()
        {
            if (layers.Count == 0)
            {
                MessageBox.Show(this, "Add at least one image first.", Text);
                return;
            }
            try
            {
                Cursor = Cursors.WaitCursor;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiMonitorWallpaper");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "wallpaper_" + DateTime.Now.Ticks + ".png");
                using (Bitmap b = Render()) b.Save(path, ImageFormat.Png);

                RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
                k.SetValue("WallpaperStyle", "22");   // Span
                k.SetValue("TileWallpaper", "0");
                k.Close();
                if (!SystemParametersInfo(20, 0, path, 3))
                    throw new Exception("Windows rejected the wallpaper (error " + Marshal.GetLastWin32Error() + ").");

                foreach (string old in Directory.GetFiles(dir, "wallpaper_*.png"))
                {
                    if (old != path) { try { File.Delete(old); } catch { } }
                }
                Cursor = Cursors.Default;
                status.Text = "Wallpaper applied (" + virt.Width + "x" + virt.Height + ").";
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, ex.Message, Text);
            }
        }
    }
}