# Project direction

Carvera Controller C# ports https://github.com/danilius/Carvera_Controller (Python/Kivy) to C# with Avalonia. It does not copy the original's look.

## Agreed with the user (25 September 2026)

- Every element of the UI comes from JSON layout files. Users create their own layouts, place components in any region, stack them horizontally or vertically in any order, create regions and group components into them. Keep machine logic independent of layout (bind to `StateStore` paths and named commands).
- Layout sizing follows web-browser principles: a container without width/height fills the available space; explicit sizes are honoured, and if every container is sized the remaining space is left empty.
- Components support custom graphics for their states (per-state `visuals` and expression-driven `conditions`, including images).
- If a layout hides or omits Feed Hold, Stop or Reset, the app shows a warning. This does not block loading.
- Layouts are JSON files. The layout editor is a separate program (`src/Carvera.Editor`, `CarveraLayoutEditor.exe`, see `docs/layout-editor.md`, added at the user's request on 30 September 2026) meant to run on a second screen: it edits those files and the Controller reloads them live, keeping its tab, scroll positions and splitters. Every valid edit is saved at once; a layout with errors is held back and the Controller keeps the last good one. Built-in layouts are edited as a copy in the user's layouts folder, which the Controller prefers.
- Windows only for now (the code builds and tests headless on Linux).
- Light theme.
- License: GPL-2.0, keeping the attribution in NOTICE.

## Working conventions

- Develop on descriptive branches off `main` and integrate through pull requests.
- Build with `build/publish.ps1`. It publishes the Controller and the layout editor to the same folder and updates the `Carvera Controller (latest build).lnk` and `Carvera Layout Editor (latest build).lnk` shortcuts in the repository root (git-ignored) so they always point at the latest build; make every build through this script.
- The component catalog (`src/Carvera.Layout/ComponentCatalog.cs`) is the single source for element types and properties. The layout editor's palette, inspector forms and code completion are built from it too, so a new element or property appears there without editor changes; a new `PropKind` needs an editor in `src/Carvera.Editor/Views/PropertyEditors.cs`, and a new element belongs in `NewElements` (palette groups and starting values). After changing it, or the commands, regenerate `schema/layout.schema.json` and `docs/layout-reference.md` by running the tests with `UPDATE_GENERATED=1`.
- There are two shipped layouts: `desktop`, and `desktop2` ("Desktop 2", added at the user's request on 29 September 2026: based on `desktop`, with pages so that nothing scrolls, a job setup page with a plan view of the bed, and a right-hand column that shows the jog controls while setting up and the overrides while a job runs). `touch` and `canvas-demo` were removed earlier. Both must validate without errors or warnings, and tests enforce this. New widgets go in both unless the user says otherwise.
- The machine's own settings (config.txt) are on the settings page, which also has a search over all its text; they are not in either layout.
- Automate checks: headless Avalonia tests cover layout geometry, visual states and window behaviour, and render layout previews (`CARVERA_SCREENSHOT_DIR`) without using the screen or mouse.
- It has not been validated against a real machine yet. Treat protocol changes carefully and mirror the Python controller's behaviour.
