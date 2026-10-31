using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

namespace IPCBlades.Gates
{
    /// <summary>
    /// Track 1 gate evidence (A-C). On every domain load it records, side by side:
    /// the native plugin's own diagnostics (gate A), the backends Unity lists (gate B), and the
    /// backend Unity actually selected versus the one saved in DynamicsManager.asset (gate C).
    /// Output goes to the Console and to Logs/RigidIPC_gate_probe.log.
    /// </summary>
    [InitializeOnLoad]
    public static class RigidIPCGateProbe
    {
        public const uint RigidIpcId = 0x52495043u; // must match kIntegrationId in RigidIPCBackend.cpp
        public const uint PhysXId = 0xF2B8EA05u;
        const string DynamicsManagerPath = "ProjectSettings/DynamicsManager.asset";
        const string ProbeLogPath = "Logs/RigidIPC_gate_probe.log";

        [DllImport("RigidIPCBackend", CallingConvention = CallingConvention.Cdecl)]
        static extern int RigidIPC_GetDiagnostics(StringBuilder buffer, int bufferSize);

        static RigidIPCGateProbe()
        {
            EditorApplication.delayCall += () => LogStatus("domain load");
        }

        [MenuItem("IPC/Gate/Log Backend Status")]
        public static void LogStatusMenu() => LogStatus("menu");

        [MenuItem("IPC/Gate/Save RigidIPC As GameObject SDK (restart required)")]
        public static void SelectRigidIpc() => SaveBackendId(RigidIpcId);

        [MenuItem("IPC/Gate/Save PhysX As GameObject SDK (restart required)")]
        public static void SelectPhysX() => SaveBackendId(PhysXId);

        public static string LogStatus(string reason)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[RigidIPC gate probe] {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({reason}) Unity {Application.unityVersion}");
            sb.AppendLine($"  native diagnostics : {NativeDiagnostics()}");
            sb.AppendLine($"  saved backend id   : {DescribeId(SavedBackendId())} (DynamicsManager.asset m_CurrentBackendId)");
            sb.AppendLine($"  current integration: {Describe(Physics.GetCurrentIntegrationInfo())}");
            sb.AppendLine("  listed integrations:");
            foreach (var info in ListedIntegrations())
                sb.AppendLine($"    - {Describe(info)}");

            var text = sb.ToString();
            Debug.Log(text);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ProbeLogPath));
                File.AppendAllText(ProbeLogPath, text + Environment.NewLine);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RigidIPC gate probe] could not write {ProbeLogPath}: {e.Message}");
            }
            return text;
        }

        static string NativeDiagnostics()
        {
            try
            {
                var buffer = new StringBuilder(512);
                RigidIPC_GetDiagnostics(buffer, buffer.Capacity);
                return buffer.ToString();
            }
            catch (Exception e)
            {
                return $"unavailable ({e.GetType().Name}: {e.Message})";
            }
        }

        // Physics.GetIntegrationInfos is internal in 6000.4. The managed overload returns a
        // ReadOnlySpan (not invokable by reflection), so call the extern (out IntPtr, out ulong)
        // overload and read the native array directly.
        static IEnumerable ListedIntegrations()
        {
            var overloads = typeof(Physics).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "GetIntegrationInfos").ToArray();
            var method = overloads.FirstOrDefault(m => m.GetParameters().Length == 2);
            if (method == null)
                return new[] { "Physics.GetIntegrationInfos(out IntPtr, out ulong) not found; overloads: " +
                    string.Join(" | ", overloads.Select(m => m.ToString())) };
            var args = new object[] { IntPtr.Zero, 0UL };
            method.Invoke(null, args);
            var ptr = (IntPtr)args[0];
            var count = Convert.ToInt32(args[1]);
            var infos = new object[count];
            unsafe
            {
                for (int i = 0; i < count; i++)
                    infos[i] = UnsafeUtility.ReadArrayElement<IntegrationInfo>((void*)ptr, i);
            }
            return infos;
        }

        // Dump every public field/property so the probe does not depend on IntegrationInfo's exact member names.
        static string Describe(object info)
        {
            if (info == null) return "null";
            var type = info.GetType();
            if (type == typeof(string)) return (string)info;
            var parts = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Select(p => $"{p.Name}={Format(p.GetValue(info))}")
                .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public).Select(f => $"{f.Name}={Format(f.GetValue(info))}"));
            return string.Join(", ", parts);
        }

        static string Format(object value) => value is uint u ? $"{u} (0x{u:X8})" : value?.ToString() ?? "null";

        static string DescribeId(uint? id)
        {
            if (id == null) return "unreadable";
            var name = id == RigidIpcId ? "RigidIPC" : id == PhysXId ? "PhysX" : id == 0xDECAFBADu ? "None/fallback" : "unknown";
            return $"{id} (0x{id:X8}, {name})";
        }

        static SerializedProperty BackendIdProperty(out SerializedObject so)
        {
            so = null;
            var asset = AssetDatabase.LoadAllAssetsAtPath(DynamicsManagerPath).FirstOrDefault();
            if (asset == null) return null;
            so = new SerializedObject(asset);
            return so.FindProperty("m_CurrentBackendId");
        }

        static uint? SavedBackendId()
        {
            var prop = BackendIdProperty(out _);
            return prop == null ? (uint?)null : prop.uintValue;
        }

        static void SaveBackendId(uint id)
        {
            var prop = BackendIdProperty(out var so);
            if (prop == null)
            {
                Debug.LogError("[RigidIPC gate probe] m_CurrentBackendId not found in DynamicsManager.asset");
                return;
            }
            prop.uintValue = id;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            LogStatus($"saved backend id {DescribeId(id)}; restart the Editor to apply");
        }
    }
}
