# Project direction

Carvera Controller C# ports https://github.com/danilius/Carvera_Controller (Python/Kivy) to C# with Avalonia. It does not copy the original's look.

## Agreed with the user (25 September 2026)

- Every element of the UI comes from JSON layout files. Users create their own layouts, place components in any region, stack them horizontally or vertically in any order, create regions and group components into them. Keep machine logic independent of layout (bind to `StateStore` paths and named commands).
- Layout sizing follows web-browser principles: a container without width/height fills the available space; explicit sizes are honoured, and if every container is sized the remaining space is left empty.
- Components support custom graphics for their states (per-state `visuals` and expression-driven `conditions`, including images).
- If a layout hides or omits Feed Hold, Stop or Reset, the app shows a warning. This does not block loading.
- Layouts are JSON only for now; there is no in-app layout editor yet.
- Windows only for now (the code builds and tests headless on Linux).
- Light theme.
- License: GPL-2.0, keeping the attribution in NOTICE.

## Working conventions

- Develop on descriptive branches off `main` and integrate through pull requests.
- The component catalog (`src/Carvera.Layout/ComponentCatalog.cs`) is the single source for element types and properties. After changing it, or the commands, regenerate `schema/layout.schema.json` and `docs/layout-reference.md` by running the tests with `UPDATE_GENERATED=1`.
- Shipped layouts must validate without errors or warnings (except `canvas-demo`, which deliberately omits Reset). Tests enforce this.
- Automate checks: headless Avalonia tests cover layout geometry, visual states and window behaviour, and render layout previews (`CARVERA_SCREENSHOT_DIR`) without using the screen or mouse.
- It has not been validated against a real machine yet. Treat protocol changes carefully and mirror the Python controller's behaviour.
