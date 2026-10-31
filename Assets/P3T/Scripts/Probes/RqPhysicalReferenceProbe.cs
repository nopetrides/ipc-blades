using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     Records the six blade markers per fixed step, in Unity world space AND projected through the
///     station camera onto the registered scale plane, so a Unity run can be looked at through the same
///     lens as the Chapter 3 Tracker exports.
/// </summary>
/// <remarks>
///     <para>
///         <b>THIS IS NOT AN EQUIVALENCE INSTRUMENT.</b> Thesis §4.1 governs: the virtual comparison tests
///         whether selected observable RELATIONSHIPS are reproduced, not whether virtual numbers match
///         physical ones. Nothing this probe writes is comparable to a Tracker value by magnitude. The two
///         coordinate systems have different origins, different scale derivations, and different cameras.
///         What is comparable is the SHAPE of a trace, the correspondence between two traces, and the
///         sequence of states — the same things Chapter 3 was limited to.
///     </para>
///     <para>
///         <b>Why the projected twin exists.</b> Chapter 3 §3.9 records that the Tracker marker positions
///         "remain perspective-projected because the markers lie closer to the camera than the scale
///         plane". A raw Unity 3D position is therefore not the same kind of quantity as a tracked marker
///         coordinate, and comparing them would compare a projected quantity with an unprojected one. This
///         probe emits both: <c>world_*</c> as the physics truth, and <c>plane_*</c> as the same point
///         carried through the station camera onto the registered plane, which is the same operation the
///         physical camera performed.
///     </para>
///     <para>
///         <b>The six markers are the Tracker set, one for one.</b> The Chapter 3 exports carry
///         <c>SW-A1_tip_pink</c>, <c>SW-B1_tip_blue</c>, <c>SW-A1_quillon_orange</c>,
///         <c>SW-A1_quillon_purple</c>, <c>SW-B1_quillon_yellow</c> and <c>SW-B1_quillon_beige</c> as
///         <c>t,x,y</c> triplets. The CSV written here uses those exact series names so a comparison script
///         can join on them without a translation table.
///     </para>
///     <para>
///         <b>Positions come from the PROXY, not the visual sword.</b> Each marker is calibrated once as a
///         fixed offset in the authored sword's local frame, then re-posed every step by the generated
///         physics proxy — the same route <see cref="DriveTrailRecorder" /> uses. Reading the authored
///         sword's transforms inside <c>FixedUpdate</c> would read a pose written by a visual sync in
///         <c>LateUpdate</c>, which is a step stale.
///     </para>
///     <para>
///         <b>Every step, no decimation, with time.</b> <see cref="DriveTrailRecorder" /> exists to draw a
///         trail and decimates by <c>minVertexSpacing</c>, so its row index does not map to time. §3.13.4
///         aligns traces at the onset of Sword A's forward movement and truncates to one second, which
///         needs a time column and an undecimated series. That is the whole reason this probe is separate
///         rather than a change to that recorder.
///     </para>
///     <para>
///         <b>One authored plane, not a second declaration.</b> The projection plane and camera are read
///         from the sibling <see cref="DriveTrailRecorder" /> so there is exactly one authored
///         <c>planeZ</c> in the station. A second copy could drift and produce two projected spaces.
///     </para>
///     <para>
///         This component writes nothing to the simulation.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
[DefaultExecutionOrder(160)]
public sealed class RqPhysicalReferenceProbe : MonoBehaviour
{
    /// <summary>Tracker series names, in the order the Chapter 3 exports declare them.</summary>
    private static readonly string[] SeriesNames =
    {
        "SW-A1_tip_pink", "SW-B1_tip_blue",
        "SW-A1_quillon_orange", "SW-A1_quillon_purple",
        "SW-B1_quillon_yellow", "SW-B1_quillon_beige"
    };

    /// <summary>Which sword each series rides: true = A.</summary>
    private static readonly bool[] SeriesIsA = { true, false, true, true, false, false };

    [Header("Marker child names on each sword (must match the Tracker marker set)")]
    [Tooltip("Child of Sword A's Markers group carrying the pink tip marker.")]
    [SerializeField] private string aTip = "Marker_Tip_Pink";

    [Tooltip("Child of Sword A's Markers group carrying the orange quillon marker.")]
    [SerializeField] private string aQuillonOrange = "Marker_Quillon_Orange";

    [Tooltip("Child of Sword A's Markers group carrying the purple quillon marker.")]
    [SerializeField] private string aQuillonPurple = "Marker_Quillon_Purple";

    [Tooltip("Child of Sword B's Markers group carrying the blue tip marker.")]
    [SerializeField] private string bTip = "Marker_Tip_Blue";

    [Tooltip("Child of Sword B's Markers group carrying the yellow quillon marker.")]
    [SerializeField] private string bQuillonYellow = "Marker_Quillon_Yellow";

    [Tooltip("Child of Sword B's Markers group carrying the beige quillon marker.")]
    [SerializeField] private string bQuillonBeige = "Marker_Quillon_Beige";

    [Header("Window")]
    [Tooltip("Animator State value that begins CONTACT. Recording starts here so the DRIVE onset has a " +
             "pre-roll and a trace can be aligned at t0 rather than starting at it.")]
    [SerializeField] private int contactState = 3;

    [Tooltip("Animator State value that is DRIVE. t_since_drive_s is zeroed on the first step at this " +
             "state, which is the virtual analogue of Chapter 3's 'onset of Sword A's forward movement'.")]
    [SerializeField] private int driveState = 4;

    [Tooltip("Steps to keep recording after DRIVE ends, so a release just past the window is not cut off.")]
    [SerializeField] private int endGraceSteps = 90;

    [Tooltip("Close the window when the station runner reports Finished, rather than waiting for the " +
             "animator to leave DRIVE. The runner ENDS PARKED AT STATE 4, so without this the window " +
             "never closes on a runner-driven run and the file is only flushed when Play stops. That cost " +
             "the cross-boundary file on two instrument runs before it was found.")]
    [SerializeField] private bool closeOnRunnerFinished = true;

    [Tooltip("Hard cap on rows per run, so a forgotten Play session cannot fill the disk. A 90 Hz run of " +
             "CONTACT (900 steps) plus DRIVE sits far below this.")]
    [SerializeField] private int maxRows = 20000;

    [Header("Output")]
    [Tooltip("Directory the CSV is written to. Deliberately NOT the DriveWindow folder: this is a " +
             "cross-boundary comparison aid, not a Chapter 5 sealed measurement.")]
    /// <remarks>
    ///     BLANK by default, deliberately. It used to ship an absolute path from one machine, which
    ///     both leaked a folder structure into a public repository and silently wrote another
    ///     machine's runs into a directory that did not describe them. Blank cannot do either, and
    ///     the write path below refuses with a named error rather than throwing on
    ///     CreateDirectory("").
    /// </remarks>
    [SerializeField] private string outputDirectory = "";

    [Tooltip("Set name, mirroring the sealed record's setName so a physical-reference file can be tied " +
             "to the run tranche it came from.")]
    [SerializeField] private string setName = "Instrumenting";

    [Tooltip("Write the CSV when the window closes.")]
    [SerializeField] private bool writeCsvOnWindowEnd = true;

    private readonly List<float> tAbs = new List<float>(4096);
    private readonly List<float> tRel = new List<float>(4096);
    private readonly List<int> phase = new List<int>(4096);
    private readonly List<Vector3>[] world = new List<Vector3>[6];
    private readonly List<Vector3>[] plane = new List<Vector3>[6];

    private DriveTrailRecorder trail;
    private RA2AttachableObject swordA, swordB;
    private Transform authoredA, authoredB;
    private Vector3[] localOffsets;
    private bool calibrated;

    private Animator animA, animB;
    private RqStationRunner runner;

    /// <summary>
    ///     Whether an ARMED run was in progress when this window opened, latched at <c>Begin</c>.
    ///     <para>
    ///         Capture is not recording. This probe samples whenever the animator is in the Contact or Drive
    ///         window, which happens on an Auto cycle, on a hand-driven State from the panel, and in free
    ///         play — none of which is a run. Before 2026-08-29 every one of those wrote a CSV, so pressing
    ///         Play produced files in the channel root that no set had declared and no ledger row covered.
    ///     </para>
    ///     <para>
    ///         There are exactly two arming paths, and both are <c>RqStationRunner</c>: <c>armKey</c>
    ///         (manual, mid-play) and <c>armWhenReady</c> (automatic, once initialization completes). It is
    ///         latched at <c>Begin</c> rather than read at <c>End</c> because the runner has already
    ///         reported <c>Finished</c> by the time the window closes, so reading it late would refuse every
    ///         armed run.
    ///     </para>
    /// </summary>
    private bool armedForThisWindow;
    private bool recording;
    private bool sawDrive;
    private float driveStartTime;
    private int offSteps;
    private int runIndex;

    /// <summary>True once the six markers, both proxies, the camera and the plane all resolved.</summary>
    public bool Resolved { get; private set; }

    /// <summary>Why the probe is not recording, when it is not.</summary>
    public string RefusalReason { get; private set; } = "";

    /// <summary>Rows captured in the window currently open, or the last one closed.</summary>
    public int Rows => tAbs.Count;

    /// <summary>True while a window is open and rows are accumulating.</summary>
    public bool Recording => recording;

    private void Awake()
    {
        for (var i = 0; i < 6; i++)
        {
            world[i] = new List<Vector3>(4096);
            plane[i] = new List<Vector3>(4096);
        }

        trail = GetComponent<DriveTrailRecorder>();
        runner = GetComponent<RqStationRunner>();
        ResolveStation();
    }

    /// <summary>Everything is taken from this probe's own <c>Station_</c> root, so no station name is baked in.</summary>
    private void ResolveStation()
    {
        var root = transform;
        while (root != null && !root.name.StartsWith("Station_")) root = root.parent;
        if (root == null) return;

        foreach (var att in root.GetComponentsInChildren<RA2AttachableObject>(true))
        {
            if (swordA == null && att.gameObject.name.StartsWith("Sword_A")) swordA = att;
            else if (swordB == null && att.gameObject.name.StartsWith("Sword_B")) swordB = att;
        }

        if (swordA != null) authoredA = swordA.transform;
        if (swordB != null) authoredB = swordB.transform;

        foreach (var an in root.GetComponentsInChildren<Animator>(true))
        {
            if (animA == null && an.gameObject.name.StartsWith("Dummy_A")) animA = an;
            else if (animB == null && an.gameObject.name.StartsWith("Dummy_B")) animB = an;
        }
    }

    /// <summary>
    ///     Locks each of the six markers to its sword as a fixed local offset, taken once from the authored
    ///     marker child. After this the authored transform is never read again — the offset is re-posed by
    ///     the proxy.
    /// </summary>
    private bool Calibrate()
    {
        if (calibrated) return true;
        if (authoredA == null || authoredB == null) return false;

        string[] names = { aTip, bTip, aQuillonOrange, aQuillonPurple, bQuillonYellow, bQuillonBeige };
        var offs = new Vector3[6];

        for (var i = 0; i < 6; i++)
        {
            var sword = SeriesIsA[i] ? authoredA : authoredB;
            var m = FindMarker(sword, names[i]);
            if (m == null)
            {
                RefusalReason = $"marker '{names[i]}' was not found under {sword.name}, so the Tracker " +
                                "marker set cannot be reproduced and no row would be comparable.";
                return false;
            }

            offs[i] = sword.InverseTransformPoint(m.position);
        }

        localOffsets = offs;
        calibrated = true;
        return true;
    }

    private static Transform FindMarker(Transform sword, string markerName)
    {
        foreach (var t in sword.GetComponentsInChildren<Transform>(true))
            if (t.name == markerName)
                return t;
        return null;
    }

    /// <summary>The body a marker rides: the generated proxy when it exists, else the authored sword.</summary>
    private Transform Source(bool isA)
    {
        var att = isA ? swordA : swordB;
        if (att == null) return null;
        var generated = att.GeneratedPhysicsObject;
        return generated != null ? generated.transform : (isA ? authoredA : authoredB);
    }

    /// <summary>
    ///     Ray from the station ViewCam through the world point onto the registered scale plane. The camera
    ///     and plane are the sibling recorder's authored values, so there is one projection in the station.
    /// </summary>
    private Vector3 ProjectToPlane(Vector3 p)
    {
        var cam = trail == null ? null : trail.viewCam;
        float z = trail == null
            ? transform.position.z
            : (trail.planeZIsStationRelative ? trail.planeZ + transform.position.z : trail.planeZ);

        if (cam == null) return new Vector3(p.x, p.y, z);
        var c = cam.position;
        var d = p - c;
        if (Mathf.Abs(d.z) < 1e-5f) return new Vector3(p.x, p.y, z);
        return c + d * ((z - c.z) / d.z);
    }

    private int CurrentState()
    {
        if (animA != null) return animA.GetInteger("State");
        if (animB != null) return animB.GetInteger("State");
        return -1;
    }

    private void FixedUpdate()
    {
        if (trail == null)
        {
            Resolved = false;
            RefusalReason = "no sibling DriveTrailRecorder, so the authored camera and scale plane are " +
                            "unavailable. This probe never declares its own plane.";
            return;
        }

        if (swordA == null || swordB == null) ResolveStation();
        if (!Calibrate())
        {
            Resolved = false;
            return;
        }

        var srcA = Source(true);
        var srcB = Source(false);
        if (srcA == null || srcB == null)
        {
            Resolved = false;
            RefusalReason = "a generated proxy is not present yet.";
            return;
        }

        Resolved = true;
        RefusalReason = "";

        int state = CurrentState();
        bool inWindow = state == contactState || state == driveState;
        bool runnerDone = closeOnRunnerFinished && runner != null && runner.Finished;

        if (inWindow && !recording && !runnerDone) Begin();

        if (!recording) return;

        if (state == driveState && !sawDrive)
        {
            sawDrive = true;
            driveStartTime = Time.time;
        }

        // The runner ends parked in DRIVE, so leaving the window is not a reliable close signal on a
        // runner-driven run. Finished is.
        if (!inWindow || runnerDone)
        {
            offSteps++;
            if (offSteps > endGraceSteps) { End(); return; }
        }
        else
        {
            offSteps = 0;
        }

        if (tAbs.Count >= maxRows) return;

        tAbs.Add(Time.time);
        tRel.Add(sawDrive ? Time.time - driveStartTime : float.NaN);
        phase.Add(state);

        for (var i = 0; i < 6; i++)
        {
            var src = SeriesIsA[i] ? srcA : srcB;
            var w = src.TransformPoint(localOffsets[i]);
            world[i].Add(w);
            plane[i].Add(ProjectToPlane(w));
        }
    }

    private void Begin()
    {
        recording = true;
        armedForThisWindow = runner != null && runner.Running;
        sawDrive = false;
        offSteps = 0;
        tAbs.Clear();
        tRel.Clear();
        phase.Clear();
        for (var i = 0; i < 6; i++)
        {
            world[i].Clear();
            plane[i].Clear();
        }
    }

    private void End()
    {
        recording = false;
        runIndex++;

        // Captured, deliberately not written. The rows stay in memory for the readout; only an ARMED run
        // earns a file. Logged rather than silent, because a window that produced nothing and a window
        // that was never opened must not look the same in the console.
        if (!armedForThisWindow)
        {
            Debug.Log("[PhysRef] " + transform.name + ": window closed with " + tAbs.Count +
                      " sample(s) but NO ARMED RUN, so no CSV was written. Capture is not recording — arm " +
                      "the station with its armKey or armWhenReady to write one.", this);
            return;
        }

        if (writeCsvOnWindowEnd && tAbs.Count > 0) Write();
    }

    private void OnDisable()
    {
        if (recording) End();
    }

    private void Write()
    {
        if (string.IsNullOrEmpty(outputDirectory))
        {
            Debug.LogError("[PhysRef] please set directory on the RqPhysicalReferenceProbe component to record. "
                           + "outputDirectory is blank, so nothing was written. It is blank by "
                           + "default so that no machine's folder layout ships with the project; "
                           + "refusing here rather than letting CreateDirectory(\"\") throw, "
                           + "because an empty output folder looks exactly like a run that never "
                           + "armed.", this);
            return;
        }

        try
        {
            var stationName = transform.name;
            var dir = Path.Combine(outputDirectory, setName);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir,
                $"NOT_EQUIVALENT_{stationName}_run{runIndex:D2}_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var sb = new StringBuilder(1 << 16);
            float planeZWorld = trail.planeZIsStationRelative
                ? trail.planeZ + transform.position.z
                : trail.planeZ;

            sb.AppendLine("# ============================================================================");
            sb.AppendLine("# NOT EQUIVALENT TO THE PHYSICAL REFERENCE. Thesis 4.1 governs.");
            sb.AppendLine("# These are Unity values looked at through the same lens as the Chapter 3");
            sb.AppendLine("# Tracker exports. They are NOT the same space and NOT calibrated to it:");
            sb.AppendLine("#   - different origin. Tracker's origin was set in Tracker; this is Unity world.");
            sb.AppendLine("#   - different scale derivation. Tracker applied a primary-view scale at the");
            sb.AppendLine("#     scale plane; these are Unity metres.");
            sb.AppendLine("#   - different camera. The station ViewCam is a registered stand-in placed by");
            sb.AppendLine("#     P3P, not the physical camera.");
            sb.AppendLine("# DO NOT compare magnitudes. What is comparable is the SHAPE of a trace, the");
            sb.AppendLine("# CORRESPONDENCE between two traces, and the SEQUENCE of states - which is what");
            sb.AppendLine("# Chapter 3 was itself limited to (3.9, 3.11).");
            sb.AppendLine("# The physical marker record carries a +/-20 mm accuracy floor from 40 mm");
            sb.AppendLine("# ping-pong markers. Nothing here is more precise than that comparison allows.");
            sb.AppendLine("# ============================================================================");
            sb.AppendLine("# world_* = Unity world position of the marker, taken from the PHYSICS PROXY via a");
            sb.AppendLine("# fixed local offset, not from the visual sword.");
            sb.AppendLine("# plane_* = that same point carried by a ray from the station ViewCam onto the");
            sb.AppendLine("# registered scale plane. This is the perspective-projected twin, and it is the");
            sb.AppendLine("# only column of the two that is the same KIND of quantity as a Tracker x,y.");
            sb.AppendLine("# Both are always written. The world original is never removed.");
            sb.AppendLine($"# station={stationName} run={runIndex} set={setName}");
            sb.AppendLine($"# planeZ={trail.planeZ.ToString("R", CultureInfo.InvariantCulture)} " +
                          $"planeZIsStationRelative={trail.planeZIsStationRelative} " +
                          $"planeZWorld={planeZWorld.ToString("R", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"# viewCam={(trail.viewCam == null ? "NULL" : trail.viewCam.name)} " +
                          $"viewCamPos={(trail.viewCam == null ? Vector3.zero : trail.viewCam.position).ToString("F6")}");
            sb.AppendLine($"# stationOrigin={transform.position.ToString("F6")} " +
                          $"fixedDeltaTime={Time.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture)}");
            sb.AppendLine("# t_s = absolute play time. t_since_drive_s = zeroed at the first DRIVE step,");
            sb.AppendLine("# which is the virtual analogue of Chapter 3's onset of Sword A's forward");
            sb.AppendLine("# movement. NaN before DRIVE. phase = Animator State (3 CONTACT, 4 DRIVE).");
            sb.AppendLine("# EVERY fixed step is written. There is no spatial decimation, so row index and");
            sb.AppendLine("# time are consistent - which DriveTrailRecorder cannot offer, by design.");
            sb.AppendLine("# Tracker series names are reproduced exactly so a join needs no translation.");

            sb.Append("t_s,t_since_drive_s,phase,step");
            foreach (var n in SeriesNames)
                sb.Append(',').Append(n).Append("_world_x,").Append(n).Append("_world_y,")
                  .Append(n).Append("_world_z,").Append(n).Append("_plane_x,").Append(n).Append("_plane_y");
            sb.AppendLine();

            for (var k = 0; k < tAbs.Count; k++)
            {
                sb.Append(tAbs[k].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(tRel[k].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(phase[k]).Append(',').Append(k);
                for (var i = 0; i < 6; i++)
                {
                    var w = world[i][k];
                    var p = plane[i][k];
                    sb.Append(',').Append(w.x.ToString("R", CultureInfo.InvariantCulture))
                      .Append(',').Append(w.y.ToString("R", CultureInfo.InvariantCulture))
                      .Append(',').Append(w.z.ToString("R", CultureInfo.InvariantCulture))
                      .Append(',').Append(p.x.ToString("R", CultureInfo.InvariantCulture))
                      .Append(',').Append(p.y.ToString("R", CultureInfo.InvariantCulture));
                }

                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[PhysRef] wrote {path} ({tAbs.Count} rows)", this);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[PhysRef] " + name + ": CSV write failed — " + e.Message, this);
        }
    }
}
