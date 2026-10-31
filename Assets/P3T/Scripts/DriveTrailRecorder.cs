using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FIMSpace.FProceduralAnimation;
using UnityEngine;

/// <summary>
///     Draws the simulated blade motion curves as the DRIVE happens, so a run can be compared directly
///     against the physical-reference curves already registered on the station's video plane
///     (<c>RefCurves_*</c>).
///     <para>
///         <b>Six</b> traced points per run - the SAME six the physical Tracker export carries, under the
///         same names and in the same order: <c>SW-A1_tip_pink</c>, <c>SW-B1_tip_blue</c>,
///         <c>SW-A1_quillon_orange</c>, <c>SW-A1_quillon_purple</c>, <c>SW-B1_quillon_yellow</c>,
///         <c>SW-B1_quillon_beige</c>. Each is a FIXED LOCAL OFFSET on its sword, calibrated once from that
///         sword's own <c>Markers/Marker_*</c> transforms, so every drawn line has one counterpart on the
///         physical side and a comparison joins without a translation table.
///         <para>
///             Each line is drawn in ITS MARKER'S COLOUR, so a trail matches the ping-pong ball it
///             corresponds to in the reference footage.
///         </para>
///         <para>
///             <b>Superseded 2026-08-28, twice.</b> This traced four points - A tip, B tip and a MIDPOINT of
///             each sword's two quillons - calibrated from the <c>SwordRef_*</c> leaves. Both were wrong for
///             comparison: a midpoint is not a marker and has no physical counterpart, and the SwordRef
///             leaves are the registered REFERENCE pose rather than the live sword, measured 0.55-1.97 m
///             from the matching marker on all ten stations. See <see cref="tracePoints" />.
///         </para>
///     </para>
///     <para>
///         Two renderings per traced point:
///         <b>3D</b> - the true world path of the point, the physical result; and
///         <b>Plane</b> - that path projected through the station <c>ViewCam</c> onto the video plane, i.e.
///         put through the same camera model that registered the references (P3P, 70 deg FOV). Only the
///         projected one is dimensionally comparable with the reference curves, which are 2D
///         primary-camera projections.
///     </para>
///     <para>
///         Each DRIVE spawns a fresh numbered group (<c>DriveTrail_001</c>, 002, ...) under a runtime
///         <c>DriveTrails_&lt;Suffix&gt;</c> child, so runs accumulate and can be overlaid. Nothing is
///         destroyed between runs. Runtime LineRenderers do not survive exiting Play mode, so every
///         completed drive is also written to CSV. That CSV is the record; the lines are the
///         "show, don't just tell".
///     </para>
///     Apparatus/demonstration, NOT a finding. Measured data are blade rigidbody poses only (D5).
/// </summary>
/// <remarks>
///     <para>
///         <b>2026-08-24 rewrite.</b> The Week 4/5 version polled <c>PilotRunController.CurrentMode</c> and
///         <c>DriveCount</c>. That class drove joint targets directly and never touched the animator, was
///         retired by deactivation, and has now been deleted. This version takes its drive signal from the
///         <b>animator state machine</b>, which is the mechanism the current apparatus actually cycles
///         through (<c>Motions_Controller</c>: <c>State</c> int, <c>Auto</c> bool). It therefore works
///         identically on all ten stations, in manual State mode and in the Auto cycle, with no per-station
///         protocol object.
///     </para>
///     <para>
///         <b>It records nothing until switched on.</b> <see cref="recordEnabled" /> is off by default and
///         is driven in-world from the station's <c>PilotModePanel_*</c> Trails column
///         (<c>RqStationModePanel</c>). Off, <c>FixedUpdate</c> returns immediately.
///     </para>
///     <para>
///         <b>Nothing here is written back to the simulation.</b> Every field read is a transform or an
///         animator state; the only objects created are LineRenderers and a CSV file.
///     </para>
/// </remarks>
[DisallowMultipleComponent]
public class DriveTrailRecorder : MonoBehaviour
{
	/// <summary>Which transform a traced point rides on.</summary>
	public enum TraceSource
	{
		/// <summary>
		///     The generated <c>:Attachable Physics</c> proxy when Ragdoll Animator has produced one, else the
		///     authored sword. The proxy is the body PhysX actually solves, so this is the physics truth.
		/// </summary>
		ProxyPreferred = 0,

		/// <summary>
		///     Always the authored sword transform. It is a kinematic visual follower of the proxy and was
		///     measured to track it to 0.013-0.029 mm after the interpolation fix, so for a demonstration trail
		///     the two are interchangeable - but say so explicitly rather than by accident.
		/// </summary>
		AuthoredOnly = 1,

		/// <summary>
		///     The generated <c>:Attachable Physics</c> proxy and NOTHING ELSE. Where
		///     <see cref="ProxyPreferred" /> falls back to the authored sword whenever the proxy is absent,
		///     this refuses: no proxy at the start of a drive means no trail, and a proxy that disappears
		///     mid-drive ends the one in progress. Both are LogErrors.
		///     <para>
		///         Why the fallback had to go, 2026-08-29. It was silent and it was evaluated per sample, while
		///         the CSV header records <c>traceSource</c> - the SETTING, not what was actually traced. A
		///         trail that fell back for part of a drive was therefore indistinguishable in the file from
		///         one taken entirely off the physics body. The two bodies do track each other to 0.013-0.029 mm
		///         after the interpolation fix, so this is a provenance guarantee rather than a magnitude
		///         correction - but a file that cannot say which body it holds cannot be cited as either.
		///     </para>
		/// </summary>
		ProxyRequired = 2
	}

	/// <summary>
	///     The six Tracker series names, reproduced CHARACTER FOR CHARACTER and in the order the Chapter 3
	///     exports declare them, so a comparison script joins a trail to the physical record without a
	///     translation table. Same list, same order as <c>RqPhysicalReferenceProbe.SeriesNames</c> - see
	///     <c>PhysicalReferenceMeasurement.md</c> §1.1.
	///     <para>
	///         CHANGED 2026-08-28 from four series to six. The old set was
	///         <c>TipA / TipB / GuardA / GuardB</c>, where each guard was the MIDPOINT of that sword's two
	///         quillon markers. A midpoint is not a marker, so it had no counterpart on the physical side and
	///         could not be compared. The quillons are now traced individually and the midpoint is gone.
	///     </para>
	/// </summary>
	private static readonly string[] SeriesNames =
	{
		"SW-A1_tip_pink", "SW-B1_tip_blue",
		"SW-A1_quillon_orange", "SW-A1_quillon_purple",
		"SW-B1_quillon_yellow", "SW-B1_quillon_beige"
	};

	/// <summary>Which sword each series rides: true = A. Mirrors the series order above.</summary>
	private static readonly bool[] SeriesIsA = {true, false, true, true, false, false};

	/// <summary>
	///     The drawn colour of each series, matched to the PHYSICAL MARKER it traces - pink, blue, orange,
	///     purple, yellow, beige. A trail line is therefore the same colour as the ping-pong ball it
	///     corresponds to in the reference footage, which is what makes an overlay readable without a legend.
	///     <para>
	///         This REVERSES the previous scheme, where hue identified the run and the series were separated by
	///         lightness. Hue now identifies the MARKER; runs are told apart by the <c>DriveTrail_NNN</c> group
	///         name and by line width, not by colour.
	///     </para>
	/// </summary>
	private static readonly (string name, Color c)[] MarkerPalette =
	{
		("pink", new Color(1.00f, 0.41f, 0.71f)),
		("blue", new Color(0.20f, 0.45f, 1.00f)),
		("orange", new Color(1.00f, 0.55f, 0.10f)),
		("purple", new Color(0.65f, 0.30f, 0.90f)),
		("yellow", new Color(1.00f, 0.92f, 0.15f)),
		("beige", new Color(0.90f, 0.82f, 0.65f))
	};

	/// <summary>
	///     Named colours, cycled per DRIVE so consecutive runs can be told apart at a glance when they are
	///     overlaid. Hue identifies the RUN; the four traced points within a run are separated by lightness and
	///     width instead (see <see cref="SeriesColor" />). Chosen to stay distinct from the registered reference
	///     curves, which use the physical marker colours (pink / blue / orange / yellow).
	/// </summary>
	private static readonly (string name, Color c)[] DrivePalette =
	{
		("red", new Color(1.00f, 0.25f, 0.25f)),
		("green", new Color(0.30f, 0.95f, 0.40f)),
		("cyan", new Color(0.20f, 0.90f, 1.00f)),
		("violet", new Color(0.70f, 0.45f, 1.00f)),
		("white", new Color(0.95f, 0.95f, 0.95f)),
		("lime", new Color(0.75f, 1.00f, 0.20f)),
		("rose", new Color(1.00f, 0.45f, 0.70f)),
		("teal", new Color(0.20f, 0.80f, 0.70f)),
		("amber", new Color(1.00f, 0.72f, 0.20f)),
		("slate", new Color(0.60f, 0.68f, 0.80f))
	};

	[Header("Master switch (driven in-world from the panel's Trails column)")]
	[Tooltip("OFF means this component does nothing at all - FixedUpdate returns on its first line. It is " +
	         "off by default so that leaving the recorder attached to all ten stations costs nothing.")]
	public bool recordEnabled;

	[Header("Drive signal - the ANIMATOR, not a protocol object")]
	[Tooltip("The station's two dummy animators. A drive is 'the animator is in the Drive state'. Both are " +
	         "commanded together, so either being in Drive counts; A is checked first. Empty, they are " +
	         "resolved from Dummy_A_<station> / Dummy_B_<station> at Awake.")]
	public Animator animatorA;

	public Animator animatorB;

	[Tooltip("The animator state name that counts as a DRIVE. Motions_Controller calls it 'Drive' (State = 4). " +
	         "Its reverse-playback twin 'DR' is deliberately NOT recorded: it is the retreat half of the Auto " +
	         "cycle, not an actuation.")]
	public string driveStateName = "Drive";

	[Tooltip("Start recording as soon as the blend INTO Drive begins, rather than when it completes. The " +
	         "State-driven transitions blend over 2 s and the swords are already moving during it, so ON " +
	         "captures the whole actuation. OFF starts at the first fully-in-Drive step.")]
	public bool includeBlendIn = true;

	[Tooltip("INSTRUMENTATION DEBOUNCE - not a measurement criterion, and it changes nothing about what is " +
	         "sampled. Fixed steps the animator may read as 'not in Drive' before the trail is closed out.\n\n" +
	         "Why it exists, measured 2026-08-24: on the step where the 2 s blend into Drive completes, " +
	         "IsInTransition has already gone false while GetCurrentAnimatorStateInfo still reports the " +
	         "SOURCE state for one more step. Without a grace window that one step ends the run and starts a " +
	         "second one, so a single drive came out as Drive001 + Drive002 with two CSVs. 0 disables it and " +
	         "restores the split.")]
	[Min(0)]
	public int endGraceSteps = 2;

	[Header("Swords (empty = resolved by name from the station root)")]
	public Transform swordA;

	public Transform swordB;

	[Tooltip("Which transform the traced points ride on. ProxyRequired is the physics body and only the " +
	         "physics body - the generated ':Attachable Physics' proxy PhysX actually solves - and it errors " +
	         "rather than falling back. ProxyPreferred is the same body with a SILENT authored-sword " +
	         "fallback, kept so trails captured before 2026-08-29 stay reproducible. AuthoredOnly traces the " +
	         "visual follower on purpose. See the enum.")]
	public TraceSource traceSource = TraceSource.ProxyRequired;

	/// <summary>Which objects the four traced points are taken from.</summary>
	public enum TracePoints
	{
		/// <summary>
		///     LEGACY, and it is displaced. The SwordRef_* leaves are the REGISTERED REFERENCE pose - where the
		///     physical reference sword was - not where this station's Unity sword is. Measured 2026-08-28 on
		///     all ten stations: the Tip leaf sits 0.55 m to 1.97 m from that sword's own tip marker. Calibrating
		///     from them and then riding the sword draws the trail at that rigid offset. Kept so trails captured
		///     before the fix stay reproducible; do not choose it for new capture.
		/// </summary>
		SwordRefLeaves = 0,

		/// <summary>
		///     The sword's own <c>Markers/Marker_*</c> transforms - the same physical markers the Chapter 3
		///     reference series are built on. These are rigid children of the sword, so the calibrated local
		///     offset is exact and the traced point is the marker position itself.
		/// </summary>
		SwordMarkers = 1
	}

	[Header("Where the traced points come from")]
	[Tooltip("SwordMarkers traces the sword's own Marker_* transforms exactly. SwordRefLeaves is the legacy " +
	         "behaviour and is DISPLACED - the SwordRef_* leaves are the registered reference pose, measured " +
	         "0.55-1.97 m from the matching marker on all ten stations - and is kept only so earlier trails " +
	         "remain reproducible.")]
	public TracePoints tracePoints = TracePoints.SwordRefLeaves;

	[Header("Marker points - used when tracePoints = SwordMarkers")]
	[Tooltip("Empty, these resolve from <Sword>/Markers by prefix: one Marker_Tip_* and two Marker_Quillon_* " +
	         "per sword. The colour names differ per side (A: Pink/Orange/Purple, B: Blue/Yellow/Beige), so " +
	         "they are found by prefix rather than by an exact name.")]
	public Transform markerATip;

	public Transform markerAQuillon1;
	public Transform markerAQuillon2;
	public Transform markerBTip;
	public Transform markerBQuillon1;
	public Transform markerBQuillon2;

	[Header("Calibration points - this station's SwordRef leaves (Tip / Q1 / Q2)")]
	[Tooltip("Traced points are locked to the swords as fixed local offsets taken from these. They give the " +
	         "trace the same origin as the reference curve, so the two start coincident. Empty, they are " +
	         "resolved from SwordRef_A_* / SwordRef_B_* under the station root.")]
	public Transform refATip;

	public Transform refAQ1;
	public Transform refAQ2;
	public Transform refBTip;
	public Transform refBQ1;
	public Transform refBQ2;

	[Header("Projection to the video plane (must match the reference registration)")]
	[Tooltip("The station's ViewCam. Empty, it is resolved from the ViewCam_* child.")]
	public Transform viewCam;

	[Tooltip("Z of the drawn reference curves, IN THE STATION'S OWN FRAME. " +
	         "RETUNED 2026-08-28 from 2.66 to -0.01. The station root was re-origined onto " +
	         "SCALE_REF_1m_Horizontal, which sits on the ViewCam's optical axis at 2.266197 m - the depth " +
	         "at which the frustum reproduces the Tracker calibration stick's measured 1210.133 px/m. The " +
	         "Video_*/Still_* quads were moved to station-local z = 0 in the same change, so -0.01 places " +
	         "the curve plane 10.00 mm in front of them, shared by the trails and the RefCurves. " +
	         "The old 2.66 became a 2.67 m error the moment the root moved, so do not restore it. " +
	         "Read station-relative, not world - see planeZIsStationRelative. Same for every station.")]
	public float planeZ = -0.01f;

	[Tooltip("Add the station root's own Z to planeZ before projecting. ON is correct and is why the " +
	         "projected trail lands on this station's reference curves. " +
	         "MEASURED 2026-08-26, which is what this field exists for. The reference curves were built " +
	         "station-relative and their vertices sit at world z = 2.660 / 8.660 / 14.660 for the three " +
	         "rows at station z = 0 / 6 / 12. planeZ was being used as a WORLD z, so every projected trail " +
	         "landed at world 2.660 regardless of station: correct on the z = 0 row and offset by exactly " +
	         "6.00 m and 12.00 m on the other two. That is the offset, and it is in Z, not Y - all ten " +
	         "stations share y = 0.10, so no between-station Y error is possible. " +
	         "OFF restores the original world-absolute reading, so the earlier behaviour is recoverable " +
	         "rather than deleted. The authored planeZ is never rewritten either way. " +
	         "SUPERSEDED 2026-08-28 - the stations DID move. Every station root was re-origined onto " +
	         "SCALE_REF_1m_Horizontal, so station z is now 2.692586 / 8.692586 / 14.692586 instead of " +
	         "0 / 6 / 12, and with planeZ retuned to -0.01 the curve plane resolves to world " +
	         "2.682586 / 8.682586 / 14.682586. Station-relative reading is what made that re-origin safe. " +
	         "The 'no station moves' assumption this tooltip used to carry no longer holds.")]
	public bool planeZIsStationRelative = true;

	[Header("What to draw")]
	public bool draw3D = true;

	public bool drawProjected = true;
	public float width = 0.008f;

	[Tooltip("Metres the point must move before a new vertex is added. 0 = a vertex every physics step.\n\n" +
	         "SET TO 0 ON 2026-08-29. At 0.0015 the trail was decimated per series and non-uniform in time: " +
	         "slow motion was dropped entirely and fast motion undersampled, so a vertex ordinal was not a " +
	         "step and no trail could be joined to a step trace. Vertex count now equals the trail's step " +
	         "count, and the step and fixed_time_s columns state it per row. Raising it again re-introduces " +
	         "that decoupling - the columns stay correct, but index stops tracking step.")]
	public float minVertexSpacing;

	public int maxPointsPerTrail = 8000;

	[Header("Persist")]
	[Tooltip("FALSE by default, changed 2026-08-29. Writing a trail is part of ARMING DriveTrails, not a " +
	         "standing property of the component: a set either declared trails or it did not. The four " +
	         "fields that arm it - recordEnabled, useProbeRecordingPath, setName and this - are set " +
	         "together on the one station being run and cleared together afterwards, exactly like the " +
	         "probes' setName.\n\n" +
	         "It was true, which meant a component left with recordEnabled on wrote a file for every drive " +
	         "whether or not anything had been declared for it. The in-world Trails CSV button toggles THIS " +
	         "field and nothing else, so a trail written that way lands in the legacy csvDirectory pool " +
	         "rather than the run's tranche - that button is for free play, not for arming a set.")]
	public bool writeCsvOnDriveEnd;

	// BLANK by default. See outputDirectory below for why.
	public string csvDirectory = "";

	[Header("Persist - protocol channel (the probe recording path)")]
	[Tooltip("ON routes the trail CSV down the SAME path the three evidence probes use: " +
	         "<outputDirectory>/<setName>/, named for the station, so a run's trail is filed WITH its " +
	         "DriveWindow, StepTrace and PhysicalReference files instead of pooling into the shared " +
	         "Curves/DriveTrails folder.\n\n" +
	         "It also stops the nine idle stations writing. Under the protocol, setName is armed on the ONE " +
	         "station being run and left blank everywhere else, so gating the write on a non-blank setName " +
	         "means exactly one trail file per run. That is what removes the extras.\n\n" +
	         "OFF is the legacy behaviour and is the default, so every trail captured before this option " +
	         "existed stays reproducible.")]
	public bool useProbeRecordingPath;

	[Tooltip("Channel root for the protocol path, used only when useProbeRecordingPath is ON. Stable " +
	         "per-channel default, exactly like the probes' outputDirectory - it is NOT part of arming and " +
	         "must never be blanked, because Directory.CreateDirectory(\"\") throws.")]
	// BLANK by default, deliberately. It used to ship an absolute path from one machine, which both
	// leaked a folder structure into a public repository and silently wrote another machine's runs
	// into a directory that did not describe them. Blank cannot do either, and the write path
	// refuses with a named error rather than throwing on CreateDirectory("").
	public string outputDirectory = "";

	[Tooltip("THE TRANCHE, and it is ARMED, not stored. Blank between runs on purpose: a set name left " +
	         "behind writes later runs into a tranche nobody declared for them, and blank cannot go stale " +
	         "because it has to be supplied every time.\n\n" +
	         "Blank fails LOUDLY here rather than silently: with useProbeRecordingPath ON and setName empty " +
	         "the recorder writes nothing and says so. That is deliberate - it is how the nine un-armed " +
	         "stations are kept out of the tranche.")]
	public string setName = "";

	/// <summary>
	///     Suffix used to name spawned objects and CSV files (Flat / NoBind / Bind). Empty, it is derived from
	///     the station root's name by stripping the frozen <c>Station_</c> prefix - which makes the CSV carry the
	///     full arm-qualified label (<c>EdgeBind_OurSolution</c>), not just the treatment shorthand.
	/// </summary>
	public string StationSuffix = "";

	private readonly List<Vector3>[] _pts = {new(), new(), new(), new(), new(), new()};

	/// <summary>
	///     The fixed step each vertex was taken on, parallel to <see cref="_pts" />. Held per series because
	///     <see cref="minVertexSpacing" /> decimates each series independently, so a single shared list would
	///     be wrong the moment it is non-zero.
	/// </summary>
	private readonly List<int>[] _steps = {new(), new(), new(), new(), new(), new()};

	/// <summary><c>Time.fixedTime</c> at each vertex, parallel to <see cref="_pts" />.</summary>
	private readonly List<float>[] _fixedTimes = {new(), new(), new(), new(), new(), new()};

	private RA2AttachableObject _attachA, _attachB;
	private bool _calibrated;
	private int _driveIndex;
	private LineRenderer[] _lr3D, _lrPlane;
	private Vector3[] _localOffsets; // per series, in its sword's local space
	private int _offSteps;
	private bool _recording;
	private float _t0;
	private Transform _trailRoot;
	private bool _warnedScale;

	/// <summary>Fixed steps since this trail opened. The sample's own step, not a vertex ordinal.</summary>
	private int _stepInTrail;

	/// <summary>One error per trail when <see cref="maxPointsPerTrail" /> is reached, not one per step.</summary>
	private bool _cappedLogged;

	/// <summary>
	///     Set when a drive is refused or abandoned, cleared when the animator leaves Drive. Without it a
	///     refusal is retried every fixed step and the error repeats for the length of the drive.
	/// </summary>
	private bool _refusedThisDrive;

	/// <summary>Number of drives recorded this session. Read-only; the panel shows it.</summary>
	public int DriveCount => _driveIndex;

	/// <summary>True while a drive is being traced right now. Read-only; the panel shows it.</summary>
	public bool IsRecording => _recording;

	private void Awake()
	{
		ResolveRefs();
		_attachA = swordA != null ? swordA.GetComponent<RA2AttachableObject>() : null;
		_attachB = swordB != null ? swordB.GetComponent<RA2AttachableObject>() : null;
	}

	private void Start()
	{
		// Calibrate from the AUTHORED pose, before anything has moved. The Week 4/5 version calibrated at the
		// first DRIVE instead, which took the offset from wherever the sword had already settled; the SwordRef
		// leaves are static station children, so that made the offset depend on the settle.
		Calibrate();
	}

	private void FixedUpdate()
	{
		if (!recordEnabled)
		{
			if (_recording) EndTrail();
			return;
		}

		if (swordA == null || swordB == null) return;

		var driving = InDriveState();
		if (driving)
		{
			_offSteps = 0;
			if (!_recording && !_refusedThisDrive) BeginTrail();
		}
		else
		{
			// Leaving Drive is what clears a refusal, and it must clear even when nothing was recording -
			// otherwise a station refused once stays refused for the rest of the session.
			_refusedThisDrive = false;
			if (_recording)
			{
				_offSteps++;
				if (_offSteps > endGraceSteps) EndTrail();
			}
		}

		if (!_recording) return;

		var srcA = Source(true);
		var srcB = Source(false);
		if (srcA == null || srcB == null)
		{
			// Under ProxyRequired this is the proxy going away MID-DRIVE. Silently skipping the step would
			// leave a hole in a file that now promises a step per row, so the trail is closed and says so.
			if (traceSource == TraceSource.ProxyRequired)
			{
				_refusedThisDrive = true;
				Debug.LogError($"[DriveTrail] {StationSuffix}: the ':Attachable Physics' proxy went away " +
				               $"MID-DRIVE at in-trail step {_stepInTrail}. traceSource is ProxyRequired, so " +
				               "there is no fallback and this trail is ended here. What was already taken is " +
				               "physics and is written; the drive is INCOMPLETE and must not be read as one.",
					this);
				EndTrail();
			}

			return;
		}

		// Incremented on every fixed step the trail is open, INCLUDING steps a series adds no vertex on.
		int stepNow = _stepInTrail++;

		for (var i = 0; i < SeriesNames.Length; i++)
		{
			var src = SeriesIsA[i] ? srcA : srcB;
			var p = src.TransformPoint(_localOffsets[i]);
			var list = _pts[i];
			if (list.Count > 0 && minVertexSpacing > 0f &&
			    (p - list[list.Count - 1]).sqrMagnitude < minVertexSpacing * minVertexSpacing) continue;
			if (list.Count >= maxPointsPerTrail)
			{
				if (!_cappedLogged)
				{
					_cappedLogged = true;
					Debug.LogError($"[DriveTrail] {StationSuffix}: drive {_driveIndex:000} reached " +
					               $"maxPointsPerTrail ({maxPointsPerTrail}) on '{SeriesNames[i]}' at " +
					               $"in-trail step {stepNow}. Every vertex from here on is DROPPED, so the " +
					               "END of this trail is missing and its CSV is truncated with no marker in " +
					               "the rows. The last vertex is NOT the end of the drive.", this);
				}

				continue;
			}

			list.Add(p);
			_steps[i].Add(stepNow);
			_fixedTimes[i].Add(Time.fixedTime);
			if (draw3D && _lr3D[i] != null) Push(_lr3D[i], p);
			if (drawProjected && _lrPlane[i] != null) Push(_lrPlane[i], ProjectToPlane(p));
		}
	}

	/// <summary>
	///     Find one <c>Marker_Tip_*</c> and two <c>Marker_Quillon_*</c> under a sword's <c>Markers</c> group.
	///     Resolved by PREFIX because the colour names differ per side - A carries Pink / Orange / Purple and B
	///     carries Blue / Yellow / Beige - so an exact-name lookup would work on one sword and silently fail on
	///     the other. Anything already assigned in the inspector is left alone.
	/// </summary>
	private static void ResolveMarkers(Transform sword, ref Transform tip, ref Transform q1, ref Transform q2)
	{
		if (sword == null) return;
		if (tip != null && q1 != null && q2 != null) return;

		Transform foundTip = null, foundQ1 = null, foundQ2 = null;
		foreach (var t in sword.GetComponentsInChildren<Transform>(true))
		{
			if (t.name.StartsWith("Marker_Tip_"))
			{
				if (foundTip == null) foundTip = t;
			}
			else if (t.name.StartsWith("Marker_Quillon_"))
			{
				if (foundQ1 == null) foundQ1 = t;
				else if (foundQ2 == null) foundQ2 = t;
			}
		}

		if (tip == null) tip = foundTip;
		if (q1 == null) q1 = foundQ1;
		if (q2 == null) q2 = foundQ2;
	}

	/// <summary>
	///     Close out and write an open trail when the component goes away - which in the editor means exiting
	///     Play.
	///     <para>
	///         <b>Why this is necessary, found 2026-08-28.</b> <see cref="EndTrail" /> was only ever reached
	///         from <see cref="FixedUpdate" />, when the animator LEAVES the Drive state. A station running the
	///         protocol is walked Pause → SNAP → HOLD → CONTACT → DRIVE and then simply stops: the runner seals
	///         when the drive clip has played through, but the animator is still in Drive, so the trail stayed
	///         open and its CSV was never written. The only stations that did write were the ones cycling Auto
	///         in the background - and under the protocol path those carry a blank <c>setName</c> and correctly
	///         write nothing. Net effect: every trail file, for the one run that mattered, was silently lost.
	///     </para>
	///     <para>
	///         The vertices are already in memory by then; this only forces the close-out that leaving Drive
	///         would have done.
	///     </para>
	/// </summary>
	private void OnDisable()
	{
		if (!_recording) return;
		Debug.Log($"[DriveTrail] {StationSuffix}: closing an open trail on disable - the animator never left " +
		          "Drive, which is the normal end state for a protocol run.", this);
		EndTrail();
	}

	private void Reset()
	{
		ResolveRefs();
	}

	// ---- in-world entry points (RqStationModePanel binds these) ----

	/// <summary>Master switch. Turning it off mid-drive closes the run out and writes its CSV.</summary>
	public void SetRecordEnabled(bool on)
	{
		recordEnabled = on;
		if (!on && _recording) EndTrail();
	}

	public void SetDraw3D(bool on)
	{
		draw3D = on;
	}

	public void SetDrawProjected(bool on)
	{
		drawProjected = on;
	}

	public void SetWriteCsv(bool on)
	{
		writeCsvOnDriveEnd = on;
	}

	/// <summary>
	///     Resolve every reference from the frozen station naming convention, so the component works on any of
	///     the ten stations with nothing wired by hand. Anything already assigned is left alone.
	/// </summary>
	private void ResolveRefs()
	{
		var root = StationRoot();
		if (root == null) return;

		if (string.IsNullOrEmpty(StationSuffix) && root.name.StartsWith("Station_"))
			StationSuffix = root.name.Substring("Station_".Length);

		if (swordA == null) swordA = root.Find("Sword_A__" + root.name);
		if (swordB == null) swordB = root.Find("Sword_B__" + root.name);

		if (animatorA == null)
		{
			var d = root.Find("Dummy_A_" + root.name);
			if (d != null) animatorA = d.GetComponent<Animator>();
		}

		if (animatorB == null)
		{
			var d = root.Find("Dummy_B_" + root.name);
			if (d != null) animatorB = d.GetComponent<Animator>();
		}

		// The shorthand children (SwordRef_A_Bind, MotionClips_Flat, ...) are NOT arm-suffixed, so they are
		// found by prefix rather than by an exact name built from the root.
		// ViewCam is the exception as of 2026-08-28: it is now ViewCam_<StationName> on all ten stations,
		// matching the Dummy and Sword convention. Ten cameras sharing three names meant any by-name lookup
		// resolved to an arbitrary station - which is exactly how a diagnostic capture came from the wrong
		// one. The prefix match below is unaffected either way, which is why the rename was safe.
		if (viewCam == null) viewCam = ChildStartingWith(root, "ViewCam_");

		ResolveMarkers(swordA, ref markerATip, ref markerAQuillon1, ref markerAQuillon2);
		ResolveMarkers(swordB, ref markerBTip, ref markerBQuillon1, ref markerBQuillon2);

		var sra = ChildStartingWith(root, "SwordRef_A_");
		var srb = ChildStartingWith(root, "SwordRef_B_");
		if (sra != null)
		{
			if (refATip == null) refATip = sra.Find("Tip");
			if (refAQ1 == null) refAQ1 = sra.Find("Q1");
			if (refAQ2 == null) refAQ2 = sra.Find("Q2");
		}

		if (srb == null) return;
		if (refBTip == null) refBTip = srb.Find("Tip");
		if (refBQ1 == null) refBQ1 = srb.Find("Q1");
		if (refBQ2 == null) refBQ2 = srb.Find("Q2");
	}

	/// <summary>Nearest ancestor (or self) carrying the load-bearing <c>Station_</c> prefix.</summary>
	private Transform StationRoot()
	{
		var t = transform;
		while (t != null && !t.name.StartsWith("Station_")) t = t.parent;
		return t;
	}

	private static Transform ChildStartingWith(Transform parent, string prefix)
	{
		for (var i = 0; i < parent.childCount; i++)
		{
			var c = parent.GetChild(i);
			if (c.name.StartsWith(prefix)) return c;
		}

		return null;
	}

	/// <summary>
	///     True while either animator is in the declared drive state. Checking the state machine rather than a
	///     protocol object is what makes this work unchanged in manual State mode and in the Auto cycle.
	/// </summary>
	private bool InDriveState()
	{
		return InDriveState(animatorA) || InDriveState(animatorB);
	}

	private bool InDriveState(Animator a)
	{
		if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return false;
		if (a.GetCurrentAnimatorStateInfo(0).IsName(driveStateName)) return true;
		if (!includeBlendIn || !a.IsInTransition(0)) return false;
		return a.GetNextAnimatorStateInfo(0).IsName(driveStateName);
	}

	/// <summary>
	///     The transform a traced point rides on. Under <see cref="TraceSource.ProxyPreferred" /> this is the
	///     generated <c>:Attachable Physics</c> root once Ragdoll Animator has made one - the body PhysX solves -
	///     falling back to the authored sword before it exists and after the sword is released.
	/// </summary>
	private Transform Source(bool isA)
	{
		var authored = isA ? swordA : swordB;
		if (traceSource == TraceSource.AuthoredOnly || authored == null) return authored;

		var att = isA ? _attachA : _attachB;
		if (att == null) return authored;
		var generated = att.GeneratedPhysicsObject;
		if (generated == null) return authored;

		// The proxy is an instantiated copy of the sword, so the authored local offsets carry over unchanged -
		// but only while the two share a scale. Say so once rather than producing a quietly wrong trail.
		if (!_warnedScale && (generated.transform.lossyScale - authored.lossyScale).sqrMagnitude > 1e-6f)
		{
			_warnedScale = true;
			Debug.LogWarning("[DriveTrail] " + StationSuffix + ": proxy scale " + generated.transform.lossyScale +
			                 " differs from the authored sword's " + authored.lossyScale +
			                 ". The calibrated local offsets assume they match, so this trail is displaced. " +
			                 "Set traceSource = AuthoredOnly, or fix the scale.", this);
		}

		return generated.transform;
	}

	private static void Push(LineRenderer lr, Vector3 worldPoint)
	{
		lr.positionCount++;
		lr.SetPosition(lr.positionCount - 1, worldPoint);
	}

	/// <summary>
	///     Ray from the station ViewCam through the world point, intersected with the video plane. This is the
	///     same camera model that placed the SwordRefs by P3P, so a projected trail and a reference curve are
	///     in one space. Points behind/at the camera plane are clamped out (returned unprojected).
	/// </summary>
	private Vector3 ProjectToPlane(Vector3 p)
	{
		float z = PlaneZWorld;
		if (viewCam == null) return new Vector3(p.x, p.y, z);
		var c = viewCam.position;
		var d = p - c;
		if (Mathf.Abs(d.z) < 1e-5f) return new Vector3(p.x, p.y, z);
		var lambda = (z - c.z) / d.z;
		return c + d * lambda;
	}

	/// <summary>
	///     The projection plane in WORLD z: the authored station-relative <see cref="planeZ" /> plus this
	///     station's own z.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The authored value is a registration figure in the station's frame and is identical at every
	///         station. It is never rewritten; the station offset is added here, at the point of use, so the
	///         serialized number still says what it meant when it was measured.
	///     </para>
	///     <para>
	///         This component sits ON the station root, verified across all ten, so the root's own transform
	///         is the station frame and no search is needed.
	///     </para>
	/// </remarks>
	private float PlaneZWorld => planeZIsStationRelative ? planeZ + transform.position.z : planeZ;

	// Lock each traced point to its sword as a fixed local offset, taken from the station's SwordRef leaves.
	private bool Calibrate()
	{
		if (swordA == null || swordB == null) return false;
		if (refATip == null || refAQ1 == null || refAQ2 == null ||
		    refBTip == null || refBQ1 == null || refBQ2 == null)
		{
			Debug.LogError("[DriveTrail] " + StationSuffix + ": the six SwordRef leaf points (A/B Tip, Q1, Q2) " +
			               "did not resolve, so no trail can be calibrated.", this);
			return false;
		}

		// Six world points, in the Tracker series order. No midpoint: each series is one marker, so every
		// trail line has a counterpart on the physical side.
		Vector3[] world;
		if (tracePoints == TracePoints.SwordMarkers)
		{
			if (markerATip == null || markerAQuillon1 == null || markerAQuillon2 == null ||
			    markerBTip == null || markerBQuillon1 == null || markerBQuillon2 == null)
			{
				Debug.LogError("[DriveTrail] " + StationSuffix + ": tracePoints = SwordMarkers but the six " +
				               "Marker_* transforms did not resolve under <Sword>/Markers, so no trail can be " +
				               "calibrated. Refusing to fall back to the SwordRef leaves - that is the " +
				               "displaced legacy source and a silent fallback is how a wrong trail looks right.",
					this);
				return false;
			}

			world = new[]
			{
				markerATip.position, markerBTip.position,
				markerAQuillon1.position, markerAQuillon2.position,
				markerBQuillon1.position, markerBQuillon2.position
			};
		}
		else
		{
			world = new[]
			{
				refATip.position, refBTip.position,
				refAQ1.position, refAQ2.position,
				refBQ1.position, refBQ2.position
			};
		}

		// Offsets are taken in the AUTHORED sword's frame even when the trace rides the proxy: the markers are
		// rigid children of the authored sword, so this is exact, and Source() re-applies it on whichever
		// transform PhysX is actually solving.
		_localOffsets = new Vector3[SeriesNames.Length];
		for (var i = 0; i < SeriesNames.Length; i++)
			_localOffsets[i] = (SeriesIsA[i] ? swordA : swordB).InverseTransformPoint(world[i]);

		if (tracePoints == TracePoints.SwordMarkers)
			Debug.Log($"[DriveTrail] {StationSuffix}: calibrated from SWORD MARKERS, six series - " +
			          $"'{markerATip.name}', '{markerBTip.name}', '{markerAQuillon1.name}', " +
			          $"'{markerAQuillon2.name}', '{markerBQuillon1.name}', '{markerBQuillon2.name}'. " +
			          $"Tip offset from the SwordRef leaf: " +
			          $"A {(world[0] - refATip.position).magnitude * 1000f:F1} mm, " +
			          $"B {(world[1] - refBTip.position).magnitude * 1000f:F1} mm.", this);

		_calibrated = true;
		return true;
	}

	private void BeginTrail()
	{
		if (!_calibrated && !Calibrate()) return;

		// ProxyRequired refuses the drive outright rather than tracing the authored sword. See the enum for
		// why the old silent per-sample fallback could not be left in place.
		if (traceSource == TraceSource.ProxyRequired)
		{
			var missing = "";
			if (_attachA == null || _attachA.GeneratedPhysicsObject == null) missing += " Sword_A";
			if (_attachB == null || _attachB.GeneratedPhysicsObject == null) missing += " Sword_B";

			if (missing.Length > 0)
			{
				_refusedThisDrive = true;
				Debug.LogError($"[DriveTrail] {StationSuffix}: NO ':Attachable Physics' proxy for{missing} at " +
				               "the start of this drive, so THIS DRIVE HAS NO TRAIL. traceSource is " +
				               "ProxyRequired: the authored sword is a kinematic visual follower, and tracing " +
				               "it would produce a file whose own header still called it physics. Check that " +
				               "the swords are worn and that Ragdoll Animator has generated the proxies " +
				               "before Drive.", this);
				return;
			}
		}

		if (_trailRoot == null)
		{
			var host = new GameObject("DriveTrails_" + (string.IsNullOrEmpty(StationSuffix) ? "Station" : StationSuffix));
			host.transform.SetParent(transform, false);
			_trailRoot = host.transform;
		}

		_driveIndex++;
		var group = new GameObject($"DriveTrail_{_driveIndex:000}");
		group.transform.SetParent(_trailRoot, false);

		_lr3D = new LineRenderer[SeriesNames.Length];
		_lrPlane = new LineRenderer[SeriesNames.Length];
		for (var i = 0; i < SeriesNames.Length; i++)
		{
			_pts[i].Clear();
			_steps[i].Clear();
			_fixedTimes[i].Clear();
			if (draw3D) _lr3D[i] = MakeLine(group.transform, $"Drive{_driveIndex:000}_{SeriesNames[i]}_3D", i, 1.0f);
			if (drawProjected)
				_lrPlane[i] = MakeLine(group.transform, $"Drive{_driveIndex:000}_{SeriesNames[i]}_Plane", i, 0.6f);
		}

		_t0 = Time.time;
		_stepInTrail = 0;
		_cappedLogged = false;
		_recording = true;
		Debug.Log($"[DriveTrail] {StationSuffix}: recording drive {_driveIndex:000} — colour " +
		          $"{DriveColorName(_driveIndex).ToUpperInvariant()}, tracing {traceSource}", this);
	}

	private LineRenderer MakeLine(Transform parent, string name, int series, float widthScale)
	{
		var go = new GameObject(name);
		go.transform.SetParent(parent, false);
		var lr = go.AddComponent<LineRenderer>();
		lr.useWorldSpace = true; // traced points are already world
		lr.alignment = LineAlignment.View;
		lr.textureMode = LineTextureMode.Stretch;
		lr.numCornerVertices = 2;
		lr.numCapVertices = 2;
		lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
		lr.receiveShadows = false;
		// width also separates the series within a run: tips thicker than guards
		// Tips read thicker than quillons; colour now carries the marker identity, so width only has to
		// separate the two ROLES, not the six series.
		var seriesWidth = series switch {0 => 1.0f, 1 => 1.0f, _ => 0.62f};
		lr.widthMultiplier = width * widthScale * seriesWidth;
		lr.positionCount = 0;

		// The run colour has to go on a MATERIAL INSTANCE, not just startColor/endColor. LineRenderer feeds
		// those through vertex colours, and URP/Unlit ignores vertex colour — so assigning a shared material
		// made every trail come out that material's colour instead of the run's, which is exactly what
		// happened the first time. One throwaway material per line, per run.
		var c = SeriesColor(series);
		var shader = Shader.Find("Universal Render Pipeline/Unlit");
		if (shader == null) shader = Shader.Find("Sprites/Default");
		var mat = new Material(shader) {name = $"TrailMat_{name}"};
		if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
		if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
		lr.material = mat;
		lr.startColor = c;
		lr.endColor = c;
		return lr;
	}

	public static string DriveColorName(int driveIndex)
	{
		return DrivePalette[(driveIndex - 1 + DrivePalette.Length) % DrivePalette.Length].name;
	}

	/// <summary>
	///     The colour of the PHYSICAL MARKER this series traces, so a drawn line matches the ball it
	///     corresponds to in the reference footage. Hue identifies the marker, not the run.
	/// </summary>
	private Color SeriesColor(int series)
	{
		return MarkerPalette[Mathf.Clamp(series, 0, MarkerPalette.Length - 1)].c;
	}

	private void EndTrail()
	{
		_recording = false;
		_offSteps = 0;
		var n = 0;
		for (var i = 0; i < SeriesNames.Length; i++) n += _pts[i].Count;
		var per = new StringBuilder();
		for (var i = 0; i < SeriesNames.Length; i++)
		{
			if (i > 0) per.Append(", ");
			per.Append(SeriesNames[i]).Append(' ').Append(_pts[i].Count);
		}

		Debug.Log($"[DriveTrail] {StationSuffix}: drive {_driveIndex:000} done - {n} vertices over " +
		          $"{_stepInTrail} fixed steps ({per})", this);
		if (writeCsvOnDriveEnd) WriteCsv();
	}

	// Runtime LineRenderers die with Play mode; the CSV is the record that survives.
	private void WriteCsv()
	{
		try
		{
			var sfx = string.IsNullOrEmpty(StationSuffix) ? "Station" : StationSuffix;
			var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");

			string dir, fileName;
			if (useProbeRecordingPath)
			{
				// Armed, not stored. A blank tranche means this station was not the one being run,
				// so it writes nothing - that is the whole point of routing through this path.
				if (string.IsNullOrEmpty(setName))
				{
					Debug.Log($"[DriveTrail] {sfx}: protocol path is ON and setName is blank, so no CSV " +
					          "was written. Under the protocol only the armed station carries a tranche, " +
					          "so this is the expected outcome for a station that did not run.", this);
					return;
				}

				if (string.IsNullOrEmpty(outputDirectory))
				{
					Debug.LogError($"[DriveTrail] {sfx}: outputDirectory is BLANK with the protocol path " +
					               "ON. Refusing to write, because CreateDirectory(\"\") throws and an " +
					               "empty output folder is indistinguishable from a run that never armed.",
						this);
					return;
				}

				dir = Path.Combine(outputDirectory, setName);
				fileName = $"drivetrail_Station_{sfx}_run01_{stamp}.csv";
			}
			else
			{
				dir = csvDirectory;
				fileName = $"{sfx}_Drive{_driveIndex:000}_{stamp}.csv";
			}

			if (string.IsNullOrEmpty(dir))
			{
				Debug.LogError("[DriveTrail] please set directory on the DriveTrailRecorder component "
				               + "to record. The resolved directory is blank - outputDirectory on the "
				               + "protocol path, csvDirectory on the legacy one - so nothing was "
				               + "written. Both are blank by default so that no machine's folder "
				               + "layout ships with the project.", this);
				return;
			}

			Directory.CreateDirectory(dir);
			var path = Path.Combine(dir, fileName);
			var sb = new StringBuilder();
			var stn = StationRoot();
			sb.AppendLine("# Unity DRIVE trail. world_* = traced world path of the point;");
			sb.AppendLine("# plane_* = that point projected through the station ViewCam onto the video plane,");
			sb.AppendLine("# i.e. the same 2D space as the registered physical-reference curves.");
			sb.AppendLine("# stn_* = the SAME world point expressed in this station's own frame. It is a");
			sb.AppendLine("# SECONDARY column and the world_* original is never removed or overwritten: the");
			sb.AppendLine("# ten stations sit at different world positions, so an absolute path is not");
			sb.AppendLine("# comparable across them while a station-relative one is. If the adjustment is");
			sb.AppendLine("# ever found wrong, the world columns are the only way back.");
			sb.AppendLine($"# set={(string.IsNullOrEmpty(setName) ? "UNDECLARED" : setName)} " +
			              $"protocolPath={useProbeRecordingPath}");
			sb.AppendLine($"# station={sfx} drive={_driveIndex} traceSource={traceSource} " +
			              $"planeZ={planeZ.ToString("R", CultureInfo.InvariantCulture)} " +
			              $"planeZIsStationRelative={planeZIsStationRelative} " +
			              $"planeZWorld={PlaneZWorld.ToString("R", CultureInfo.InvariantCulture)} " +
			              $"fixedDeltaTime={Time.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture)}");
			sb.AppendLine($"# minVertexSpacing={minVertexSpacing.ToString("R", CultureInfo.InvariantCulture)} " +
			              $"maxPointsPerTrail={maxPointsPerTrail} includeBlendIn={includeBlendIn} " +
			              $"tracePoints={tracePoints}");
			sb.AppendLine($"# stationOrigin={(stn == null ? Vector3.zero : stn.position).ToString("F6")} " +
			              "note=planeZ is STATION-RELATIVE. It was read as a world z until 2026-08-26, " +
			              "which put every projected trail at world z 2.660 regardless of station - correct " +
			              "on the z=0 row and offset by 6.00 m and 12.00 m on the other two rows.");
			sb.AppendLine("# step = the FIXED STEP this vertex was sampled on, 0 at the first step of THIS");
			sb.AppendLine("# TRAIL. That zero is the step the trail opened, which with includeBlendIn = true is");
			sb.AppendLine("# the start of the blend INTO Drive. It is NOT the DriveWindow recorder's step 0,");
			sb.AppendLine("# which is the Drive transition, and it is not the boundaries file's step_in_segment.");
			sb.AppendLine("# The bases differ. DO NOT JOIN ON step ACROSS FILES.");
			sb.AppendLine("# fixed_time_s = Time.fixedTime at the sample - the absolute physics clock, which");
			sb.AppendLine("# carries no origin assumption. This is the column to join on across files.");
			sb.AppendLine("# index remains the vertex ordinal. With minVertexSpacing = 0 there is one vertex per");
			sb.AppendLine("# step and index equals step, but that is a consequence of the setting and not a");
			sb.AppendLine("# guarantee: any non-zero spacing decimates each series separately and they diverge.");
			sb.AppendLine("series,index,step,fixed_time_s,world_x,world_y,world_z,plane_x,plane_y,plane_z," +
			              "stn_x,stn_y,stn_z,plane_stn_x,plane_stn_y,plane_stn_z");
			for (var i = 0; i < SeriesNames.Length; i++)
			{
				var list = _pts[i];
				for (var k = 0; k < list.Count; k++)
				{
					var w = list[k];
					var p = ProjectToPlane(w);
					// Secondary, beside the originals. Full inverse transform, not a subtraction, so a
					// rotated or scaled station root is handled; the origin is in the header above.
					var ws = stn == null ? w : stn.InverseTransformPoint(w);
					var ps = stn == null ? p : stn.InverseTransformPoint(p);
					sb.Append(SeriesNames[i]).Append(',').Append(k).Append(',')
						.Append(k < _steps[i].Count
							? _steps[i][k].ToString(CultureInfo.InvariantCulture)
							: "").Append(',')
						.Append(k < _fixedTimes[i].Count
							? _fixedTimes[i][k].ToString("R", CultureInfo.InvariantCulture)
							: "").Append(',')
						.Append(w.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
						.Append(w.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
						.Append(w.z.ToString("R", CultureInfo.InvariantCulture)).Append(',')
						.Append(p.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
						.Append(p.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
						.Append(p.z.ToString("R", CultureInfo.InvariantCulture)).Append(',')
						.Append(ws.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
						.Append(ws.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
						.Append(ws.z.ToString("R", CultureInfo.InvariantCulture)).Append(',')
						.Append(ps.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
						.Append(ps.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
						.Append(ps.z.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
				}
			}

			File.WriteAllText(path, sb.ToString());
			Debug.Log("[DriveTrail] wrote " + path, this);
		}
		catch (System.Exception e)
		{
			Debug.LogWarning("[DriveTrail] CSV write failed: " + e.Message, this);
		}
	}

	[ContextMenu("Clear all drive trails")]
	public void ClearTrails()
	{
		if (_recording) EndTrail();
		if (_trailRoot != null)
		{
			if (Application.isPlaying) Destroy(_trailRoot.gameObject);
			else DestroyImmediate(_trailRoot.gameObject);
			_trailRoot = null;
		}

		// Also sweep any groups left directly under this object by the pre-2026-08-24 version.
		for (var i = transform.childCount - 1; i >= 0; i--)
		{
			var c = transform.GetChild(i);
			if (!c.name.StartsWith("DriveTrail")) continue;
			if (Application.isPlaying) Destroy(c.gameObject);
			else DestroyImmediate(c.gameObject);
		}

		_driveIndex = 0;
	}
}
