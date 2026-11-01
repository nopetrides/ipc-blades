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

        /// <summary>Mirrors RigidIPC_BodySnapshot in RigidIPCBackend.cpp field for field.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct BodySnapshot
        {
            public IntPtr userData;
            public uint serial;
            public int isStatic, flags, ccd, sleeping, hasKinematicTarget, shapeCount;
            public float mass, linearDamping, angularDamping, maxLinVel, maxAngVel, maxDepenetration;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] pos;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] rot;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] linVel;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] angVel;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] com;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] inertia;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] inertiaRot;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] kinematicTargetPos;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] kinematicTargetRot;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] lastForce;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] lastTorque;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] lastImpulse;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] lastAngImpulse;
        }

        /// <summary>Mirrors RigidIPC_JointSnapshot in RigidIPCBackend.cpp field for field.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct JointSnapshot
        {
            public IntPtr userData;
            public IntPtr parentUserData, childUserData;
            public uint serial, parentSerial, childSerial;
            public int type, flags;
            public uint targetRotationSets;
            public float breakForce, breakTorque, parentInvMassScale, childInvMassScale;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] localPos0;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] localRot0;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] localPos1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] localRot1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] targetRotation;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] targetPosition;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] targetLinVel;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] targetAngVel;
            /// <summary>9 rows by Physics::ConstraintAxis (AngularX, AngularY, AngularZ, AngularYZ, LinearX, LinearY, LinearZ,
            /// Distance, Slerp); each row: lock, min, max, limitSpring, limitDamping, driveSpring, driveDamper, driveMaxForce, driveAccel.</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 81)] public float[] axes;

            public float Axis(int axis, int field) => axes[9 * axis + field];
        }

        // Physics::ConstraintAxis and the row fields of JointSnapshot.axes.
        public const int AxAngularX = 0, AxAngularY = 1, AxAngularZ = 2, AxAngularYZ = 3, AxLinearX = 4, AxLinearY = 5, AxLinearZ = 6,
            AxDistance = 7, AxSlerp = 8;
        public const int FLock = 0, FMin = 1, FMax = 2, FLimitSpring = 3, FLimitDamping = 4, FDriveSpring = 5, FDriveDamper = 6,
            FDriveMaxForce = 7, FDriveAccel = 8;

        // Physics::BodyFlags.
        public const int BodyGravity = 1, BodyKinematic = 2, BodySimulated = 4;

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

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int RigidIPC_GetBodySnapshots([Out] BodySnapshot[] output, int maxCount);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int RigidIPC_GetJointSnapshots([Out] JointSnapshot[] output, int maxCount);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int RigidIPC_GetCommandCounts([Out] uint[] codes, [Out] ulong[] counts, int maxCount);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int RigidIPC_GetCommandName(uint code, StringBuilder buffer, int bufferSize);

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

        public static BodySnapshot[] Bodies()
        {
            int count = RigidIPC_GetBodySnapshots(null, 0);
            var bodies = new BodySnapshot[Math.Max(0, count)];
            if (count > 0) RigidIPC_GetBodySnapshots(bodies, count);
            return bodies;
        }

        public static JointSnapshot[] Joints()
        {
            int count = RigidIPC_GetJointSnapshots(null, 0);
            var joints = new JointSnapshot[Math.Max(0, count)];
            if (count > 0) RigidIPC_GetJointSnapshots(joints, count);
            return joints;
        }

        /// <summary>The backend's running command histogram, keyed by command code.</summary>
        public static System.Collections.Generic.Dictionary<uint, ulong> CommandCounts()
        {
            int count = RigidIPC_GetCommandCounts(null, null, 0);
            var codes = new uint[Math.Max(0, count)];
            var counts = new ulong[codes.Length];
            if (count > 0) RigidIPC_GetCommandCounts(codes, counts, count);
            var result = new System.Collections.Generic.Dictionary<uint, ulong>(codes.Length);
            for (int i = 0; i < codes.Length; i++) result[codes[i]] = counts[i];
            return result;
        }

        static readonly System.Collections.Generic.Dictionary<uint, string> CommandNames = new System.Collections.Generic.Dictionary<uint, string>();

        /// <summary>"Group.Name(index)", as Logs/RigidIPC_commands.log prints it.</summary>
        public static string CommandName(uint code)
        {
            if (CommandNames.TryGetValue(code, out var name)) return name;
            var buffer = new StringBuilder(96);
            RigidIPC_GetCommandName(code, buffer, buffer.Capacity);
            return CommandNames[code] = buffer.ToString();
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
