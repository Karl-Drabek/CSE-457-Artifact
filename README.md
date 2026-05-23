# CSE 457 Artifact

Unity project with:
- a generated procedural ground plane
- a generated URP low-poly water plane
- simple rigidbody buoyancy

## Open

Open the project in Unity 6 with URP enabled.

## Setup

Assign materials on the `World` component:
- `groundMaterial`
- `waterMaterial`

Starter materials live in `Assets/Materials`.

## Credits

- Original `Low Poly Water` package source is retained in `Assets/LowPolyWater_Pack`.
- The active project-owned URP rewrite lives in `Assets/Scripts/UrpLowPolyWater.cs` and `Assets/Shaders/UrpLowPolyWater.shader`.

## License

Original project-authored code and docs are available under the MIT license.
Third-party content keeps its own terms. See `LICENSE` and `THIRD_PARTY_NOTICES.md`.
