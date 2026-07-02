# ShareX HDR

<a href="https://github.com/shuakami/ShareX-HDR/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/shuakami/ShareX-HDR/build.yml?branch=main&label=build" alt="Build Status"/></a> <a href="https://github.com/shuakami/ShareX-HDR/releases/latest"><img src="https://img.shields.io/github/v/release/shuakami/ShareX-HDR?label=release" alt="Latest Release"/></a> <a href="https://github.com/shuakami/ShareX-HDR/releases"><img src="https://img.shields.io/github/downloads/shuakami/ShareX-HDR/total?label=downloads" alt="Downloads"/></a> <a href="./LICENSE.txt"><img src="https://img.shields.io/github/license/shuakami/ShareX-HDR?label=license" alt="License"/></a>

If you play HDR games or run an HDR desktop, you know the problem: you press Print Screen and the screenshot comes out gray, too bright, with the color drained out of it. This happens with stock ShareX, Snipping Tool, and pretty much every tool that still captures through GDI.

This fork fixes it at the capture level. Everything else about ShareX stays the same.

| Before (stock capture) | After (this fork) |
| --- | --- |
| ![before](docs/hdr-comparison-before.jpg) | ![after](docs/hdr-comparison-after.jpg) |

<sup>CS2 with Windows HDR enabled, same hotkey, same scene.</sup>

#### Install

Download the installer or portable zip from [Releases](https://github.com/shuakami/ShareX-HDR/releases/latest) (x64 and ARM64).

HDR handling is on by default and only kicks in on displays that are actually in HDR mode. On SDR displays the behavior is identical to stock ShareX. It can be turned off with the `UseHDRColorCorrection` capture task setting.

Updates are checked against this repository instead of upstream ShareX, so updating won't replace this build with the official one.

#### Why screenshots break under HDR

With HDR enabled, Windows composes the desktop in FP16 linear scRGB, where 1.0 means 80 nits and values go far above that. GDI-based capture reads an 8-bit clipped view of that buffer, so highlights blow out and everything looks faded.

What this fork does instead:

- Captures through DXGI Desktop Duplication in `R16G16B16A16_FLOAT`, keeping the full HDR signal all the way to the tone mapper.
- Tone maps to SDR with the BT.2390 EETF evaluated in the PQ domain. The curve is anchored to your monitor's actual SDR reference white (the "SDR content brightness" slider in Windows), so UI and text keep their exact brightness and only highlights above it get compressed.
- Estimates each frame's real peak brightness from a histogram, so frames without bright highlights aren't compressed at all.
- Tone maps luminance instead of individual channels and softly desaturates out-of-gamut colors, avoiding the hue shifts you get from per-channel clipping.
- Dithers before quantizing to 8-bit, so smooth gradients like skies and fog don't band.

Mixed HDR and SDR multi-monitor setups are handled per display. If anything in this path fails (old GPU, remote session, etc.), it falls back to standard GDI capture so a screenshot always comes out.

#### Other changes from stock ShareX

- JPEG and PNG are encoded through WIC instead of GDI+. JPEG in particular has noticeably fewer artifacts around text at the same quality setting, and both are faster.
- Region captures only read back the selected region from the GPU rather than the whole frame, which matters on 4K+ displays.
- Duplication sessions are cached per monitor, so repeated captures skip the setup cost.

#### Building

Standard .NET 9 solution: `dotnet build ShareX.sln -c Release -p:Platform=x64` on Windows. Releases are built by [the workflow](.github/workflows/build.yml).

#### Credits and license

All the heavy lifting is [ShareX](https://github.com/ShareX/ShareX) by the ShareX Team; this fork only touches the capture and encoding paths. [GPL v3](./LICENSE.txt), same as upstream.
