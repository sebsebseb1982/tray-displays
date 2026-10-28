using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TrayDisplays
{
    // Diagnostics : vérifie l'ABI, lit la configuration courante et ne valide que les
    // changements possibles (SDC_VALIDATE / CDS_TEST). Rien n'est jamais appliqué.
    internal static class SelfTest
    {
        private static readonly List<string> _lines = new List<string>();
        private static bool _ok = true;

        public static bool Run()
        {
            Line("--- tailles des structures ---");
            CheckSize("DevMode", Marshal.SizeOf(typeof(DevMode)), 220);
            CheckSize("DisplayDeviceW", Marshal.SizeOf(typeof(DisplayDeviceW)), 840);
            CheckSize("MonitorInfoEx", Marshal.SizeOf(typeof(MonitorInfoEx)), 104);

            DumpCurrentMode();
            DumpAdapters();
            DumpPaths();

            DisplayState state = new DisplayState();
            try
            {
                state.Refresh();
            }
            catch (Exception ex)
            {
                Fail("Refresh : " + ex.Message);
                return Flush();
            }

            Line("--- écrans ---");
            Line(string.Format("desktop : {0}", state.DesktopBounds));
            foreach (Screen s in state.Screens)
            {
                Line(string.Format("  {0} · {1} · paths={2} · {3}",
                    s.Title, s, s.BaselinePaths.Length, s.Enabled ? "actif" : "coupé"));
            }
            Screen primary = state.PrimaryScreen;
            Line(string.Format("principal : {0}", primary == null ? "(aucun)" : primary.Title));
            if (primary == null)
            {
                Fail("aucun écran principal détecté");
            }

            Line("--- validation : configuration identique ---");
            DisplayConfigData same = DisplayConfigData.Query(
                Native.QdcOnlyActivePaths | Native.QdcVirtualModeAware);
            uint hr = same.Apply(Native.SdcUseSuppliedConfig | Native.SdcVirtualModeAware | Native.SdcValidate);
            Expect("config identique", hr, 0);

            Line("--- validation : couper chaque écran (SDC_VALIDATE seul) ---");
            foreach (Screen s in state.Screens)
            {
                if (!s.Enabled) continue;
                string err = state.ValidateToggle(s);
                if (err != null)
                {
                    Fail(string.Format("couper {0} : {1}", s.Title, err));
                }
                else
                {
                    Line(string.Format("  couper {0} : OK", s.Title));
                }
            }

            Line("--- validation : réactiver chaque écran coupé (SDC_VALIDATE seul) ---");
            foreach (Screen s in state.Screens)
            {
                if (s.Enabled) continue;
                string err = state.ValidateToggle(s);
                if (err != null)
                {
                    Fail(string.Format("réactiver {0} : {1}", s.Title, err));
                }
                else
                {
                    Line(string.Format("  réactiver {0} : OK", s.Title));
                }
            }

            Line("--- validation : déplacer chaque écran au-dessus des autres (SDC_VALIDATE seul) ---");
            foreach (Screen s in state.Screens)
            {
                if (!s.Enabled || state.EnabledCount < 2) continue;
                Point to = state.SnapPosition(s, new Point(s.Bounds.X, state.DesktopBounds.Top - s.Bounds.Height), 0);
                string err = to == s.Bounds.Location ? "aucune position libre" : state.ValidateMove(s, to);
                if (err != null)
                {
                    Fail(string.Format("déplacer {0} : {1}", s.Title, err));
                }
                else
                {
                    Line(string.Format("  déplacer {0} vers {1} : OK", s.Title, to));
                }
            }

            Line("--- validation : écran principal sur chaque écran (CDS_TEST seul) ---");
            foreach (Screen s in state.Screens)
            {
                if (!s.Enabled) continue;
                string err = state.ValidatePrimary(s);
                if (err != null)
                {
                    Fail(string.Format("principal {0} : {1}", s.Title, err));
                }
                else
                {
                    Line(string.Format("  principal {0} : OK", s.Title));
                }
            }

            Line("--- validation : réactiver tout (SDC_TOPOLOGY_EXTEND + SDC_VALIDATE) ---");
            uint restore = Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero,
                Native.SdcTopologyExtend | Native.SdcValidate, IntPtr.Zero);
            Expect("restore all", restore, 0);

            return Flush();
        }

        private static void DumpCurrentMode()
        {
            Line("--- EnumDisplaySettingsExW ---");
            for (int n = 1; n <= 8; n++)
            {
                DevMode dm = new DevMode();
                dm.dmSize = (ushort)Marshal.SizeOf(typeof(DevMode));
                // \\.\DISPLAYn (nom du moniteur, tel que renvoyé par GetMonitorInfoW)
                string device = string.Format("\\\\.\\DISPLAY{0}", n);
                if (!Native.EnumDisplaySettingsExW(device, Native.EnumCurrentSettings, ref dm, 0))
                {
                    continue;
                }
                Line(string.Format("  {0} : size={1} fields=0x{2:x8} pos=({3},{4}) {5}x{6} @{7} Hz",
                    device, dm.dmSize, dm.dmFields, dm.dmPositionX, dm.dmPositionY,
                    dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
            }
        }

        private static void DumpAdapters()
        {
            Line("--- EnumDisplayDevicesW ---");
            DisplayDeviceW adapter = new DisplayDeviceW();
            adapter.cb = Marshal.SizeOf(typeof(DisplayDeviceW));
            for (uint i = 0; Native.EnumDisplayDevicesW(null, i, ref adapter, 0); i++)
            {
                if ((adapter.StateFlags & Native.DeviceMirroringDriver) != 0) continue;
                Line(string.Format("  adapter {0} : {1} / {2} flags=0x{3:x}", i, adapter.DeviceName,
                    adapter.DeviceString, adapter.StateFlags));

                DisplayDeviceW disp = new DisplayDeviceW();
                disp.cb = Marshal.SizeOf(typeof(DisplayDeviceW));
                for (uint j = 0; Native.EnumDisplayDevicesW(adapter.DeviceName, j, ref disp,
                                                        Native.EnumDisplayDevicesInitialized); j++)
                {
                    Line(string.Format("    moniteur {0} : {1} / {2}", j, disp.DeviceName, disp.DeviceString));
                }
            }
        }

        private static void DumpPaths()
        {
            Line("--- QueryDisplayConfig ---");
            DisplayConfigData active = DisplayConfigData.Query(
                Native.QdcOnlyActivePaths | Native.QdcVirtualModeAware);
            Line(string.Format("  only_active : paths={0} modes={1}", active.PathCount, active.ModeCount));
            for (int i = 0; i < active.PathCount; i++)
            {
                int dm = active.DesktopModeIndex(i);
                Rectangle region = dm != Native.InvalidModeIndex && dm < active.ModeCount
                    ? active.DesktopRegion(dm)
                    : Rectangle.Empty;
                int tm = active.TargetModeIndex(i);
                string res = tm != Native.InvalidModeIndex && tm < active.ModeCount
                    ? string.Format("{0} @ {1} Hz", active.TargetResolution(tm), active.TargetRefreshHz(tm))
                    : "(aucun mode target)";
                Line(string.Format("    path {0} flags=0x{1:x} src={2}/{3} tgt={4}/{5} desktop={6} {7}",
                    i, active.PathFlags(i), active.PathAdapterId(i), active.PathSourceId(i),
                    active.PathAdapterId(i), active.PathTargetId(i), region, res));
            }

            DisplayConfigData all = DisplayConfigData.Query(
                Native.QdcAllPaths | Native.QdcVirtualModeAware);
            Line(string.Format("  all_paths : paths={0} modes={1}", all.PathCount, all.ModeCount));
            for (int i = 0; i < all.PathCount; i++)
            {
                if (!all.PathTargetAvailable(i)) continue;
                Line(string.Format("    path {0} flags=0x{1:x} src={2} tgt={3} srcMode={4} tgtMode={5} tech={6} name={7}",
                    i, all.PathFlags(i), all.PathSourceId(i), all.PathTargetId(i),
                    all.SourceModeIndex(i), all.TargetModeIndex(i), all.PathOutputTechnology(i),
                    all.TargetFriendlyName(i)));
            }
        }

        private static void CheckSize(string name, int actual, int expected)
        {
            if (actual != expected)
            {
                Fail(string.Format("{0} : taille {1} (attendu {2})", name, actual, expected));
            }
            else
            {
                Line(string.Format("  {0} : {1} octets OK", name, actual));
            }
        }

        private static void Expect(string label, uint hr, uint expected)
        {
            if (hr != expected)
            {
                Fail(string.Format("{0} : hr={1} ({2})", label, hr, DisplayConfigData.DescribeError(hr)));
            }
            else
            {
                Line(string.Format("  {0} : hr=0 OK", label));
            }
        }

        private static void Fail(string message)
        {
            _ok = false;
            Line("  ÉCHEC : " + message);
        }

        private static void Line(string text)
        {
            _lines.Add(text);
            Console.WriteLine(text);
        }

        private static bool Flush()
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                string dir = string.IsNullOrEmpty(exe)
                    ? Environment.CurrentDirectory
                    : Path.GetDirectoryName(exe);
                File.WriteAllLines(Path.Combine(dir, "selftest.log"), _lines, new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Console.WriteLine("selftest.log impossible : " + ex.Message);
            }
            Line(_ok ? "=== SELFTEST OK ===" : "=== SELFTEST ÉCHEC ===");
            return _ok;
        }
    }
}
