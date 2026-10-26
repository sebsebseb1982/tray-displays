using System;
using System.Windows.Forms;

namespace TrayDisplays
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Coordonnées physiques : nécessaire pour mapper les régions desktop aux moniteurs.
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                return SelfTest.Run() ? 0 : 1;
            }

            DisplayState state = new DisplayState();

            // Rendu du panneau dans un PNG, pour vérifier l'interface sans passer par l'icône.
            if (args != null && args.Length > 1 &&
                string.Equals(args[0], "--screenshot", StringComparison.OrdinalIgnoreCase))
            {
                state.Refresh();
                using (PopupForm popup = new PopupForm(state))
                {
                    popup.SaveSnapshot(args[1]);
                }
                return 0;
            }

            // Une seule instance : deux processus se disputeraient la configuration des écrans.
            bool created;
            using (System.Threading.Mutex single = new System.Threading.Mutex(true, "TrayDisplays.SingleInstance", out created))
            {
                if (!created)
                {
                    return 0;
                }
                TrayApp app = new TrayApp(state);
                try
                {
                    Application.Run();
                }
                finally
                {
                    app.Dispose();
                }
            }
            return 0;
        }
    }
}
