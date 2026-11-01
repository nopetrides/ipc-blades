using System;
using System.Runtime.InteropServices;
using System.Text;

namespace IPCBlades.Gates
{
    /// <summary>
    /// DllImport surface of Native/RigidIPCBackend's diagnostics exports. Nothing here changes what the
    /// backend simulates; it only reads its state or arms its optional per-step trace.
    /// </summary>
    public static class RigidIPCNative
    {
        const string Dll = "RigidIPCBackend";

        /// <summary>Mirrors RigidIPC_ShapeSnapshot in RigidIPCBackend.cpp field for field.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ShapeSnapshot
        {
            public IntPtr shapeUserData;
            public IntPtr bodyUserData;
            public uint shapeSerial;
            public uint bodySerial;
            public int geometryType;
            public int bodyIsStatic;
            public float geom0, geom1, geom2, geom3;
            public float bodyPosX, bodyPosY, bodyPosZ, bodyRotX, bodyRotY, bodyRotZ, bodyRotW;
            public float localPosX, localPosY, localPosZ, localRotX, localRotY, localRotZ, localRotW;
            public float worldPosX, worldPosY, worldPosZ, worldRotX, worldRotY, worldRotZ, worldRotW;
            public int vertexCount;
        }

        // Values of Physics::GeometryType in PhysicsShapeTypes.h (6000.6.7f1).
        public const int GeomSphere = 1, GeomCapsule = 3, GeomBox = 4, GeomConvexMesh = 5, GeomTriangleMesh = 6;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int RigidIPC_GetDiagnostics(StringBuilder buffer, int bufferSize);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int RigidIPC_GetShapeSnapshots([Out] ShapeSnapshot[] output, int maxCount);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int RigidIPC_GetShapeWorldVertices(uint shapeSerial, [Out] float[] output, int maxVertices);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void RigidIPC_TraceNextSteps(int steps);

        public static string Diagnostics()
        {
            var buffer = new StringBuilder(1024);
            RigidIPC_GetDiagnostics(buffer, buffer.Capacity);
            return buffer.ToString();
        }

        /// <summary>The backend's Simulate call count, parsed from the diagnostics line (-1 if absent).</summary>
        public static long SimulateCount()
        {
            foreach (var part in Diagnostics().Split(' '))
                if (part.StartsWith("simulate=") && long.TryParse(part.Substring(9), out var n))
                    return n;
            return -1;
        }

        public static ShapeSnapshot[] Shapes()
        {
            int count = RigidIPC_GetShapeSnapshots(null, 0);
            var shapes = new ShapeSnapshot[Math.Max(0, count)];
            if (count > 0) RigidIPC_GetShapeSnapshots(shapes, count);
            return shapes;
        }

        /// <summary>The backend's world-space characteristic points for one shape (see LocalShapePoints).</summary>
        public static UnityEngine.Vector3[] WorldVertices(uint shapeSerial)
        {
            int count = RigidIPC_GetShapeWorldVertices(shapeSerial, null, 0);
            if (count <= 0) return Array.Empty<UnityEngine.Vector3>();
            var flat = new float[count * 3];
            RigidIPC_GetShapeWorldVertices(shapeSerial, flat, count);
            var points = new UnityEngine.Vector3[count];
            for (int i = 0; i < count; i++) points[i] = new UnityEngine.Vector3(flat[3 * i], flat[3 * i + 1], flat[3 * i + 2]);
            return points;
        }
    }
}
