# Third-party notices

Wisp includes components that are not covered by the Wisp Proprietary Source
License.

## .NET 8.0.31

The self-contained Windows build includes components from the .NET runtime and
Windows Desktop runtime. Their exact package notices are preserved in:

- `LICENSES/dotnet-runtime-8.0.31-LICENSE.txt`
- `LICENSES/dotnet-runtime-8.0.31-THIRD-PARTY-NOTICES.txt`
- `LICENSES/windowsdesktop-runtime-8.0.31-LICENSE.txt`

The runtime license and third-party notices are copied byte-for-byte from
[Microsoft's .NET 8.0.31 runtime package](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/8.0.31).
The Windows Desktop license comes from the matching
[Microsoft Windows Desktop runtime package](https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/8.0.31).

## Forza Horizon 6 Game Content

The 240 PNG files under `src/Wisp.App/Assets/Native` are based on publicly
circulated, swatchbin-derived HUD assets from Forza Horizon 6. The Native
digital-gauge shader under `src/Wisp.App/Shaders` reproduces the corresponding
game material. Wisp uses this content only for interoperability and faithful
HUD presentation. It remains Microsoft Game Content and is not covered by the
Wisp Proprietary Source License.

Wisp is distributed free of charge for personal, non-commercial use.

Forza Horizon 6 © Microsoft Corporation. Wisp is an unofficial community
project and is not endorsed by or affiliated with Microsoft.

I claim no ownership of or license to Microsoft Game Content. Public
circulation does not itself grant rights, and Wisp does not claim that Microsoft
authorized the extraction or redistribution of these assets. Their identities
and rendering roles are recorded in the
[Native asset manifest](src/Wisp.App/Assets/Native/ASSET-MANIFEST.csv); the
[Native asset notice](src/Wisp.App/Assets/Native/THIRD-PARTY-NOTICE.txt) must
remain with every copy.

[Microsoft Game Content Usage Rules](https://www.xbox.com/en-us/developers/rules)

## FH6 Time Attack circuit coordinates

The start/finish coordinates for Legend Island, Hokubu, Soni and Sekibe come from
[t1moleh's FH6 Time Attack Tracker](https://github.com/t1moleh/Forza-Horizon-6-Time-Attack-Tracker/blob/9b41bf4f2994e9919568437dd4a3f9316385dad0/circuits.csv).
The original MIT license is included in `LICENSES/fh6-time-attack-tracker-LICENSE.txt`
and in the installer's `Licenses` directory. Wisp uses these coordinates with its
own lap tracking and native HUD.

## Lossless clip thumbnails: LibVLCSharp and LibVLC

Wisp's lossless clip thumbnail decoder uses LibVLCSharp 3.10.1 and the x64 subset of
VideoLAN.LibVLC.Windows 3.0.24. These libraries are copyright their respective
VideoLAN and LibVLCSharp contributors and are distributed under the GNU Lesser
General Public License, version 2.1 or later. Wisp claims no ownership of them.
The license text is preserved in `LICENSES/LGPL-2.1.txt`.
Original upstream notices for these libraries and their source dependencies are
preserved under `LICENSES/LibVLCThirdParty/`. Optional component and build-tool
notices identify those upstream materials; they do not relicense Wisp.

The selected LibVLC modules include libraries from the FFmpeg project. Other
native dependencies are incorporated into the library binaries; selecting fewer
plugins does not remove their license or source-distribution requirements.
`LICENSES/libvlc-3.0.24-source-manifest.json` records the exact package identities,
selected modules, upstream source locations and recipe checksums. The source
companion must include the matching upstream sources, VLC's dependency patches
and build instructions; an upstream link alone is not a supplied source bundle.

`LibVLCSharp.dll` and the `libvlc/win-x64` directory are separate, replaceable
files. With Wisp closed, keep a backup and replace them with interface-compatible
versions built for the same x64 architecture. Wisp does not require the original
library hashes at runtime. The limited LGPL replacement and debugging exception
in Wisp's `LICENSE` applies; the remaining Wisp terms are unchanged.

Official projects: [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp),
[LibVLC](https://www.videolan.org/vlc/libvlc.html),
and [FFmpeg](https://ffmpeg.org/).

## Lossless clip playback: libmpv

The lossless clip player uses the official mpv project's x64 LGPL build from
commit `a1f50f2c38206dc943f331cf5a5b02f97a0ce219`, workflow run `36640285359`.
This development build combines mpv with LGPL FFmpeg and is distributed under
LGPL version 3. The license texts are in `LICENSES/LGPL-3.0.txt` and
`LICENSES/GPL-3.0.txt`; component notices are under `LICENSES/libmpv-thirdparty/`.
Those licenses apply to the libraries, not to Wisp's original code or artwork.

`LICENSES/libmpv-source-manifest.json` identifies the exact library, upstream
build inputs, and matching source companion supplied with this installer.
The companion contains the pinned mpv and FFmpeg sources, dependency sources,
build recipes and patches, and the separate LibVLC source bundle used for
thumbnails. It does not claim a reproduced native binary build. Source archives
also include some unlinked build, test and platform dependencies; their presence
does not imply that Wisp uses those components at runtime.

`libmpv/win-x64/libmpv-2.dll` remains a separate, replaceable shared library.
With Wisp closed, back it up and replace it with an interface-compatible x64
libmpv build. Wisp does not enforce the original library hash at runtime. The
LGPL replacement and debugging exception in Wisp's `LICENSE` applies.

Official sources: [mpv](https://github.com/mpv-player/mpv),
[FFmpeg](https://ffmpeg.org/), and
[the dependency build recipes](https://github.com/BtbN/FFmpeg-Builds).

## NVIDIA video encoder API header

The recorder is compiled with NVIDIA's `nvEncodeAPI.h` API 13.0 header,
copyright (c) 2010-2024 NVIDIA Corporation, under the MIT license. Its complete
original notice is in `LICENSES/NVIDIA-nvEncodeAPI-MIT.txt`. The pinned header
comes from the [FFmpeg nv-codec-headers mirror](https://github.com/FFmpeg/nv-codec-headers/blob/e844e5b26f46bb77479f063029595293aa8f812d/include/ffnvcodec/nvEncodeAPI.h).
Wisp does not bundle or install an NVIDIA driver; it uses the compatible driver
already installed on the computer.
