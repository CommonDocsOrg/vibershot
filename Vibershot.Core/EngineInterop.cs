using System.Runtime.InteropServices;

namespace Vibershot.Core;

/// <summary>
/// Thin managed wrapper over the native Vibershot.Engine DLL (C ABI).
/// The DLL name is bound at runtime, so it must be present in the app folder.
/// </summary>
public static class EngineInterop
{
    private const string DllName = "Vibershot.Engine";

    [DllImport(DllName, EntryPoint = "VH_EngineVersion", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr VH_EngineVersionRaw();

    [DllImport(DllName, EntryPoint = "VH_CaptureSupported", CallingConvention = CallingConvention.Cdecl)]
    private static extern int VH_CaptureSupportedRaw();

    public static string Version => Marshal.PtrToStringAnsi(VH_EngineVersionRaw()) ?? "unknown";

    public static bool CaptureSupported => VH_CaptureSupportedRaw() != 0;
}
