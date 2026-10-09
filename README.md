# AS_FBX-reader

Current version: **0.2.2**.

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

The main window and export dialog open maximized. Their left panels start at
about one fifth of the width and can be resized by dragging the splitter.
On narrow windows, the main toolbar wraps to keep its buttons visible.

Enable **Free camera** to zoom with the mouse wheel (roughly 12% per notch)
and pan with left drag in either preview. In the main window, **Fit view**
resets the view. In the export dialog, mouse navigation changes the framing
used by the final video and keeps the numeric Zoom/X/Y fields in sync; the red
border marks the output frame. **Fit current pose** resets that framing.

The export scope offers **Only this take**, **All takes**, or **Selected takes**.
The last option reveals a checked list of takes to export. All/selected takes
write separate files to the chosen folder; a single take uses a file path.
The export controls and progress remain visible at the bottom of the settings
panel. **Cancel export** stops after the current frame save, or terminates
FFmpeg if it is encoding. A canceled take leaves no partial final file; takes
already completed in a batch remain in the output folder. Settings can be
changed again after the job stops.

## Visibility during an animation

A checked part is **allowed**. The reader follows the FBX mesh-scale 0/1 keys
to switch that part on and off at the recorded times. These switches are sampled
as steps, so a mouth does not shrink or appear as a translucent ghost between
two keyframes. FBX parts exported at zero scale are reconstructed for rendering,
but stay hidden until an animation key turns them on.

If AssetStudio did not export a particular switch, pause or scrub to the desired
time, right-click the part, and choose **Set visible at ...** or **Set hidden at
...**. For a mouth swap, add an OFF key to mouth A and an ON key to mouth B at
the same time; set their initial states at 0 seconds. The most recent manual key
for that take applies until the next key or the loop restarts. Right-click again
to remove a key. Save the profile to keep these timing keys.

**Force visible (ignore FBX swaps)** is a per-part inspection override. Soloing
one part also forces that part visible so even a normally hidden mesh can be
examined. Uncheck the force option to return to the FBX timing.

The top toolbar has **Force all** to expose every part, including those hidden
by FBX or manual timing keys. **Allow all** clears the forced state and resumes
the FBX/manual timing while keeping all parts enabled. **Hide all** disables all
parts. These choices and the per-part aliases are stored in the profile.

Double-click a mesh name, press F2, or use **Rename...** in its context menu to
edit its alias directly in the tree. A blank alias restores the FBX name.
Drag a mesh or group above/below a row to insert it at the blue line; dragging
to the middle of a group puts it inside that group. The tree scrolls while
dragging near its top or bottom edge. The top of the tree renders in front.

Select multiple meshes in the parts tree with **Ctrl+click** to toggle individual
rows or **Shift+click** to select the range between rows. Drag the selected
meshes together to move them as a batch in the layer tree. To permanently
remove unwanted spline/FFD pieces from the *active scene*, press **Delete** or
choose **Exclude selected meshes** in the context menu. These excluded IDs are
saved to the profile as `ExcludedPartIds`, and loading a saved profile removes
them from the active tree, preview and export automatically. The source FBX is
never changed, and switching profiles can bring excluded pieces back. Exclusion
does not bypass the initial import of the original FBX.

To correct a part's placement, right-click its row and choose **Move mesh in
preview (drag)**, then drag anywhere in the preview. If several meshes are selected, the same
movement is applied to the whole selection. Release to finish, or press Esc
to cancel. **Reset mesh position** removes the correction. This
shifts the whole mesh after its animation and skinning, so playback and export
use the same placement. Save the profile to keep it; the FBX itself is not
rewritten.

For a mesh whose vertices are deformed by bad skin bind data, right-click and
toggle **Rebuild skin bind pose (deformed mesh)**. It reconstructs that mesh's
bone offsets from the FBX node hierarchy, making the undeformed mesh its rest
shape while retaining animated bone movement. This is per mesh and is stored
in the profile; toggle it off if the FBX's original bind data looks better.

If the bind repair still leaves stretched triangles, choose **Preserve shape
(follow main bone)** on that part. It draws the original vertex/UV shape as
one piece while moving with the bone that carries the most weight. This
avoids tearing caused by incompatible weights, but removes that part's local
skin deformation. It is a per-mesh, profile-saved option; normal parts keep
their existing skinning.

To try a different bone, right-click the part and choose **Edit bone
binding...**. The window lists the mesh's original bones and weights, plus
rig nodes grouped by the meshes that use them. Search for a related mesh such
as "face", select its bone, and scrub through the animation. The skeleton and
mesh outline in the editor and the main preview update as you try choices.
Wheel zooms and dragging pans the skeleton view; Fit mesh and Fit mesh + rig
reset its view. **Original skin** restores the FBX's weighted deformation;
**Automatic** follows this mesh's strongest original bone. Choosing another
bone preserves the mesh's rest shape and follows that bone rigidly. Apply
keeps the selection in the current scene; Cancel restores the old binding.
Save profile to persist it for preview and export. The source FBX stays intact.

For hand edits, right-click a part and open **Edit mesh shape...**. The editor
shows its texture, the original triangles and draggable vertex handles over
a grid. **Neighbor radius** bends nearby vertices along with the selected
one; zero moves one vertex. Ctrl+click several handles to select them, then
drag any selected handle to move the group. The radius also affects their
neighbors. Right-click a selected handle to reset the group. Enable **Free
camera** in this editor to pan with left drag and zoom with the mouse wheel;
**Fit view** restores the editor's view. Uncheck it to drag vertices again.
Camera navigation changes only the editor view, never the saved mesh shape.
Use Undo/Reset shape for broader corrections, and Apply to keep the changes in the current
scene. The main preview updates while dragging. Save the profile to persist
the vertex changes; Cancel restores the shape from before opening the editor.
Edits change local vertex positions before skinning and are used by both
preview and export. The source FBX file is not modified.
The editor shows the mesh as it appears at the current animation time, so
pause or scrub to the frame you want before opening it. Playback pauses while
the window is open and resumes afterward. Its handles follow the animated
vertices; repeated vertices at the same point move together.

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
