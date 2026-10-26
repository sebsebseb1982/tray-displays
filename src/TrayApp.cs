using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace TrayDisplays
{
    // Icône dans la barre des tâches : clic gauche = panneau des écrans, clic droit = menu.
    // Le menu et l'icône ne sont reconstruits que si la configuration change (signature).
    internal sealed class TrayApp : IDisposable
    {
        // L'icône est dessinée en 32x32 ; la barre des tâches la réduit à sa taille.
        private const int IconSize = 32;
        private const int IconMargin = 3;

        private static readonly Color IconOn = Color.FromArgb(228, 236, 244);
        private static readonly Color IconOnBorder = Color.FromArgb(120, 140, 162);
        private static readonly Color IconPrimary = Color.FromArgb(236, 170, 90);
        private static readonly Color IconOff = Color.FromArgb(110, 120, 132);

        private readonly DisplayState _state;
        private readonly PopupForm _popup;
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly Timer _timer;
        private readonly DisplayFilter _filter;

        private Icon _iconImage = null;
        private IntPtr _iconHandle = IntPtr.Zero;
        private string _signature = null;
        private bool _disposed = false;

        public TrayApp(DisplayState state)
        {
            _state = state;

            _popup = new PopupForm(state);

            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;

            _icon = new NotifyIcon();
            _icon.Text = "Écrans connectés — clic pour afficher";
            _icon.ContextMenuStrip = _menu;
            _icon.Visible = true;
            _icon.MouseClick += OnTrayMouseClick;

            _filter = new DisplayFilter(this);
            Application.AddMessageFilter(_filter);

            _timer = new Timer();
            _timer.Interval = 1500;
            _timer.Tick += delegate { Rebuild(); };
            _timer.Start();

            Rebuild();
        }

        private void OnTrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _popup.ToggleVisibility();
            }
        }

        // Relit la configuration et, si elle a changé, redessine l'icône / le menu.
        public void Rebuild()
        {
            string status = null;
            try
            {
                _state.Refresh();
            }
            catch (Exception ex)
            {
                status = "Relecture des écrans impossible : " + ex.Message;
            }

            string signature = BuildSignature();
            if (signature != _signature)
            {
                _signature = signature;
                RebuildMenu();
                RebuildIcon();
            }

            if (_popup.Visible)
            {
                _popup.UpdateLayout();
                if (status != null)
                {
                    _popup.SetStatus(status);
                }
            }
        }

        private string BuildSignature()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(_state.DesktopBounds.ToString());
            foreach (Screen s in _state.Screens)
            {
                sb.Append('|');
                sb.Append(s.Bounds.ToString());
                sb.Append(s.Enabled ? "on" : "off");
                sb.Append(s.IsPrimary ? "primary" : "");
                sb.Append(s.Title);
                sb.Append(s.SizeText);
            }
            return sb.ToString();
        }

        private void RebuildMenu()
        {
            _menu.Items.Clear();

            _menu.Items.Add(MenuItem("Afficher les écrans", delegate { _popup.ToggleVisibility(); }));
            _menu.Items.Add(new ToolStripSeparator());

            foreach (Screen s in _state.Screens)
            {
                Screen captured = s;
                string text = captured.Enabled
                    ? string.Format("{0} · {1}", captured.Title, captured.SizeText)
                    : string.Format("{0} · coupé", captured.Title);
                ToolStripMenuItem item = MenuItem(text, delegate { Report(_state.Toggle(captured)); });
                item.Checked = captured.Enabled;
                _menu.Items.Add(item);
            }

            _menu.Items.Add(new ToolStripSeparator());
            foreach (Screen s in _state.Screens)
            {
                if (!s.Enabled) continue;
                Screen captured = s;
                ToolStripMenuItem item = MenuItem(
                    string.Format("Écran principal : {0}", captured.Title),
                    delegate { Report(_state.SetPrimary(captured)); });
                item.Checked = captured.IsPrimary;
                _menu.Items.Add(item);
            }

            // Toujours proposé : après un redémarrage de l'application, les écrans déjà coupés
            // ne sont plus connus, mais cette commande les réactive quand même.
            _menu.Items.Add(MenuItem("Réactiver tous les écrans", delegate { Report(_state.RestoreAll()); }));

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(MenuItem("Quitter", delegate { Quit(); }));
        }

        private static ToolStripMenuItem MenuItem(string text, EventHandler handler)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += handler;
            return item;
        }

        // Dessine l'icône : reproduit le layout des écrans (coupés = pointillés).
        private void RebuildIcon()
        {
            Bitmap bmp = new Bitmap(IconSize, IconSize);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Rectangle desktop = _state.DesktopBounds;
                if (desktop.Width <= 0 || desktop.Height <= 0)
                {
                    desktop = new Rectangle(0, 0, 1, 1);
                }
                float scale = Math.Min(
                    (float)(IconSize - 2 * IconMargin) / (float)desktop.Width,
                    (float)(IconSize - 2 * IconMargin) / (float)desktop.Height);

                foreach (Screen s in _state.Screens)
                {
                    int x = IconMargin + (int)Math.Round((s.Bounds.Left - desktop.Left) * scale);
                    int y = IconMargin + (int)Math.Round((s.Bounds.Top - desktop.Top) * scale);
                    int w = Math.Max(3, (int)Math.Round(s.Bounds.Width * scale));
                    int h = Math.Max(3, (int)Math.Round(s.Bounds.Height * scale));
                    Rectangle r = new Rectangle(x, y, w, h);

                    if (s.Enabled)
                    {
                        using (SolidBrush brush = new SolidBrush(IconOn))
                        {
                            g.FillRectangle(brush, r);
                        }
                        using (Pen pen = new Pen(s.IsPrimary ? IconPrimary : IconOnBorder))
                        {
                            g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                        }
                        if (s.IsPrimary)
                        {
                            // petit carré d'angle : repère de l'écran principal
                            using (SolidBrush accent = new SolidBrush(IconPrimary))
                            {
                                g.FillRectangle(accent, r.X + 1, r.Y + 1, 3, 3);
                            }
                        }
                    }
                    else
                    {
                        using (Pen pen = new Pen(IconOff))
                        {
                            pen.DashStyle = DashStyle.Dash;
                            g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                        }
                    }
                }
            }

            IntPtr handle = bmp.GetHicon();
            Icon image = Icon.FromHandle(handle);

            ReleaseIcon();
            _iconHandle = handle;
            _iconImage = image;
            _icon.Icon = image;

            bmp.Dispose();
        }

        private void ReleaseIcon()
        {
            if (_iconImage != null)
            {
                _iconImage.Dispose();
                _iconImage = null;
            }
            if (_iconHandle != IntPtr.Zero)
            {
                Native.DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }
        }

        // Affiche une erreur dans le panneau ; après une action réussie on relit l'état.
        private void Report(string error)
        {
            if (error != null)
            {
                _popup.SetStatus(error);
                MessageBox.Show(error, "Tray Displays", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Rebuild();
        }

        private void Quit()
        {
            Application.Exit();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            _timer.Stop();
            Application.RemoveMessageFilter(_filter);
            _icon.Visible = false;
            ReleaseIcon();
            _icon.Dispose();
            _menu.Dispose();
            _popup.Dispose();
        }
    }

    // Filtre de messages : WM_DISPLAYCHANGE est broadcast à toutes les fenêtres,
    // donc le loop applicatif le voit sans fenêtre dédiée.
    internal sealed class DisplayFilter : IMessageFilter
    {
        private readonly TrayApp _app;

        public DisplayFilter(TrayApp app)
        {
            _app = app;
        }

        public bool PreFilterMessage(ref Message message)
        {
            if (message.Msg == Native.WmDisplayChange)
            {
                _app.Rebuild();
            }
            return false; // le message continue vers les fenêtres
        }
    }
}
