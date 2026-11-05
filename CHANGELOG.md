# AS_FBX-reader changelog

Keep this file updated whenever the reader changes. Record the behavior, files,
verification, and remaining limits in the same commit. Read it alongside the
current code when resuming work in a later conversation.

## VLote 1.1.2 — 2026-11-04 — Mitigate native AssimpNetter double-free during batch import

- Analyzed a real user WinDbg full crash dump. Confirmed `0xC0000374` / `HEAP_CORRUPTION_ACTIONABLE_BlockNotBusy_DOUBLE_FREE` during `FbxSession.Open → AssimpContext.ImportFile → ApplyPostProcessing → aiReleaseImport`. This occurred on the batch folder scan before model preview, not in OpenGL or animation playback.
- Confirmed against AssimpNetter 6.0.5 source (`Saalvage/AssimpNetter`, `AssimpNet/AssimpContext.cs`): `ImportFile` first imports with `PostProcessSteps.None`, then explicitly runs `ApplyPostProcessing`. If native postprocessing rejects/invalidates a scene, the wrapper may still invoke `ReleaseImport` on a stale scene pointer in `finally`. Validation is strict and can reject imperfect FBX models.
- VLote's automatic queue now uses `FbxSession.Open(..., safeBatchImport: true)` and passes **no native postprocessing flags** to AssimpNetter. This avoids the specific failure path in the crash trace and retains FBX loading, bone/animation data, and the custom source visibility parser. Manual `Open FBX` keeps its previous native flags and rendering behavior unchanged.
- Added `Rendering/MeshTriangleIndices.cs` and replaced GL draw/pick indices with safe managed face triangulation. This preserves already-triangular polygons, converts larger polygons with a triangle fan, and skips invalid/degenerate face references without invoking native triangulation. A concave n-gon might need a more advanced triangulator; AssetStudio sprite meshes usually contain triangles.
- Added smoke tests for triangles, quads, pentagons and malformed faces; preview status now notes safe import. No changes to the recovered static SpriteSkin switches, export settings, queue sorting, textures or profiles.
- Files: `FbxSession.cs`, `MainForm.cs`, `VLoteMainForm.cs`, `SceneRenderer.cs`, new `MeshTriangleIndices.cs`, `tests/VLoteSmoke/Program.cs`, csproj, workflow artifact name and this changelog.
- The actual user-triggering FBX and repeated batch scanning still require a Windows run. This addresses the dump's *postprocessing* failure path; unrelated crashes inside base FBX import would require process isolation.

## VLote 1.1.1 — 2026-11-04 — Fix intermittent IndexOutOfRange on consecutive FBX loads

- Fixed a crash when switching from a model with many animations to another model with fewer animations. The previous FBX's selected animation index survived `AnimationPlayer.Rebuild()`; `SelectAnimation` could keep that invalid index, and `EvaluateNode` then accessed `_session.Animations[AnimationIndex]`, producing `System.ArgumentOutOfRangeException` before the new model's animation UI could select take 0.
- `AnimationPlayer.Rebuild()` now clears the previous take, playback and scene-tree references before reconstructing the imported FBX. The MainForm animation dropdown still selects the new FBX's first animation normally after loading.
- `SelectAnimation` now normalizes indices against **both** Assimp's imported animation count and the indexed take list, falling back to a safe rest pose for absent/invalid indices. The visibility evaluator uses the validated current take name, avoiding stale list access.
- Added smoke regression checks for 12-take to 3-take switches, catalog-count mismatches, empty scenes, valid indices and playback reset after a rebuild.
- This is a **reader-only** safety fix. FBX files, textures, export settings, fixed output layout, SpriteSkin visibility and VLote queue classification are unchanged.
- Changed `Core/AnimationPlayer.cs`, `tests/VLoteSmoke/Program.cs`, version strings, workflow artifact label and this changelog.
- Verification: Windows .NET 9 Release GitHub Actions build and VLote smoke suite required before distributing the new artifact; reproducing the exact user FBX sequence on Windows remains a recommended visual test.

## VLote 1.1 — 2026-11-04 — Live2D-style fixed outputs and shared background controls

- Compared Live2D v0.1.10 (`AS_Live2D-reader/CHANGELOG.md`, `as_live2d/output.py`): fixed export root is **required**, **initially blank per app launch**, never saved to a model profile, preserved while switching models within the same program session; output subfolder derives from the actual model filename.
- Added a separate **Saída fixa** picker and editable path on the VLote sidebar. Input scan root and output root are distinct. Batch export now validates that fixed root exists before rendering and creates `<fixed root>/<FBX filename without .fbx>/` only when encoding begins. It no longer automatically writes new videos under `_EXT`. Korean/Unicode names remain intact; existing completed output folders are not overwritten (numeric suffix on collision).
- **Kept existing _OK / _Null classification** and safe queue folder moving unchanged. Output roots inside the source group being moved are rejected; failed/canceled exports leave the FBX pending. Completed batch files are staged and published as one directory; old `_EXT` contents are untouched.
- `Export / Framing` now has three actual background modes: **transparent**, **solid color**, and **image** (PNG/JPG/JPEG/BMP/GIF). Image selection and reload appear in both exporters. The single exporter uses the same center-crop OpenGL preview/export path as VLote; no post-export image replacement.
- The single export window now uses the same **session fixed root** as VLote. Every animation, including a single selected take, is saved inside `<fixed root>/<FBX filename>/`. Clicking other takes, switching formats, opening a different FBX, or returning to VLote does not silently overwrite the fixed output path. Neither profiles nor settings persist that folder between launches.
- Background mode/color/image is synchronized between the VLote sidebar and Export / Framing when switching windows. MP4 continues to require an opaque background, while WebP/MOV can remain transparent. Added deferred background texture reload after external edits, on the active GL context.
- Introduced pure path helper `Batch/FixedOutputFolder.cs` and extended `tests/VLoteSmoke/Program.cs` for Unicode, .FBX case-insensitive filename stem, absent fixed path, nonexistent folder, no premature output folder, collision safety and existing render/background tests.
- Updated `VLoteMainForm.cs`, `MainForm.cs`, `ExportDialog.cs`, `SceneRenderer.cs`, `ViewerControl.cs`, `AS.FBXReader.csproj`, `build.yml`, tests and this changelog. No changes to the working SpriteSkin visibility recovery.
- Validation: Windows .NET 9 Release compilation and VLote smoke suite completed successfully before version stamping. Windows GUI/GL preview and actual FFmpeg background-image encoding still require an interactive test with a real user FBX.

## VLote 0.3 — 2026-11-02 — exact output aspect in MAIN preview

- Fixed a visible preview/export mismatch: the original editor used the full available window (often 16:9) even when the VLote output setting was 1024×1024 (1:1). `SceneRenderer.BuildView` uses the viewport's aspect ratio for camera projection, so objects could be positioned/framed differently in the final PNG/FFmpeg export.
- The **MAIN OpenGL viewer** now sits inside a centered, thin blue-bordered export frame with letterboxing. In the `VLote` tab, this frame automatically follows **Largura / Altura** (square, landscape, portrait, or custom) in real time. The actual OpenGL viewport is fitted to the exact selected width:height proportion; the offscreen render continues using the full requested pixel resolution. The source model remains centered in the same framing, and mouse camera controls continue working inside the export frame.
- Changing output format / size does **not** create a second preview or change any FBX data. Switching to `Meshes` keeps the legacy full-window viewer by default; after using the original `Export / Framing` dialog, the main viewer keeps the export frame aspect and camera to make its last chosen output settings visible.
- The original `Export / Framing` window now **opens with the main viewer's current width, height and framing** rather than resetting unconditionally to its own 2048×2048 preset. Its selected output width, height and framing are returned to the main viewer when the dialog closes. VLote size inputs are kept in sync, so both exporters operate on the same apparent crop. Exporting with the original dialog still uses its own letterboxed export preview and existing export logic.
- Added a pure `Rendering/OutputAspectLayout.cs` layout calculator that keeps the thin border *outside* the GL image. Includes handling for empty and very small temporary sizes during window resizing. Added smoke tests for square (1:1), wide (16:9), vertical (9:16), and 4:3/3:4 ratios, centered fitting and safe small-viewports.
- Changed `MainForm.cs`, `VLoteMainForm.cs`, `ExportDialog.cs`, `AS.FBXReader.csproj`, `tests/VLoteSmoke/Program.cs`, the VLote GitHub Actions artifact name and this changelog. No README edit; `main` and the separate `3D` branch remain unchanged.
- Verification: **Windows .NET 9 Release build and the VLote smoke tests PASSED**, including automated output-aspect checks for 1:1, 16:9, 9:16, 4:3, 3:4, centered resizing and tiny windows. Compiled artifact **AS_FBX-reader-VLote-0.3-Windows** published: https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/41512420139. Visual checking on the user's high-DPI desktop and comparison against an actual 1024×1024 exported animation remain to be performed.

## VLote 0.2 — 2026-11-02 — DPI-aware sidebar and readable batch controls

- User-reported regression: VLote 0.1 rendered at high Windows display scaling (e.g. 150–200%) with clipped section titles, cut-off inputs, overlapping lines and nearly invisible black text on dark buttons/checkboxes.
- Rebuilt the VLote sidebar layout using a vertically scrollable, one-column **TableLayoutPanel** with `AutoSize` rows instead of a top-docked FlowLayoutPanel containing hard-coded 28/36 px label and editor heights. Labels and status now wrap to the available sidebar width, and the parent can scroll vertically rather than truncate controls.
- Field rows measure their own preferred control sizes and are given minimum sizes that account for Segoe UI font height. The section headings, current model name, queue counter and export status grow naturally and are never deliberately cropped to fixed heights.
- Explicit dark-panel/text contrast: labels and checkboxes render white/light-blue, neutral buttons use a slate background with WHITE text, and OK/NULL retain clearly contrasted green/red buttons. Editable text, ComboBox and NumericUpDown keep light inputs with dark readable text. Status and tips are muted, readable gray-blue.
- The sidebar uses a slightly more compact Segoe UI 10pt font independently of the main preview, with padding and minimum button heights to remain usable at higher DPI. Resize recalculates wrapping width while accounting for the vertical scrollbar.
- No changes to batch discovery, FFmpeg encoding, file classification, destination folders, PMA synchronization, or the normal Meshes tab. Changed `VLoteMainForm.cs`, version in `MainForm.cs` and `AS.FBXReader.csproj`, and this changelog. README and `main` unchanged.
- Verification: **Windows .NET 9 Release build and VLote folder-sorting smoke tests PASSED**, and compiled artifact `AS_FBX-reader-VLote-0.2-Windows` uploaded: https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/41487571999. A real high-DPI desktop visual check is still required to verify sizing at the user's scaling.

## VLote 0.1 — 2026-11-02 — FBX queue and quick export (branch `VLote`)

- Created independent branch **`VLote` from `main` 0.2.3**, without modifying `main` or the frozen V1 branch. The original Meshes tree, direct FBX loading and Export / Framing dialog remain available.
- New **VLote** tab inside the MAIN reader's left sidebar. Choose a root directory through Explorer or type its path. Recursively discover `.fbx` models inside its immediate child folders (including deeper subfolders), ignoring `_OK`, `_Null`, `_EXT` at every depth and avoiding junction/symlink loops. No loose FBX files directly in the selected root are automatically moved.
- Show one FBX at a time in the **original central OpenGL preview** (no redundant window). Automatic next-model loading after Export or NULL, model name/group, `finished/total/remaining` status, and simple export settings alongside classification controls. The original toolbar animation selector/play/pause and mesh visibility remain usable for preview.
- **EXPORTAR → _OK** captures the existing main preview/camera (including Zoom/X/Y, alpha, mesh visibility), using the existing `AnimationExporter` for PNG-frame rendering plus FFmpeg encoding. Supports WebP, MP4 or MOV; resolution, FPS, quality, PMA, transparent background (WebP/MOV), and **All takes** or the currently selected take. Keeps temporary PNG frames out of the source tree and processes frames asynchronously so WinForms stays responsive. Cancel during rendering or encoding is supported.
- Videos go to **`<root>\_EXT\<source-group>\<relative-FBX-name>\`**. Each animation is encoded to a numbered filename; no overwrite of prior exports. Export is first completed in a hidden unique staging folder and published with an atomic directory move. On encoder failure/cancel, the FBX remains pending and partial staging files are cleaned up. `_EXT` is always ignored by the queue.
- **NULL → _Null** classifies a broken/unwanted FBX without exporting. A folder containing multiple FBXs moves only after **every** FBX has a classification. If all are OK or all NULL, the group folder moves intact (with textures and sidecars). Mixed decisions produce a separated `_OK` and `_Null` copy with the corresponding FBX files, while shared textures/resources stay present in each; the source is retired only after both copies are ready. Distinct suffixes prevent overwriting existing folder names.
- Saves partial classification to `.fbx-vlote-progress.json` in the selected root, allowing a stopped queue to resume; already classified FBXs within an unfinished group are skipped on re-open. Manual Open FBX clears only the live queue (saved progress remains). Prevents changing settings or closing the window mid-export without requesting cancellation.
- Added `Batch/VLoteQueue.cs` (scanner, grouping, persistence, safe sorting) and `VLoteMainForm.cs` (embedded batch UI and exporter). Updated `MainForm.cs` with reusable file loader and batch tab, `FbxSession.cs` with explicit scene clear, version string and CI workflow for branch builds and downloadable Windows artifact. README unchanged.
- Known limits: first experimental release, actual FBX data and GPU export/FFmpeg must be tested on Windows. Textures shared **outside** a model's top-level group folder are not moved automatically. Mixed group sorting duplicates non-FBX shared resources to preserve dependencies. This version uses the existing 2D FBX scene renderer from `main` rather than the experimental separate `3D` branch.
- Verification: Windows .NET 9 Release build PASSED on branch `VLote` (run https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/41483870890); GitHub Actions uploaded runnable artifact. **VLote smoke tests also PASSED** on Windows/.NET 9: recursive scanner, ignored folders, persistent partial progress, group moves, mixed OK/NULL splits, shared textures and collision-safe destinations. Validated end-to-end CI build, smoke tests and packaged Windows artifact at https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/41484254803 (artifact `AS_FBX-reader-VLote-0.1-Windows`). Interactive OpenGL preview, FFmpeg video output and folder movement using actual FBX assets still require user testing.
- Additional VLote-only FFmpeg location fallback: use `ffmpeg.exe` beside the application when present, otherwise `C:\ffmpeg\bin\ffmpeg.exe` if available, otherwise the system PATH. No change to main.

## 0.2.3 — 2026-10-09

- Fixed accidental rename while selecting batches with Ctrl/Shift: WinForms'
  delayed single-click LabelEdit is disabled. Mouse renaming now requires a
  real double-click on the mesh label with no Ctrl/Shift; F2 and explicit
  Rename from the context menu remain available.
- Added **Copy mesh (separate PNG)** to the mesh context menu. Creates an
  independent scene entry with its own GUID-based part ID and texture copy
  under `AS_FBX-reader_Copies` beside the original texture. A PNG is copied
  unchanged; other supported images are converted to PNG for editing.
  The original texture and FBX are never modified.
- A clone reuses source FBX geometry, UVs, skinning and animation while
  receiving independent visibility, position, vertex edits and texture data.
  Override textures are honored by preview, frame export and mesh editing.
  **Open copied texture** uses the default image app; **Reload copied texture**
  evicts the GPU texture cache after editing externally.
- Profiles persist clones under `ClonedParts` with original-part identifiers,
  stable clone IDs, names, and PNG paths relative to the FBX directory.
  Loading recreates clones before applying the scene tree, draw order, timing,
  position edits, exclusions and aliases. Older profiles still load.
- Constraints: the clone retains its source mesh UV layout and copies the
  full original image/atlas; editing requires an external image program.
  Missing clone textures are reported in the status bar. Excluding a clone
  doesn't automatically delete the PNG on disk. Initial FBX import remains.
- Version set to 0.2.3. Changes committed directly to `main`; V1 branch
  `1.17.0-Final` remains frozen. README intentionally not modified.
- Files: `MainForm.cs`, `ScenePart.cs`, `FbxSession.cs`,
  `VisibilityProfile.cs`, `SceneRenderer.cs`, `TextureCache.cs`,
  `ViewerControl.cs`, `AS.FBXReader.csproj`, this changelog.
- Verification: Windows/.NET 9 build passed:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37986877167.
  Interactive checks (Ctrl/Shift selection, two-click rename, image cloning,
  external PNG changes and profile reload) on the user's FBX remain pending.

## 0.2.2 — 2026-10-09

- Added Explorer-style multi-selection for meshes in the main parts tree:
  Ctrl+click toggles individual meshes, while Shift+click selects a visible
  interval. Drag multiple selected meshes together to reorder them in the tree.
  Groups retain their existing single-node move behavior.
- Press Delete or use **Exclude selected meshes** in the context menu to remove
  selected parts from the active scene. Excluded parts disappear from the tree
  and are omitted by the same active-parts iteration used by preview and export.
  The source FBX is never rewritten. This is different from simply hiding a
  mesh, which leaves it in the active parts registry.
- Save profile now persists the excluded IDs as `ExcludedPartIds`. Loading a
  profile restores the full in-memory part registry from the original FBX import
  first, then excludes the saved IDs. Switching to another profile can restore
  parts without reopening the FBX. Existing profiles without `ExcludedPartIds`
  remain compatible.
- **Move mesh(es) in preview** uses the current multi-selection to translate all
  selected parts by the same drag distance, preserving their relative offsets.
  Escape cancels the displacement for the whole batch. The movement is saved
  using the existing per-mesh adjustment data in the profile.
- Changed window title and project version to 0.2.2; continued development is
  performed on `main` throughout V2. The `1.17.0-Final` branch remains the
  frozen V1 snapshot; a new final branch is reserved for the end of V2.
- Files: `MainForm.cs`, `FbxSession.cs`, `ViewerControl.cs`,
  `VisibilityProfile.cs`, `AS.FBXReader.csproj`, `README.md`,
  this changelog.
- Verification: Windows/.NET 9 CI passed for the code changes before this
  version/documentation update:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37983108414.
  Main-branch Windows/.NET 9 CI also passed:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37983809115.
  Interactive tests on the user's FBX are still required.
  Exclusion occurs after the initial FBX import; it does not remove native
  import costs or animation-bone evaluation work.

## 0.2.1 — 2026-10-05

- Fixed a startup crash in Edit bone binding reported by the user:
  `SplitterDistance deve ficar entre Panel1MinSize e Width - Panel2MinSize`.
  The WinForms SplitContainer was given 260/300px panel minima while it still
  had its tiny pre-layout width. Set its divider and scaled minima only when
  the editor is shown and its actual width is known. File: `BoneFollowForm.cs`.
  The Windows/.NET 9 build passed:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37251773406.
  The editor still needs an interactive check on the user's machine.
- Added **Edit bone binding...** to the part context menu. The separate rig
  editor lists original weighted bones, rig nodes and the meshes using each
  node; search by mesh or bone name, select Original skin, Automatic strongest
  bone, or a specific node, and scrub the pose. Its wireframe/skeleton view
  supports fit, zoom and pan. Switching choices updates the main preview at
  once. Apply keeps the choice in memory; Cancel restores the prior binding
  and closing restores the original animation time and playback state.
- An explicit node follows its animation rigidly relative to the mesh and
  node bind poses, preserving the mesh's rest shape. The same renderer path
  serves main preview and export. Profiles now save/load FollowBoneName;
  older profiles still default to Original/Automatic behavior. A missing
  saved node is ignored, and an invalid rig transform cannot be applied.
- Files: `BoneFollowForm.cs`, `MainForm.cs`, `AnimationPlayer.cs`,
  `SceneRenderer.cs`, `ScenePart.cs`, `VisibilityProfile.cs`,
  `AS.FBXReader.csproj`, `README.md`, this changelog.
- Verification: the first Windows/.NET 9 build found a WinForms designer
  serialization annotation missing from the rig canvas; it was fixed and the
  follow-up Windows/.NET 9 build passed:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37251138158.
  An interactive visual check using the user's hair/face animation 7 FBX is
  still pending; that FBX was not attached in this turn. Linux workspace
  lacks `dotnet` and a Windows UI.

## 0.2.0 — 2026-10-04

- Closed the 0.1.x interface cycle after the user confirmed that the scaling
  and controls are visible. The editor now has its own Free camera checkbox:
  wheel zooms around the pointer, left drag pans, and Fit view restores the
  original editor view. Camera movement never edits or saves vertex offsets.
- Ctrl+click toggles multiple vertex handles. Dragging a selected handle moves
  the whole selection, with optional neighbor-radius falloff around each
  selected point; right-click a selected handle resets the group. Editing and
  camera navigation remain separate modes.
- Set the window title and assembly/package version to 0.2.0. `main` and the
  `1.17.0-Final` branch point to the same release commit; Git branch names
  cannot contain the space in the requested label "1.17.0 Final".
- Files: `MeshDeformForm.cs`, `MainForm.cs`, `AS.FBXReader.csproj`, `README.md`,
  this changelog.
- Verification: Windows/.NET 9 build passed:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37218656974.
  Interactive vertex/camera checks remain pending. The export cancellation
  path from 0.1.16 still needs an interactive test; its Windows build passed.

## 0.1.16 — 2026-10-04

- Export responsiveness: OpenGL frame readback stays on the UI thread, while
  pixel conversion, PNG compression and cleanup now run asynchronously so
  the window can process input. The progress/actions footer stays visible
  below the scrollable settings, with an enabled Cancel export button only
  while a job runs. Export settings are locked during a job and restored after.
  The progress bar animates during FFmpeg encoding rather than sitting at a
  misleading completed percentage.
- Cancel stops after the current frame save or terminates FFmpeg during encode,
  then removes the temporary frame folder. FFmpeg writes to a temporary file
  beside the destination and replaces the final output only after successful
  encoding; previously completed takes in a batch remain intact. Closing the
  window during export requests cancellation. Reduced FFmpeg's verbose logs.
- Files: `AnimationExporter.cs`, `ViewerControl.cs`, `ExportDialog.cs`,
  `README.md`, this changelog.
- Verification for this export update: Windows/.NET 9 build passed:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37214360627.
  The final animated encoding progress change also passed:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37214537457.
  Interactive export/cancel checks remain pending. The synchronous OpenGL
  capture of a single very large frame can still take time; cancellation is
  checked between frames.
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
- Verification for these new changes: the first Windows CI build identified
  a WinForms serialization annotation and two local-name conflicts in the
  camera control; these were corrected. The second Windows/.NET 9 build passed:
  https://github.com/OliverIsNotAvailble/AS_FBX-reader/actions/runs/37169425954.
  The user subsequently confirmed that interface scaling and control
  visibility were working and asked to finalize 0.2.0.
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
