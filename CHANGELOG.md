# AS_FBX-reader changelog

Keep this file updated whenever the reader changes. Record the behavior, files,
verification, and remaining limits in the same commit. Read it alongside the
current code when resuming work in a later conversation.

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
- Verification: `git diff --check` passed. Windows/.NET 9 build and interactive
  drag/drop verification remain to be run; the current workspace has no
  `dotnet` executable.

### Work carried into this checkout

The current working tree already contained the 0.1.10 SpriteSkin recovery,
per-part force visibility, and manually timed visibility keys when these UI
changes began. The recovery and visibility settings are preserved here. Their
source changes are in `AnimationPlayer.cs`, `SceneRenderer.cs`, `ScenePart.cs`,
`VisibilityProfile.cs`, `MainForm.cs`, and `README.md`; they still need the same
Windows build and visual verification.
