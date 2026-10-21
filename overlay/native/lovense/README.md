# Optional native Lovense dependency

For local BLE discovery and bounded manual control, obtain the official Windows x64 `LovenseBLE_Lib.dll` v1.2 and place it here with any required native dependencies. Build/publish copies DLLs into the same `native/lovense` path beside the EXE. No download occurs during build or at runtime.

The DLL is not supplied by this repository; redistribution permission and packaged native dependencies remain unverified. DLLs in this directory are ignored by Git. Simulated discovery works without them. See [the setup and verification guide](../../../docs/lovense-remote/LOCAL-TOYS.md).
