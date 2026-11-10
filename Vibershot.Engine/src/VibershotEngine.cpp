#include "VibershotEngine.h"

// Phase 0: the native engine exposes a stable ABI boundary. Actual DXGI capture
// is implemented starting in Phase 1. These stubs prove the native build +
// managed interop pipeline end to end.

extern "C"
{
    VH_API const char* VH_EngineVersion()
    {
        return "0.1.0-phase0";
    }

    VH_API int VH_CaptureSupported()
    {
        // Real DXGI capability probe goes here in Phase 1.
        return 1;
    }
}
