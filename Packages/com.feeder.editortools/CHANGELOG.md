# Changelog

## [1.2.0] - 2026-09-21

### Added

- Scene Loader window (`Tools > Feeder > Scene Loader`, `Alt+S`): scenes grouped by folder, Favorites, Recent, In Build, fuzzy search, open or add additively, and a LOADED strip to set the active scene or remove a scene. The shortcut can be rebound from the window.
- `Scenes` button next to Play/Pause/Step in the main toolbar, with a dropdown for Favorites, Recent and Add Additive. Works on Unity 2022.3 and newer.
- Project Settings page `Feeder > Scene Loader`: scan roots, top-level folder order, and per-folder label, color and visibility. Saved to `ProjectSettings/FeederSceneLoader.asset`; favorites and recent scenes stay per machine in `UserSettings/FeederSceneLoader.asset`.

### Removed

- `Tools > Feeder > Scenes Loader Window`. Use the Scene Loader window instead.
