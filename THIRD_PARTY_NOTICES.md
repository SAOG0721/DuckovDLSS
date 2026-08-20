# Third-party notices

DuckovDLSS is an independent community mod and is not affiliated with or endorsed by Team Soda, Bilibili, Unity, or NVIDIA.

## NVIDIA DLSS runtime

The binary release includes the NVIDIA-signed `nvngx_dlss.dll`. NVIDIA DLSS, NGX, GeForce RTX, and related names are trademarks and technology of NVIDIA Corporation. The NVIDIA license supplied with the runtime is included in the release archive as `nvngx_dlss.license.txt`.

## NVIDIA NGX development files

The native source builds against NGX headers and an import library from NVIDIA Streamline SDK 2.12. Those SDK files are not included in this repository; developers must obtain them from NVIDIA and comply with the applicable terms.

## Unity NativeRenderingPlugin headers

The native source uses Unity's NativeRenderingPlugin interface headers. Their MIT license is included at `licenses/Unity-NativeRenderingPlugin-MIT.txt`. The Unity headers themselves are not copied into this repository.

## Game assets and code

The repository does not contain Escape from Duckov assemblies, assets, saves, or generated game-code output. Building the managed project requires the user to provide a legally installed copy of the game.
