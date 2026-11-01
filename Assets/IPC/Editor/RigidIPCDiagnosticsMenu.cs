using UnityEditor;
using UnityEngine;

namespace IPCBlades.Gates
{
    /// <summary>On-demand diagnostics while in Play mode; both switch themselves off after the given steps.</summary>
    public static class RigidIPCDiagnosticsMenu
    {
        [MenuItem("IPC/Diagnostics/Vertex Compare Next 3 Steps")]
        public static void VertexCompare()
        {
            RigidIPCGateDiagnostics.ArmVertexCompare(3);
            Debug.Log("[RigidIPC diagnostics] vertex compare armed for 3 steps -> Logs/RigidIPC_gateD_diag.log");
        }

        [MenuItem("IPC/Diagnostics/Native Pose Trace Next 10 Steps")]
        public static void NativeTrace()
        {
            RigidIPCNative.RigidIPC_TraceNextSteps(10);
            Debug.Log("[RigidIPC diagnostics] native trace armed for 10 Simulate calls -> Logs/RigidIPC_commands.log");
        }

        [MenuItem("IPC/Diagnostics/Vertex Compare Next 3 Steps", true)]
        [MenuItem("IPC/Diagnostics/Native Pose Trace Next 10 Steps", true)]
        static bool PlayModeOnly() => EditorApplication.isPlaying;
    }
}
