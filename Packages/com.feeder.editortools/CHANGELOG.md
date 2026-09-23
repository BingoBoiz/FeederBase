# Changelog

## [Unreleased]

### Changed

- Google Sheet importer: every type name on a sheet is a full name, nothing is looked up implicitly. Cell A1 of a tab names the generated class (`RawItem` is global, `MyGame.RawItem` is generated inside `namespace MyGame`). An enum header token names the enum the same way (`s_Kind:ItemKind` is the global `ItemKind`, `s_Kind:MyGame.ItemKind` the one in `MyGame`).
- Enum and component fields are written with the shortest name that still binds to the right type from the generated class, following the compiler's lookup (the class namespace, each parent namespace, global, then the file's usings). A namespace-qualified or `global::` name appears only when a same-named type would otherwise capture it. Output for existing global classes is unchanged.
- Update Enum reads enums from every project assembly but writes only to the sheet's Enum Scripts and to files directly in its Script Folder. Values missing from an enum in any other file are reported, not written.
- A new enum `Ns.MyEnum` is created in the first Enum Script with a `namespace Ns { }` block, a new global enum in the first Enum Script that declares no namespace. With no matching script, or when the insertion point sits inside an `#if` block, the plan reports it instead of guessing. With no Enum Scripts, each new enum still gets its own file in the Script Folder.
- Update Enum no longer creates a global enum when an enum with the same short name exists in a namespace; it points to the full name instead. It also refuses to create an enum nested in a class.

### Removed

- The sheet `Namespace` field and the single `Enum Script` field, replaced by the `Enum Scripts` list. Existing sheets migrate when opened: the old Enum Script becomes the first entry of the list. A non-empty old Namespace no longer changes any output; it stays visible with a warning, and Generate Script flags an A1 cell without a namespace, until the field is cleared.

## [1.2.0] - 2026-09-21

### Added

- Scene Loader window (`Tools > Feeder > Scene Loader`, `Alt+S`): scenes grouped by folder, Favorites, Recent, In Build, fuzzy search, open or add additively, and a LOADED strip to set the active scene or remove a scene. The shortcut can be rebound from the window.
- `Scenes` button next to Play/Pause/Step in the main toolbar, with a dropdown for Favorites, Recent and Add Additive. Works on Unity 2022.3 and newer.
- Project Settings page `Feeder > Scene Loader`: scan roots, top-level folder order, and per-folder label, color and visibility. Saved to `ProjectSettings/FeederSceneLoader.asset`; favorites and recent scenes stay per machine in `UserSettings/FeederSceneLoader.asset`.

### Removed

- `Tools > Feeder > Scenes Loader Window`. Use the Scene Loader window instead.
