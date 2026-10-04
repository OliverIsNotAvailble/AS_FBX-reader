# AS_FBX-reader changelog

Keep this file updated whenever the reader changes. Record the behavior, files,
verification, and remaining limits in the same commit. Read it alongside the
current code when resuming work in a later conversation.

## 0.1.16 — 2026-10-04

- In progress toward 0.2.0: the main window, export dialog and mesh editor
  open maximized. Main parts and export settings sidebars start at one fifth
  of the window width after layout and remain draggable; they no longer start
  as a collapsed strip at high DPI. The main toolbar wraps when needed.
- Added Free camera to the main and export previews: mouse wheel changes zoom
  by about 12% per notch, left drag pans, and Fit view/Fit current pose resets
  the framing. In the exporter the Zoom/X/Y inputs reflect mouse navigation,
  and captured frames use that same view. Framing arrow steps are now 1%.
- Added export scope for selected takes with a checked list. Single take uses
  a file destination; all/selected takes use a folder, and an empty selection
  is rejected. The exporter's radio options stack vertically to stay readable.
- Files: `UiLayout.cs`, `MainForm.cs`, `MeshDeformForm.cs`, `ViewerControl.cs`,
  `ExportDialog.cs`, `README.md`, this changelog.
- Verification for these new changes: pending Windows CI build and in-app
  visual check at 4K/250% scaling. Keep the app version at 0.1.16 until the
  interface and behavior are confirmed before calling it 0.2.0.
- Fixed clipped text and buttons at large Windows text/DPI scaling. The mesh
  shape editor's top and bottom bars and the main toolbar now size to their
  controls; the radius input reserves space for its value and spinner.
- The export button and progress rows now grow with their controls. The
  rename dialog uses a layout instead of fixed positions, and the main
  timeline/status use their preferred heights.
- Files: `MeshDeformForm.cs`, `MainForm.cs`, `ExportDialog.cs`, this changelog.
- Verification: `git diff --check` passed. Windows build and in-app visual
  confirmation at the user's display scaling are pending.

## 0.1.15 — 2026-10-03

- Fixed the mesh editor showing imported local coordinates while the main
  preview showed the skinned animation pose. It now opens at the selected
  animation frame, pauses playback, and draws the same world-space vertex
  positions as the renderer. Drag deltas are mapped back into each vertex's
  local coordinates, so edits remain animated. Coincident duplicate vertices
  move together when the neighbor radius is zero.
- Replaced the unreliable GDI+ per-triangle image transform with direct
  triangle rasterization using the imported UVs and bilinear atlas sampling.
  This addresses the clothing editor screenshot where only one triangle had
  texture and the rest appeared as lines on a dark background.
- Files: `MeshDeformForm.cs`, `MainForm.cs`, `ViewerControl.cs`,
  `SceneRenderer.cs`, `README.md`, this changelog.
- Verification: raw `속옷` geometry/UVs rendered as a complete garment from
  `vamp_h.fbx`; `git diff --check` passed. The first Windows build found a
  variable-name collision in the editor; after fixing it, the Windows/.NET 9
  build succeeded:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37166790636.
  In-app visual confirmation remains pending.

## 0.1.14 — 2026-10-03

- Added **Edit mesh shape...** to the part context menu. The window shows a
  grid, source texture mapped to its mesh triangles, draggable vertex handles,
  adjustable neighbor-radius falloff, per-vertex reset, Undo, Reset shape,
  Apply and Cancel. It updates the main preview as vertices move.
- Applied edits to local vertices before skinning in the shared renderer, so
  playback and export use the edited geometry. Cancel restores the initial
  edits, while Apply keeps them in the current scene. `MeshAdjustments` stores
  indexed vertex offsets in profiles; invalid indices/non-finite values are
  ignored on load. The FBX on disk remains unchanged.
- Files: `MeshDeformForm.cs`, `MainForm.cs`, `ScenePart.cs`,
  `VisibilityProfile.cs`, `SceneRenderer.cs`, `README.md`, this changelog.
- Verification: `git diff --check` passed. The first Windows build caught a
  WinForms designer serialization requirement on the editor's radius property;
  that was fixed and the subsequent Windows/.NET 9 build succeeded:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37163730093.
  Interactive editor and visual texture mapping verification are pending.

## 0.1.13 — 2026-10-03

- Added per-mesh **Preserve shape (follow main bone)** when rebuilding bind
  offsets alone does not remove stretched triangles. The reader chooses the
  bone with the largest total vertex weight, applies its animated transform
  to every vertex, and keeps the source mesh/UV shape intact. This retains
  movement while intentionally removing local skin deformation on that part.
- Profiles now persist `PreserveShape` in `MeshAdjustments`; older profiles
  default to the normal per-vertex skinning mode.
- Compared the two `가슴` screenshots and independently rendered the raw
  `vamp_h.fbx` geometry/UVs with skinning off. Its 60 vertices and 77
  triangles produce a clean two-piece image; the triangular tearing happens
  during bone deformation. `bone_2` carries 35.28 of 60 total vertex weights,
  ahead of `bone_33` (15.81) and `bone_32` (8.91), so it is the selected
  motion anchor for this mesh. The mouth placement drag was confirmed by the
  user to work.
- Files: `MainForm.cs`, `ScenePart.cs`, `VisibilityProfile.cs`,
  `SceneRenderer.cs`, `README.md`, this changelog.
- Verification: raw mesh/UV reconstruction and weight totals inspected;
  Windows/.NET 9 build succeeded:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37160254915.
  In-app `vamp_h` visual verification is still pending.

## 0.1.12 — 2026-10-03

- Added right-click **Move mesh in preview (drag)**, a one-drag placement
  correction and **Reset mesh position**. The offset is applied after skinning
  in the shared preview/export renderer; automatic framing is frozen when
  entering drag mode so it does not follow the cursor. Escape cancels.
- Added per-mesh **Rebuild skin bind pose (deformed mesh)** for skinned parts.
  It uses node bind transforms in place of suspect finite skin-cluster offsets
  on that part only. The existing automatic recovery for zero-scale/NaN parts
  remains active.
- Profiles now store per-mesh X/Y offsets and bind-repair flags in
  `MeshAdjustments`. Older profiles load with zero offsets and repair disabled.
- Inspected `vamp_h.fbx`: `입오무` has zero-scale mesh and NaN skin bind
  matrices, while `가슴` has 60 vertices and complete normalized weights but
  finite, differing cluster bind matrices on its weighted bones. The mouth
  can be placed manually; the bind-repair toggle offers a rest-shape recovery
  for the distorted `가슴` without changing other meshes.
- Files: `AnimationPlayer.cs`, `MainForm.cs`, `ScenePart.cs`,
  `VisibilityProfile.cs`, `SceneRenderer.cs`, `ViewerControl.cs`, `README.md`,
  this changelog.
- Verification: raw FBX structure/weights inspected; `git diff --check`
  passed; Windows/.NET 9 build succeeded:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37156884871.
  The user confirmed that dragging the mouth into place worked; export visual
  confirmation is still needed.

## 0.1.11 — 2026-09-28

- Put the part actions on the same top toolbar row as playback/export, removed
  the separate left button row and the `TOP = FRONT` note. The toolbar scrolls
  horizontally if the window is too narrow.
- Added **Force all**. It enables and forces all parts even when FBX/manual
  visibility keys hide them; **Allow all** clears those force flags and resumes
  timing. Force flags and manual timing keys are retained in saved profiles.
- Mesh aliases can be edited inline with a double-click, F2, or the context
  menu. An empty alias restores the source FBX name.
- Dragging parts/groups shows an insertion line before or after a row. Dropping
  in the middle of a group adds to that group; blank space above/below the list
  accepts a first/last root drop. Dragging near an edge scrolls the tree.
- Files: `MainForm.cs`, `ScenePart.cs`, `README.md`, this changelog.
- Verification: `git diff --check` passed and the Windows/.NET 9 GitHub Actions
  build succeeded: https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/36495788294.
  Interactive drag/drop and layout verification on Windows remain to be done.

### Work carried into this checkout

The current working tree already contained the 0.1.10 SpriteSkin recovery,
per-part force visibility, and manually timed visibility keys when these UI
changes began. The recovery and visibility settings are preserved here. Their
source changes are in `AnimationPlayer.cs`, `SceneRenderer.cs`, `ScenePart.cs`,
`VisibilityProfile.cs`, `MainForm.cs`, and `README.md`; they still need the same
visual verification on Windows; the 0.1.11 CI build succeeded.
