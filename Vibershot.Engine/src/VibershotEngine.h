#pragma once

#ifdef VIBERSHOT_ENGINE_EXPORTS
#define VH_API __declspec(dllexport)
#else
#define VH_API __declspec(dllimport)
#endif

extern "C"
{
    // Returns a NUL-terminated version string, e.g. "0.1.0-phase0".
    VH_API const char* VH_EngineVersion();

    // Best-effort check: 1 if DXGI desktop duplication is usable on this machine.
    VH_API int VH_CaptureSupported();
}
