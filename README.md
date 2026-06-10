# CSE 457 Artifact

A boat building and sailing game built in Unity 6 (URP). Each day the you sail into the arctic ocean with your home brew ship and try to uncover the lies mainstream narrative, one icegerg at a time.

## Core systems

- **Procedural open world** — chunked terrain and water stream around the boat
- **Ship builder** — drag-and-drop boat construction with physics-based mass and buoyancy; pieces take cumulative damage and break off
- **Voyage cycle** — gold and objectives saved over days in a roguelike manner
- **Objectives** — configurable targets  placed in the world. health persists across days so damage is cumulative. defeated objectives stay gone for the rest of the run. The final boss ends the game
- **Physics collision damage** — ramming an objective deals impulse-based damage equally to the obstacle and the specific boat piece that made contact; objectives sink when destroyed
- **Water damage**
hiting large waves also causes damage to the components of the ship based on the size and speed

## Credits

- Original `Low Poly Water` package source is retained in `Assets/LowPolyWater_Pack`.
- The active project-owned URP rewrite lives in `Assets/Scripts/UrpLowPolyWater.cs` and `Assets/Shaders/UrpLowPolyWater.shader`.

## License

Original project-authored code and docs are available under the MIT license.
Third-party content keeps its own terms. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.
