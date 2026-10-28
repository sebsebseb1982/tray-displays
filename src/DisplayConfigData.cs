using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TrayDisplays
{
    // Configuration DISPLAYCONFIG dans des buffers bruts (little-endian).
    // paths : PathCount * 72 octets, modes : ModeCount * 64 octets.
    internal sealed class DisplayConfigData
    {
        private readonly byte[] _paths;
        private readonly byte[] _modes;

        public readonly int PathCount;
        public readonly int ModeCount;

        public DisplayConfigData(byte[] paths, byte[] modes, int pathCount, int modeCount)
        {
            this._paths = paths;
            this._modes = modes;
            this.PathCount = pathCount;
            this.ModeCount = modeCount;
        }

        public DisplayConfigData Clone()
        {
            return new DisplayConfigData(
                (byte[])this._paths.Clone(),
                (byte[])this._modes.Clone(),
                this.PathCount,
                this.ModeCount);
        }

        // --- lecture ---

        public uint PathFlags(int i)
        {
            return ReadU32(this._paths, i * Native.PathSize + Native.PathFlags);
        }

        public bool PathIsActive(int i)
        {
            return (this.PathFlags(i) & Native.PathActive) != 0;
        }

        public int PathAdapterId(int i)
        {
            return ReadInt(this._paths, i * Native.PathSize + Native.PathSrc);
        }

        public int PathSourceId(int i)
        {
            return ReadInt(this._paths, i * Native.PathSize + Native.PathSrc + Native.SrcId);
        }

        public int PathTargetId(int i)
        {
            return ReadInt(this._paths, i * Native.PathSize + Native.PathTgt + Native.TgtId);
        }

        // Identifiants stables d'une sortie (target) / d'une source : LUID de l'adaptateur + id.
        public string PathTargetKey(int i)
        {
            int tgt = i * Native.PathSize + Native.PathTgt;
            return string.Format("{0:x8}{1:x8}/{2}", ReadU32(this._paths, tgt + Native.TgtAdapterId + 4),
                ReadU32(this._paths, tgt + Native.TgtAdapterId), ReadU32(this._paths, tgt + Native.TgtId));
        }

        public string PathSourceKey(int i)
        {
            int src = i * Native.PathSize + Native.PathSrc;
            return string.Format("{0:x8}{1:x8}/{2}", ReadU32(this._paths, src + Native.SrcAdapterId + 4),
                ReadU32(this._paths, src + Native.SrcAdapterId), ReadU32(this._paths, src + Native.SrcId));
        }

        // high 16 bits de src.modeInfoIdx -> index du mode source
        public int SourceModeIndex(int i)
        {
            uint v = ReadU32(this._paths, i * Native.PathSize + Native.PathSrc + Native.SrcModeInfoIdx);
            return (int)((v >> 16) & 0xFFFF);
        }

        // high 16 bits de tgt.modeInfoIdx -> index du mode target
        public int TargetModeIndex(int i)
        {
            uint v = ReadU32(this._paths, i * Native.PathSize + Native.PathTgt + Native.TgtModeInfoIdx);
            return (int)((v >> 16) & 0xFFFF);
        }

        // low 16 bits de tgt.modeInfoIdx -> index du mode desktop
        public int DesktopModeIndex(int i)
        {
            uint v = ReadU32(this._paths, i * Native.PathSize + Native.PathTgt + Native.TgtModeInfoIdx);
            return (int)(v & 0xFFFF);
        }

        // Nom du modèle de moniteur (EDID) via DisplayConfigGetDeviceInfo ; null si indisponible.
        // Paquet : header {type=2, size=420, adapterId LUID@8, id@16}, nom UTF-16 de 64 car. @36.
        public string TargetFriendlyName(int i)
        {
            const int packetSize = 420;
            const int targetDeviceName = 2;
            byte[] packet = new byte[packetSize];
            int tgt = i * Native.PathSize + Native.PathTgt;
            WriteU32(packet, 0, targetDeviceName);
            WriteU32(packet, 4, packetSize);
            Buffer.BlockCopy(this._paths, tgt + Native.TgtAdapterId, packet, 8, 8);
            Buffer.BlockCopy(this._paths, tgt + Native.TgtId, packet, 16, 4);

            GCHandle h = GCHandle.Alloc(packet, GCHandleType.Pinned);
            try
            {
                if (Native.DisplayConfigGetDeviceInfo(h.AddrOfPinnedObject()) != 0)
                {
                    return null;
                }
            }
            finally
            {
                h.Free();
            }

            string name = System.Text.Encoding.Unicode.GetString(packet, 36, 128);
            int end = name.IndexOf('\0');
            if (end >= 0)
            {
                name = name.Substring(0, end);
            }
            name = name.Trim();
            return name.Length == 0 ? null : name;
        }

        public bool PathTargetAvailable(int i)
        {
            return ReadU32(this._paths, i * Native.PathSize + Native.PathTgt + Native.TgtAvailable) != 0;
        }

        public uint PathOutputTechnology(int i)
        {
            return ReadU32(this._paths, i * Native.PathSize + Native.PathTgt + Native.TgtOutputTechnology);
        }

        public uint ModeInfoType(int m)
        {
            return ReadU32(this._modes, m * Native.ModeSize + Native.ModeInfoType);
        }

        public Point SourcePosition(int m)
        {
            return new Point(
                ReadInt(this._modes, m * Native.ModeSize + Native.SmPosX),
                ReadInt(this._modes, m * Native.ModeSize + Native.SmPosY));
        }

        public Size SourceSize(int m)
        {
            return new Size(
                (int)ReadU32(this._modes, m * Native.ModeSize + Native.SmWidth),
                (int)ReadU32(this._modes, m * Native.ModeSize + Native.SmHeight));
        }

        public Rectangle DesktopRegion(int m)
        {
            return Rectangle.FromLTRB(
                ReadInt(this._modes, m * Native.ModeSize + Native.DiRegionLeft),
                ReadInt(this._modes, m * Native.ModeSize + Native.DiRegionTop),
                ReadInt(this._modes, m * Native.ModeSize + Native.DiRegionRight),
                ReadInt(this._modes, m * Native.ModeSize + Native.DiRegionBottom));
        }

        public Size TargetResolution(int m)
        {
            return new Size(
                (int)ReadU32(this._modes, m * Native.ModeSize + Native.TmActiveCx),
                (int)ReadU32(this._modes, m * Native.ModeSize + Native.TmActiveCy));
        }

        public int TargetRefreshHz(int m)
        {
            uint num = ReadU32(this._modes, m * Native.ModeSize + Native.TmRefreshNumerator);
            uint den = ReadU32(this._modes, m * Native.ModeSize + Native.TmRefreshDenominator);
            if (den == 0)
            {
                return 0;
            }
            return (int)Math.Round((double)num / (double)den);
        }

        // --- écriture ---

        public void SetPathFlags(int i, uint flags)
        {
            WriteU32(this._paths, i * Native.PathSize + Native.PathFlags, flags);
        }

        public void SetSourcePosition(int m, Point p)
        {
            WriteU32(this._modes, m * Native.ModeSize + Native.SmPosX, (uint)p.X);
            WriteU32(this._modes, m * Native.ModeSize + Native.SmPosY, (uint)p.Y);
        }

        // Copie de la configuration avec, en plus, le chemin `path` de `from` activé sans mode
        // (index invalides) : avec SDC_ALLOW_CHANGES, Windows lui choisit mode et position.
        // En mode « virtual mode aware », le chemin doit avoir son propre groupe de clonage
        // (low16 de src.modeInfoIdx), sinon SetDisplayConfig renvoie ERROR_INVALID_PARAMETER.
        public DisplayConfigData WithActivatedPath(DisplayConfigData from, int path)
        {
            int cloneGroup = 0;
            for (int i = 0; i < this.PathCount; i++)
            {
                uint group = ReadU32(this._paths, i * Native.PathSize + Native.PathSrc + Native.SrcModeInfoIdx) & 0xFFFF;
                if (group != Native.InvalidModeIndex && group >= cloneGroup)
                {
                    cloneGroup = (int)group + 1;
                }
            }

            byte[] paths = new byte[(this.PathCount + 1) * Native.PathSize];
            Buffer.BlockCopy(this._paths, 0, paths, 0, this.PathCount * Native.PathSize);
            Buffer.BlockCopy(from._paths, path * Native.PathSize, paths, this.PathCount * Native.PathSize, Native.PathSize);

            DisplayConfigData cfg = new DisplayConfigData(paths, (byte[])this._modes.Clone(), this.PathCount + 1, this.ModeCount);
            int added = this.PathCount * Native.PathSize;
            WriteU32(paths, added + Native.PathSrc + Native.SrcModeInfoIdx, 0xFFFF0000 | (uint)cloneGroup);
            WriteU32(paths, added + Native.PathTgt + Native.TgtModeInfoIdx, 0xFFFFFFFF);
            cfg.SetPathFlags(this.PathCount, cfg.PathFlags(this.PathCount) | Native.PathActive);
            return cfg;
        }

        // Déplace toutes les sources de -origin : l'écran qui était en `origin` passe en (0,0).
        public void ShiftSources(Point origin)
        {
            for (int m = 0; m < this.ModeCount; m++)
            {
                if (this.ModeInfoType(m) != Native.ModeTypeSource) continue;
                Point p = this.SourcePosition(m);
                this.SetSourcePosition(m, new Point(p.X - origin.X, p.Y - origin.Y));
            }
        }

        // --- requête / application ---

        public static DisplayConfigData Query(uint flags)
        {
            uint numPaths;
            uint numModes;
            uint hr = Native.GetDisplayConfigBufferSizes(flags, out numPaths, out numModes);
            if (hr != Native.ErrorSuccess)
            {
                throw new DisplayApiException("QueryDisplayConfig (size) a échoué", hr);
            }

            byte[] paths = new byte[numPaths * Native.PathSize];
            byte[] modes = new byte[numModes * Native.ModeSize];

            GCHandle ph = GCHandle.Alloc(paths, GCHandleType.Pinned);
            GCHandle mh = GCHandle.Alloc(modes, GCHandleType.Pinned);
            try
            {
                hr = Native.QueryDisplayConfig(
                    flags, ref numPaths, ph.AddrOfPinnedObject(), ref numModes, mh.AddrOfPinnedObject(), IntPtr.Zero);
                if (hr != Native.ErrorSuccess)
                {
                    throw new DisplayApiException("QueryDisplayConfig a échoué", hr);
                }
            }
            finally
            {
                ph.Free();
                mh.Free();
            }

            return new DisplayConfigData(paths, modes, (int)numPaths, (int)numModes);
        }

        // flags doivent contenir SDC_USE_SUPPLIED et (SDC_VALIDATE ou SDC_APPLY), jamais les deux.
        public uint Apply(uint flags)
        {
            GCHandle ph = GCHandle.Alloc(this._paths, GCHandleType.Pinned);
            GCHandle mh = GCHandle.Alloc(this._modes, GCHandleType.Pinned);
            try
            {
                return Native.SetDisplayConfig(
                    (uint)this.PathCount,
                    ph.AddrOfPinnedObject(),
                    (uint)this.ModeCount,
                    mh.AddrOfPinnedObject(),
                    flags,
                    IntPtr.Zero);
                // NOTE : SetDisplayConfig peut réécrire les buffers en place ; ils ne sont réutilisés
                // que si l'appel réussit, et un Refresh() recapturé ensuite.
            }
            finally
            {
                ph.Free();
                mh.Free();
            }
        }

        // Codes WIN32 attendus pour Query/SetDisplayConfig.
        public static string DescribeError(uint hr)
        {
            switch (hr)
            {
                case 0: return "OK";
                case 2: return "ERROR_NOT_FOUND (2) : aucun écran correspondant";
                case 31: return "ERROR_GEN_FAILURE (31) : le pilote a refusé la configuration";
                case 50: return "ERROR_NOT_SUPPORTED (50) : opération non supportée";
                case 87: return "ERROR_INVALID_PARAMETER (87) : configuration invalide";
                case 120: return "ERROR_CALL_NOT_IMPLEMENTED (120)";
                case 122: return "ERROR_INSUFFICIENT_BUFFER (122) : buffer trop petit";
                case 259: return "ERROR_NO_MORE_ITEMS (259) : rien à modifier";
                case 5: return "ERROR_ACCESS_DENIED (5)";
                default: return string.Format("Erreur Windows {0}", hr);
            }
        }

        private static uint ReadU32(byte[] buf, int offset)
        {
            return BitConverter.ToUInt32(buf, offset);
        }

        private static int ReadInt(byte[] buf, int offset)
        {
            return BitConverter.ToInt32(buf, offset);
        }

        private static void WriteU32(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
