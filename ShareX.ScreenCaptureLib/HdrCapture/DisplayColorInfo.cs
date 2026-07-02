#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Reads per-display advanced color (HDR) state and SDR reference white level
    /// through the Windows display configuration API.
    /// </summary>
    public static class DisplayColorInfo
    {
        private const float DefaultSDRWhiteLevelNits = 200f;
        private const float SDRWhiteLevelStep = 80f / 1000f;

        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public uint refreshRateNumerator;
            public uint refreshRateDenominator;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            public ulong reserved1;
            public ulong reserved2;
            public ulong reserved3;
            public ulong reserved4;
            public ulong reserved5;
            public ulong reserved6;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public int size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

        public readonly struct AdvancedColorState
        {
            public AdvancedColorState(bool advancedColorEnabled, float sdrWhiteLevelNits)
            {
                AdvancedColorEnabled = advancedColorEnabled;
                SDRWhiteLevelNits = sdrWhiteLevelNits;
            }

            /// <summary>Whether the display is currently composed in advanced color (HDR / scRGB) mode.</summary>
            public bool AdvancedColorEnabled { get; }

            /// <summary>Luminance in nits that SDR reference white is composed at on this display.</summary>
            public float SDRWhiteLevelNits { get; }
        }

        /// <summary>
        /// Returns the advanced color state for every active display, keyed by GDI device name
        /// (for example "\\.\DISPLAY1", matching <c>DXGI_OUTPUT_DESC.DeviceName</c>).
        /// </summary>
        public static Dictionary<string, AdvancedColorState> GetAdvancedColorStates()
        {
            Dictionary<string, AdvancedColorState> states = new Dictionary<string, AdvancedColorState>(StringComparer.OrdinalIgnoreCase);

            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
            {
                return states;
            }

            DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            DISPLAYCONFIG_MODE_INFO[] modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            {
                return states;
            }

            for (int i = 0; i < pathCount; i++)
            {
                DISPLAYCONFIG_PATH_INFO path = paths[i];

                DISPLAYCONFIG_SOURCE_DEVICE_NAME sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                sourceName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                sourceName.header.size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                sourceName.header.adapterId = path.sourceInfo.adapterId;
                sourceName.header.id = path.sourceInfo.id;

                if (DisplayConfigGetDeviceInfo(ref sourceName) != 0 || string.IsNullOrEmpty(sourceName.viewGdiDeviceName))
                {
                    continue;
                }

                bool advancedColorEnabled = false;

                DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO colorInfo = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                colorInfo.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                colorInfo.header.size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
                colorInfo.header.adapterId = path.targetInfo.adapterId;
                colorInfo.header.id = path.targetInfo.id;

                if (DisplayConfigGetDeviceInfo(ref colorInfo) == 0)
                {
                    // Bit 0: advancedColorSupported, bit 1: advancedColorEnabled
                    advancedColorEnabled = (colorInfo.value & 0x2) != 0;
                }

                float sdrWhiteLevelNits = DefaultSDRWhiteLevelNits;

                DISPLAYCONFIG_SDR_WHITE_LEVEL whiteLevel = new DISPLAYCONFIG_SDR_WHITE_LEVEL();
                whiteLevel.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
                whiteLevel.header.size = Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>();
                whiteLevel.header.adapterId = path.targetInfo.adapterId;
                whiteLevel.header.id = path.targetInfo.id;

                if (DisplayConfigGetDeviceInfo(ref whiteLevel) == 0 && whiteLevel.SDRWhiteLevel > 0)
                {
                    // SDRWhiteLevel is expressed in units of 1/1000 of 80 nits
                    sdrWhiteLevelNits = whiteLevel.SDRWhiteLevel * SDRWhiteLevelStep;
                }

                states[sourceName.viewGdiDeviceName] = new AdvancedColorState(advancedColorEnabled, sdrWhiteLevelNits);
            }

            return states;
        }
    }
}
