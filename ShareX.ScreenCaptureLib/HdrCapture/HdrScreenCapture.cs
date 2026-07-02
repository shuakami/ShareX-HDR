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

using ShareX.HelpersLib;
using SharpGen.Runtime;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// HDR-aware screen capture built on DXGI Desktop Duplication.
    ///
    /// When Windows advanced color (HDR) is enabled, the desktop is composed in FP16 linear scRGB,
    /// which legacy GDI capture cannot represent: it silently clips and mis-encodes the frame,
    /// producing the classic washed-out / overbright screenshots. This backend captures the desktop
    /// duplication stream in DXGI_FORMAT_R16G16B16A16_FLOAT end to end and tone maps it to SDR with
    /// <see cref="HdrToneMapper"/> using each monitor's actual SDR reference white level.
    ///
    /// Duplication sessions are cached per output, so repeated captures (auto capture, screen
    /// recording) avoid the expensive duplication setup and only pay for a GPU copy + tone map.
    /// </summary>
    public static class HdrScreenCapture
    {
        private const int AcquireFrameTimeoutMs = 200;

        private static readonly object syncLock = new object();
        private static readonly FeatureLevel[] featureLevels =
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0
        };

        private static IDXGIFactory1 factory;
        private static readonly List<OutputSession> sessions = new List<OutputSession>();

        private sealed class OutputSession : IDisposable
        {
            public ID3D11Device Device;
            public ID3D11DeviceContext Context;
            public IDXGIOutputDuplication Duplication;
            public ID3D11Texture2D LastFrame;
            public ID3D11Texture2D StagingTexture;
            public Rectangle DesktopBounds;
            public string DeviceName;
            public bool IsHDR;
            public float SDRWhiteLevelNits;
            public float MaxLuminanceNits;
            public bool HasFrame;

            public void Dispose()
            {
                StagingTexture?.Dispose();
                LastFrame?.Dispose();
                Duplication?.Dispose();
                Context?.Dispose();
                Device?.Dispose();
            }
        }

        /// <summary>
        /// Returns true if any display intersecting <paramref name="rect"/> is currently in HDR
        /// (advanced color) mode, meaning GDI capture would produce incorrect colors.
        /// </summary>
        public static bool IsHDRCaptureRequired(Rectangle rect)
        {
            try
            {
                foreach (KeyValuePair<string, DisplayColorInfo.AdvancedColorState> state in DisplayColorInfo.GetAdvancedColorStates())
                {
                    if (state.Value.AdvancedColorEnabled)
                    {
                        Rectangle bounds = GetDisplayBounds(state.Key);
                        if (!bounds.IsEmpty && bounds.IntersectsWith(rect))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR display state query failed.");
            }

            return false;
        }

        private static Rectangle GetDisplayBounds(string deviceName)
        {
            foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
            {
                if (string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return screen.Bounds;
                }
            }

            return Rectangle.Empty;
        }

        /// <summary>
        /// Captures <paramref name="rect"/> (virtual desktop coordinates) with correct HDR handling.
        /// Returns null if capture is not possible, in which case the caller should fall back to GDI.
        /// </summary>
        public static Bitmap CaptureRectangle(Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return null;
            }

            lock (syncLock)
            {
                try
                {
                    EnsureSessions();

                    List<OutputSession> intersecting = new List<OutputSession>();

                    foreach (OutputSession session in sessions)
                    {
                        if (session.DesktopBounds.IntersectsWith(rect))
                        {
                            intersecting.Add(session);
                        }
                    }

                    if (intersecting.Count == 0)
                    {
                        return null;
                    }

                    Bitmap bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);

                    try
                    {
                        BitmapData bmpData = bmp.LockBits(new Rectangle(0, 0, rect.Width, rect.Height), ImageLockMode.WriteOnly,
                            PixelFormat.Format32bppArgb);

                        try
                        {
                            foreach (OutputSession session in intersecting)
                            {
                                CaptureOutputRegion(session, rect, bmpData);
                            }
                        }
                        finally
                        {
                            bmp.UnlockBits(bmpData);
                        }

                        return bmp;
                    }
                    catch
                    {
                        bmp.Dispose();
                        throw;
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "HDR capture failed, falling back to GDI capture.");
                    ResetSessions();
                    return null;
                }
            }
        }

        private static void EnsureSessions()
        {
            if (factory == null || !factory.IsCurrent || sessions.Count == 0)
            {
                ResetSessions();
                CreateSessions();
            }
        }

        private static void ResetSessions()
        {
            foreach (OutputSession session in sessions)
            {
                session.Dispose();
            }

            sessions.Clear();

            factory?.Dispose();
            factory = null;
        }

        private static void CreateSessions()
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            Dictionary<string, DisplayColorInfo.AdvancedColorState> colorStates = DisplayColorInfo.GetAdvancedColorStates();

            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    ID3D11Device device = null;

                    for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Success; outputIndex++)
                    {
                        using (output)
                        using (IDXGIOutput6 output6 = output.QueryInterface<IDXGIOutput6>())
                        {
                            OutputDescription1 desc = output6.Description1;

                            if (!desc.AttachedToDesktop || desc.Rotation != ModeRotation.Identity)
                            {
                                continue;
                            }

                            if (device == null)
                            {
                                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                    featureLevels, out device).CheckError();
                            }

                            ID3D11Device sessionDevice = device.QueryInterface<ID3D11Device>();

                            OutputSession session = new OutputSession
                            {
                                Device = sessionDevice,
                                Context = sessionDevice.ImmediateContext,
                                DesktopBounds = new Rectangle(desc.DesktopCoordinates.Left, desc.DesktopCoordinates.Top,
                                    desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                                    desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top),
                                DeviceName = desc.DeviceName,
                                IsHDR = desc.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020,
                                MaxLuminanceNits = desc.MaxLuminance,
                                SDRWhiteLevelNits = 200f
                            };

                            if (colorStates.TryGetValue(desc.DeviceName, out DisplayColorInfo.AdvancedColorState state))
                            {
                                session.IsHDR |= state.AdvancedColorEnabled;
                                session.SDRWhiteLevelNits = state.SDRWhiteLevelNits;
                            }

                            try
                            {
                                // Request FP16 so HDR desktops are captured in the canonical scRGB
                                // composition space; SDR desktops still arrive as BGRA8
                                Format[] supportedFormats = { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm };
                                session.Duplication = output6.DuplicateOutput1(device, (uint)supportedFormats.Length, supportedFormats);
                            }
                            catch (Exception e)
                            {
                                DebugHelper.WriteException(e, $"Desktop duplication unavailable for {desc.DeviceName}.");
                                session.Dispose();
                                continue;
                            }

                            DebugHelper.WriteLine($"Desktop duplication session created for {desc.DeviceName}: HDR={session.IsHDR}, " +
                                $"SDRWhiteLevel={session.SDRWhiteLevelNits}nits, MaxLuminance={session.MaxLuminanceNits}nits");

                            sessions.Add(session);
                        }
                    }

                    device?.Dispose();
                }
            }
        }

        private static void CaptureOutputRegion(OutputSession session, Rectangle rect, BitmapData bmpData)
        {
            UpdateLastFrame(session);

            if (!session.HasFrame)
            {
                throw new InvalidOperationException($"No desktop frame available for {session.DeviceName}.");
            }

            Rectangle intersection = Rectangle.Intersect(session.DesktopBounds, rect);

            Texture2DDescription frameDesc = session.LastFrame.Description;

            if (session.StagingTexture == null ||
                session.StagingTexture.Description.Width != frameDesc.Width ||
                session.StagingTexture.Description.Height != frameDesc.Height ||
                session.StagingTexture.Description.Format != frameDesc.Format)
            {
                session.StagingTexture?.Dispose();

                Texture2DDescription stagingDesc = new Texture2DDescription
                {
                    Width = frameDesc.Width,
                    Height = frameDesc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = frameDesc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                };

                session.StagingTexture = session.Device.CreateTexture2D(stagingDesc);
            }

            session.Context.CopyResource(session.StagingTexture, session.LastFrame);

            MappedSubresource mapped = session.Context.Map(session.StagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                Rectangle sourceRect = new Rectangle(intersection.X - session.DesktopBounds.X,
                    intersection.Y - session.DesktopBounds.Y, intersection.Width, intersection.Height);

                IntPtr destination = bmpData.Scan0
                    + (nint)((long)(intersection.Y - rect.Y) * bmpData.Stride
                    + (long)(intersection.X - rect.X) * 4);

                if (frameDesc.Format == Format.R16G16B16A16_Float)
                {
                    HdrToneMapper.ConvertScRgbToSdr(mapped.DataPointer, (int)mapped.RowPitch, sourceRect,
                        destination, bmpData.Stride, session.SDRWhiteLevelNits, session.MaxLuminanceNits);
                }
                else
                {
                    CopyBgra(mapped.DataPointer, (int)mapped.RowPitch, sourceRect, destination, bmpData.Stride);
                }
            }
            finally
            {
                session.Context.Unmap(session.StagingTexture, 0);
            }
        }

        private static unsafe void CopyBgra(IntPtr source, int sourceRowPitch, Rectangle sourceRect, IntPtr destination, int destinationRowPitch)
        {
            byte* srcBase = (byte*)source;
            byte* dstBase = (byte*)destination;

            for (int y = 0; y < sourceRect.Height; y++)
            {
                byte* srcRow = srcBase + (long)(sourceRect.Y + y) * sourceRowPitch + (long)sourceRect.X * 4;
                byte* dstRow = dstBase + (long)y * destinationRowPitch;

                Buffer.MemoryCopy(srcRow, dstRow, (long)sourceRect.Width * 4, (long)sourceRect.Width * 4);

                // Desktop duplication alpha is undefined; force opaque
                for (int x = 3; x < sourceRect.Width * 4; x += 4)
                {
                    dstRow[x] = 255;
                }
            }
        }

        private static void UpdateLastFrame(OutputSession session)
        {
            // The first frame after starting duplication can take a few vsyncs to arrive,
            // especially on a static desktop, so retry before giving up
            int attempts = session.HasFrame ? 1 : 5;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (TryAcquireFrame(session) || session.HasFrame)
                {
                    return;
                }
            }

            if (!session.HasFrame)
            {
                throw new InvalidOperationException($"Desktop duplication produced no frame for {session.DeviceName}.");
            }
        }

        private static bool TryAcquireFrame(OutputSession session)
        {
            Result result = session.Duplication.AcquireNextFrame(AcquireFrameTimeoutMs, out OutduplFrameInfo frameInfo,
                out IDXGIResource desktopResource);

            if (result.Success)
            {
                try
                {
                    using (ID3D11Texture2D frameTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
                    {
                        Texture2DDescription desc = frameTexture.Description;

                        if (session.LastFrame == null ||
                            session.LastFrame.Description.Width != desc.Width ||
                            session.LastFrame.Description.Height != desc.Height ||
                            session.LastFrame.Description.Format != desc.Format)
                        {
                            session.LastFrame?.Dispose();

                            Texture2DDescription copyDesc = new Texture2DDescription
                            {
                                Width = desc.Width,
                                Height = desc.Height,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = desc.Format,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Default,
                                BindFlags = BindFlags.None,
                                CPUAccessFlags = CpuAccessFlags.None,
                                MiscFlags = ResourceOptionFlags.None
                            };

                            session.LastFrame = session.Device.CreateTexture2D(copyDesc);
                        }

                        session.Context.CopyResource(session.LastFrame, frameTexture);
                        session.HasFrame = true;
                    }
                }
                finally
                {
                    desktopResource.Dispose();
                    session.Duplication.ReleaseFrame();
                }

                return true;
            }

            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                // Desktop has not changed since the last acquired frame; a cached copy stays valid
                return false;
            }

            result.CheckError();
            return false;
        }
    }
}
