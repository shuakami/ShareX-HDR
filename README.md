<h1 align="center">ShareX HDR</h1>

<p align="center">ShareX, but screenshots don't turn gray when Windows HDR is on.</p>

<div align="center">
  <a href="https://github.com/shuakami/ShareX-HDR/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/shuakami/ShareX-HDR/build.yml?branch=main&label=build" alt="Build Status"/></a>
  <a href="https://github.com/shuakami/ShareX-HDR/releases/latest"><img src="https://img.shields.io/github/v/release/shuakami/ShareX-HDR?label=release" alt="Latest Release"/></a>
  <a href="https://github.com/shuakami/ShareX-HDR/releases"><img src="https://img.shields.io/github/downloads/shuakami/ShareX-HDR/total?label=downloads" alt="Downloads"/></a>
  <a href="./LICENSE.txt"><img src="https://img.shields.io/github/license/shuakami/ShareX-HDR?label=license" alt="License"/></a>
</div>

---

If you play HDR games or run an HDR desktop, you've seen this: you hit Print Screen and the screenshot comes out washed out, too bright, with all the color drained out of it. It happens with stock ShareX, Snipping Tool, and basically every tool that still captures through GDI.

This fork fixes that at the capture level. Same ShareX, same uploads, same hotkeys, same everything — the pixels are just right now.

| Before (stock capture) | After (this fork) |
| --- | --- |
| ![before](docs/hdr-comparison-before.jpg) | ![after](docs/hdr-comparison-after.jpg) |

<sup>CS2 with Windows HDR enabled, same hotkey, same scene.</sup>

## Install

Download from [Releases](https://github.com/shuakami/ShareX-HDR/releases/latest) — there's an installer and a portable zip, for x64 and ARM64.

That's it. HDR handling is on by default and only kicks in on displays that are actually in HDR mode. On SDR displays this behaves exactly like stock ShareX. If you ever want to turn it off, it's the `UseHDRColorCorrection` capture task setting.

Updates are checked against this repository, not upstream ShareX, so updating won't quietly replace this build with the official one.

## Why screenshots break under HDR

With HDR enabled, Windows composes the desktop in FP16 linear scRGB, where 1.0 means 80 nits and values go way above that. GDI-based capture reads an 8-bit clipped view of that buffer — highlights blow out, midtones shift, and you get the classic gray, faded screenshot.

What this fork does instead:

- Captures through DXGI Desktop Duplication in `R16G16B16A16_FLOAT`, so the full HDR signal survives all the way to the tone mapper.
- Tone maps to SDR using the BT.2390 EETF, evaluated in the PQ domain. The curve is anchored to your monitor's actual SDR reference white (the "SDR content brightness" slider in Windows settings), so UI and text keep their exact brightness — only highlights above it get compressed.
- Estimates each frame's real peak brightness from a histogram, so a frame with no bright highlights isn't compressed at all.
- Maps luminance rather than individual channels, and desaturates out-of-gamut colors softly. Per-channel clipping shifts hues; this doesn't.
- Dithers before quantizing to 8-bit, so smooth gradients (skies, fog) don't band.

Multi-monitor mixed HDR + SDR setups are handled per display. If anything in this path fails — old GPU, remote session, whatever — it silently falls back to the standard GDI capture, so a screenshot always comes out.

## Other changes from stock ShareX

- JPEG and PNG are encoded through WIC instead of GDI+. JPEG in particular has noticeably fewer artifacts around text at the same quality setting, and both are faster.
- Region captures only read back the selected region from the GPU rather than the whole frame, which matters on 4K+ displays.
- Duplication sessions are cached per monitor, so repeated captures skip the setup cost.

## Building

Standard .NET 9 solution. `dotnet build ShareX.sln -c Release -p:Platform=x64` on Windows, or just look at [the workflow](.github/workflows/build.yml) — releases are built there.

## Credits & license

All the heavy lifting is [ShareX](https://github.com/ShareX/ShareX) by the ShareX Team — this fork only touches the capture and encoding paths. [GPL v3](./LICENSE.txt), same as upstream.
