using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace TrayDisplays
{
    // Panneau sans bordure affiché près de la barre des tâches : dessine le layout des écrans
    // (comme les Paramètres d'affichage) et permet de les réorganiser (glisser), de couper/réactiver
    // un écran (double-clic) ou de définir l'écran principal (clic droit).
    internal sealed class PopupForm : Form
    {
        private static readonly Color Background = Color.FromArgb(32, 36, 44);
        private static readonly Color Border = Color.FromArgb(70, 80, 96);
        private static readonly Color ScreenFill = Color.FromArgb(52, 72, 96);
        private static readonly Color ScreenHover = Color.FromArgb(68, 94, 124);
        private static readonly Color ScreenBorder = Color.FromArgb(104, 140, 176);
        private static readonly Color OffFill = Color.FromArgb(26, 29, 35);
        private static readonly Color OffBorder = Color.FromArgb(96, 106, 120);
        private static readonly Color TextClr = Color.FromArgb(236, 242, 248);
        private static readonly Color DimText = Color.FromArgb(150, 162, 178);
        private static readonly Color PrimaryAccent = Color.FromArgb(236, 170, 90);
        private static readonly Color ButtonFill = Color.FromArgb(50, 58, 72);
        private static readonly Color ButtonHover = Color.FromArgb(66, 78, 96);
        private static readonly Color ButtonText = Color.FromArgb(226, 236, 246);

        private readonly DisplayState _state;
        private readonly float _k;                 // facteur DPI (1.0 = 96 dpi)
        private readonly Font _titleFont;
        private readonly Font _smallFont;
        private readonly Font _numberFont;
        private readonly Font _boldFont;

        private Rectangle _screenArea;
        private Rectangle _areaBox;                // place réservée aux écrans, fixe pendant un glisser
        private Rectangle _desktop;
        private Rectangle _statusRect;
        private float _scale;
        private List<HitZone> _zones = new List<HitZone>();
        private string _status = null;
        private DateTime _statusUntil = DateTime.MinValue;
        private HitZone _hover = null;
        private int _lastHideTick = 0;

        // Glisser d'un écran : zone pressée, point de départ, échelle au départ et position
        // (aimantée, en coordonnées du bureau) où l'écran serait déposé.
        private HitZone _pressed = null;
        private Point _pressPoint;
        private float _pressScale;
        private bool _dragging = false;
        private Point _dropLocation;

        public PopupForm(DisplayState state)
        {
            _state = state;
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.DoubleBuffered = true;
            this.BackColor = Background;

            using (Graphics g = this.CreateGraphics())
            {
                _k = g.DpiY / 96.0f;
            }
            // Tailles en points : GDI+ applique déjà le DPI, seules les distances en pixels sont mises à l'échelle.
            this.Font = new Font("Segoe UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);
            _boldFont = new Font("Segoe UI", 9.0f, FontStyle.Bold, GraphicsUnit.Point);
            _titleFont = new Font("Segoe UI Semibold", 11.0f, FontStyle.Regular, GraphicsUnit.Point);
            _smallFont = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            _numberFont = new Font("Segoe UI Semibold", 15.0f, FontStyle.Regular, GraphicsUnit.Point);
            RebuildLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _boldFont.Dispose();
                _titleFont.Dispose();
                _smallFont.Dispose();
                _numberFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private int S(int pixels)
        {
            return (int)Math.Round(pixels * _k);
        }

        public void UpdateLayout()
        {
            if (_pressed != null)
            {
                return; // pas de relecture pendant un glisser : la disposition est reconstruite au dépôt
            }
            RebuildLayout();
            this.Invalidate();
        }

        public void SetStatus(string message)
        {
            _status = message;
            _statusUntil = DateTime.Now.AddSeconds(8);
            this.Invalidate();
        }

        public void ToggleVisibility()
        {
            if (this.Visible)
            {
                this.Hide();
                return;
            }
            // Cliquer sur l'icône alors que le panneau est ouvert le désactive (donc le ferme)
            // juste avant ce clic : on ne le rouvre pas dans la foulée.
            if (Environment.TickCount - _lastHideTick < 300)
            {
                return;
            }
            RebuildLayout();
            PositionNearTray();
            this.Show();
            this.Activate();
            this.Invalidate();
        }

        // Le panneau se ferme dès qu'il perd l'activation (clic ailleurs, comme les menus).
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            CancelDrag();
            _lastHideTick = Environment.TickCount;
            this.Hide();
        }

        // Enregistre le rendu dans un PNG (option --screenshot, sans interaction).
        public void SaveSnapshot(string path)
        {
            RebuildLayout();
            IntPtr force = this.Handle;
            using (Bitmap bmp = new Bitmap(this.Width, this.Height))
            {
                this.DrawToBitmap(bmp, new Rectangle(0, 0, this.Width, this.Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        private void RebuildLayout()
        {
            _desktop = _state.DesktopBounds;
            if (_desktop.Width <= 0 || _desktop.Height <= 0)
            {
                _desktop = new Rectangle(0, 0, 1920, 1080);
            }

            int pad = S(16);
            int headerHeight = pad + _titleFont.Height + S(2) + _smallFont.Height + S(12);
            int buttonHeight = S(28);
            int statusHeight = _smallFont.Height * 2 + S(2);

            _scale = Math.Min((float)S(340) / (float)_desktop.Width,
                              (float)S(170) / (float)_desktop.Height);
            int areaWidth = (int)Math.Round(_desktop.Width * _scale);
            int areaHeight = (int)Math.Round(_desktop.Height * _scale);

            int width = Math.Max(S(300), Math.Min(S(560), areaWidth + 2 * pad));
            int buttonTop = headerHeight + areaHeight + S(14);
            int height = buttonTop + buttonHeight + S(10) + statusHeight + pad - S(4);

            Size wanted = new Size(width, height);
            if (this.ClientSize != wanted)
            {
                // Le panneau reste ancré par son coin bas-droit quand sa taille change.
                int right = this.Right;
                int bottom = this.Bottom;
                this.ClientSize = wanted;
                if (this.Visible)
                {
                    this.Location = new Point(right - this.Width, bottom - this.Height);
                }
            }

            _screenArea = new Rectangle((width - areaWidth) / 2, headerHeight, areaWidth, areaHeight);
            _areaBox = _screenArea;
            _statusRect = new Rectangle(pad, buttonTop + buttonHeight + S(10), width - 2 * pad, statusHeight);

            BuildRoundedRegion(width, height);
            BuildHitZones(pad, buttonTop, buttonHeight);
        }

        private GraphicsPath RoundedPath(RectangleF r, float radius)
        {
            float d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void BuildRoundedRegion(int width, int height)
        {
            using (GraphicsPath path = RoundedPath(new RectangleF(0, 0, width, height), S(8)))
            {
                this.Region = new Region(path);
            }
        }

        // Positionne le panneau en bas à droite, juste au-dessus de la barre des tâches.
        private void PositionNearTray()
        {
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            WinRect rect;
            int margin = S(12);
            int right = Native.GetSystemMetrics(0) - margin;
            int bottom = Native.GetSystemMetrics(1) - margin;
            if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out rect))
            {
                right = rect.Right - margin;
                bottom = rect.Top - margin;
            }

            int left = Math.Max(4, right - this.Width);
            int top = Math.Max(4, bottom - this.Height);
            this.Location = new Point(left, top);
        }

        private void BuildHitZones(int pad, int buttonTop, int buttonHeight)
        {
            List<HitZone> zones = new List<HitZone>();

            foreach (Screen s in _state.Screens)
            {
                Rectangle r = ScreenToClient(s.Bounds);
                if (r.Width < 4 || r.Height < 4) continue;
                zones.Add(new HitZone(r, s, HitKind.Screen));
            }

            // Boutons alignés à droite : [Réactiver tout] [Fermer]
            int x = this.ClientSize.Width - pad;
            int closeWidth = TextWidth("Fermer") + S(24);
            x -= closeWidth;
            zones.Add(new HitZone(new Rectangle(x, buttonTop, closeWidth, buttonHeight), null, HitKind.Close));
            if (_state.HasDisabled)
            {
                int w = TextWidth("Réactiver tout") + S(24);
                x -= w + S(8);
                zones.Add(new HitZone(new Rectangle(x, buttonTop, w, buttonHeight), null, HitKind.Restore));
            }

            _zones = zones;
        }

        // Rectangle d'un écran dans le panneau ; les écrans voisins gardent un petit écart visible.
        private Rectangle ScreenToClient(Rectangle bounds)
        {
            int gap = Math.Max(1, S(2));
            int x = _screenArea.X + (int)Math.Round((bounds.Left - _desktop.Left) * _scale);
            int y = _screenArea.Y + (int)Math.Round((bounds.Top - _desktop.Top) * _scale);
            int w = (int)Math.Round(bounds.Width * _scale);
            int h = (int)Math.Round(bounds.Height * _scale);
            return new Rectangle(x + gap / 2, y + gap / 2, Math.Max(2, w - gap), Math.Max(2, h - gap));
        }

        private int TextWidth(string text)
        {
            return TextRenderer.MeasureText(text, this.Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int width = this.ClientSize.Width;
            int height = this.ClientSize.Height;

            using (SolidBrush bg = new SolidBrush(Background))
            {
                g.FillRectangle(bg, 0, 0, width, height);
            }
            using (GraphicsPath outline = RoundedPath(new RectangleF(0.5f, 0.5f, width - 1, height - 1), S(8)))
            using (Pen pen = new Pen(Border))
            {
                g.DrawPath(pen, outline);
            }

            DrawHeader(g);
            DrawScreens(g);
            DrawButtons(g);
            DrawStatus(g);
        }

        private static StringFormat EllipsisFormat()
        {
            StringFormat f = new StringFormat(StringFormat.GenericTypographic);
            f.Trimming = StringTrimming.EllipsisCharacter;
            f.FormatFlags |= StringFormatFlags.NoWrap;
            return f;
        }

        private void DrawHeader(Graphics g)
        {
            int pad = S(16);
            int enabled = _state.EnabledCount;
            string title = enabled == 0
                ? "Aucun écran détecté"
                : string.Format("{0} écran{1} connecté{1}", enabled, enabled > 1 ? "s" : "");

            string sub = "";
            Screen primary = _state.PrimaryScreen;
            if (primary != null)
            {
                sub = "Principal : " + primary.Title;
            }
            if (_state.DisabledCount > 0)
            {
                sub += (sub.Length > 0 ? " · " : "") +
                    string.Format("{0} coupé{1}", _state.DisabledCount, _state.DisabledCount > 1 ? "s" : "");
            }

            float w = this.ClientSize.Width - 2 * pad;
            using (StringFormat f = EllipsisFormat())
            using (SolidBrush titleBrush = new SolidBrush(TextClr))
            using (SolidBrush subBrush = new SolidBrush(DimText))
            {
                g.DrawString(title, _titleFont, titleBrush, new RectangleF(pad, pad, w, _titleFont.Height), f);
                g.DrawString(sub, _smallFont, subBrush,
                    new RectangleF(pad, pad + _titleFont.Height + S(2), w, _smallFont.Height), f);
            }
        }

        // Les rectangles sont recalculés à partir des bornes : pendant un glisser, la vue est recadrée
        // (UpdateDragView) et l'écran déplacé est dessiné en dernier, à sa position de dépôt.
        private void DrawScreens(Graphics g)
        {
            foreach (HitZone zone in _zones)
            {
                if (zone.Kind != HitKind.Screen || (_dragging && zone == _pressed)) continue;
                DrawScreen(g, zone.Screen, ScreenToClient(zone.Screen.Bounds), _hover == zone);
            }
            if (_dragging)
            {
                Screen s = _pressed.Screen;
                DrawScreen(g, s, ScreenToClient(new Rectangle(_dropLocation, s.Bounds.Size)), true);
            }
        }

        private void DrawScreen(Graphics g, Screen s, Rectangle r, bool hovered)
        {
            bool primary = s.IsPrimary && s.Enabled;

            Color fill = s.Enabled ? (hovered ? ScreenHover : ScreenFill) : (hovered ? ScreenFill : OffFill);
            Color outline = primary ? PrimaryAccent : (s.Enabled ? ScreenBorder : OffBorder);

            using (GraphicsPath path = RoundedPath(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(4)))
            {
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(outline, primary ? 2.0f : 1.0f))
                {
                    if (!s.Enabled)
                    {
                        pen.DashStyle = DashStyle.Dash;
                    }
                    g.DrawPath(pen, path);
                }
            }

            DrawScreenLabel(g, r, s, primary);
        }

        private void DrawScreenLabel(Graphics g, Rectangle r, Screen s, bool primary)
        {
            string number = s.Number > 0 ? s.Number.ToString() : "–";
            Color main = s.Enabled ? TextClr : DimText;
            int inset = S(8);
            float textWidth = r.Width - 2 * inset;

            using (SolidBrush mainBrush = new SolidBrush(main))
            using (SolidBrush dimBrush = new SolidBrush(DimText))
            using (SolidBrush accent = new SolidBrush(PrimaryAccent))
            using (StringFormat f = EllipsisFormat())
            {
                bool roomForDetails = r.Width >= S(90) && r.Height >= S(58);
                if (!roomForDetails)
                {
                    // Trop petit : seul le numéro, centré.
                    using (StringFormat center = new StringFormat())
                    {
                        center.Alignment = StringAlignment.Center;
                        center.LineAlignment = StringAlignment.Center;
                        g.DrawString(number, _numberFont, mainBrush, r, center);
                    }
                    return;
                }

                float y = r.Y + S(4);
                g.DrawString(number, _numberFont, mainBrush, r.X + inset, y);
                y += _numberFont.Height;

                g.DrawString(s.Title, _boldFont, mainBrush, new RectangleF(r.X + inset, y, textWidth, _boldFont.Height), f);
                y += _boldFont.Height;

                string detail = s.Enabled ? s.SizeText : "coupé";
                if (y + _smallFont.Height <= r.Bottom - S(2))
                {
                    g.DrawString(detail, _smallFont, dimBrush, new RectangleF(r.X + inset, y, textWidth, _smallFont.Height), f);
                }

                if (primary)
                {
                    // Pastille d'écran principal en haut à droite.
                    int d = S(8);
                    g.FillEllipse(accent, r.Right - inset - d, r.Y + S(10), d, d);
                }
            }
        }

        private void DrawButtons(Graphics g)
        {
            foreach (HitZone zone in _zones)
            {
                if (zone.Kind == HitKind.Screen) continue;
                string text = zone.Kind == HitKind.Restore ? "Réactiver tout" : "Fermer";
                Rectangle r = zone.Rect;

                using (GraphicsPath path = RoundedPath(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(4)))
                using (SolidBrush brush = new SolidBrush(_hover == zone ? ButtonHover : ButtonFill))
                {
                    g.FillPath(brush, path);
                }
                TextRenderer.DrawText(g, text, this.Font, r, ButtonText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private void DrawStatus(Graphics g)
        {
            bool isStatus = !string.IsNullOrEmpty(_status) && _statusUntil > DateTime.Now;
            string text = isStatus ? _status : "Glisser : déplacer · Double-clic : couper / réactiver · Clic droit : écran principal";
            using (SolidBrush brush = new SolidBrush(isStatus ? TextClr : DimText))
            using (StringFormat f = new StringFormat(StringFormat.GenericTypographic))
            {
                f.Trimming = StringTrimming.EllipsisWord;
                g.DrawString(text, _smallFont, brush, _statusRect, f);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pressed != null)
            {
                int dx = e.X - _pressPoint.X;
                int dy = e.Y - _pressPoint.Y;
                Size threshold = SystemInformation.DragSize;
                if (!_dragging && (Math.Abs(dx) > threshold.Width / 2 || Math.Abs(dy) > threshold.Height / 2))
                {
                    _dragging = true;
                }
                if (_dragging)
                {
                    Screen s = _pressed.Screen;
                    Point proposed = new Point(
                        s.Bounds.X + (int)Math.Round(dx / _pressScale),
                        s.Bounds.Y + (int)Math.Round(dy / _pressScale));
                    _dropLocation = _state.SnapPosition(s, proposed, (int)Math.Round(S(10) / _pressScale));
                    UpdateDragView();
                    this.Invalidate();
                }
                return;
            }

            HitZone zone = HitTest(e.Location);
            if (zone != _hover)
            {
                _hover = zone;
                this.Cursor = zone == null ? Cursors.Default : (CanDrag(zone) ? Cursors.SizeAll : Cursors.Hand);
                this.Invalidate();
            }
        }

        private bool CanDrag(HitZone zone)
        {
            return zone.Kind == HitKind.Screen && zone.Screen.Enabled && _state.EnabledCount > 1;
        }

        // Recadre la vue sur les écrans fixes + l'écran à sa position de dépôt, dans la même place
        // que le panneau réserve aux écrans : l'écran déplacé reste visible même hors du bureau actuel.
        private void UpdateDragView()
        {
            Rectangle view = new Rectangle(_dropLocation, _pressed.Screen.Bounds.Size);
            foreach (HitZone zone in _zones)
            {
                if (zone.Kind != HitKind.Screen || zone == _pressed) continue;
                view = Rectangle.Union(view, zone.Screen.Bounds);
            }
            _desktop = view;
            _scale = Math.Min((float)_areaBox.Width / view.Width, (float)_areaBox.Height / view.Height);
            int w = (int)Math.Round(view.Width * _scale);
            int h = (int)Math.Round(view.Height * _scale);
            _screenArea = new Rectangle(_areaBox.X + (_areaBox.Width - w) / 2, _areaBox.Y + (_areaBox.Height - h) / 2, w, h);
        }

        private void CancelDrag()
        {
            bool wasDragging = _dragging;
            _pressed = null;
            _dragging = false;
            if (wasDragging)
            {
                RebuildLayout();
                this.Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            HitZone pressed = _pressed;
            bool dragged = _dragging;
            _pressed = null;
            _dragging = false;
            if (pressed == null || !dragged)
            {
                return;
            }

            if (_dropLocation != pressed.Screen.Bounds.Location)
            {
                string err = _state.MoveScreen(pressed.Screen, _dropLocation);
                SetStatus(err ?? string.Format("{0} déplacé.", pressed.Screen.Title));
            }
            _hover = null;
            RebuildLayout();
            this.Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != null)
            {
                _hover = null;
                this.Cursor = Cursors.Default;
                this.Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            HitZone zone = HitTest(e.Location);
            if (zone == null)
            {
                return;
            }

            switch (zone.Kind)
            {
                case HitKind.Close:
                    this.Hide();
                    return;
                case HitKind.Restore:
                    string restoreError = _state.RestoreAll();
                    SetStatus(restoreError ?? "Tous les écrans réactivés.");
                    break;
                case HitKind.Screen:
                    string err;
                    if (e.Button == MouseButtons.Right)
                    {
                        err = _state.SetPrimary(zone.Screen);
                        SetStatus(err ?? string.Format("{0} est maintenant l'écran principal.", zone.Screen.Title));
                    }
                    else if (e.Button != MouseButtons.Left)
                    {
                        return;
                    }
                    else if (e.Clicks >= 2)
                    {
                        CancelDrag();
                        bool wasEnabled = zone.Screen.Enabled;
                        err = _state.Toggle(zone.Screen);
                        SetStatus(err ?? string.Format("{0} {1}.", zone.Screen.Title, wasEnabled ? "coupé" : "réactivé"));
                    }
                    else
                    {
                        // Simple clic : début d'un éventuel glisser, rien n'est appliqué avant le dépôt.
                        if (CanDrag(zone))
                        {
                            _pressed = zone;
                            _pressPoint = e.Location;
                            _pressScale = _scale;
                            _dragging = false;
                        }
                        return;
                    }
                    break;
            }

            _hover = null;
            RebuildLayout();
            this.Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                if (_dragging)
                {
                    CancelDrag(); // Échap pendant un glisser : l'écran revient à sa place
                    return;
                }
                this.Hide();
            }
        }

        private HitZone HitTest(Point p)
        {
            foreach (HitZone zone in _zones)
            {
                if (zone.Rect.Contains(p))
                {
                    return zone;
                }
            }
            return null;
        }

        private enum HitKind
        {
            Screen,
            Restore,
            Close
        }

        private sealed class HitZone
        {
            public readonly Rectangle Rect;
            public readonly Screen Screen;
            public readonly HitKind Kind;

            public HitZone(Rectangle rect, Screen screen, HitKind kind)
            {
                this.Rect = rect;
                this.Screen = screen;
                this.Kind = kind;
            }
        }
    }
}
