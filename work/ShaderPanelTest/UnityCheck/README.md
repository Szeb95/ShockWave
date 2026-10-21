# Shared visual Unity check

This is Editor-only verification for the PC shader primitives, not an avatar installer.

Create a **new scratch project** using Unity 2022.3.22f1. Copy `../Shaders/GraphCore.hlsli`, `TouchVisuals.hlsli` and `AvatarVisualProof.shader` into its `Assets/Shaders/`, and `ShaderVisualCheck.cs` into its `Assets/Editor/`. Run the Editor in batch mode with Direct3D11 and `-executeMethod ShaderVisualCheck.Run`. Do not use the original avatar project. The verifier writes a preview and report into that scratch project's `checks/` and exits with 0 on success.

The ordinary Unity shader exposes `_TouchX`, `_TouchY`, `_Touching`, live color and two held level/enabled/color sets. Avatar animations can eventually drive corresponding material properties; no custom runtime C# belongs on the avatar. The test deliberately supplies these properties directly. It proves rendering of shared stateless held lines/dot and compilation of the stereo-instancing variant, not calibrated finger input, actual headset stereo, full panel names/history, avatar upload or hardware/remote output.

Actual scratch check: `../checks/unity-visual-project/`, Unity 2022.3.22f1 / Direct3D11, eight checks passed. Source and scratch copies of the shared HLSL were compared after the run.
