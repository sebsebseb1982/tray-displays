using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TrayDisplays
{
    internal sealed class Screen
    {
        public string DeviceName;          // \\.\DISPLAY2 (null si l'écran est coupé : plus de moniteur)
        public int Number;                 // n° de \\.\DISPLAYn, 0 si inconnu (écran coupé)
        public string Name;                // nom amical du moniteur
        public Rectangle Bounds;           // pixels physiques dans le bureau virtuel
        public int Width;
        public int Height;
        public int RefreshHz;
        public bool IsPrimary;
        public bool Enabled;
        public int[] BaselinePaths = new int[0];   // indices des paths du snapshot « tout actif »

        public string SizeText
        {
            get
            {
                if (this.RefreshHz > 0)
                {
                    return string.Format("{0}×{1} @ {2} Hz", this.Width, this.Height, this.RefreshHz);
                }
                return string.Format("{0}×{1}", this.Width, this.Height);
            }
        }

        public string Title
        {
            get
            {
                string name = string.IsNullOrEmpty(this.Name) ? this.DeviceName : this.Name;
                if (string.IsNullOrEmpty(name))
                {
                    name = "Écran";
                }
                return name;
            }
        }

        public override string ToString()
        {
            return string.Format("{0} {1} {2}{3}", this.Title, this.SizeText, this.Bounds.Location,
                this.Enabled ? "" : " (coupé)");
        }
    }

    internal sealed class MonitorEntry
    {
        public string Device;
        public Rectangle Bounds;
        public bool IsPrimary;
    }

    // État des écrans + opérations (couper / réactiver / écran principal).
    // Le « snapshot » (_baseline) est la configuration avec tous les écrans actifs : c'est la
    // référence à partir de laquelle les flags des chemins sont recréés pour chaque bascule.
    internal sealed class DisplayState
    {
        private readonly List<Screen> _screens = new List<Screen>();
        private DisplayConfigData _baseline;
        private HashSet<int> _mappedPaths = new HashSet<int>();

        // Délégué conservé pour éviter la collecte du GC pendant EnumDisplayMonitors.
        private Native.MonitorEnumProc _callback;

        public List<Screen> Screens
        {
            get { return _screens; }
        }

        public int EnabledCount
        {
            get
            {
                int n = 0;
                foreach (Screen s in _screens)
                {
                    if (s.Enabled) n++;
                }
                return n;
            }
        }

        public int DisabledCount
        {
            get { return _screens.Count - this.EnabledCount; }
        }

        public bool HasDisabled
        {
            get { return this.DisabledCount > 0; }
        }

        public Screen PrimaryScreen
        {
            get
            {
                foreach (Screen s in _screens)
                {
                    if (s.Enabled && s.IsPrimary) return s;
                }
                return null;
            }
        }

        // Zone englobant tous les écrans, coupés compris : c'est elle qui sert à dessiner la
        // disposition (icône et panneau), sinon les écrans coupés sortiraient du cadre.
        public Rectangle DesktopBounds
        {
            get
            {
                Rectangle r = Rectangle.Empty;
                foreach (Screen s in _screens)
                {
                    if (r.Width == 0 && r.Height == 0)
                    {
                        r = s.Bounds;
                    }
                    else
                    {
                        r = Rectangle.Union(r, s.Bounds);
                    }
                }
                return r;
            }
        }

        public void Refresh()
        {
            DisplayConfigData active = DisplayConfigData.Query(
                Native.QdcOnlyActivePaths | Native.QdcVirtualModeAware);
            List<MonitorEntry> monitors = EnumerateMonitors();

            Dictionary<Point, List<int>> baselineGroups = BuildPathGroups(this._baseline);
            HashSet<Point> livePositions = new HashSet<Point>();
            foreach (MonitorEntry m in monitors)
            {
                livePositions.Add(m.Bounds.Location);
            }

            List<Screen> screens = new List<Screen>();

            // Écrans actifs : présents dans la liste des moniteurs.
            foreach (MonitorEntry m in monitors)
            {
                Screen s = new Screen();
                s.DeviceName = m.Device;
                s.Bounds = m.Bounds;
                s.IsPrimary = m.IsPrimary;
                s.Enabled = true;
                s.Width = m.Bounds.Width;
                s.Height = m.Bounds.Height;

                // Infos (résolution /Hz/nom du moniteur) via les chemins actifs.
                for (int i = 0; i < active.PathCount; i++)
                {
                    if (!active.PathIsActive(i)) continue;
                    if (!PathMatchesRect(active, i, m.Bounds)) continue;

                    int tm = active.TargetModeIndex(i);
                    if (tm != Native.InvalidModeIndex && tm < active.ModeCount
                        && active.ModeInfoType(tm) == Native.ModeTypeTarget)
                    {
                        Size res = active.TargetResolution(tm);
                        if (res.Width > 0 && res.Height > 0)
                        {
                            s.Width = res.Width;
                            s.Height = res.Height;
                        }
                        s.RefreshHz = active.TargetRefreshHz(tm);
                    }

                    string friendly = active.TargetFriendlyName(i) ?? MonitorFriendlyName(m.Device);
                    if (!string.IsNullOrEmpty(friendly))
                    {
                        s.Name = friendly;
                    }
                }

                if (string.IsNullOrEmpty(s.Name))
                {
                    s.Name = m.Device;
                }
                s.Number = ParseDisplayNumber(m.Device);

                List<int> bl;
                if (baselineGroups.TryGetValue(m.Bounds.Location, out bl))
                {
                    s.BaselinePaths = bl.ToArray();
                }
                screens.Add(s);
            }

            // Écrans coupés : présents dans le snapshot mais plus dans la liste des moniteurs.
            foreach (KeyValuePair<Point, List<int>> kv in baselineGroups)
            {
                if (livePositions.Contains(kv.Key)) continue;

                int path = kv.Value[0];
                Size sourceSize = Size.Empty;
                int smi = this._baseline.SourceModeIndex(path);
                if (smi != Native.InvalidModeIndex && smi < this._baseline.ModeCount
                    && this._baseline.ModeInfoType(smi) == Native.ModeTypeSource)
                {
                    sourceSize = this._baseline.SourceSize(smi);
                }

                Screen s = new Screen();
                s.Name = this._baseline.TargetFriendlyName(path);
                s.Bounds = new Rectangle(kv.Key, sourceSize);
                s.Enabled = false;
                s.IsPrimary = false;
                s.BaselinePaths = kv.Value.ToArray();
                s.Width = sourceSize.Width;
                s.Height = sourceSize.Height;

                int tm = this._baseline.TargetModeIndex(path);
                if (tm != Native.InvalidModeIndex && tm < this._baseline.ModeCount
                    && this._baseline.ModeInfoType(tm) == Native.ModeTypeTarget)
                {
                    Size res = this._baseline.TargetResolution(tm);
                    if (res.Width > 0 && res.Height > 0)
                    {
                        s.Width = res.Width;
                        s.Height = res.Height;
                    }
                    s.RefreshHz = this._baseline.TargetRefreshHz(tm);
                }
                screens.Add(s);
            }

            // Si rien n'est coupé, le snapshot courant devient la nouvelle référence
            // (il intègre les changements faits hors de l'application : résolution, hot-plug).
            if (this._baseline == null || (baselineGroups.Count == monitors.Count && monitors.Count > 0))
            {
                this._baseline = active.Clone();
                baselineGroups = BuildPathGroups(this._baseline);
                foreach (Screen s in screens)
                {
                    List<int> bl;
                    if (baselineGroups.TryGetValue(s.Bounds.Location, out bl))
                    {
                        s.BaselinePaths = bl.ToArray();
                    }
                }
            }

            HashSet<int> mapped = new HashSet<int>();
            foreach (Screen s in screens)
            {
                foreach (int idx in s.BaselinePaths)
                {
                    mapped.Add(idx);
                }
            }
            this._mappedPaths = mapped;

            _screens.Clear();
            _screens.AddRange(screens);
        }

        // Coupe ou réactive un écran en jouant uniquement sur le flag DISPLAYCONFIG_PATH_ACTIVE
        // des chemins du snapshot (les modes sont conservés tels quels).
        public string Toggle(Screen target)
        {
            HashSet<int> activePaths;
            string error = ComputeToggle(target, out activePaths);
            if (error != null)
            {
                return error;
            }
            return ApplyPathFlags(activePaths, true);
        }

        // Même calcul que Toggle, mais validation seule (SDC_VALIDATE) : rien n'est appliqué.
        public string ValidateToggle(Screen target)
        {
            HashSet<int> activePaths;
            string error = ComputeToggle(target, out activePaths);
            if (error != null)
            {
                return error;
            }
            return ApplyPathFlags(activePaths, false);
        }

        private string ComputeToggle(Screen target, out HashSet<int> activePaths)
        {
            activePaths = new HashSet<int>();

            if (target.Enabled)
            {
                foreach (Screen s in _screens)
                {
                    if (!s.Enabled || s == target) continue;
                    foreach (int idx in s.BaselinePaths) activePaths.Add(idx);
                }
                if (activePaths.Count == 0)
                {
                    return "Impossible de couper tous les écrans : au moins un doit rester actif.";
                }
                return null;
            }

            if (target.BaselinePaths.Length == 0)
            {
                return "Aucun chemin connu pour cet écran : utilisez « Réactiver tous les écrans ».";
            }

            foreach (Screen s in _screens)
            {
                if (!s.Enabled) continue;
                foreach (int idx in s.BaselinePaths) activePaths.Add(idx);
            }
            foreach (int idx in target.BaselinePaths) activePaths.Add(idx);
            return null;
        }

        // Réactive tous les écrans. Si on connaît la configuration d'origine (écrans coupés depuis
        // cette session), on rallume exactement ses chemins : c'est déterministe. Sinon (application
        // relancée), repli sur SDC_TOPOLOGY_EXTEND, où Windows choisit seul ce qu'il rallume.
        public string RestoreAll()
        {
            if (this._baseline != null && this.HasDisabled)
            {
                string error = ApplyPathFlags(new HashSet<int>(this._mappedPaths), true);
                if (error == null)
                {
                    return null;
                }
            }

            uint validate = Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero,
                Native.SdcTopologyExtend | Native.SdcValidate, IntPtr.Zero);
            if (validate != Native.ErrorSuccess)
            {
                return "Validation refusée : " + DescribeWinError(validate);
            }
            uint apply = Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero,
                Native.SdcTopologyExtend | Native.SdcApply, IntPtr.Zero);
            if (apply != Native.ErrorSuccess)
            {
                return "Application refusée : " + DescribeWinError(apply);
            }
            this.Refresh();
            return null;
        }

        // Définit `target` comme écran principal : sa position devient (0,0) et les autres
        // moniteurs gardent le même décalage relatif, via ChangeDisplaySettingsExW.
        public string SetPrimary(Screen target)
        {
            if (!target.Enabled)
            {
                return "Réactivez d'abord cet écran avant de le définir comme principal.";
            }
            if (target.IsPrimary && target.Bounds.Left == 0 && target.Bounds.Top == 0)
            {
                return null; // déjà principal
            }

            return ApplyPrimary(target, true);
        }

        // Validation seule du changement d'écran principal (SDC_VALIDATE) : rien n'est appliqué.
        public string ValidatePrimary(Screen target)
        {
            if (!target.Enabled)
            {
                return "Réactivez d'abord cet écran avant de le définir comme principal.";
            }
            return ApplyPrimary(target, false);
        }

        // L'écran principal est celui dont la source est en (0,0) : on décale toutes les sources
        // actives de la position de `target`, en une seule configuration (pas de chevauchement
        // intermédiaire).
        private string ApplyPrimary(Screen target, bool apply)
        {
            DisplayConfigData cfg = DisplayConfigData.Query(Native.QdcOnlyActivePaths | Native.QdcVirtualModeAware);
            cfg.ShiftSources(target.Bounds.Location);

            const uint baseFlags = Native.SdcUseSuppliedConfig | Native.SdcVirtualModeAware
                | Native.SdcAllowChanges;

            uint hr = cfg.Apply(baseFlags | Native.SdcValidate);
            if (hr != Native.ErrorSuccess)
            {
                return "Validation refusée : " + DisplayConfigData.DescribeError(hr);
            }
            if (!apply)
            {
                return null;
            }

            hr = cfg.Apply(baseFlags | Native.SdcApply | Native.SdcSaveToDatabase);
            if (hr != Native.ErrorSuccess)
            {
                return "Application refusée : " + DisplayConfigData.DescribeError(hr);
            }

            this.Refresh();
            return null;
        }

        // `apply=false` : validation seule (SDC_VALIDATE), aucune modification de la configuration.
        private string ApplyPathFlags(HashSet<int> activePaths, bool apply)
        {
            DisplayConfigData cfg = this._baseline.Clone();
            for (int i = 0; i < cfg.PathCount; i++)
            {
                if (!this._mappedPaths.Contains(i))
                {
                    continue; // chemin non rattaché à un écran connu : on ne le touche pas
                }
                uint flags = cfg.PathFlags(i);
                if (activePaths.Contains(i))
                {
                    flags |= Native.PathActive;
                }
                else
                {
                    flags &= ~Native.PathActive;
                }
                cfg.SetPathFlags(i, flags);
            }

            // Windows exige un écran actif en (0,0). Si l'écran principal est coupé, le premier
            // écran restant devient principal : on décale toute la disposition (et la référence,
            // pour que les positions restent cohérentes avec ce qui sera affiché).
            Point shift = Point.Empty;
            bool needShift = !HasActiveSourceAtOrigin(cfg);
            if (needShift)
            {
                shift = FirstActiveSourcePosition(cfg);
                cfg.ShiftSources(shift);
            }

            const uint baseFlags = Native.SdcUseSuppliedConfig | Native.SdcVirtualModeAware
                | Native.SdcAllowChanges;

            uint hr = cfg.Apply(baseFlags | Native.SdcValidate);
            if (hr != Native.ErrorSuccess)
            {
                return "Validation refusée : " + DisplayConfigData.DescribeError(hr);
            }
            if (!apply)
            {
                return null; // validation seule : on s'arrête ici
            }

            hr = cfg.Apply(baseFlags | Native.SdcApply | Native.SdcSaveToDatabase);
            if (hr != Native.ErrorSuccess)
            {
                return "Application refusée : " + DisplayConfigData.DescribeError(hr);
            }

            if (needShift)
            {
                this._baseline.ShiftSources(shift);
            }
            this.Refresh();
            return null;
        }

        private static bool HasActiveSourceAtOrigin(DisplayConfigData cfg)
        {
            for (int i = 0; i < cfg.PathCount; i++)
            {
                if (!cfg.PathIsActive(i)) continue;
                int sm = cfg.SourceModeIndex(i);
                if (sm != Native.InvalidModeIndex && sm < cfg.ModeCount
                    && cfg.ModeInfoType(sm) == Native.ModeTypeSource
                    && cfg.SourcePosition(sm) == Point.Empty)
                {
                    return true;
                }
            }
            return false;
        }

        private static Point FirstActiveSourcePosition(DisplayConfigData cfg)
        {
            for (int i = 0; i < cfg.PathCount; i++)
            {
                if (!cfg.PathIsActive(i)) continue;
                int sm = cfg.SourceModeIndex(i);
                if (sm != Native.InvalidModeIndex && sm < cfg.ModeCount
                    && cfg.ModeInfoType(sm) == Native.ModeTypeSource)
                {
                    return cfg.SourcePosition(sm);
                }
            }
            return Point.Empty;
        }

        // La position d'un écran sur le bureau est celle de son mode source (la « desktop image »
        // décrit seulement la zone affichée, toujours à partir de (0,0)).
        private static bool PathMatchesRect(DisplayConfigData cfg, int path, Rectangle rect)
        {
            int sm = cfg.SourceModeIndex(path);
            if (sm != Native.InvalidModeIndex && sm < cfg.ModeCount
                && cfg.ModeInfoType(sm) == Native.ModeTypeSource
                && cfg.SourcePosition(sm) == rect.Location)
            {
                return true;
            }
            return false;
        }

        // Regroupe les paths actifs du snapshot par position source (des clones partagent la position).
        private static Dictionary<Point, List<int>> BuildPathGroups(DisplayConfigData cfg)
        {
            Dictionary<Point, List<int>> groups = new Dictionary<Point, List<int>>();
            if (cfg == null)
            {
                return groups; // pas encore de snapshot (premier Refresh)
            }
            for (int i = 0; i < cfg.PathCount; i++)
            {
                if (!cfg.PathIsActive(i)) continue;

                int sm = cfg.SourceModeIndex(i);
                if (sm == Native.InvalidModeIndex || sm >= cfg.ModeCount
                    || cfg.ModeInfoType(sm) != Native.ModeTypeSource)
                {
                    continue;
                }
                Point key = cfg.SourcePosition(sm);

                List<int> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<int>();
                    groups[key] = list;
                }
                list.Add(i);
            }
            return groups;
        }

        private static int ParseDisplayNumber(string device)
        {
            const string prefix = "DISPLAY";
            int at = string.IsNullOrEmpty(device) ? -1 : device.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            int n;
            if (at >= 0 && int.TryParse(device.Substring(at + prefix.Length), out n))
            {
                return n;
            }
            return 0;
        }

        // Repli si DisplayConfigGetDeviceInfo ne donne rien : description du moniteur d'après
        // EnumDisplayDevicesW(\\.\DISPLAYn, j) (ex. « Generic PnP Monitor »).
        private static string MonitorFriendlyName(string monitorDevice)
        {
            DisplayDeviceW disp = new DisplayDeviceW();
            disp.cb = Marshal.SizeOf(typeof(DisplayDeviceW));
            if (Native.EnumDisplayDevicesW(monitorDevice, 0, ref disp, Native.EnumDisplayDevicesInitialized))
            {
                return disp.DeviceString;
            }
            return null;
        }

        private List<MonitorEntry> EnumerateMonitors()
        {
            List<MonitorEntry> list = new List<MonitorEntry>();
            Native.MonitorEnumProc cb = delegate(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data)
            {
                MonitorInfoEx info = new MonitorInfoEx();
                info.cb = Marshal.SizeOf(typeof(MonitorInfoEx));
                if (!Native.GetMonitorInfoW(hMonitor, ref info))
                {
                    return true;
                }
                MonitorEntry m = new MonitorEntry();
                m.Device = info.szDevice;
                m.Bounds = Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right, info.rcMonitor.Bottom);
                m.IsPrimary = (info.dwFlags & (int)Native.MonitorIsPrimary) != 0;
                list.Add(m);
                return true;
            };
            this._callback = cb;
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            return list;
        }

        public static string DescribeWinError(uint hr)
        {
            return DisplayConfigData.DescribeError(hr);
        }
    }
}
