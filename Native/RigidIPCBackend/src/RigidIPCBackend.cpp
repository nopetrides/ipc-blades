// RigidIPCBackend — Track 1 gate backend for Unity's IUnityPhysicsBackendSystem (Unity 6000.6.7f1).
//
// Purpose: prove Unity will load, list, select, and drive a third GameObject physics backend
// (gates A-E), and record the exact command stream Unity emits (including RA2's joint/motor
// traffic). It is deliberately NOT a physics solver: Simulate integrates gravity, applied
// forces and kinematic targets only. No contact, no joint solve. The IPC solver is Track 2.
//
// Protocol semantics that the headers leave open (which handle arrives as sdkObject, whether
// AttachShape's pose is local or world, ...) are handled defensively and logged so the
// command log can settle them.

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cmath>
#include <ctime>
#include <mutex>
#include <vector>
#include <map>
#include <set>
#include <string>
#include <algorithm>
#include <filesystem>

#include "IUnityInterface.h"
#include "IUnityPhysicsBackendSystem.h"
#include "PhysicsCommonTypes.h"

#include "PhysicsCommands.h"
#include "PhysicsGeneralCommandData.h"
#include "PhysicsMaterialCommandData.h"
#include "PhysicsWorldCommandData.h"
#include "PhysicsBodyCommandData.h"
#include "PhysicsShapeCommandData.h"
#include "PhysicsQueryCommandData.h"
#include "PhysicsJointCommandData.h"

// Selective imports: the protocol's World/Body/Shape/Joint enums would clash with our object types.
using PhysicsCommands::Command;
using PhysicsCommands::Context;
using PhysicsCommands::SDK;
using PhysicsCommands::kSDKFunctionMask;
using PhysicsCommands::kWorldFunctionMask;
using PhysicsCommands::kBodyFunctionMask;
using PhysicsCommands::kShapeFunctionMask;
using PhysicsCommands::kQueryFunctionMask;
using PhysicsCommands::kJointFunctionMask;
using PhysicsCommands::kArticulationFunctionMask;
using PhysicsCommands::kFullMask;
using PhysicsCommands::kReverseFullMask;
namespace SdkData = PhysicsCommands::SdkData;
namespace MaterialData = PhysicsCommands::MaterialData;
namespace WorldData = PhysicsCommands::WorldData;
namespace BodyData = PhysicsCommands::BodyData;
namespace ShapeData = PhysicsCommands::ShapeData;
namespace QueryData = PhysicsCommands::QueryData;
namespace JointData = PhysicsCommands::JointData;

// ---------------------------------------------------------------------------------------------
// Identity
// ---------------------------------------------------------------------------------------------

static constexpr uint32_t kIntegrationId = 0x52495043u; // 'RIPC' = 1380536387
static constexpr const char* kIntegrationName = "RigidIPC";
static constexpr const char* kIntegrationDesc =
    "Rigid-IPC research backend (Track 1 gate build: gravity/forces/kinematic only, no contact).";

// ---------------------------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------------------------

static std::recursive_mutex g_lock;
static FILE* g_log = nullptr;
static std::map<uint32_t, uint64_t> g_cmdCounts;
static uint64_t g_totalCommands = 0;
static uint64_t g_simulateCalls = 0;
static bool g_registered = false;
static bool g_initialized = false;
static uint16_t g_engineObjectIdOffset = 0;
static int g_traceSteps = 0;   // >0: log every dynamic body's pose after each of the next N Simulate calls (off by default)

static void LogOpen()
{
    if (g_log)
        return;
    std::error_code ec;
    std::filesystem::path dir = std::filesystem::current_path(ec) / "Logs";
    std::filesystem::create_directories(dir, ec);
    g_log = std::fopen((dir / "RigidIPC_commands.log").string().c_str(), "a");
}

static void Logf(const char* fmt, ...)
{
    LogOpen();
    if (!g_log)
        return;
    char stamp[32];
    std::time_t t = std::time(nullptr);
    std::strftime(stamp, sizeof(stamp), "%Y-%m-%d %H:%M:%S", std::localtime(&t));
    std::fprintf(g_log, "[%s] ", stamp);
    va_list args;
    va_start(args, fmt);
    std::vfprintf(g_log, fmt, args);
    va_end(args);
    std::fputc('\n', g_log);
    std::fflush(g_log);
}

static const char* const kSdkNames[] = { "Invalid", "InitializeSdk", "ShutdownSdk", "GetIntegrationInfo", "SetErrorVerbosityLevel",
    "ConnectVisualDebugger", "DisconnectVisualDebugger", "GetIsVisualDebuggerConnected", "SetVisualDebuggerViewport",
    "CreateMaterial", "ReleaseMaterial", "GetDefaultMaterial", "SetImprovedPatchFrictionEnabledGlobal",
    "SetMaterialConfiguration", "GetMaterialConfiguration", "SetMaterialCombineModes", "GetMaterialCombineModes" };
static const char* const kWorldNames[] = { "Invalid", "CreateWorld", "DestroyWorld", "SetGravity", "GetGravity", "SetBounceThreshold",
    "GetBounceThreshold", "GetBasicStats", "SetScratchBufferChunkCount", "Simulate", "GetLastSimulationStepResults",
    "ReleaseLastSimulationStepData", "GetLastSimulationStats", "GetUserData", "CreateBody", "AddActorsToScene", "DestroyBody",
    "CreateArticulation", "DestroyArticulation", "CreateJoint", "DestroyJoint", "GetArticulationCount", "GetArticulations",
    "GetAllShapesByLayerMask" };
static const char* const kBodyNames[] = { "Invalid", "SetFlags", "GetFlags", "SetMass", "GetMass", "SetDamping", "GetDamping",
    "SetPose", "GetPose", "AttachShape", "DetachShape", "GetShapeCount", "GetShapes", "GetShapesUserData", "GetUserData",
    "SetCollisionDetectionMode", "AddForce", "AddTorque", "SetLinearVelocity", "GetLinearVelocity", "SetAngularVelocity",
    "GetAngularVelocity", "SetMaxLinearVelocity", "GetMaxLinearVelocity", "SetMaxAngularVelocity", "GetMaxAngularVelocity",
    "SetMaxDepenetrationVelocity", "GetMaxDepenetrationVelocity", "SetInertiaTensor", "GetInertiaTensor",
    "SetInertiaTensorRotation", "GetInertiaTensorRotation", "SetLocalCenterOfMass", "GetLocalCenterOfMass",
    "GetWorldInertiaTensorMatrix", "RecomputeMassProperties", "GetAccumulatedForce", "GetAccumulatedTorque",
    "GetLocalPointVelocity", "GetWorldPointVelocity", "GetClosestWorldPointOnBounds", "SetKinematicTarget",
    "GetKinematicTarget", "GetIsSleeping", "Sleep", "WakeUp", "SetSleepThresholdOverride", "GetSleepThresholdOverride",
    "SetSolverIterationsOverride", "GetSolverIterationsOverride" };
static const char* const kShapeNames[] = { "Invalid", "CreateShape", "DestroyShape", "GetActorDescriptor", "GetUserData", "GetFlags",
    "SetFlags", "GetFilterData", "SetFilterData", "SetModifiableContacts", "SetSupportedMessages", "SetMaterial",
    "GetLocalPose", "GetPose", "SetPose", "SetContactOffset", "GetWorldBounds", "GetBoundsAtPose", "ShouldIgnoreCollision",
    "IgnoreCollision", "CleanupIgnoredColliders", "CookCollisionMesh", "CookCollisionMeshStream",
    "CreateCollisionMeshFromStream", "CreateHeightField", "UpdateHeightFieldRegion", "DestroyHeightField",
    "DestroyCollisionMesh", "ExtractCollisionMeshData", "GetGeometryType", "SetGeometry", "GetGeometry",
    "RemapTriangleIndexToSourceIndex" };
static const char* const kQueryNames[] = { "Invalid", "ComputeShapePenetration", "ClosestPointOnShape", "RayCast",
    "RayCastAgainstShape", "RayCastWithCollector", "Overlap", "OverlapWithCollector", "OverlapWithBroadphaseCollector",
    "ShapeCast", "ShapeCastAgainstShape", "ShapeCastWithCollector", "BodyCast", "BodyCastWithCollector" };
static const char* const kJointNames[] = { "Invalid", "SetFlags", "GetFlags", "GetUserData", "SetActorLocalPose",
    "GetActorLocalPose", "SetBreakForce", "GetIsConstraintValid", "GetForces", "SetInvMassAndInertiaScale", "SetActors",
    "GetActors", "WakeUpActors", "Set6DofLimit", "Get6DofLimit", "Set6DofSpringLimit", "Set6DofAxisLock",
    "Set6DofAxisMotorConfiguration", "Set6DofMotorTargetPosition", "Set6DofMotorTargetRotation",
    "Set6DofMotorTargetLinearVelocity", "Set6DofMotorTargetAngularVelocity", "Set6DofProjectionTolerance",
    "SetDistanceLimit", "SetDistanceSpringLimit", "SetDistanceErrorTolerance", "SetHingeMotorConfiguration",
    "SetHingeMotorTargetVelocity", "SetHingeLimit", "SetHingeLimitEnabled" };

template <size_t N>
static const char* Lookup(const char* const (&names)[N], uint32_t i) { return i < N ? names[i] : "?"; }

static std::string CommandName(uint32_t cmdFunc)
{
    const uint32_t group = cmdFunc & kFullMask;
    const uint32_t index = cmdFunc & kReverseFullMask;
    const char* g = "Unknown";
    const char* n = "?";
    switch (group)
    {
        case kSDKFunctionMask: g = "SDK"; n = Lookup(kSdkNames, index); break;
        case kWorldFunctionMask: g = "World"; n = Lookup(kWorldNames, index); break;
        case kBodyFunctionMask: g = "Body"; n = Lookup(kBodyNames, index); break;
        case kShapeFunctionMask: g = "Shape"; n = Lookup(kShapeNames, index); break;
        case kQueryFunctionMask: g = "Query"; n = Lookup(kQueryNames, index); break;
        case kJointFunctionMask: g = "Joint"; n = Lookup(kJointNames, index); break;
        case kArticulationFunctionMask: g = "Articulation"; break;
        default: break;
    }
    char buf[96];
    std::snprintf(buf, sizeof(buf), "%s.%s(%u)", g, n, index);
    return buf;
}

static void LogHistogram(const char* reason)
{
    Logf("---- command histogram (%s): total=%llu simulate=%llu ----", reason,
        (unsigned long long)g_totalCommands, (unsigned long long)g_simulateCalls);
    for (auto& kv : g_cmdCounts)
        Logf("    %-48s %llu", CommandName(kv.first).c_str(), (unsigned long long)kv.second);
}

// ---------------------------------------------------------------------------------------------
// Math
// ---------------------------------------------------------------------------------------------

struct V3 { float x = 0, y = 0, z = 0; };
struct Q4 { float x = 0, y = 0, z = 0, w = 1; };

static V3 Add(V3 a, V3 b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
static V3 Sub(V3 a, V3 b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
static V3 Mul(V3 a, float s) { return { a.x * s, a.y * s, a.z * s }; }
static V3 Cross(V3 a, V3 b) { return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x }; }
static float Dot(V3 a, V3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
static float Len(V3 a) { return std::sqrt(Dot(a, a)); }
static V3 Load3(const float* f) { return { f[0], f[1], f[2] }; }
static void Store3(V3 v, float* f) { f[0] = v.x; f[1] = v.y; f[2] = v.z; }
static V3 Load3(const Physics::Vec3& v) { return { v.x, v.y, v.z }; }
static void Store3(V3 v, Physics::Vec3& o) { o.x = v.x; o.y = v.y; o.z = v.z; }

static Q4 QMul(Q4 a, Q4 b)
{
    return { a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
             a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
             a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
             a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z };
}
static Q4 QConj(Q4 q) { return { -q.x, -q.y, -q.z, q.w }; }
static Q4 QNorm(Q4 q)
{
    float n = std::sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
    if (n < 1e-12f) return {};
    return { q.x / n, q.y / n, q.z / n, q.w / n };
}
static V3 QRot(Q4 q, V3 v)
{
    V3 u{ q.x, q.y, q.z };
    V3 t = Mul(Cross(u, v), 2.0f);
    return Add(Add(v, Mul(t, q.w)), Cross(u, t));
}

struct Xf { V3 p; Q4 q; };
static Xf FromPose(const Physics::Pose& pose) { return { Load3(pose.pos), { pose.quat.x, pose.quat.y, pose.quat.z, pose.quat.w } }; }
static Physics::Pose ToPose(const Xf& x) { return Physics::Pose(x.p.x, x.p.y, x.p.z, x.q.x, x.q.y, x.q.z, x.q.w); }
static Xf Compose(const Xf& a, const Xf& b) { return { Add(a.p, QRot(a.q, b.p)), QNorm(QMul(a.q, b.q)) }; }
static Xf Inverse(const Xf& a) { Q4 ci = QConj(a.q); return { QRot(ci, Mul(a.p, -1.0f)), ci }; }

// 3x3 helpers for the inertia tensor (row-major).
struct M3 { float m[9] = { 1,0,0, 0,1,0, 0,0,1 }; };
static M3 FromQuat(Q4 q)
{
    M3 r;
    float xx = q.x * q.x, yy = q.y * q.y, zz = q.z * q.z, xy = q.x * q.y, xz = q.x * q.z, yz = q.y * q.z;
    float wx = q.w * q.x, wy = q.w * q.y, wz = q.w * q.z;
    float v[9] = { 1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy),
                   2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx),
                   2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy) };
    std::memcpy(r.m, v, sizeof(v));
    return r;
}
// R * diag(d) * R^T
static M3 RDRt(const M3& R, V3 d)
{
    M3 o;
    float dd[3] = { d.x, d.y, d.z };
    for (int i = 0; i < 3; ++i)
        for (int j = 0; j < 3; ++j)
        {
            float s = 0;
            for (int k = 0; k < 3; ++k) s += R.m[i * 3 + k] * dd[k] * R.m[j * 3 + k];
            o.m[i * 3 + j] = s;
        }
    return o;
}
static V3 MulMV(const M3& M, V3 v)
{
    return { M.m[0] * v.x + M.m[1] * v.y + M.m[2] * v.z,
             M.m[3] * v.x + M.m[4] * v.y + M.m[5] * v.z,
             M.m[6] * v.x + M.m[7] * v.y + M.m[8] * v.z };
}

// ---------------------------------------------------------------------------------------------
// Objects. Every handle we hand to Unity is a pointer to an Obj; g_objects validates handles
// before they are dereferenced, and destroyed objects are kept (alive=false) so a stale handle
// is logged instead of crashing the editor.
// ---------------------------------------------------------------------------------------------

enum class Kind : uint32_t { World, Body, Shape, Joint, Material, Mesh, HeightField, Articulation };
static const char* KindName(Kind k)
{
    static const char* n[] = { "World", "Body", "Shape", "Joint", "Material", "Mesh", "HeightField", "Articulation" };
    return n[(int)k];
}

struct Obj
{
    explicit Obj(Kind k) : kind(k) {}
    virtual ~Obj() = default;
    Kind kind;
    bool alive = true;
    void* userData = nullptr;
    uint32_t serial = 0;
};

static std::set<Obj*> g_objects;
static uint32_t g_nextSerial = 1;

template <typename T, typename... A>
static T* Make(A&&... a)
{
    T* o = new T(std::forward<A>(a)...);
    o->serial = g_nextSerial++;
    g_objects.insert(o);
    return o;
}

template <typename T>
static T* As(void* handle, Kind k)
{
    Obj* o = static_cast<Obj*>(handle);
    if (!handle || g_objects.find(o) == g_objects.end() || o->kind != k)
        return nullptr;
    return static_cast<T*>(o);
}

struct Material : Obj
{
    Material() : Obj(Kind::Material) {}
    float dynamicFriction = 0.6f, staticFriction = 0.6f, bounciness = 0;
    Physics::MaterialCombineMode frictionCombine = Physics::MaterialCombineMode::Average;
    Physics::MaterialCombineMode bounceCombine = Physics::MaterialCombineMode::Average;
};

struct Mesh : Obj
{
    Mesh() : Obj(Kind::Mesh) {}
    bool convex = false;
    std::vector<float> vertices;    // xyz triplets
    std::vector<uint32_t> indices;  // triangle list
    V3 boundsMin, boundsMax;
};

struct HeightField : Obj
{
    HeightField() : Obj(Kind::HeightField) {}
    int resolution = 0;
};

struct Body;

struct Shape : Obj
{
    Shape() : Obj(Kind::Shape) {}
    Physics::ShapeGeometry geom{};
    Physics::ShapeFlags flags = Physics::ShapeFlags::None;
    Physics::FilterData filter;
    Physics::ShapePairEventFlags supportedMessages = Physics::ShapePairEventFlags::None;
    int layer = 0;
    bool modifiableContacts = false;
    float contactOffset = 0.01f;
    Material* material = nullptr;
    Body* body = nullptr;
    Xf local;
};

struct World;

struct Body : Obj
{
    Body() : Obj(Kind::Body) {}
    World* world = nullptr;
    bool isStatic = false;
    Physics::BodyFlags flags = Physics::BodyFlags::None;
    Physics::CollisionDetectionMode ccd = Physics::CollisionDetectionMode::Discrete;
    Xf pose;
    float mass = 1, linearDamping = 0, angularDamping = 0.05f;
    V3 linVel, angVel;
    float maxLinVel = 1e16f, maxAngVel = 7.0f, maxDepenetration = 1e16f;
    V3 inertia{ 1, 1, 1 };
    Q4 inertiaRot;
    V3 com;
    V3 force, torque, impulse, angImpulse;   // accumulated until the next Simulate
    V3 lastForce, lastTorque;                // what the last Simulate consumed (GetAccumulated*)
    V3 lastImpulse, lastAngImpulse;          // impulses the last Simulate consumed (diagnostics only)
    bool hasKinematicTarget = false;
    Xf kinematicTarget;
    bool sleeping = false;
    float sleepThreshold = -1;
    uint32_t posIters = 0, velIters = 0;
    std::vector<Shape*> shapes;
};

struct Joint : Obj
{
    Joint() : Obj(Kind::Joint) {}
    Physics::JointType type = Physics::JointType::Invalid;
    Physics::JointFlags flags = Physics::JointFlags::None;
    Physics::ActorDescriptor actors[2]{};
    Xf localPose[2];
    float breakForce = INFINITY, breakTorque = INFINITY;
    float invMassScale[2] = { 1, 1 };
    struct Axis { Physics::JointDofLock lock = Physics::JointDofLock::Locked; float min = 0, max = 0, spring = 0, damping = 0;
                  float driveSpring = 0, driveDamper = 0, driveMaxForce = INFINITY; int driveAccel = 0; };
    Axis axes[9];   // indexed by Physics::ConstraintAxis
    V3 targetPosition, targetLinVel, targetAngVel;
    Q4 targetRotation;
    uint32_t targetRotationSets = 0;   // Set6DofMotorTargetRotation calls received (diagnostics only)
};

struct World : Obj
{
    World() : Obj(Kind::World) {}
    V3 gravity{ 0, -9.81f, 0 };
    float bounceThreshold = 2;
    std::vector<Body*> bodies;
    std::vector<Joint*> joints;
    Physics::SimulationStepStats lastStats;
};

struct Articulation : Obj
{
    Articulation() : Obj(Kind::Articulation) {}
};

static Material* g_defaultMaterial = nullptr;
static std::vector<std::vector<uint8_t>*> g_cookedStreams;  // kept alive; Unity may read after the callback returns

static std::set<std::pair<uint16_t, uint16_t>> g_ignoredPairs;  // keyed by Unity ignore id (6000.6 protocol)
static std::pair<uint16_t, uint16_t> IgnoreKey(uint16_t a, uint16_t b) { return a < b ? std::make_pair(a, b) : std::make_pair(b, a); }

static Physics::ActorDescriptor DescribeBody(Body* b)
{
    Physics::ActorDescriptor d{};
    if (!b) return d;
    d.actor = b;
    d.userData = b->userData;
    d.type = b->isStatic ? Physics::ActorType::Static : Physics::ActorType::Dynamic;
    return d;
}

// ---------------------------------------------------------------------------------------------
// Geometry helpers (bounds only — this build has no narrow phase)
// ---------------------------------------------------------------------------------------------

static void LocalExtents(const Physics::ShapeGeometry& g, V3& center, V3& half)
{
    center = {};
    switch (g.type)
    {
        case Physics::GeometryType::Box: half = { g.boxGeometry.halfExtents.x, g.boxGeometry.halfExtents.y, g.boxGeometry.halfExtents.z }; break;
        case Physics::GeometryType::Sphere: half = { g.sphereGeometry.radius, g.sphereGeometry.radius, g.sphereGeometry.radius }; break;
        case Physics::GeometryType::Capsule:
        {
            // 'height' is the PhysX half-height (half the cylinder length, caps excluded), not the total height the
            // header comment describes: Gate E measured CapsuleCollider.height == 2 * (height + radius) on 9 capsules.
            // The capsule axis is local x.
            half = { g.capsuleGeometry.height + g.capsuleGeometry.radius, g.capsuleGeometry.radius, g.capsuleGeometry.radius };
            break;
        }
        case Physics::GeometryType::ConvexMesh:
        case Physics::GeometryType::TriangleMesh:
        {
            Mesh* m = As<Mesh>(g.meshGeometry.mesh, Kind::Mesh);
            if (!m || m->vertices.empty()) { half = {}; break; }
            V3 s{ std::fabs(g.meshGeometry.scale.x), std::fabs(g.meshGeometry.scale.y), std::fabs(g.meshGeometry.scale.z) };
            V3 c = Mul(Add(m->boundsMin, m->boundsMax), 0.5f);
            V3 h = Mul(Sub(m->boundsMax, m->boundsMin), 0.5f);
            center = { c.x * g.meshGeometry.scale.x, c.y * g.meshGeometry.scale.y, c.z * g.meshGeometry.scale.z };
            half = { h.x * s.x, h.y * s.y, h.z * s.z };
            break;
        }
        default: half = {}; break;
    }
}

static void WorldBounds(const Physics::ShapeGeometry& g, const Xf& pose, V3& center, V3& half)
{
    V3 lc, lh;
    LocalExtents(g, lc, lh);
    M3 R = FromQuat(pose.q);
    center = Add(pose.p, MulMV(R, lc));
    half = { std::fabs(R.m[0]) * lh.x + std::fabs(R.m[1]) * lh.y + std::fabs(R.m[2]) * lh.z,
             std::fabs(R.m[3]) * lh.x + std::fabs(R.m[4]) * lh.y + std::fabs(R.m[5]) * lh.z,
             std::fabs(R.m[6]) * lh.x + std::fabs(R.m[7]) * lh.y + std::fabs(R.m[8]) * lh.z };
}

static Xf ShapeWorldPose(const Shape* s)
{
    return s->body ? Compose(s->body->pose, s->local) : s->local;
}

static void RecomputeInertia(Body* b, const Physics::MassPropertiesOverride& o)
{
    b->com = o.overrideCenterOfMass ? Load3(o.centerOfMass) : V3{};
    if (o.overrideInertiaTensor)
    {
        b->inertia = Load3(o.inertiaTensor);
        b->inertiaRot = { o.inertiaTensorRotation.x, o.inertiaTensorRotation.y, o.inertiaTensorRotation.z, o.inertiaTensorRotation.w };
        return;
    }
    // Box inertia of the union of shape AABBs in body space; good enough for a gate stepper.
    V3 mn{ 1e30f, 1e30f, 1e30f }, mx{ -1e30f, -1e30f, -1e30f };
    for (Shape* s : b->shapes)
    {
        V3 c, h;
        WorldBounds(s->geom, s->local, c, h);
        mn = { std::min(mn.x, c.x - h.x), std::min(mn.y, c.y - h.y), std::min(mn.z, c.z - h.z) };
        mx = { std::max(mx.x, c.x + h.x), std::max(mx.y, c.y + h.y), std::max(mx.z, c.z + h.z) };
    }
    V3 e = b->shapes.empty() ? V3{ 1, 1, 1 } : Sub(mx, mn);
    float k = b->mass / 12.0f;
    b->inertia = { std::max(1e-6f, k * (e.y * e.y + e.z * e.z)), std::max(1e-6f, k * (e.x * e.x + e.z * e.z)),
                   std::max(1e-6f, k * (e.x * e.x + e.y * e.y)) };
    b->inertiaRot = {};
    if (!o.overrideCenterOfMass && !b->shapes.empty())
        b->com = Mul(Add(mn, mx), 0.5f);
}

static M3 WorldInvInertia(const Body* b)
{
    M3 R = FromQuat(QNorm(QMul(b->pose.q, b->inertiaRot)));
    return RDRt(R, { 1.0f / b->inertia.x, 1.0f / b->inertia.y, 1.0f / b->inertia.z });
}

static bool HasFlag(Physics::BodyFlags f, Physics::BodyFlags bit) { return (f & bit) == bit; }

// ---------------------------------------------------------------------------------------------
// Gate stepper: gravity + applied forces/impulses + kinematic targets. No contact, no joints.
// ---------------------------------------------------------------------------------------------

static void StepWorld(World* w, float dt)
{
    Physics::SimulationStepStats stats{};
    for (Body* b : w->bodies)
    {
        if (!b->alive) continue;
        if (b->isStatic) { stats.staticBodies++; continue; }
        const bool kinematic = HasFlag(b->flags, Physics::BodyFlags::Kinematic);
        if (kinematic)
        {
            stats.kinematicBodies++;
            if (b->hasKinematicTarget && dt > 0)
            {
                b->linVel = Mul(Sub(b->kinematicTarget.p, b->pose.p), 1.0f / dt);
                Q4 dq = QMul(b->kinematicTarget.q, QConj(b->pose.q));
                if (dq.w < 0) dq = { -dq.x, -dq.y, -dq.z, -dq.w };
                float s = std::sqrt(dq.x * dq.x + dq.y * dq.y + dq.z * dq.z);
                float angle = 2.0f * std::atan2(s, dq.w);
                b->angVel = s > 1e-9f ? Mul(V3{ dq.x, dq.y, dq.z }, angle / (s * dt)) : V3{};
                b->pose = b->kinematicTarget;
                b->hasKinematicTarget = false;
                stats.activeKinematicBodies++;
            }
            else
            {
                b->linVel = {};
                b->angVel = {};
            }
            b->force = b->torque = b->impulse = b->angImpulse = {};
            continue;
        }

        stats.dynamicBodies++;
        b->lastForce = b->force;
        b->lastTorque = b->torque;
        b->lastImpulse = b->impulse;
        b->lastAngImpulse = b->angImpulse;
        if (b->sleeping || dt <= 0)
        {
            b->force = b->torque = b->impulse = b->angImpulse = {};
            continue;
        }
        stats.activeDynamicBodies++;

        const float invMass = b->mass > 0 ? 1.0f / b->mass : 0.0f;
        V3 g = HasFlag(b->flags, Physics::BodyFlags::Gravity) ? w->gravity : V3{};
        b->linVel = Add(b->linVel, Add(Mul(Add(g, Mul(b->force, invMass)), dt), Mul(b->impulse, invMass)));
        M3 invI = WorldInvInertia(b);
        b->angVel = Add(b->angVel, MulMV(invI, Add(Mul(b->torque, dt), b->angImpulse)));
        b->linVel = Mul(b->linVel, std::max(0.0f, 1.0f - dt * b->linearDamping));
        b->angVel = Mul(b->angVel, std::max(0.0f, 1.0f - dt * b->angularDamping));
        float lv = Len(b->linVel), av = Len(b->angVel);
        if (lv > b->maxLinVel) b->linVel = Mul(b->linVel, b->maxLinVel / lv);
        if (av > b->maxAngVel) b->angVel = Mul(b->angVel, b->maxAngVel / av);

        // Integrate about the centre of mass, then recover the actor origin.
        V3 comWorld = Add(b->pose.p, QRot(b->pose.q, b->com));
        comWorld = Add(comWorld, Mul(b->linVel, dt));
        Q4 spin{ b->angVel.x, b->angVel.y, b->angVel.z, 0 };
        Q4 dq = QMul(spin, b->pose.q);
        Q4 q{ b->pose.q.x + 0.5f * dt * dq.x, b->pose.q.y + 0.5f * dt * dq.y, b->pose.q.z + 0.5f * dt * dq.z, b->pose.q.w + 0.5f * dt * dq.w };
        b->pose.q = QNorm(q);
        b->pose.p = Sub(comWorld, QRot(b->pose.q, b->com));

        b->force = b->torque = b->impulse = b->angImpulse = {};
    }
    for (Joint* j : w->joints)
        if (j->alive) stats.constraints++;
    w->lastStats = stats;
}

// ---------------------------------------------------------------------------------------------
// Command handlers
// ---------------------------------------------------------------------------------------------

template <typename T> static T& D(Command& c) { return static_cast<T&>(c); }

static void FillIntegrationInfo(Physics::IntegrationInfo& info)
{
    info = Physics::IntegrationInfo{};
    info.id = kIntegrationId;
    info.version[0] = 0; info.version[1] = 1; info.version[2] = 0;
    info.sdkVersion[0] = 0; info.sdkVersion[1] = 1; info.sdkVersion[2] = 0;
    info.features = Physics::FeatureSupport::DynamicsSupport;
    std::snprintf(info.name, sizeof(info.name), "%s", kIntegrationName);
    std::snprintf(info.desc, sizeof(info.desc), "%s", kIntegrationDesc);
}

static void HandleSdk(void* sdkObject, const Context&, uint32_t index, Command& c)
{
    switch ((SDK)index)
    {
        case SDK::InitializeSdk:
        {
            auto& d = D<SdkData::InitializeSdk>(c);
            g_engineObjectIdOffset = d.engineObjectIdOffset;
            if (!g_defaultMaterial) g_defaultMaterial = Make<Material>();
            g_initialized = true;
            d.result = true;
            Logf("InitializeSdk engineObjectIdOffset=%u vehicleModule=%d -> true", d.engineObjectIdOffset, d.vehicleExtensionModulePresent);
            break;
        }
        case SDK::ShutdownSdk:
            Logf("ShutdownSdk");
            LogHistogram("ShutdownSdk");
            g_initialized = false;
            break;
        case SDK::GetIntegrationInfo:
            FillIntegrationInfo(D<SdkData::GetSdkIntegrationInfo>(c).value);
            break;
        case SDK::SetErrorVerbosityLevel: break;
        case SDK::ConnectVisualDebugger: D<SdkData::ConnectVisualDebugger>(c).result = false; break;
        case SDK::DisconnectVisualDebugger: break;
        case SDK::GetIsVisualDebuggerConnected: D<SdkData::GetIsVisualDebuggerConnected>(c).value = false; break;
        case SDK::SetVisualDebuggerViewport: break;
        case SDK::CreateMaterial:
        {
            auto& d = D<MaterialData::CreateMaterial>(c);
            Material* m = Make<Material>();
            m->userData = d.userData;
            d.outMaterial = m;
            break;
        }
        case SDK::ReleaseMaterial:
            if (Material* m = As<Material>(sdkObject, Kind::Material)) m->alive = false;
            break;
        case SDK::GetDefaultMaterial:
            if (!g_defaultMaterial) g_defaultMaterial = Make<Material>();
            D<MaterialData::GetDefaultMaterial>(c).value = g_defaultMaterial;
            break;
        case SDK::SetImprovedPatchFrictionEnabledGlobal: break;
        case SDK::SetMaterialConfiguration:
            if (Material* m = As<Material>(sdkObject, Kind::Material))
            {
                auto& d = D<MaterialData::SetMaterialConfiguration>(c);
                m->dynamicFriction = d.dynamicFriction; m->staticFriction = d.staticFriction; m->bounciness = d.bounciness;
            }
            break;
        case SDK::GetMaterialConfiguration:
        {
            auto& d = D<MaterialData::GetMaterialConfiguration>(c);
            Material* m = As<Material>(sdkObject, Kind::Material);
            if (!m) m = g_defaultMaterial;
            d.dynamicFriction = m ? m->dynamicFriction : 0.6f; d.staticFriction = m ? m->staticFriction : 0.6f; d.bounciness = m ? m->bounciness : 0;
            break;
        }
        case SDK::SetMaterialCombineModes:
            if (Material* m = As<Material>(sdkObject, Kind::Material))
            {
                auto& d = D<MaterialData::SetMaterialCombineModes>(c);
                m->frictionCombine = d.friction; m->bounceCombine = d.bounciness;
            }
            break;
        case SDK::GetMaterialCombineModes:
        {
            auto& d = D<MaterialData::GetMaterialCombineModes>(c);
            Material* m = As<Material>(sdkObject, Kind::Material);
            d.friction = m ? m->frictionCombine : Physics::MaterialCombineMode::Average;
            d.bounciness = m ? m->bounceCombine : Physics::MaterialCombineMode::Average;
            break;
        }
        default: Logf("UNHANDLED %s", CommandName(kSDKFunctionMask | index).c_str()); break;
    }
}

static World* ResolveWorld(void* sdkObject, const Context& ctx)
{
    if (World* w = As<World>(sdkObject, Kind::World)) return w;
    return As<World>(ctx.world, Kind::World);
}

static void HandleWorld(void* sdkObject, const Context& ctx, uint32_t index, Command& c)
{
    World* w = ResolveWorld(sdkObject, ctx);
    switch ((PhysicsCommands::World)index)
    {
        case PhysicsCommands::World::CreateWorld:
        {
            auto& d = D<WorldData::CreateWorld>(c);
            World* nw = Make<World>();
            nw->userData = d.userData;
            nw->gravity = Load3(d.gravity);
            nw->bounceThreshold = d.bounceThreshold;
            d.outWorld = nw;
            Logf("CreateWorld #%u solver=%d gravity=(%g,%g,%g) workers=%u", nw->serial, (int)d.solverType, d.gravity.x, d.gravity.y, d.gravity.z, d.jobWorkerCount);
            break;
        }
        case PhysicsCommands::World::DestroyWorld:
            if (w) { w->alive = false; Logf("DestroyWorld #%u", w->serial); }
            break;
        case PhysicsCommands::World::SetGravity: if (w) w->gravity = Load3(D<WorldData::SetGravity>(c).value); break;
        case PhysicsCommands::World::GetGravity: Store3(w ? w->gravity : V3{ 0, -9.81f, 0 }, D<WorldData::GetGravity>(c).value); break;
        case PhysicsCommands::World::SetBounceThreshold: if (w) w->bounceThreshold = D<WorldData::SetBounceThreshold>(c).value; break;
        case PhysicsCommands::World::GetBounceThreshold: D<WorldData::GetBounceThreshold>(c).value = w ? w->bounceThreshold : 2.0f; break;
        case PhysicsCommands::World::GetBasicStats:
        {
            auto& v = D<WorldData::GetBasicStats>(c).value;
            v = Physics::WorldStats{};
            if (w)
            {
                for (Body* b : w->bodies) if (b->alive) v.bodies++;
                for (Joint* j : w->joints) if (j->alive) v.constraints++;
            }
            break;
        }
        case PhysicsCommands::World::SetScratchBufferChunkCount: break;
        case PhysicsCommands::World::Simulate:
        {
            auto& d = D<WorldData::Simulate>(c);
            g_simulateCalls++;
            if (w) StepWorld(w, d.deltaTime);
            d.ranSimulation = w != nullptr;
            if (w && g_traceSteps > 0)
            {
                g_traceSteps--;
                for (Body* b : w->bodies)
                    if (b->alive && !b->isStatic)
                        Logf("trace Simulate #%llu dt=%.9g body#%u pos=(%.9g,%.9g,%.9g) rot=(%.9g,%.9g,%.9g,%.9g) linVel=(%.9g,%.9g,%.9g) angVel=(%.9g,%.9g,%.9g)",
                            (unsigned long long)g_simulateCalls, d.deltaTime, b->serial, b->pose.p.x, b->pose.p.y, b->pose.p.z,
                            b->pose.q.x, b->pose.q.y, b->pose.q.z, b->pose.q.w, b->linVel.x, b->linVel.y, b->linVel.z,
                            b->angVel.x, b->angVel.y, b->angVel.z);
            }
            if (g_simulateCalls <= 3 || g_simulateCalls % 450 == 0)
                Logf("Simulate #%llu world=%u dt=%g bodies=%zu joints=%zu", (unsigned long long)g_simulateCalls, w ? w->serial : 0,
                    d.deltaTime, w ? w->bodies.size() : 0, w ? w->joints.size() : 0);
            if (g_simulateCalls % 450 == 0) LogHistogram("periodic");
            break;
        }
        case PhysicsCommands::World::GetLastSimulationStepResults:
        {
            auto& d = D<WorldData::GetLastSimulationStepResults>(c);
            d.bodyContactPairs = nullptr; d.bodyContactPairsCount = 0;
            d.triggerEvents = nullptr; d.triggerEventsCount = 0;
            d.jointBreakEvents = nullptr; d.jointBreakEventsCount = 0;
            break;
        }
        case PhysicsCommands::World::ReleaseLastSimulationStepData: break;
        case PhysicsCommands::World::GetLastSimulationStats:
            D<WorldData::GetLastSimulationStepStats>(c).value = w ? w->lastStats : Physics::SimulationStepStats{};
            break;
        case PhysicsCommands::World::GetUserData: D<WorldData::GetUserData>(c).userData = w ? w->userData : nullptr; break;
        case PhysicsCommands::World::CreateBody:
        {
            auto& d = D<WorldData::CreateBody>(c);
            Body* b = Make<Body>();
            b->world = w;
            b->userData = d.userData;
            b->pose = FromPose(d.pose);
            b->mass = d.mass;
            b->linearDamping = d.linearDamping;
            b->angularDamping = d.angularDamping;
            b->flags = d.flags;
            b->isStatic = d.isStatic;
            if (w) w->bodies.push_back(b);
            d.outBody = b;
            Logf("CreateBody #%u world=%u static=%d flags=0x%x mass=%g pos=(%g,%g,%g) userData=%p", b->serial, w ? w->serial : 0,
                d.isStatic, (unsigned)d.flags, d.mass, d.pose.pos.x, d.pose.pos.y, d.pose.pos.z, d.userData);
            break;
        }
        case PhysicsCommands::World::AddActorsToScene:
            break;
        case PhysicsCommands::World::DestroyBody:
            if (Body* b = As<Body>(D<WorldData::DestroyBody>(c).value, Kind::Body))
            {
                b->alive = false;
                if (b->world) b->world->bodies.erase(std::remove(b->world->bodies.begin(), b->world->bodies.end(), b), b->world->bodies.end());
                for (Shape* s : b->shapes) s->body = nullptr;
                b->shapes.clear();
                Logf("DestroyBody #%u", b->serial);
            }
            break;
        case PhysicsCommands::World::CreateArticulation:
        {
            auto& d = D<WorldData::CreateArticulation>(c);
            Articulation* a = Make<Articulation>();
            a->userData = d.userData;
            d.outArticulation = a;
            Logf("CreateArticulation #%u (unsupported in gate build)", a->serial);
            break;
        }
        case PhysicsCommands::World::DestroyArticulation:
            if (Articulation* a = As<Articulation>(D<WorldData::DestoryArticulation>(c).value, Kind::Articulation)) a->alive = false;
            break;
        case PhysicsCommands::World::CreateJoint:
        {
            auto& d = D<WorldData::CreateJoint>(c);
            Joint* j = Make<Joint>();
            j->userData = d.userData;
            j->type = d.type;
            j->flags = d.flags;
            j->actors[0] = DescribeBody(As<Body>(d.body0, Kind::Body));
            j->actors[1] = DescribeBody(As<Body>(d.body1, Kind::Body));
            if (w) w->joints.push_back(j);
            d.outJoint = j;
            Logf("CreateJoint #%u type=%d flags=0x%x body0=#%u body1=#%u userData=%p", j->serial, (int)d.type, (unsigned)d.flags,
                j->actors[0].actor ? static_cast<Obj*>(j->actors[0].actor)->serial : 0,
                j->actors[1].actor ? static_cast<Obj*>(j->actors[1].actor)->serial : 0, d.userData);
            break;
        }
        case PhysicsCommands::World::DestroyJoint:
            if (Joint* j = As<Joint>(D<WorldData::DestroyJoint>(c).value, Kind::Joint))
            {
                j->alive = false;
                if (w) w->joints.erase(std::remove(w->joints.begin(), w->joints.end(), j), w->joints.end());
                Logf("DestroyJoint #%u", j->serial);
            }
            break;
        case PhysicsCommands::World::GetArticulationCount: D<WorldData::GetArticulationCount>(c).value = 0; break;
        case PhysicsCommands::World::GetArticulations: D<WorldData::GetArticulations>(c).written = 0; break;
        case PhysicsCommands::World::GetAllShapesByLayerMask:
        {
            auto& d = D<WorldData::GetAllShapesByLayerMask>(c);
            std::vector<void*> found;
            if (w)
                for (Body* b : w->bodies)
                    for (Shape* s : b->shapes)
                        if (s->alive && (s->filter.belongsTo & d.layerMask)) found.push_back(s->userData);
            if (d.callback) d.callback(found.empty() ? nullptr : found.data(), found.size());
            break;
        }
        default: Logf("UNHANDLED %s", CommandName(kWorldFunctionMask | index).c_str()); break;
    }
}

static void HandleBody(void* sdkObject, const Context& ctx, uint32_t index, Command& c)
{
    Body* b = As<Body>(sdkObject, Kind::Body);
    if (!b) b = As<Body>(ctx.body, Kind::Body);
    if (!b)
    {
        Logf("Body command %s on unknown handle %p (ctx.body=%p) — ignored", CommandName(kBodyFunctionMask | index).c_str(), sdkObject, ctx.body);
        return;
    }
    using namespace BodyData;
    switch ((PhysicsCommands::Body)index)
    {
        case PhysicsCommands::Body::SetFlags:
        {
            auto& d = D<BodyData::SetFlags>(c);
            b->flags = d.enabled ? (b->flags | d.value) : (b->flags & ~d.value);
            break;
        }
        case PhysicsCommands::Body::GetFlags: D<BodyData::GetFlags>(c).value = b->flags; break;
        case PhysicsCommands::Body::SetMass: b->mass = D<SetMass>(c).value; break;
        case PhysicsCommands::Body::GetMass: D<GetMass>(c).value = b->mass; break;
        case PhysicsCommands::Body::SetDamping: b->linearDamping = D<SetDamping>(c).linear; b->angularDamping = D<SetDamping>(c).angular; break;
        case PhysicsCommands::Body::GetDamping: D<GetDamping>(c).linear = b->linearDamping; D<GetDamping>(c).angular = b->angularDamping; break;
        case PhysicsCommands::Body::SetPose:
            b->pose = FromPose(D<BodyData::SetPose>(c).value);
            if (D<BodyData::SetPose>(c).awake) b->sleeping = false;
            break;
        case PhysicsCommands::Body::GetPose: D<BodyData::GetPose>(c).value = ToPose(b->pose); break;
        case PhysicsCommands::Body::AttachShape:
        {
            auto& d = D<AttachShape>(c);
            Shape* s = As<Shape>(d.shape, Kind::Shape);
            if (!s) break;
            if (s->body && s->body != b)
                s->body->shapes.erase(std::remove(s->body->shapes.begin(), s->body->shapes.end(), s), s->body->shapes.end());
            s->body = b;
            if (std::find(b->shapes.begin(), b->shapes.end(), s) == b->shapes.end()) b->shapes.push_back(s);
            // Without forceLocalPose the pose's frame depends on the body type (PhysicsBodyCommandData.h):
            // relative to the body for a Rigidbody, world for a static body. Gate D vertex compare confirmed
            // both on 6000.6.7f1 (dynamic boxes got their body-relative offset, the static floor its world pose).
            const bool local = d.forceLocalPose || !b->isStatic;
            s->local = local ? FromPose(d.pose) : Compose(Inverse(b->pose), FromPose(d.pose));
            Logf("AttachShape shape#%u -> body#%u forceLocal=%d pose=(%g,%g,%g) bodyPos=(%g,%g,%g)", s->serial, b->serial,
                d.forceLocalPose, d.pose.pos.x, d.pose.pos.y, d.pose.pos.z, b->pose.p.x, b->pose.p.y, b->pose.p.z);
            break;
        }
        case PhysicsCommands::Body::DetachShape:
            if (Shape* s = As<Shape>(D<DetachShape>(c).shape, Kind::Shape))
            {
                b->shapes.erase(std::remove(b->shapes.begin(), b->shapes.end(), s), b->shapes.end());
                if (s->body == b) s->body = nullptr;
            }
            break;
        case PhysicsCommands::Body::GetShapeCount: D<GetShapeCount>(c).value = b->shapes.size(); break;
        case PhysicsCommands::Body::GetShapes:
        {
            auto& d = D<GetShapes>(c);
            d.written = 0;
            for (Shape* s : b->shapes) { if (d.written >= d.bufferSize || !d.buffer) break; d.buffer[d.written++] = s; }
            break;
        }
        case PhysicsCommands::Body::GetShapesUserData:
        {
            auto& d = D<GetShapesUserData>(c);
            d.written = 0;
            for (Shape* s : b->shapes) { if (d.written >= d.bufferSize || !d.buffer) break; d.buffer[d.written++] = s->userData; }
            break;
        }
        case PhysicsCommands::Body::GetUserData: D<BodyData::GetUserData>(c).value = b->userData; break;
        case PhysicsCommands::Body::SetCollisionDetectionMode: b->ccd = D<SetCollisionDetectionMode>(c).value; break;
        case PhysicsCommands::Body::AddForce:
        {
            auto& d = D<AddForce>(c);
            V3 f = Load3(d.value);
            if (d.mode == Physics::ForceMode::Impulse) b->impulse = Add(b->impulse, f);
            else if (d.mode == Physics::ForceMode::VelocityChange) b->impulse = Add(b->impulse, Mul(f, b->mass));
            else if (d.mode == Physics::ForceMode::Acceleration) b->force = Add(b->force, Mul(f, b->mass));
            else b->force = Add(b->force, f);
            if (d.awake) b->sleeping = false;
            break;
        }
        case PhysicsCommands::Body::AddTorque:
        {
            auto& d = D<AddTorque>(c);
            V3 t = Load3(d.value);
            if (d.mode == Physics::ForceMode::Impulse || d.mode == Physics::ForceMode::VelocityChange) b->angImpulse = Add(b->angImpulse, t);
            else b->torque = Add(b->torque, t);
            if (d.awake) b->sleeping = false;
            break;
        }
        case PhysicsCommands::Body::SetLinearVelocity: b->linVel = Load3(D<SetLinearVelocity>(c).value); if (D<SetLinearVelocity>(c).awake) b->sleeping = false; break;
        case PhysicsCommands::Body::GetLinearVelocity: Store3(b->linVel, D<GetLinearVelocity>(c).value); break;
        case PhysicsCommands::Body::SetAngularVelocity: b->angVel = Load3(D<SetAngularVelocity>(c).value); if (D<SetAngularVelocity>(c).awake) b->sleeping = false; break;
        case PhysicsCommands::Body::GetAngularVelocity: Store3(b->angVel, D<GetAngularVelocity>(c).value); break;
        case PhysicsCommands::Body::SetMaxLinearVelocity: b->maxLinVel = D<SetMaxLinearVelocity>(c).value; break;
        case PhysicsCommands::Body::GetMaxLinearVelocity: D<GetMaxLinearVelocity>(c).value = b->maxLinVel; break;
        case PhysicsCommands::Body::SetMaxAngularVelocity: b->maxAngVel = D<SetMaxAngularVelocity>(c).value; break;
        case PhysicsCommands::Body::GetMaxAngularVelocity: D<GetMaxAngularVelocity>(c).value = b->maxAngVel; break;
        case PhysicsCommands::Body::SetMaxDepenetrationVelocity: b->maxDepenetration = D<SetMaxDepenetrationVelocity>(c).value; break;
        case PhysicsCommands::Body::GetMaxDepenetrationVelocity: D<GetMaxDepenetrationVelocity>(c).value = b->maxDepenetration; break;
        case PhysicsCommands::Body::SetInertiaTensor: b->inertia = Load3(D<SetInertiaTensor>(c).value); break;
        case PhysicsCommands::Body::GetInertiaTensor: Store3(b->inertia, D<GetInertiaTensor>(c).value); break;
        case PhysicsCommands::Body::SetInertiaTensorRotation:
        {
            auto& v = D<SetInertiaTensorRotation>(c).value;
            b->inertiaRot = QNorm({ v.x, v.y, v.z, v.w });
            break;
        }
        case PhysicsCommands::Body::GetInertiaTensorRotation:
        {
            auto& v = D<GetInertiaTensorRotation>(c).value;
            v.x = b->inertiaRot.x; v.y = b->inertiaRot.y; v.z = b->inertiaRot.z; v.w = b->inertiaRot.w;
            break;
        }
        case PhysicsCommands::Body::SetLocalCenterOfMass: b->com = Load3(D<SetLocalCenterOfMass>(c).value); break;
        case PhysicsCommands::Body::GetLocalCenterOfMass: Store3(b->com, D<GetLocalCenterOfMass>(c).value); break;
        case PhysicsCommands::Body::GetWorldInertiaTensorMatrix:
        {
            M3 R = FromQuat(QNorm(QMul(b->pose.q, b->inertiaRot)));
            M3 I = RDRt(R, b->inertia);
            std::memcpy(D<GetWorldInertiaTensorMatrix>(c).value, I.m, sizeof(I.m));
            break;
        }
        case PhysicsCommands::Body::RecomputeMassProperties: RecomputeInertia(b, D<RecomputeMassProperties>(c).overrides); break;
        case PhysicsCommands::Body::GetAccumulatedForce: Store3(b->force, D<GetAccumulatedForce>(c).value); break;
        case PhysicsCommands::Body::GetAccumulatedTorque: Store3(b->torque, D<GetAccumulatedTorque>(c).value); break;
        case PhysicsCommands::Body::GetLocalPointVelocity:
        {
            auto& d = D<GetLocalPointVelocity>(c);
            V3 r = QRot(b->pose.q, Sub(Load3(d.localPoint), b->com));
            Store3(Add(b->linVel, Cross(b->angVel, r)), d.value);
            break;
        }
        case PhysicsCommands::Body::GetWorldPointVelocity:
        {
            auto& d = D<GetWorldPointVelocity>(c);
            V3 comWorld = Add(b->pose.p, QRot(b->pose.q, b->com));
            Store3(Add(b->linVel, Cross(b->angVel, Sub(Load3(d.worldPoint), comWorld))), d.value);
            break;
        }
        case PhysicsCommands::Body::GetClosestWorldPointOnBounds:
        {
            auto& d = D<GetClosestWorldPointOnBounds>(c);
            V3 p = Load3(d.worldPosition), best = p;
            float bestD = 1e30f;
            for (Shape* s : b->shapes)
            {
                V3 ctr, h;
                WorldBounds(s->geom, ShapeWorldPose(s), ctr, h);
                V3 q{ std::clamp(p.x, ctr.x - h.x, ctr.x + h.x), std::clamp(p.y, ctr.y - h.y, ctr.y + h.y), std::clamp(p.z, ctr.z - h.z, ctr.z + h.z) };
                float dd = Dot(Sub(q, p), Sub(q, p));
                if (dd < bestD) { bestD = dd; best = q; }
            }
            Store3(best, d.outClosestWorldPositionOnBounds);
            d.outSqrDistance = b->shapes.empty() ? 0 : bestD;
            break;
        }
        case PhysicsCommands::Body::SetKinematicTarget:
            b->kinematicTarget = FromPose(D<SetKinematicTarget>(c).value);
            b->hasKinematicTarget = true;
            break;
        case PhysicsCommands::Body::GetKinematicTarget:
            D<GetKinematicTarget>(c).value = ToPose(b->hasKinematicTarget ? b->kinematicTarget : b->pose);
            break;
        case PhysicsCommands::Body::GetIsSleeping: D<GetIsSleeping>(c).value = b->sleeping; break;
        case PhysicsCommands::Body::Sleep: b->sleeping = true; b->linVel = b->angVel = {}; break;
        case PhysicsCommands::Body::WakeUp: b->sleeping = false; break;
        case PhysicsCommands::Body::SetSleepThresholdOverride: b->sleepThreshold = D<SetSleepThresholdOverride>(c).value; break;
        case PhysicsCommands::Body::GetSleepThresholdOverride: D<GetSleepThresholdOverride>(c).value = b->sleepThreshold; break;
        case PhysicsCommands::Body::SetSolverIterationsOverride:
            b->posIters = D<SetSolverIterationsOverride>(c).positionIterations;
            b->velIters = D<SetSolverIterationsOverride>(c).velocityIterations;
            break;
        case PhysicsCommands::Body::GetSolverIterationsOverride:
            D<GetSolverIterationsOverride>(c).positionIterations = b->posIters;
            D<GetSolverIterationsOverride>(c).velocityIterations = b->velIters;
            break;
        default: Logf("UNHANDLED %s", CommandName(kBodyFunctionMask | index).c_str()); break;
    }
}

static void HandleShape(void* sdkObject, const Context&, uint32_t index, Command& c)
{
    using namespace ShapeData;
    Shape* s = As<Shape>(sdkObject, Kind::Shape);
    switch ((PhysicsCommands::Shape)index)
    {
        case PhysicsCommands::Shape::CreateShape:
        {
            auto& d = D<CreateShape>(c);
            Shape* ns = Make<Shape>();
            ns->geom = d.geom;
            ns->material = As<Material>(d.materialPtr, Kind::Material);
            ns->userData = d.userData;
            ns->flags = d.flags;
            d.value = ns;
            Logf("CreateShape #%u geom=%u flags=0x%x userData=%p", ns->serial, (unsigned)d.geom.type, (unsigned)d.flags, d.userData);
            return;
        }
        case PhysicsCommands::Shape::CookCollisionMesh:
        case PhysicsCommands::Shape::CookCollisionMeshStream:
        {
            // Both cook variants share the descriptor; copy positions + triangle indices.
            const Physics::CollisionMeshCookingDescriptor& desc = index == (uint32_t)PhysicsCommands::Shape::CookCollisionMesh
                ? D<CookCollisionMesh>(c).descriptor : D<CookCollisionMeshStream>(c).descriptor;
            Mesh* m = Make<Mesh>();
            m->convex = desc.convex;
            const uint8_t* vb = static_cast<const uint8_t*>(desc.vertexStream);
            const uint32_t vstride = desc.vertexElementSize ? desc.vertexElementSize : 12;
            m->boundsMin = { 1e30f, 1e30f, 1e30f }; m->boundsMax = { -1e30f, -1e30f, -1e30f };
            for (uint32_t i = 0; vb && i < desc.vertexElementCount; ++i)
            {
                const float* p = reinterpret_cast<const float*>(vb + (size_t)i * vstride);
                m->vertices.insert(m->vertices.end(), { p[0], p[1], p[2] });
                m->boundsMin = { std::min(m->boundsMin.x, p[0]), std::min(m->boundsMin.y, p[1]), std::min(m->boundsMin.z, p[2]) };
                m->boundsMax = { std::max(m->boundsMax.x, p[0]), std::max(m->boundsMax.y, p[1]), std::max(m->boundsMax.z, p[2]) };
            }
            const uint8_t* ib = static_cast<const uint8_t*>(desc.indexStream);
            for (uint32_t i = 0; ib && i < desc.indexElementCount; ++i)
                m->indices.push_back(desc.indexElementSize == 2 ? reinterpret_cast<const uint16_t*>(ib)[i] : reinterpret_cast<const uint32_t*>(ib)[i]);
            if (m->vertices.empty()) m->boundsMin = m->boundsMax = {};
            Logf("Cook%s #%u convex=%d verts=%u (stride %u) indices=%u (size %u) options=0x%x", index == (uint32_t)PhysicsCommands::Shape::CookCollisionMesh ? "CollisionMesh" : "CollisionMeshStream",
                m->serial, desc.convex, desc.vertexElementCount, desc.vertexElementSize, desc.indexElementCount, desc.indexElementSize, (unsigned)desc.options);
            if (index == (uint32_t)PhysicsCommands::Shape::CookCollisionMesh)
            {
                auto& d = D<CookCollisionMesh>(c);
                if (d.onMeshReadyCallback) d.onMeshReadyCallback(Physics::CookingError::Success, nullptr, d.dataContext, m);
            }
            else
            {
                // Serialized form: "RIPC" magic, convex, vertex float count, index count, payload.
                auto* stream = new std::vector<uint8_t>();
                auto put = [&](const void* p, size_t n) { const uint8_t* b = static_cast<const uint8_t*>(p); stream->insert(stream->end(), b, b + n); };
                const uint32_t magic = 0x43504952u, convex = m->convex ? 1u : 0u;
                const uint32_t nv = (uint32_t)m->vertices.size(), ni = (uint32_t)m->indices.size();
                put(&magic, 4); put(&convex, 4); put(&nv, 4); put(&ni, 4);
                put(m->vertices.data(), nv * sizeof(float)); put(m->indices.data(), ni * sizeof(uint32_t));
                g_cookedStreams.push_back(stream);
                m->alive = false;
                auto& d = D<CookCollisionMeshStream>(c);
                if (d.onStreamReadyCallback) d.onStreamReadyCallback(Physics::CookingError::Success, nullptr, d.dataContext, stream->data(), stream->size());
            }
            return;
        }
        case PhysicsCommands::Shape::CreateCollisionMeshFromStream:
        {
            auto& d = D<CreateCollisionMeshFromStream>(c);
            Mesh* m = Make<Mesh>();
            m->convex = d.convex;
            uint32_t hdr[4] = {};
            if (d.stream && d.streamSize >= sizeof(hdr)) std::memcpy(hdr, d.stream, sizeof(hdr));
            const size_t need = sizeof(hdr) + (size_t)hdr[2] * sizeof(float) + (size_t)hdr[3] * sizeof(uint32_t);
            if (hdr[0] == 0x43504952u && d.streamSize >= need)
            {
                m->vertices.resize(hdr[2]);
                m->indices.resize(hdr[3]);
                std::memcpy(m->vertices.data(), d.stream + sizeof(hdr), hdr[2] * sizeof(float));
                std::memcpy(m->indices.data(), d.stream + sizeof(hdr) + hdr[2] * sizeof(float), hdr[3] * sizeof(uint32_t));
                m->boundsMin = { 1e30f, 1e30f, 1e30f }; m->boundsMax = { -1e30f, -1e30f, -1e30f };
                for (size_t i = 0; i + 2 < m->vertices.size(); i += 3)
                {
                    m->boundsMin = { std::min(m->boundsMin.x, m->vertices[i]), std::min(m->boundsMin.y, m->vertices[i + 1]), std::min(m->boundsMin.z, m->vertices[i + 2]) };
                    m->boundsMax = { std::max(m->boundsMax.x, m->vertices[i]), std::max(m->boundsMax.y, m->vertices[i + 1]), std::max(m->boundsMax.z, m->vertices[i + 2]) };
                }
            }
            else
                Logf("CreateCollisionMeshFromStream: foreign stream (%zu bytes, magic 0x%08x) — empty mesh #%u", d.streamSize, hdr[0], m->serial);
            d.outCollisionMesh = m;
            return;
        }
        case PhysicsCommands::Shape::CreateHeightField:
        {
            auto& d = D<ShapeData::CreateHeightField>(c);
            HeightField* h = Make<HeightField>();
            h->resolution = d.descriptor.resolution;
            d.heightField = h;
            Logf("CreateHeightField #%u resolution=%d (bounds only)", h->serial, h->resolution);
            return;
        }
        case PhysicsCommands::Shape::UpdateHeightFieldRegion: return;
        case PhysicsCommands::Shape::DestroyHeightField:
            if (HeightField* h = As<HeightField>(D<ShapeData::DestroyHeightField>(c).heightField, Kind::HeightField)) h->alive = false;
            return;
        case PhysicsCommands::Shape::DestroyCollisionMesh:
            if (Mesh* m = As<Mesh>(D<DestroyCollisionMesh>(c).collisionMesh, Kind::Mesh)) m->alive = false;
            return;
        case PhysicsCommands::Shape::ShouldIgnoreCollision:
        {
            auto& d = D<ShouldIgnoreCollision>(c);
            d.value = g_ignoredPairs.count(IgnoreKey(d.ignoreId0, d.ignoreId1)) != 0;
            return;
        }
        case PhysicsCommands::Shape::IgnoreCollision:
        {
            auto& d = D<ShapeData::IgnoreCollision>(c);
            if (d.value) g_ignoredPairs.insert(IgnoreKey(d.ignoreId0, d.ignoreId1));
            else g_ignoredPairs.erase(IgnoreKey(d.ignoreId0, d.ignoreId1));
            return;
        }
        case PhysicsCommands::Shape::CleanupIgnoredColliders:
        {
            const uint16_t id = D<CleanupIgnoredColliders>(c).ignoreID;
            for (auto it = g_ignoredPairs.begin(); it != g_ignoredPairs.end();)
                it = (it->first == id || it->second == id) ? g_ignoredPairs.erase(it) : std::next(it);
            return;
        }
        default: break;
    }

    if (!s)
    {
        Logf("Shape command %s on unknown handle %p — ignored", CommandName(kShapeFunctionMask | index).c_str(), sdkObject);
        return;
    }
    switch ((PhysicsCommands::Shape)index)
    {
        case PhysicsCommands::Shape::DestroyShape:
            s->alive = false;
            if (s->body) s->body->shapes.erase(std::remove(s->body->shapes.begin(), s->body->shapes.end(), s), s->body->shapes.end());
            s->body = nullptr;
            break;
        case PhysicsCommands::Shape::GetActorDescriptor: D<GetActorDescriptor>(c).value = DescribeBody(s->body); break;
        case PhysicsCommands::Shape::GetUserData: D<ShapeData::GetUserData>(c).value = s->userData; break;
        case PhysicsCommands::Shape::GetFlags: D<ShapeData::GetFlags>(c).flags = s->flags; break;
        case PhysicsCommands::Shape::SetFlags: s->flags = D<ShapeData::SetFlags>(c).flags; break;
        case PhysicsCommands::Shape::GetFilterData: D<GetFilterData>(c).data = s->filter; break;
        case PhysicsCommands::Shape::SetFilterData: s->filter = D<SetFilterData>(c).data; break;
        case PhysicsCommands::Shape::SetModifiableContacts: s->modifiableContacts = D<SetModifiableContacts>(c).modifiable; break;
        case PhysicsCommands::Shape::SetSupportedMessages: s->supportedMessages = D<SetSupportedMessages>(c).flags; s->layer = D<SetSupportedMessages>(c).layer; break;
        case PhysicsCommands::Shape::SetMaterial: s->material = As<Material>(D<ShapeData::SetMaterial>(c).value, Kind::Material); break;
        case PhysicsCommands::Shape::GetLocalPose: D<GetLocalPose>(c).value = ToPose(s->local); break;
        case PhysicsCommands::Shape::GetPose: D<ShapeData::GetPose>(c).value = ToPose(ShapeWorldPose(s)); break;
        case PhysicsCommands::Shape::SetPose:
        {
            // Same frame rule as AttachShape: body-relative for a Rigidbody, world for a static body
            // (Gate D: moving a static floor's collider center sent body position + scaled center).
            const Physics::Pose& p = D<ShapeData::SetPose>(c).value;
            s->local = (s->body && s->body->isStatic) ? Compose(Inverse(s->body->pose), FromPose(p)) : FromPose(p);
            Logf("Shape.SetPose shape#%u body#%u%s pose=(%.9g,%.9g,%.9g) rot=(%.9g,%.9g,%.9g,%.9g)", s->serial, s->body ? s->body->serial : 0,
                s->body && s->body->isStatic ? " static" : "", p.pos.x, p.pos.y, p.pos.z, p.quat.x, p.quat.y, p.quat.z, p.quat.w);
            break;
        }
        case PhysicsCommands::Shape::SetContactOffset: s->contactOffset = D<SetContactOffset>(c).value; break;
        case PhysicsCommands::Shape::GetWorldBounds:
        {
            auto& d = D<GetWorldBounds>(c);
            V3 ctr, h;
            WorldBounds(s->geom, ShapeWorldPose(s), ctr, h);
            Store3(h, d.extents); Store3(ctr, d.center);
            break;
        }
        case PhysicsCommands::Shape::GetBoundsAtPose:
        {
            auto& d = D<GetBoundsAtPose>(c);
            V3 ctr, h;
            WorldBounds(s->geom, FromPose(d.pose), ctr, h);
            Store3(h, d.extents); Store3(ctr, d.center);
            break;
        }
        case PhysicsCommands::Shape::ExtractCollisionMeshData:
        {
            auto& d = D<ExtractCollisionMeshData>(c);
            Mesh* m = (s->geom.type == Physics::GeometryType::ConvexMesh || s->geom.type == Physics::GeometryType::TriangleMesh)
                ? As<Mesh>(s->geom.meshGeometry.mesh, Kind::Mesh) : nullptr;
            Physics::SDKCollisionMeshData md{};
            if (m)
            {
                md.vertices = m->vertices.data(); md.vertexCount = m->vertices.size() / 3; md.vertexStride = 3 * sizeof(float);
                md.indices = m->indices.data(); md.indexCount = m->indices.size(); md.indexStride = sizeof(uint32_t);
            }
            if (d.callback) d.callback(d.ctx, md);
            break;
        }
        case PhysicsCommands::Shape::GetGeometryType: D<GetGeometryType>(c).value = s->geom.type; break;
        case PhysicsCommands::Shape::SetGeometry: s->geom = D<SetGeometry>(c).value; break;
        case PhysicsCommands::Shape::GetGeometry: D<GetGeometry>(c).value = s->geom; break;
        case PhysicsCommands::Shape::RemapTriangleIndexToSourceIndex:
            D<RemapTriangleIndexToSourceIndex>(c).outSourceMeshTriangleIndex = D<RemapTriangleIndexToSourceIndex>(c).physicsMeshTriangleIndex;
            break;
        default: Logf("UNHANDLED %s", CommandName(kShapeFunctionMask | index).c_str()); break;
    }
}

// Queries: the gate build has no narrow phase, so every query reports "no hit". Outputs are
// still fully written so Unity never reads uninitialised memory.
static void HandleQuery(void*, const Context&, uint32_t index, Command& c)
{
    using namespace QueryData;
    switch ((PhysicsCommands::Query)index)
    {
        case PhysicsCommands::Query::ComputeShapePenetration:
        {
            auto& d = D<ComputeShapePenetration>(c);
            d.outDirection = Physics::Vec3(0.0f); d.outDistance = 0; d.areOverlapping = false;
            break;
        }
        case PhysicsCommands::Query::ClosestPointOnShape:
        {
            auto& d = D<ClosestPointOnShape>(c);
            d.outPoint = d.point; d.outDistance = 0;
            break;
        }
        case PhysicsCommands::Query::RayCast: { auto& d = D<RayCast>(c); d.hit = Physics::QueryHit::Invalid(); d.hitCount = 0; break; }
        case PhysicsCommands::Query::RayCastAgainstShape: { auto& d = D<RayCastAgainstShape>(c); d.hit = Physics::QueryHit::Invalid(); d.hitCount = 0; break; }
        case PhysicsCommands::Query::RayCastWithCollector: break;
        case PhysicsCommands::Query::Overlap: D<Overlap>(c).shapeUserData = nullptr; break;
        case PhysicsCommands::Query::OverlapWithCollector: break;
        case PhysicsCommands::Query::OverlapWithBroadphaseCollector: break;
        case PhysicsCommands::Query::ShapeCast: { auto& d = D<ShapeCast>(c); d.hit = Physics::QueryHit::Invalid(); d.hitCount = 0; break; }
        case PhysicsCommands::Query::ShapeCastAgainstShape: { auto& d = D<ShapeCastAgainstShape>(c); d.hit = Physics::QueryHit::Invalid(); d.hitCount = 0; break; }
        case PhysicsCommands::Query::ShapeCastWithCollector: break;
        case PhysicsCommands::Query::BodyCast: { auto& d = D<BodyCast>(c); d.hit = Physics::QueryHit::Invalid(); d.hitCount = 0; break; }
        case PhysicsCommands::Query::BodyCastWithCollector: break;
        default: Logf("UNHANDLED %s", CommandName(kQueryFunctionMask | index).c_str()); break;
    }
}

static const char* AxisName(Physics::ConstraintAxis a)
{
    static const char* n[] = { "AngularX", "AngularY", "AngularZ", "AngularYZ", "LinearX", "LinearY", "LinearZ", "Distance", "Slerp" };
    int i = (int)a;
    return i >= 0 && i < 9 ? n[i] : "?";
}

static void HandleJoint(void* sdkObject, const Context&, uint32_t index, Command& c)
{
    using namespace JointData;
    Joint* j = As<Joint>(sdkObject, Kind::Joint);
    if (!j)
    {
        Logf("Joint command %s on unknown handle %p — ignored", CommandName(kJointFunctionMask | index).c_str(), sdkObject);
        return;
    }
    auto axis = [&](Physics::ConstraintAxis a) -> Joint::Axis& { int i = (int)a; return j->axes[(i >= 0 && i < 9) ? i : 0]; };
    switch ((PhysicsCommands::Joint)index)
    {
        case PhysicsCommands::Joint::SetFlags:
        {
            auto& d = D<JointData::SetFlags>(c);
            j->flags = d.enabled ? (j->flags | d.value) : (Physics::JointFlags)((int)j->flags & ~(int)d.value);
            break;
        }
        case PhysicsCommands::Joint::GetFlags: D<JointData::GetFlags>(c).value = j->flags; break;
        case PhysicsCommands::Joint::GetUserData: D<JointData::GetUserData>(c).value = j->userData; break;
        case PhysicsCommands::Joint::SetActorLocalPose:
        {
            auto& d = D<SetActorLocalPose>(c);
            j->localPose[d.target == Physics::PoseTarget::Child ? 1 : 0] = FromPose(d.value);
            break;
        }
        case PhysicsCommands::Joint::GetActorLocalPose:
        {
            auto& d = D<GetActorLocalPose>(c);
            d.value = ToPose(j->localPose[d.target == Physics::PoseTarget::Child ? 1 : 0]);
            break;
        }
        case PhysicsCommands::Joint::SetBreakForce: j->breakForce = D<SetBreakForce>(c).force; j->breakTorque = D<SetBreakForce>(c).torque; break;
        case PhysicsCommands::Joint::GetIsConstraintValid: D<GetIsConstraintValid>(c).value = true; break;
        case PhysicsCommands::Joint::GetForces:
        {
            auto& d = D<GetForces>(c);
            d.linear = Physics::Vec3(0.0f); d.angular = Physics::Vec3(0.0f);
            break;
        }
        case PhysicsCommands::Joint::SetInvMassAndInertiaScale:
        {
            auto& d = D<SetInvMassAndInertiaScale>(c);
            j->invMassScale[d.target == Physics::PoseTarget::Child ? 1 : 0] = d.value;
            break;
        }
        case PhysicsCommands::Joint::SetActors:
        {
            auto& d = D<SetActors>(c);
            j->actors[0] = d.actors[0]; j->actors[1] = d.actors[1];
            break;
        }
        case PhysicsCommands::Joint::GetActors:
        {
            auto& d = D<GetActors>(c);
            d.actors[0] = j->actors[0]; d.actors[1] = j->actors[1];
            break;
        }
        case PhysicsCommands::Joint::WakeUpActors: break;
        case PhysicsCommands::Joint::Set6DofLimit:
        {
            auto& d = D<Set6DofLimit>(c);
            auto& a = axis(d.axis); a.min = d.min; a.max = d.max;
            break;
        }
        case PhysicsCommands::Joint::Get6DofLimit:
        {
            auto& d = D<Get6DofLimit>(c);
            auto& a = axis(d.axis); d.min = a.min; d.max = a.max; d.bounciness = 0; d.bounceMinVelocity = 0; d.contactDistance = 0;
            break;
        }
        case PhysicsCommands::Joint::Set6DofSpringLimit: { auto& d = D<Set6DofSpringLimit>(c); auto& a = axis(d.axis); a.spring = d.spring; a.damping = d.damping; break; }
        case PhysicsCommands::Joint::Set6DofAxisLock: { auto& d = D<Set6DofAxisLock>(c); axis(d.axis).lock = d.value; break; }
        case PhysicsCommands::Joint::Set6DofAxisMotorConfiguration:
        {
            auto& d = D<Set6DofAxisMotorConfiguration>(c);
            auto& a = axis(d.axis);
            a.driveSpring = d.positionSpring; a.driveDamper = d.positionDamper; a.driveMaxForce = d.maximumForce; a.driveAccel = d.usesAcceleration;
            Logf("Joint#%u motor %s spring=%g damper=%g maxForce=%g accel=%d", j->serial, AxisName(d.axis), d.positionSpring, d.positionDamper, d.maximumForce, d.usesAcceleration);
            break;
        }
        case PhysicsCommands::Joint::Set6DofMotorTargetPosition: j->targetPosition = Load3(D<Set6DofMotorTargetPosition>(c).value); break;
        case PhysicsCommands::Joint::Set6DofMotorTargetRotation:
        {
            auto& v = D<Set6DofMotorTargetRotation>(c).value;
            j->targetRotation = { v.x, v.y, v.z, v.w };
            j->targetRotationSets++;
            break;
        }
        case PhysicsCommands::Joint::Set6DofMotorTargetLinearVelocity: j->targetLinVel = Load3(D<Set6DofMotorTargetLinearVelocity>(c).value); break;
        case PhysicsCommands::Joint::Set6DofMotorTargetAngularVelocity: j->targetAngVel = Load3(D<Set6DofMotorTargetAngularVelocity>(c).value); break;
        case PhysicsCommands::Joint::Set6DofProjectionTolerance: break;
        case PhysicsCommands::Joint::SetDistanceLimit: { auto& d = D<SetDistanceLimit>(c); auto& a = axis(Physics::ConstraintAxis::Distance); a.min = d.min; a.max = d.max; break; }
        case PhysicsCommands::Joint::SetDistanceSpringLimit: { auto& d = D<SetDistanceSpringLimit>(c); auto& a = axis(Physics::ConstraintAxis::Distance); a.spring = d.spring; a.damping = d.damping; break; }
        case PhysicsCommands::Joint::SetDistanceErrorTolerance: break;
        case PhysicsCommands::Joint::SetHingeMotorConfiguration: break;
        case PhysicsCommands::Joint::SetHingeMotorTargetVelocity: break;
        case PhysicsCommands::Joint::SetHingeLimit: { auto& d = D<SetHingeLimit>(c); auto& a = axis(Physics::ConstraintAxis::AngularX); a.min = d.min; a.max = d.max; break; }
        case PhysicsCommands::Joint::SetHingeLimitEnabled: break;
        default: Logf("UNHANDLED %s", CommandName(kJointFunctionMask | index).c_str()); break;
    }
}

// ---------------------------------------------------------------------------------------------
// Entry point + per-command logging policy
// ---------------------------------------------------------------------------------------------

// Commands whose every occurrence (up to a cap) is logged with its handle; everything else only
// logs its first few occurrences and then appears in the periodic histogram.
static bool IsStructuralCommand(uint32_t cmdFunc)
{
    const uint32_t group = cmdFunc & kFullMask;
    const uint32_t index = cmdFunc & kReverseFullMask;
    if (group == kJointFunctionMask)
        return index != (uint32_t)PhysicsCommands::Joint::Set6DofMotorTargetRotation
            && index != (uint32_t)PhysicsCommands::Joint::Set6DofMotorTargetAngularVelocity
            && index != (uint32_t)PhysicsCommands::Joint::GetForces;
    if (group == kBodyFunctionMask)
        return index == (uint32_t)PhysicsCommands::Body::SetFlags || index == (uint32_t)PhysicsCommands::Body::RecomputeMassProperties
            || index == (uint32_t)PhysicsCommands::Body::SetMass;
    return false;
}

static void BackendEntry(void* sdkObject, const Context& ctx, const uint32_t cmdFunc, Command& c)
{
    std::lock_guard<std::recursive_mutex> guard(g_lock);
    g_totalCommands++;
    const uint64_t n = ++g_cmdCounts[cmdFunc];
    if (n <= 4 || (IsStructuralCommand(cmdFunc) && n <= 4000))
    {
        const Obj* o = (sdkObject && g_objects.count(static_cast<Obj*>(sdkObject))) ? static_cast<Obj*>(sdkObject) : nullptr;
        Logf("cmd %-46s n=%-6llu obj=%s#%u", CommandName(cmdFunc).c_str(), (unsigned long long)n, o ? KindName(o->kind) : "-", o ? o->serial : 0);
    }

    const uint32_t index = cmdFunc & kReverseFullMask;
    switch (cmdFunc & kFullMask)
    {
        case kSDKFunctionMask: HandleSdk(sdkObject, ctx, index, c); break;
        case kWorldFunctionMask: HandleWorld(sdkObject, ctx, index, c); break;
        case kBodyFunctionMask: HandleBody(sdkObject, ctx, index, c); break;
        case kShapeFunctionMask: HandleShape(sdkObject, ctx, index, c); break;
        case kQueryFunctionMask: HandleQuery(sdkObject, ctx, index, c); break;
        case kJointFunctionMask: HandleJoint(sdkObject, ctx, index, c); break;
        default:
            if (n <= 4) Logf("UNHANDLED %s (articulation/extension command; outputs left untouched)", CommandName(cmdFunc).c_str());
            break;
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    std::lock_guard<std::recursive_mutex> guard(g_lock);
    Logf("==== UnityPluginLoad (RigidIPC gate build, id=%u) ====", kIntegrationId);
    IUnityPhysicsBackendSystem* backendSystem = unityInterfaces ? unityInterfaces->Get<IUnityPhysicsBackendSystem>() : nullptr;
    if (!backendSystem)
    {
        Logf("IUnityPhysicsBackendSystem not available — not registering");
        return;
    }
    g_registered = backendSystem->Register(&BackendEntry);
    Logf("IUnityPhysicsBackendSystem::Register(...) == %s", g_registered ? "true" : "false");
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    std::lock_guard<std::recursive_mutex> guard(g_lock);
    LogHistogram("UnityPluginUnload");
    Logf("==== UnityPluginUnload ====");
    if (g_log) { std::fclose(g_log); g_log = nullptr; }
}

// Diagnostics for the C# gate probe (DllImport). Returns a one-line status string.
extern "C" UNITY_INTERFACE_EXPORT int RigidIPC_GetDiagnostics(char* buffer, int bufferSize)
{
    std::lock_guard<std::recursive_mutex> guard(g_lock);
    if (!buffer || bufferSize <= 0) return 0;
    size_t alive[8] = {};
    for (Obj* o : g_objects)
        if (o->alive) alive[(int)o->kind]++;
    int n = std::snprintf(buffer, (size_t)bufferSize,
        "registered=%d initialized=%d id=%u commands=%llu simulate=%llu objects=%zu engineObjectIdOffset=%u "
        "alive: worlds=%zu bodies=%zu shapes=%zu joints=%zu materials=%zu meshes=%zu",
        g_registered, g_initialized, kIntegrationId, (unsigned long long)g_totalCommands, (unsigned long long)g_simulateCalls,
        g_objects.size(), g_engineObjectIdOffset, alive[(int)Kind::World], alive[(int)Kind::Body], alive[(int)Kind::Shape],
        alive[(int)Kind::Joint], alive[(int)Kind::Material], alive[(int)Kind::Mesh]);
    return n;
}

// ---------------------------------------------------------------------------------------------
// Gate diagnostics (DllImport from Assets/IPC/Runtime/RigidIPCGateDiagnostics.cs). Nothing here runs
// unless the C# side calls it, so the backend's normal path is unchanged.
// ---------------------------------------------------------------------------------------------

// One attached-or-static shape as the backend currently holds it. Layout mirrors ShapeSnapshot in C#.
struct RigidIPC_ShapeSnapshot
{
    void* shapeUserData;    // what CreateShape passed as userData
    void* bodyUserData;     // what CreateBody passed as userData (null when the shape is unattached)
    uint32_t shapeSerial, bodySerial;
    int32_t geometryType;   // Physics::GeometryType
    int32_t bodyIsStatic;
    float geom[4];          // box: half extents | sphere: radius | capsule: radius, half-height | mesh: scale xyz
    float bodyPos[3], bodyRot[4];
    float localPos[3], localRot[4];
    float worldPos[3], worldRot[4];
    int32_t vertexCount;    // what RigidIPC_GetShapeWorldVertices returns for this shape
};

// Characteristic points of a shape in the shape's local frame, in a fixed order the C# side mirrors.
//   Box:     8 corners, corner i has sign (+ if bit set) x: bit 0, y: bit 1, z: bit 2.
//   Sphere:  centre, then +x, -x, +y, -y, +z, -z at the radius.
//   Capsule: tips at +/- (height + radius) on local x (the PhysX capsule axis; height is the half-height), then +y, -y, +z, -z at the radius.
//   Mesh:    every cooked vertex times the geometry scale.
static std::vector<V3> LocalShapePoints(const Physics::ShapeGeometry& g)
{
    std::vector<V3> p;
    switch (g.type)
    {
        case Physics::GeometryType::Box:
        {
            const auto& h = g.boxGeometry.halfExtents;
            for (int i = 0; i < 8; ++i)
                p.push_back({ (i & 1) ? h.x : -h.x, (i & 2) ? h.y : -h.y, (i & 4) ? h.z : -h.z });
            break;
        }
        case Physics::GeometryType::Sphere:
        {
            const float r = g.sphereGeometry.radius;
            p = { {}, { r, 0, 0 }, { -r, 0, 0 }, { 0, r, 0 }, { 0, -r, 0 }, { 0, 0, r }, { 0, 0, -r } };
            break;
        }
        case Physics::GeometryType::Capsule:
        {
            const float r = g.capsuleGeometry.radius, t = g.capsuleGeometry.height + r;   // height = PhysX half-height (see LocalExtents)
            p = { { t, 0, 0 }, { -t, 0, 0 }, { 0, r, 0 }, { 0, -r, 0 }, { 0, 0, r }, { 0, 0, -r } };
            break;
        }
        case Physics::GeometryType::ConvexMesh:
        case Physics::GeometryType::TriangleMesh:
        {
            Mesh* m = As<Mesh>(g.meshGeometry.mesh, Kind::Mesh);
            if (!m) break;
            const auto& s = g.meshGeometry.scale;
            for (size_t i = 0; i + 2 < m->vertices.size(); i += 3)
                p.push_back({ m->vertices[i] * s.x, m->vertices[i + 1] * s.y, m->vertices[i + 2] * s.z });
            break;
        }
        default: break;
    }
    return p;
}

static Shape* FindShapeBySerial(uint32_t serial)
{
    for (Obj* o : g_objects)
        if (o->alive && o->kind == Kind::Shape && o->serial == serial) return static_cast<Shape*>(o);
    return nullptr;
}

static void Put3(float* d, V3 v) { d[0] = v.x; d[1] = v.y; d[2] = v.z; }
static void Put4(float* d, Q4 q) { d[0] = q.x; d[1] = q.y; d[2] = q.z; d[3] = q.w; }

// Fills up to maxCount snapshots of alive shapes (ordered by serial) and returns how many alive shapes exist.
extern "C" UNITY_INTERFACE_EXPORT int RigidIPC_GetShapeSnapshots(RigidIPC_ShapeSnapshot* out, int maxCount)
{
    std::lock_guard<std::recursive_mutex> guard(g_lock);
    std::vector<Shape*> shapes;
    for (Obj* o : g_objects)
        if (o->alive && o->kind == Kind::Shape) shapes.push_back(static_cast<Shape*>(o));
    std::sort(shapes.begin(), shapes.end(), [](const Shape* a, const Shape* b) { return a->serial < b->serial; });
    for (int i = 0; out && i < maxCount && i < (int)shapes.size(); ++i)
    {
        const Shape* s = shapes[i];
        RigidIPC_ShapeSnapshot& d = out[i];
        d = RigidIPC_ShapeSnapshot{};
        d.shapeUserData = s->userData;
        d.bodyUserData = s->body ? s->body->userData : nullptr;
        d.shapeSerial = s->serial;
        d.bodySerial = s->body ? s->body->serial : 0;
        d.geometryType = (int32_t)s->geom.type;
        d.bodyIsStatic = s->body && s->body->isStatic;
        switch (s->geom.type)
        {
            case Physics::GeometryType::Box: Put3(d.geom, Load3(s->geom.boxGeometry.halfExtents)); break;
            case Physics::GeometryType::Sphere: d.geom[0] = s->geom.sphereGeometry.radius; break;
            case Physics::GeometryType::Capsule: d.geom[0] = s->geom.capsuleGeometry.radius; d.geom[1] = s->geom.capsuleGeometry.height; break;
            case Physics::GeometryType::ConvexMesh:
            case Physics::GeometryType::TriangleMesh: Put3(d.geom, Load3(s->geom.meshGeometry.scale)); break;
            default: break;
        }
        const Xf bodyPose = s->body ? s->body->pose : Xf{};
        const Xf world = ShapeWorldPose(s);
        Put3(d.bodyPos, bodyPose.p); Put4(d.bodyRot, bodyPose.q);
        Put3(d.localPos, s->local.p); Put4(d.localRot, s->local.q);
        Put3(d.worldPos, world.p); Put4(d.worldRot, world.q);
        d.vertexCount = (int32_t)LocalShapePoints(s->geom).size();
    }
    return (int)shapes.size();
}

// Writes the shape's characteristic points in world space (backend math: body pose * local pose)
// as xyz triplets and returns how many points the shape has; -1 if the serial is not an alive shape.
extern "C" UNITY_INTERFACE_EXPORT int RigidIPC_GetShapeWorldVertices(uint32_t shapeSerial, float* out, int maxVertices)
{
    std::lock_guard<std::recursive_mutex> guard(g_lock);
    const Shape* s = FindShapeBySerial(shapeSerial);
    if (!s) return -1;
    const Xf world = ShapeWorldPose(s);
    const std::vector<V3> local = LocalShapePoints(s->geom);
    for (int i = 0; out && i < maxVertices && i < (int)local.size(); ++i)
        Put3(out + 3 * i, Add(world.p, QRot(world.q, local[i])));
    return (int)local.size();
}

// Logs every dynamic body's pose and velocities after each of the next 'steps' Simulate calls.
extern "C" UNITY_INTERFACE_EXPORT void RigidIPC_TraceNextSteps(int steps)
{
    std::lock_guard<std::recursive_mutex> guard(g_lock);
    g_traceSteps = std::max(0, steps);
    Logf("trace armed for %d Simulate call(s)", g_traceSteps);
}

// One body as the backend currently holds it. Layout mirrors BodySnapshot in C#.
struct RigidIPC_BodySnapshot
{
    void* userData;                 // what CreateBody passed as userData
    uint32_t serial;
    int32_t isStatic, flags, ccd, sleeping, hasKinematicTarget, shapeCount;
    float mass, linearDamping, angularDamping, maxLinVel, maxAngVel, maxDepenetration;
    float pos[3], rot[4];
    float linVel[3], angVel[3];
    float com[3], inertia[3], inertiaRot[4];
    float kinematicTargetPos[3], kinematicTargetRot[4];
    float lastForce[3], lastTorque[3], lastImpulse[3], lastAngImpulse[3];   // what the last Simulate consumed
};

// Fills up to maxCount snapshots of alive bodies (ordered by serial) and returns how many alive bodies exist.
extern "C" UNITY_INTERFACE_EXPORT int RigidIPC_GetBodySnapshots(RigidIPC_BodySnapshot* out, int maxCount)
{
    std::lock_guard<std::recursive_mutex> guard(g_lock);
    std::vector<Body*> bodies;
    for (Obj* o : g_objects)
        if (o->alive && o->kind == Kind::Body) bodies.push_back(static_cast<Body*>(o));
    std::sort(bodies.begin(), bodies.end(), [](const Body* a, const Body* b) { return a->serial < b->serial; });
    for (int i = 0; out && i < maxCount && i < (int)bodies.size(); ++i)
    {
        const Body* b = bodies[i];
        RigidIPC_BodySnapshot& d = out[i];
        d = RigidIPC_BodySnapshot{};
        d.userData = b->userData;
        d.serial = b->serial;
        d.isStatic = b->isStatic;
        d.flags = (int32_t)b->flags;
        d.ccd = (int32_t)b->ccd;
        d.sleeping = b->sleeping;
        d.hasKinematicTarget = b->hasKinematicTarget;
        d.shapeCount = (int32_t)b->shapes.size();
        d.mass = b->mass; d.linearDamping = b->linearDamping; d.angularDamping = b->angularDamping;
        d.maxLinVel = b->maxLinVel; d.maxAngVel = b->maxAngVel; d.maxDepenetration = b->maxDepenetration;
        Put3(d.pos, b->pose.p); Put4(d.rot, b->pose.q);
        Put3(d.linVel, b->linVel); Put3(d.angVel, b->angVel);
        Put3(d.com, b->com); Put3(d.inertia, b->inertia); Put4(d.inertiaRot, b->inertiaRot);
        Put3(d.kinematicTargetPos, b->kinematicTarget.p); Put4(d.kinematicTargetRot, b->kinematicTarget.q);
        Put3(d.lastForce, b->lastForce); Put3(d.lastTorque, b->lastTorque);
        Put3(d.lastImpulse, b->lastImpulse); Put3(d.lastAngImpulse, b->lastAngImpulse);
    }
    return (int)bodies.size();
}

// One joint as the backend currently holds it. Layout mirrors JointSnapshot in C#.
// axes: 9 rows indexed by Physics::ConstraintAxis, each
//   lock, min, max, limitSpring, limitDamping, driveSpring, driveDamper, driveMaxForce, driveAccel (all as float).
struct RigidIPC_JointSnapshot
{
    void* userData;                 // what CreateJoint passed as userData
    void* actorUserData[2];         // ActorDescriptor.userData for Parent (0) and Child (1)
    uint32_t serial, actorSerial[2];
    int32_t type, flags;
    uint32_t targetRotationSets;
    float breakForce, breakTorque, invMassScale[2];
    float localPos0[3], localRot0[4], localPos1[3], localRot1[4];
    float targetRotation[4], targetPosition[3], targetLinVel[3], targetAngVel[3];
    float axes[9 * 9];
};

extern "C" UNITY_INTERFACE_EXPORT int RigidIPC_GetJointSnapshots(RigidIPC_JointSnapshot* out, int maxCount)
{
    std::lock_guard<std::recursive_mutex> guard(g_lock);
    std::vector<Joint*> joints;
    for (Obj* o : g_objects)
        if (o->alive && o->kind == Kind::Joint) joints.push_back(static_cast<Joint*>(o));
    std::sort(joints.begin(), joints.end(), [](const Joint* a, const Joint* b) { return a->serial < b->serial; });
    for (int i = 0; out && i < maxCount && i < (int)joints.size(); ++i)
    {
        const Joint* j = joints[i];
        RigidIPC_JointSnapshot& d = out[i];
        d = RigidIPC_JointSnapshot{};
        d.userData = j->userData;
        d.serial = j->serial;
        for (int k = 0; k < 2; ++k)
        {
            d.actorUserData[k] = j->actors[k].userData;
            const Obj* a = (j->actors[k].actor && g_objects.count(static_cast<Obj*>(j->actors[k].actor))) ? static_cast<Obj*>(j->actors[k].actor) : nullptr;
            d.actorSerial[k] = a ? a->serial : 0;
            d.invMassScale[k] = j->invMassScale[k];
        }
        d.type = (int32_t)j->type;
        d.flags = (int32_t)j->flags;
        d.targetRotationSets = j->targetRotationSets;
        d.breakForce = j->breakForce; d.breakTorque = j->breakTorque;
        Put3(d.localPos0, j->localPose[0].p); Put4(d.localRot0, j->localPose[0].q);
        Put3(d.localPos1, j->localPose[1].p); Put4(d.localRot1, j->localPose[1].q);
        Put4(d.targetRotation, j->targetRotation);
        Put3(d.targetPosition, j->targetPosition); Put3(d.targetLinVel, j->targetLinVel); Put3(d.targetAngVel, j->targetAngVel);
        for (int a = 0; a < 9; ++a)
        {
            const Joint::Axis& x = j->axes[a];
            float* r = d.axes + 9 * a;
            r[0] = (float)(int)x.lock; r[1] = x.min; r[2] = x.max; r[3] = x.spring; r[4] = x.damping;
            r[5] = x.driveSpring; r[6] = x.driveDamper; r[7] = x.driveMaxForce; r[8] = (float)x.driveAccel;
        }
    }
    return (int)joints.size();
}

// The running per-command histogram: fills up to maxCount (code, count) pairs and returns how many codes exist.
extern "C" UNITY_INTERFACE_EXPORT int RigidIPC_GetCommandCounts(uint32_t* codes, uint64_t* counts, int maxCount)
{
    std::lock_guard<std::recursive_mutex> guard(g_lock);
    int i = 0;
    for (auto& kv : g_cmdCounts)
    {
        if (codes && counts && i < maxCount) { codes[i] = kv.first; counts[i] = kv.second; }
        ++i;
    }
    return i;
}

// "Group.Name(index)" for a command code, as the command log prints it.
extern "C" UNITY_INTERFACE_EXPORT int RigidIPC_GetCommandName(uint32_t code, char* buffer, int bufferSize)
{
    if (!buffer || bufferSize <= 0) return 0;
    return std::snprintf(buffer, (size_t)bufferSize, "%s", CommandName(code).c_str());
}
