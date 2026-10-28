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
        public string DetachedTarget;      // sortie d'un écran branché mais éteint, absent du snapshot

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
            bool baselineStale = this._baseline == null;
            foreach (MonitorEntry m in monitors)
            {
                livePositions.Add(m.Bounds.Location);
                // Un moniteur actif sans chemin au même endroit dans le snapshot : la disposition
                // a changé hors de l'application (écran principal, hot-plug...), le snapshot est périmé.
                if (!baselineGroups.ContainsKey(m.Bounds.Location))
                {
                    baselineStale = true;
                }
            }

            // Rien n'est coupé (ou le snapshot est périmé) : le snapshot courant devient la nouvelle
            // référence, avant de chercher les écrans coupés, sinon d'anciennes positions
            // apparaîtraient comme des écrans « coupés » fantômes.
            if (monitors.Count > 0 && (baselineStale || baselineGroups.Count == monitors.Count))
            {
                this._baseline = active.Clone();
                baselineGroups = BuildPathGroups(this._baseline);
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

            // Écrans branchés mais éteints et absents du snapshot (coupés avant le lancement de
            // l'application) : sorties disponibles sans chemin actif. Leur taille est inconnue tant
            // qu'ils sont éteints ; ils sont dessinés à droite de la disposition.
            DisplayConfigData all = DisplayConfigData.Query(Native.QdcAllPaths | Native.QdcVirtualModeAware);
            HashSet<string> known = TargetKeys(active);
            known.UnionWith(TargetKeys(this._baseline));
            Rectangle layout = Rectangle.Empty;
            foreach (Screen s in screens)
            {
                layout = layout.IsEmpty ? s.Bounds : Rectangle.Union(layout, s.Bounds);
            }
            int nextLeft = layout.Right;
            foreach (KeyValuePair<string, int> kv in FindDetachedPaths(all, known, SourceKeys(active)))
            {
                Screen s = new Screen();
                s.Name = all.TargetFriendlyName(kv.Value);
                s.DetachedTarget = kv.Key;
                s.Enabled = false;
                s.IsPrimary = false;
                s.Width = 1920;
                s.Height = 1080;
                s.Bounds = new Rectangle(nextLeft, layout.Top, s.Width, s.Height);
                nextLeft += s.Width;
                screens.Add(s);
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
            if (!target.Enabled && target.DetachedTarget != null)
            {
                return EnableDetached(target, true);
            }
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
            if (!target.Enabled && target.DetachedTarget != null)
            {
                return EnableDetached(target, false);
            }
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

        // Réactive tous les écrans. Les écrans coupés depuis cette session retrouvent exactement
        // leurs chemins du snapshot (déterministe), puis les écrans éteints inconnus du snapshot
        // reçoivent un chemin un par un. En cas d'échec, ou si rien n'est coupé, repli sur
        // SDC_TOPOLOGY_EXTEND, où Windows choisit seul ce qu'il rallume.
        public string RestoreAll()
        {
            if (!this.HasDisabled)
            {
                return RestoreByTopology();
            }

            bool cutFromBaseline = false;
            foreach (Screen s in _screens)
            {
                if (!s.Enabled && s.BaselinePaths.Length > 0) cutFromBaseline = true;
            }
            if (cutFromBaseline && ApplyPathFlags(new HashSet<int>(this._mappedPaths), true) != null)
            {
                return RestoreByTopology();
            }

            // Chaque activation relance Refresh() : on reprend la liste à jour à chaque tour.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Screen detached = null;
                foreach (Screen s in _screens)
                {
                    if (!s.Enabled && s.DetachedTarget != null) { detached = s; break; }
                }
                if (detached == null)
                {
                    return null;
                }
                if (EnableDetached(detached, true) != null)
                {
                    return RestoreByTopology();
                }
            }
            return null;
        }

        private string RestoreByTopology()
        {
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

            // Le snapshot est indexé par position : il suit le même décalage que les écrans actifs.
            if (this._baseline != null)
            {
                this._baseline.ShiftSources(target.Bounds.Location);
            }
            this.Refresh();
            return null;
        }

        // Position la plus proche de `proposed` où `target` touche un autre écran actif par un bord,
        // sans chevaucher personne ; les bords alignés à `tolerance` près s'aimantent.
        // Renvoie la position actuelle si aucune place n'est possible.
        public Point SnapPosition(Screen target, Point proposed, int tolerance)
        {
            List<Rectangle> others = new List<Rectangle>();
            foreach (Screen s in _screens)
            {
                if (s.Enabled && s.Bounds.Location != target.Bounds.Location) others.Add(s.Bounds);
            }

            Size size = target.Bounds.Size;
            Point best = target.Bounds.Location;
            long bestDistance = long.MaxValue;
            List<Point> candidates = new List<Point>();
            foreach (Rectangle o in others)
            {
                // À gauche / à droite de `o` : y libre tant que les bords se recouvrent.
                int y = Align(Clamp(proposed.Y, o.Top - size.Height + 1, o.Bottom - 1),
                    size.Height, o.Top, o.Bottom, tolerance);
                candidates.Add(new Point(o.Left - size.Width, y));
                candidates.Add(new Point(o.Right, y));
                // Au-dessus / en dessous : x libre.
                int x = Align(Clamp(proposed.X, o.Left - size.Width + 1, o.Right - 1),
                    size.Width, o.Left, o.Right, tolerance);
                candidates.Add(new Point(x, o.Top - size.Height));
                candidates.Add(new Point(x, o.Bottom));
            }

            foreach (Point p in candidates)
            {
                Rectangle r = new Rectangle(p, size);
                bool overlaps = false;
                foreach (Rectangle o in others)
                {
                    if (r.IntersectsWith(o)) { overlaps = true; break; }
                }
                if (overlaps) continue;
                long dx = p.X - proposed.X;
                long dy = p.Y - proposed.Y;
                if (dx * dx + dy * dy < bestDistance)
                {
                    bestDistance = dx * dx + dy * dy;
                    best = p;
                }
            }
            return best;
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        // Aligne le début (start) ou la fin (end) du segment de longueur `length` qui commence en `value`.
        private static int Align(int value, int length, int start, int end, int tolerance)
        {
            if (Math.Abs(value - start) <= tolerance) return start;
            if (Math.Abs(value + length - end) <= tolerance) return end - length;
            return value;
        }

        // Déplace `target` en `location` (coordonnées du bureau). L'écran principal reste en (0,0) :
        // si c'est lui qu'on déplace, ce sont les autres qui bougent d'autant en sens inverse.
        public string MoveScreen(Screen target, Point location)
        {
            return ApplyMove(target, location, true);
        }

        // Validation seule du déplacement (SDC_VALIDATE) : rien n'est appliqué.
        public string ValidateMove(Screen target, Point location)
        {
            return ApplyMove(target, location, false);
        }

        private string ApplyMove(Screen target, Point location, bool apply)
        {
            if (!target.Enabled)
            {
                return "Réactivez d'abord cet écran avant de le déplacer.";
            }
            Point from = target.Bounds.Location;
            if (location == from)
            {
                return null;
            }

            DisplayConfigData cfg = DisplayConfigData.Query(Native.QdcOnlyActivePaths | Native.QdcVirtualModeAware);
            if (!MoveSource(cfg, from, location))
            {
                return "Écran introuvable dans la configuration active.";
            }
            Point shift = target.IsPrimary ? location : Point.Empty;
            cfg.ShiftSources(shift);

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

            // Le snapshot est indexé par position : il suit le même déplacement.
            if (this._baseline != null)
            {
                MoveSource(this._baseline, from, location);
                this._baseline.ShiftSources(shift);
            }
            this.Refresh();
            return null;
        }

        private static bool MoveSource(DisplayConfigData cfg, Point from, Point to)
        {
            bool found = false;
            for (int m = 0; m < cfg.ModeCount; m++)
            {
                if (cfg.ModeInfoType(m) != Native.ModeTypeSource || cfg.SourcePosition(m) != from) continue;
                cfg.SetSourcePosition(m, to);
                found = true;
            }
            return found;
        }

        // Rallume un écran éteint absent du snapshot : un chemin libre vers sa sortie est ajouté à
        // la configuration active, Windows lui choisit mode et position (SDC_ALLOW_CHANGES).
        private string EnableDetached(Screen target, bool apply)
        {
            DisplayConfigData active = DisplayConfigData.Query(Native.QdcOnlyActivePaths | Native.QdcVirtualModeAware);
            DisplayConfigData all = DisplayConfigData.Query(Native.QdcAllPaths | Native.QdcVirtualModeAware);
            int path;
            if (!FindDetachedPaths(all, TargetKeys(active), SourceKeys(active)).TryGetValue(target.DetachedTarget, out path))
            {
                return "Cet écran n'est plus disponible (débranché ou sans source libre).";
            }
            DisplayConfigData cfg = active.WithActivatedPath(all, path);

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

        // Pour chaque sortie disponible hors de `knownTargets`, un chemin inactif dont la source
        // n'est pas déjà utilisée (clé de sortie -> index du chemin dans `all`).
        private static Dictionary<string, int> FindDetachedPaths(DisplayConfigData all,
            HashSet<string> knownTargets, HashSet<string> usedSources)
        {
            Dictionary<string, int> result = new Dictionary<string, int>();
            for (int i = 0; i < all.PathCount; i++)
            {
                if (all.PathIsActive(i) || !all.PathTargetAvailable(i)) continue;
                string target = all.PathTargetKey(i);
                if (knownTargets.Contains(target) || result.ContainsKey(target)) continue;
                if (usedSources.Contains(all.PathSourceKey(i))) continue;
                result[target] = i;
            }
            return result;
        }

        private static HashSet<string> TargetKeys(DisplayConfigData cfg)
        {
            HashSet<string> keys = new HashSet<string>();
            if (cfg == null) return keys;
            for (int i = 0; i < cfg.PathCount; i++)
            {
                if (cfg.PathIsActive(i)) keys.Add(cfg.PathTargetKey(i));
            }
            return keys;
        }

        private static HashSet<string> SourceKeys(DisplayConfigData cfg)
        {
            HashSet<string> keys = new HashSet<string>();
            for (int i = 0; i < cfg.PathCount; i++)
            {
                if (cfg.PathIsActive(i)) keys.Add(cfg.PathSourceKey(i));
            }
            return keys;
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
