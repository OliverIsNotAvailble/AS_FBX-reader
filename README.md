# AS_FBX-reader

Small Windows tool for FBX files exported by the custom AssetStudio/AS19 patch.

The goal is deliberately **not** to become Blender. The app is meant to do four things well:

1. open an FBX exported by AssetStudio;
2. show every visual part as a simple on/off list;
3. select and play one FBX animation/take at a time;
4. export the selected animation with the chosen part visibility to MP4/WebP/PNG frames.

## Why this exists

Some Unity 2D Animation / SpriteSkin characters export correctly to FBX but are painful to inspect in Blender because the file can contain dozens of alternate SpriteRenderer pieces and many animation takes.

The viewer keeps the workflow simple:

```text
AssetStudio -> FBX + textures -> AS_FBX-reader -> choose take -> toggle parts -> export animation
```

## Current test case

The current `agnes_h` test FBX has been checked at the FBX object level:

- 45 Geometry objects
- 45 Materials
- 40 SpriteSkin meshes
- 5 ordinary sprite meshes
- 6 Texture objects
- 9 AnimationStacks / animation takes
  - `agnes_h_0` ... `agnes_h_7`
  - `agnes_h_back`

That matches the 45 SpriteRenderer visual parts seen by the V4.2 diagnostic, so **the current AssetStudio V4.3 export contains the complete visual part set**. The remaining issue is visibility/state selection, not missing geometry.

This means the AssetStudio side can be treated as the source exporter for this case. The reader/viewer is allowed to override visibility without requiring another AS change.

## Planned UI

```text
+---------------------------------------------------------------+
| Open FBX | Animation [agnes_h_0 v] | Play | Pause | Export    |
+-----------------------+---------------------------------------+
| PARTS                 |                                       |
| [x] hair              |                                       |
| [x] body              |                VIEWPORT               |
| [ ] alt face          |                                       |
| [ ] black bar         |                                       |
| [x] left arm          |                                       |
| ...                   |                                       |
|                       |                                       |
| Show all              |                                       |
| Hide all              |                                       |
| Solo selected         |                                       |
+-----------------------+---------------------------------------+
| frame/time slider                                             |
+---------------------------------------------------------------+
```

## Project stages

### v0.1 — reader / inspector

- WinForms .NET 9
- open FBX through Assimp 6.0.5 (`AssimpNetter`)
- list meshes/parts
- list animation takes
- part on/off controls
- save/load visibility profiles as JSON
- detect texture folder relative to the FBX

### v0.2 — viewport / playback

- OpenGL viewport through OpenTK
- orthographic camera by default
- CPU bone animation/skinning
- play one take at a time
- wireframe / textured / bones toggles
- click/solo selected part

### v0.3 — render export

- PNG sequence
- MP4 through ffmpeg
- animated WebP through ffmpeg
- transparent background option
- output resolution / FPS / loop controls

### later, only if useful

- filtered FBX re-export containing only enabled parts and the selected animation
- direct `Open in AS_FBX-reader` button from AssetStudio

## Build target

- Windows 11 x64
- .NET 9 SDK

```powershell
dotnet restore .\AS_FBX-reader.sln
dotnet build .\AS_FBX-reader.sln -c Release
```

## Dependencies

- `AssimpNetter 6.0.5` — FBX import / animation data
- `OpenTK 4.9.4` — OpenGL
- `OpenTK.GLControl 4.0.2` — WinForms viewport host

The code is structured so FBX parsing is not tied to the UI.
