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

Views2k claims no ownership of or license to Microsoft Game Content. Public
circulation does not itself grant rights, and Wisp does not claim that Microsoft
authorized the extraction or redistribution of these assets. Their identities
and rendering roles are recorded in the
[Native asset manifest](src/Wisp.App/Assets/Native/ASSET-MANIFEST.csv); the
[Native asset notice](src/Wisp.App/Assets/Native/THIRD-PARTY-NOTICE.txt) must
remain with every copy.

[Microsoft Game Content Usage Rules](https://www.xbox.com/en-us/developers/rules)
