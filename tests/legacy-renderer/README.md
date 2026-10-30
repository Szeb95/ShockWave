# Historical renderer checks

The old desktop HLSL renderer is test support only. The application uses GDI+
desktop frames and a persistent SteamVR bitmap-copy surface.

`LegacyRenderer.props` gives the toy-control and remote check programs their own
renderer types, shader assets and shader-compiler dependency. None are referenced
or packaged by `ShockWave.Overlay.csproj`. The original standalone Shader Lab and
the active avatar OSC/Unity tool sources remain unchanged.
