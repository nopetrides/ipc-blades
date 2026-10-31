using System.Collections.Generic;
using UnityEngine;

/// <summary>
///     The engine and physics settings that influence the outcome but are NOT sliders — Bill's dispositions 1 and
///     2 (justify / test-and-fix). Drop one of these in the scene and the <see cref="TunerPanel" /> lists them
///     beside the exposed parameters, so a reviewer who cannot open the Unity inspector still sees the whole
///     accounting rather than only the movable part.
///     <para>
///         <b>Why this is a scene component and not only a document.</b>
///         <c>the Week-5 physics-settings declaration (removed; see git history if required)</c> is the citable write-up, but a document can be
///         missed, left behind on a branch, or quietly go stale — all three of which have already happened in
///         this project. The values live here as well, next to the thing they configure, and
///         <see cref="Verify" /> compares each one against what Unity is actually running. A setting changed in
///         Project Settings without updating its declaration raises a console error on the next play, instead of
///         being discovered a month later in a draft.
///     </para>
///     <para>
///         Every entry carries a <c>declared</c> value, so the same staleness check that guards the sliders
///         guards these too — see <see cref="TunableAttribute.Declared" />.
///     </para>
/// </summary>
public class DeclaredPhysicsSettings : MonoBehaviour
{
	/// <summary>The authored-body half of the worn-sword interpolation split, quoted in its declaration.</summary>
	private const string AUTHORED_SWORD_NOTE =
		"AUTHORED (visible) sword is forced to None while worn, by RqAttachableVisualSync, and its original " +
		"value is restored on unwear - which matters for the freeplay swords, which become dynamic again when " +
		"released. While worn that body is a visual stand-in: kinematic, detectCollisions = false, colliders " +
		"disabled, transform authored by Ragdoll Animator. There is nothing for interpolation to smooth. " +
		"Suppression is applied only once GeneratedPhysicsObject exists, so it can never run before the proxy " +
		"has been generated. ";

	private const string GJ = "Engine — justified";
	private const string GF = "Engine — tested and fixed";

	// ---------- justified: a sentence in the paper suffices ----------

	[Tunable(GJ, 0f, 0f, "Gravity (m/s²)",
		"Earth gravity. The physical reference was recorded under it, so switching it off would model a " +
		"different experiment. Screened as a factor in the 126-run remaining-factors screen.",
		0, Disposition.Justified, -9.81f)]
	public float gravityY = -9.81f;

	[Tunable(GJ, 0f, 0f, "Blade collider type (0=convex mesh)",
		"Convex MeshCollider on each blade. Non-convex mesh colliders cannot be dynamic rigidbodies in PhysX, " +
		"so this is forced. Cost: the 0.4 mm central hollow is filled. The 25° cutting edge is on the hull and " +
		"survives — see PHYSICS_SETTINGS_DECLARATION.md [removed - see git history] F6.",
		1, Disposition.Justified, 0f)]
	public float bladeColliderIsConvexMesh;

	[Tunable(GJ, 0f, 0f, "Blade rigidbodies kinematic (0=no)",
		"Kinematic must be off or the blades do not simulate — the physical reference showed a physics-driven " +
		"interaction from a declared start state, which is the whole apparatus.",
		2, Disposition.Justified, 0f)]
	public float bladesKinematic;

	[Tunable(GJ, 0f, 0f, "Bounce threshold (m/s)",
		"Unity default, 2 m/s. Our closing speeds are sub-mm/s, orders below it, so restitution is INACTIVE by " +
		"construction. Correct for steel-on-steel; stated rather than left implicit.",
		3, Disposition.Justified, 2f)]
	public float bounceThreshold = 2f;

	// The four rows below were SURFACED, not chosen. They were found on 2026-08-19 to differ from the
	// 2026-08-09 freeze (commit 5deb256), having been changed by commit d74e730 ("using Fimpossible to author
	// animation targets", 2026-08-10), which bumped DynamicsManager's serializedVersion 17 -> 23 and moved
	// several physics fields alongside work whose subject was animation authoring. They are values introduced
	// in the same commit and later surfaced during reconciliation — the commit message and diff do not
	// establish an intent for them, and none is asserted here. All four are restored to their pre-d74e730
	// values and declared so the same drift is visible next time.
	//
	// Scope note, not a statement of motive: none of these is a sanctioned lever. Pop-through is a documented
	// consequence of the absent angular CCD and is out of scope, and the response under study is friction-
	// based rather than solver-based, so nothing below should be read as a solver-side remedy.
	// Undeclared engine state is the failure this component exists to catch, and it caught these late.

	[Tunable(GJ, 0f, 0f, "Worn sword rigidbody interpolation: authored=None, generated proxy=Interpolate  [not script-readable]",
		"DECLARED 2026-08-21 — a DELIBERATE SPLIT, and the two halves must not be reconciled to each other. " +
		"The GENERATED PROXY (':Attachable Physics') keeps Interpolate: it is the body that actually simulates " +
		"and that every measurement reads, and interpolation is desirable there for video capture. The " +
		AUTHORED_SWORD_NOTE +
		"WHY IT IS NOT MERELY COSMETIC HOUSEKEEPING: with the authored body on Interpolate, Unity overwrote its " +
		"transform between the fixed loop and Update by a MEDIAN 100-490 mm on EVERY frame. Ragdoll Animator's " +
		"late pass corrected it before render, so it was invisible except on render frames with no new fixed " +
		"step, where that pass returns early (RagdollHandler.Update.cs:209) and the displaced pose was drawn. " +
		"Interpolation never touches the simulation, so no contact, force, classification or attribution result " +
		"was affected - AUDITED, not assumed: DriveTrailRecorder samples in FixedUpdate, before " +
		"the interpolation write; RqStationContactRecorder and both attribution recorders resolve the proxy " +
		"explicitly; no Update() in our scripts reads the authored sword transform. " +
		"MUST ALSO BE VALIDATED THROUGH THE FINAL GENERATED RAGDOLLS: this value is not readable from a prefab, " +
		"because the proxy does not exist until a sword is worn at runtime. Verifying the authored body alone is " +
		"NOT sufficient. Enter play mode and confirm BOTH halves on every worn sword - authored None and proxy " +
		"Interpolate. Last verified 2026-08-21: 29/29 on both. Re-verify after any change to the dummy prefabs, " +
		"to RagdollHandler's RigidbodiesInterpolation, or to how swords are attached. " +
		"See DECLARATION_Draft9_RagdollHandler.md N11 and FIX_WornSwordVisualDesync_MinSpec.md.",
		22, Disposition.Justified, 1f)]
	public float wornSwordInterpolationSplit = 1f;

	[Tunable(GJ, 0f, 0f, "Project default material (1=BladeGrip assigned)  [not script-readable]",
		"DECLARED, NOT RESTORED — the one field from d74e730 that was kept. That commit assigned a " +
		"project-wide default physics material where there had been none: BladeGrip, 0.45 static / 0.35 " +
		"dynamic, frictionCombine Maximum. It is declared here rather than reverted because reverting it " +
		"would change what the ragdoll bodies run, and it does NOT touch the measured contact. " +
		"WHAT IT DOES NOT GOVERN: the swords. Verified in play mode 2026-08-19 — SwordBladeSetup.Awake -> " +
		"Setup() -> BuildRuntimeMaterial() assigns a runtime clone to every non-trigger collider, and " +
		"RagdollBonesChain.Colliders.cs COPIES that clone onto the ':Attachable Physics' proxies Ragdoll " +
		"Animator re-hosts the blade on. All 20 station blades and crossguards read \"BladeGrip (runtime)\", " +
		"and the QuickDirty hulls read their own QuickDirty_EdgeHigh / QuickDirty_FlatLow. No blade anywhere " +
		"falls back to this default, so an edit here cannot silently move the RQ's friction channel. " +
		"WHAT IT DOES GOVERN: colliders left genuinely unassigned at runtime — in practice the ragdoll bone " +
		"colliders, about 20 per dummy. Before d74e730 those ran PhysX's built-in 0.6/0.6; they now run " +
		"0.45/0.35. That is a real change to arm-vs-arm and arm-vs-sword contact, judged acceptable and " +
		"recorded rather than reverted. It is NOT inert, which is why it gets a row. " +
		"Ragdoll Animator assigns no material of its own: CollidersPhysicMaterial, PhysicMaterialOnFall and " +
		"OverrideMaterial are null on all 97 ragdoll components, and the three vendor PM_* assets are " +
		"referenced by zero files in the project. " +
		"Not script-readable — UnityEngine.Physics exposes no default-material property (confirmed by " +
		"reflection 2026-08-19): verify by hand in ProjectSettings/DynamicsManager.asset.",
		8, Disposition.Justified, 1f)]
	public float defaultMaterial_BladeGrip = 1f;

	[Tunable(GJ, 0f, 0f, "Contact pairs mode (0=default)  [not script-readable]",
		"Restored to 0 on 2026-08-19, from 3. Governs WHICH contact pairs PhysX reports, including " +
		"kinematic-kinematic and kinematic-static pairs. It is a reporting-semantics change, and this rig " +
		"reads contacts through collision callbacks, so a non-default here alters what the instrument sees " +
		"rather than only what the engine does. Kinematic-contact reporting is already an open risk on this " +
		"apparatus — the driven arm hosts kinematic bodies — so this must not sit at an undeclared value. " +
		"Not script-readable (confirmed by reflection: UnityEngine.Physics exposes no contact-pairs-mode " +
		"property): verify by hand in ProjectSettings/DynamicsManager.asset.",
		4, Disposition.Justified, 0f)]
	public float contactPairsMode;

	[Tunable(GJ, 0f, 0f, "Queries hit backfaces (0=off)",
		"Restored to 0 on 2026-08-19, from 1. Decides whether raycasts and other scene queries register hits " +
		"on triangle backfaces. Our measurement path queries blade hulls from outside — the edge polylines in " +
		"StationDebugGizmos are built by probing the cooked hull with Collider.ClosestPoint from far out — so " +
		"a query that can also latch a backface changes the semantics of the instrument, not just its " +
		"performance. Off is Unity's default and the semantics every existing measurement was taken under. " +
		"Script-readable as Physics.queriesHitBackfaces (confirmed by reflection) and asserted in Verify().",
		5, Disposition.Justified, 0f)]
	public float queriesHitBackfaces;

	[Tunable(GJ, 0f, 0f, "Enable adaptive force (0=off)  [not script-readable]",
		"Restored to 0 on 2026-08-19, from 1. Adaptive force adjusts the forces PhysX applies to resolve " +
		"contacts, so it changes contact behaviour directly — the one quantity this study reports on. It was " +
		"switched on without declaration or a screen. Off is Unity's default. Turning it on would be a " +
		"factor to declare and screen, not collateral state. " +
		"Not script-readable (confirmed by reflection): verify by hand in ProjectSettings/DynamicsManager.asset.",
		6, Disposition.Justified, 0f)]
	public float enableAdaptiveForce;

	[Tunable(GJ, 0f, 0f, "Physics log verbosity (0=off)  [not script-readable]",
		"Restored to 0 on 2026-08-19, from 3. This is diagnostics and provenance rather than mechanics: it " +
		"does not change the equations. It is declared because level 3 raises PhysX log volume, and log " +
		"volume can perturb frame timing on a rig whose runs are timed — a measurement-conditions variable, " +
		"which is the reason to state it rather than assume it is inert. " +
		"Not script-readable (confirmed by reflection): verify by hand in ProjectSettings/DynamicsManager.asset.",
		7, Disposition.Justified, 0f)]
	public float logVerbosity;

	// ---------- tested and fixed: one value works, and here is what fixed it ----------

	[Tunable(GF, 0f, 0f, "Fixed timestep (s)",
		"90 Hz. Chosen to match the Quest 3S display for the DEMO, then inherited by the measurement rig. " +
		"UNDER TEST: 90/180/360 Hz is a mandatory unrun screen, and the rate is part of the claim, not a free " +
		"parameter — see VALIDITY_LEDGER.md [removed - see git history] Gate A.",
		10, Disposition.TestedAndFixed, 1f / 90f)]
	public float fixedTimestep = 1f / 90f;

	[Tunable(GF, 0f, 0f, "Default solver iterations",
		"16, against Unity's documented default of 6. DECLARED DEMO BASELINE — semi-deliberate and " +
		"scope-specific: set in commit 47635c7 (\"settings for oculus\", 2026-08-10) for the 90 Hz Quest build, " +
		"not chosen as an experimental pin. Updated here 2026-08-19 from a stale declared 25, which had not " +
		"matched the engine since 2026-08-10. " +
		"⛔ SOLVER ITERATIONS ARE NOT A LEVER. They are not to be tuned, screened, or investigated as a " +
		"response to pop-through: pop-through follows from the absent angular CCD and is a bounded, documented " +
		"limitation, and the response under study is friction-based, not solver-based. " +
		"Read the live value, never a document — see Workspace/Week7/DECLARATION_Draft9_RagdollHandler.md N24, " +
		"which is authoritative and supersedes Week5/PHYSICS_SETTINGS_DECLARATION.md [removed - see git history]'s 25/15. At least THREE " +
		"counts are live at once across body classes: project default 16, the RagdollAnimator2 arm bones 12 " +
		"(RagdollHandler.UnitySolverIterations), and the blades' own values from SwordBladeSetup. N24's point " +
		"stands and belongs beside any pressure result: the actuator producing the press runs the LOWEST " +
		"position-iteration count of the three.",
		11, Disposition.TestedAndFixed, 16f)]
	public float defaultSolverIterations = 16f;

	[Tunable(GF, 0f, 0f, "Default solver velocity iterations",
		"24. DECLARED DEMO BASELINE — same provenance as the position count above (47635c7, \"settings for " +
		"oculus\"), semi-deliberate and scope-specific rather than an experimental pin. Updated here " +
		"2026-08-19 from a stale declared 15. Governs exit velocity accuracy after a bounce; our outcome is " +
		"maintained contact, not bounce, so this is likely over-provisioned. Unscreened, and NOT to be screened " +
		"as a pop-through remedy — see the note above. " +
		"The arm bones inherit this project value rather than overriding it: RagdollChainBone reads " +
		"UnityVelocitySolverIterations, and 0 there means \"use the project value\", not zero — so the arm " +
		"bones run 12 position / 24 velocity (N24).",
		12, Disposition.TestedAndFixed, 24f)]
	public float defaultSolverVelocityIterations = 24f;

	[Tunable(GF, 0f, 0f, "Auto sync transforms (1=on)  [DEPRECATED BY UNITY]",
		"ON, against a documented default of DISABLED — an undeclared non-default until 2026-08-06. Diagnostic A " +
		"root-caused the ARM divergence to one Physics.SyncTransforms direction error, so this setting and that " +
		"defect are the same subject. Unity 6000.4 has now DEPRECATED it outright: \"please use " +
		"Physics.SyncTransforms instead to manually sync physics transforms when required\". We are relying on a " +
		"deprecated non-default whose failure mode has already bitten us once. Declaration F3.",
		13, Disposition.TestedAndFixed, 1f)]
	public float autoSyncTransforms = 1f;

	[Tunable(GF, 0f, 0f, "Sleep threshold",
		"Unity default 0.005. UNVERIFIED RISK: ARM is deliberately near-zero-velocity, so a blade that sleeps " +
		"mid-hold stops updating its contact. Must be confirmed not to fire during ARM.",
		14, Disposition.TestedAndFixed, 0.005f)]
	public float sleepThreshold = 0.005f;

	[Tunable(GF, 0f, 0f, "Max depenetration velocity (m/s)",
		"Unity default 10. Directly bounds how violent the phantom-contact ejection can be; the observed " +
		"~9 m/s sits just under it, so the cap is not currently binding. Lowering it is an untested mitigation.",
		15, Disposition.TestedAndFixed, 10f)]
	public float maxDepenetrationVelocity = 10f;

	// Not exposed to script by Unity, so these are declared-only and cannot be auto-verified.
	// Read them from ProjectSettings/DynamicsManager.asset when auditing.

	[Tunable(GF, 0f, 0f, "Solver type (0=PGS, 1=TGS)  [not script-readable]",
		"PGS, applied 2026-08-06 and FROZEN 2026-08-09. Justified as Unity's own documented default — " +
		"\"the default PhysX solver\" — and as the fixed representative solver for the experiment. Nothing " +
		"more is claimed for it. " +
		"⛔ DO NOT restore the earlier justification. This field previously argued PGS from failure severity " +
		"(TGS ~450 mm ejection at 3.22 m/s vs 16.7 mm at 0.645 m/s, citing UUM-47140). That framing is " +
		"RETRACTED: PGS pops at 128/128 and 1000 Hz, so raising precision PRODUCES the failure and PGS is not " +
		"corrective. See the retraction block in the Week-5 TODO (removed; see git history if required). The value stands; the reason changed. " +
		"Not script-readable — verify by hand in DynamicsManager.asset (confirmed by reflection: UnityEngine." +
		"Physics exposes no solver-type property).",
		16, Disposition.TestedAndFixed, 0f)]
	public float solverType_PGS;

	[Tunable(GF, 0f, 0f, "Friction type (0=patch)  [not script-readable]",
		"Patch friction: \"only up to four scalar solver constraints per pair of touching objects\" — and our " +
		"manifold is four points, so the bind is carried by the coarsest model exactly where it runs out. " +
		"A DECLARED FACTOR, not an inherited default. Declaration F2.",
		17, Disposition.TestedAndFixed, 0f)]
	public float frictionType_Patch;

	[Tunable(GF, 0f, 0f, "Improved patch friction (1=on)",
		"ON as of the 2026-08-09 freeze, against Unity's default of off. Documented as \"optimize patch " +
		"friction so that static and dynamic friction do not exceed analytical results\" — it distributes " +
		"normal force across the friction anchors so total static and dynamic friction stay within the " +
		"analytical bound. With it OFF the friction PhysX applies may EXCEED that bound, i.e. the sim can show " +
		"a bind stronger than the declared μ justifies: a construct-validity threat in the direction that " +
		"flatters the result. μ is destined to be an experimental parameter, so the bound matters. Affects " +
		"Patch friction only, which is what we run. " +
		"NOTE this row previously said \"not script-readable\" — that was WRONG. Physics.improvedPatchFriction " +
		"exists in 6000.4 (confirmed by reflection against the running editor) and is now asserted in Verify(). " +
		"Solver type, friction type and Enhanced Determinism are the three that genuinely are not readable.",
		18, Disposition.TestedAndFixed, 1f)]
	public float improvedPatchFriction = 1f;

	[Tunable(GF, 0f, 0f, "Enhanced determinism (0=off)  [not script-readable]",
		"OFF. Documented as \"simulation in the scene is consistent regardless the actors present, provided " +
		"that the game inserts the actors in a deterministic order. This mode sacrifices some performance to " +
		"ensure this additional determinism.\" This is a measurement instrument, not a game, so run-to-run " +
		"reproducibility is worth more than frame time — turning it ON is recommended and DEFERRED, not " +
		"rejected, because it changes contact behaviour and so must not land mid-screen. " +
		"Not script-readable (confirmed by reflection): verify by hand in DynamicsManager.asset.",
		19, Disposition.TestedAndFixed, 0f)]
	public float enhancedDeterminism;

	[Tunable(GF, 0f, 0f, "Default contact offset (m)",
		"0.01 m PROJECT-WIDE — but this is only the default applied to NEWLY CREATED colliders, and the blades " +
		"override themselves to 0.005 m each. Never quote this number as the blades' offset. PhysX generates " +
		"contact when separation falls below the SUM of the two colliders' own offsets, so the blade PAIR " +
		"begins generating contact at 10 mm, against a blade 5.00 mm thick at the guard and 2.00 mm at the " +
		"point. Verify() dumps every contacting collider's actual offset beside this value for exactly that " +
		"reason. Declared and frozen — see PHYSICS_SETTINGS_DECLARATION.md [removed - see git history] F6; prior ablation indicates it does " +
		"not control the failure, so it is recorded, not re-opened.",
		20, Disposition.TestedAndFixed, 0.01f)]
	public float defaultContactOffset = 0.01f;

	[Tunable(GF, 0f, 0f, "Default max angular speed (rad/s)",
		"20, against Unity's documented default of 50 — an undeclared non-default until 2026-08-09. It is a " +
		"CEILING ON ROTATIONAL RESPONSE, and the primary outcome measure is rotation coupling gain Gθ, so a " +
		"binding ceiling would clip the very quantity being reported. Note the arm bodies override back to 50 " +
		"(ActiveArmRagdoll.maxAngularVelocity), so the apparatus is split across two values. Declared here; " +
		"confirm it is not binding at the frozen operating point.",
		21, Disposition.TestedAndFixed, 20f)]
	public float defaultMaxAngularSpeed = 20f;

	private void Start()
	{
		Verify();
	}

	/// <summary>
	///     Compare every declaration that Unity exposes to script against what the engine is actually running.
	///     A mismatch means Project Settings was changed without updating the declaration beside it, so the
	///     explanation a reviewer would read is describing a configuration that is not in effect.
	/// </summary>
	public void Verify()
	{
		var bad = 0;
		bad += Check("Gravity (m/s²)", Physics.gravity.y, gravityY);
		bad += Check("Bounce threshold (m/s)", Physics.bounceThreshold, bounceThreshold);
		bad += Check("Fixed timestep (s)", Time.fixedDeltaTime, fixedTimestep);
		bad += Check("Default solver iterations", Physics.defaultSolverIterations, defaultSolverIterations);
		bad += Check("Default solver velocity iterations", Physics.defaultSolverVelocityIterations,
			defaultSolverVelocityIterations);
		// Deliberately reading a deprecated API: it is the only way to observe the setting we actually depend on,
		// and the whole point of this component is to report what is really in effect. The deprecation itself is
		// recorded in the note on the field.
#pragma warning disable CS0618
		bad += Check("Auto sync transforms", Physics.autoSyncTransforms ? 1f : 0f, autoSyncTransforms);
#pragma warning restore CS0618
		bad += Check("Sleep threshold", Physics.sleepThreshold, sleepThreshold);
		bad += Check("Max depenetration velocity (m/s)", Physics.defaultMaxDepenetrationVelocity,
			maxDepenetrationVelocity);
		bad += Check("Improved patch friction", Physics.improvedPatchFriction ? 1f : 0f, improvedPatchFriction);
		bad += Check("Queries hit backfaces", Physics.queriesHitBackfaces ? 1f : 0f, queriesHitBackfaces);
		bad += Check("Default contact offset (m)", Physics.defaultContactOffset, defaultContactOffset);
		bad += Check("Default max angular speed (rad/s)", Physics.defaultMaxAngularSpeed, defaultMaxAngularSpeed);

		bad += VerifyPerObject();
		DumpResolvedContactSurfaces();

		if (bad > 0)
			Debug.LogError($"[Declared] {bad} declared value(s) do not match the running configuration. " +
			               "Results recorded in this state are not citable: the declaration and the engine " +
			               "disagree, so nothing here documents what actually ran. Fix the setting or fix the " +
			               "declaration — and update the Week-5 physics-settings declaration (removed; see git history if required) with it.",
				this);
		else
			Debug.Log("[Declared] engine settings and per-object apparatus state both match their declarations. " +
			          "SIX declarations are not script-readable and are NOT covered by the line above — solver " +
			          "type, friction type, enhanced determinism, contact pairs mode, adaptive force and log " +
			          "verbosity. Verify those by hand in ProjectSettings/DynamicsManager.asset. " +
			          "(This message previously named improved-patch friction as unreadable; it is readable, " +
			          "is asserted above, and enhanced determinism is the third genuinely unreadable one.)");
	}

	/// <summary>
	///     The per-object half of the check: every sword's declared mass, centre of mass, contact offset,
	///     collision-detection mode and contact material against what is actually live.
	///     <para>
	///         Project settings alone were never enough. The apparatus carries its own declared values on each
	///         blade, and those were the ones that had actually drifted — a crossguard with no physics material
	///         at all was running PhysX's built-in 0.6/0.6 while the blade beside it ran the declared 0.3, on the
	///         same sword, undetected. This is the check that would have caught it on the first play.
	///     </para>
	/// </summary>
	private int VerifyPerObject()
	{
		var bad = 0;
		var checkedCount = 0;
		foreach (var s in FindObjectsByType<SwordBladeSetup>(FindObjectsInactive.Include))
		{
			checkedCount++;
			string report;
			if (s.VerifyAgainstLive(out report)) continue;
			bad++;
			Debug.LogError($"[Declared] APPARATUS DISAGREES WITH DECLARATION — {Path(s.transform)}: {report}",
				s);
		}

		// anything sword-shaped that has no SwordBladeSetup at all is invisible to the check above, which is
		// exactly how the two XRI pickup blades ended up on an undeclared material.
		foreach (var c in FindObjectsByType<Collider>(FindObjectsInactive.Include))
		{
			if (c.isTrigger || c.GetComponentInParent<SwordBladeSetup>() != null) continue;
			var n = c.gameObject.name.ToLowerInvariant();
			var pn = c.transform.parent != null ? c.transform.parent.name.ToLowerInvariant() : "";
			var swordish = n.Contains("blade") || n.Contains("sword") || n.Contains("crossguard")
			               || pn.Contains("sword") || pn.Contains("sw_");
			if (!swordish || c.sharedMaterial != null) continue;
			bad++;
			Debug.LogError($"[Declared] UNDECLARED CONTACT SURFACE — {Path(c.transform)} has no physics material " +
			               "and no SwordBladeSetup, so it falls back to the PROJECT DEFAULT material. Since " +
			               "2026-08-10 that default is BladeGrip (0.45/0.35), not PhysX's built-in 0.6/0.6 — " +
			               "see the defaultMaterial_BladeGrip declaration. Either way the surface is undeclared.",
				c);
		}

		if (bad == 0) Debug.Log($"[Declared] {checkedCount} sword(s) match their declared apparatus values.");
		return bad;
	}

	/// <summary>
	///     Dump the values that actually reach PhysX at every contacting surface: each collider's OWN
	///     <c>contactOffset</c> and the resolved material properties.
	///     <para>
	///         <b>Why a dump and not only a check.</b> Two of the numbers that matter most cannot be read off a
	///         project setting. <c>Physics.defaultContactOffset</c> is documented as the default applied to
	///         *newly created* colliders, so it says nothing about what an existing blade is running — and
	///         contact is generated below the SUM of the two colliders' own offsets, which is a pair property
	///         that no single field holds. Second, each sword runs a runtime CLONE, not the asset it came
	///         from, so the asset's numbers are not automatically the numbers that ran.
	///     </para>
	///     <para>
	///         <b>Corrected 2026-08-19.</b> This note previously said no <c>.physicsMaterial</c> asset exists
	///         and that every sword's material is built by <c>SwordBladeSetup.MakeMaterial()</c>. Both were
	///         stale. THREE assets exist — <c>BladeGrip</c> (0.45/0.35), <c>QuickDirty_EdgeHigh</c>
	///         (0.80/0.60) and <c>QuickDirty_FlatLow</c> (0.45/0.35), all <c>frictionCombine = Maximum</c> —
	///         the method is <c>BuildRuntimeMaterial()</c>, and the prefabs and scene now hold a real
	///         serialized <c>physicsMaterial</c> reference to BladeGrip, so a blade DOES show its material in
	///         the inspector. What the dump is still for is the clone: verified in play mode on 2026-08-19,
	///         every blade proxy runs "BladeGrip (runtime)" and there are 29 DISTINCT clone instances with
	///         none reused, so a tuner slider can move one sword's friction without writing the project asset.
	///         That isolation is the reason the clone exists, and it is why the asset value alone is not the
	///         record of what ran.
	///     </para>
	///     <para>
	///         Note <c>frictionCombine</c> is <b>Maximum</b>, not Unity's default of Average, and that is
	///         deliberate. Blade-vs-blade is unaffected — both blades carry the same BladeGrip values, and
	///         every combine mode returns the same result from equal inputs — but the crossguard and the
	///         markers are real contact surfaces (F8/F8a), and the quillon/marker catch is the one event
	///         validated against the physical record. Switching to Average would change that pairing for no
	///         benefit to the Edge-vs-Flat comparison. The QuickDirty hulls are the one place the inputs are
	///         NOT equal (0.80/0.60 against 0.45/0.35), so Maximum is load-bearing there.
	///     </para>
	/// </summary>
	private void DumpResolvedContactSurfaces()
	{
		var sb = new System.Text.StringBuilder();
		sb.Append("[Declared] resolved contact surfaces — what actually reaches PhysX.\n");
		sb.AppendFormat("  Physics.defaultContactOffset = {0:0.#####} m (applies to NEWLY CREATED colliders " +
		                "only — not necessarily any blade below)\n", Physics.defaultContactOffset);

		foreach (var s in FindObjectsByType<SwordBladeSetup>(FindObjectsInactive.Include))
		{
			sb.AppendFormat("  {0}\n", Path(s.transform));
			foreach (var c in s.ContactColliders())
			{
				var m = c.sharedMaterial;
				sb.AppendFormat("    {0,-14} contactOffset {1:0.#####} m", c.gameObject.name, c.contactOffset);
				if (m == null)
					sb.Append("   ⛔ NO MATERIAL — running PhysX's built-in 0.6/0.6\n");
				else
					sb.AppendFormat("   μs {0:0.###}  μd {1:0.###}  bounce {2:0.###}  " +
					                "frictionCombine {3}  bounceCombine {4}\n",
						m.staticFriction, m.dynamicFriction, m.bounciness, m.frictionCombine, m.bounceCombine);
			}
		}

		// The pair threshold is what governs contact generation, and it is a property of no single collider.
		var blades = new List<Collider>();
		foreach (var s in FindObjectsByType<SwordBladeSetup>(FindObjectsInactive.Include))
		foreach (var c in s.ContactColliders())
			if (c.gameObject.name.ToLowerInvariant().Contains("blade"))
				blades.Add(c);
		if (blades.Count >= 2)
			sb.AppendFormat("  blade-pair contact GENERATION begins below {0:0.###} mm combined separation " +
			                "({1:0.###} + {2:0.###} mm)\n",
				(blades[0].contactOffset + blades[1].contactOffset) * 1000f,
				blades[0].contactOffset * 1000f, blades[1].contactOffset * 1000f);

		sb.Append("  Not script-readable, verify by hand in DynamicsManager.asset: solver type (expect 0 = PGS), " +
		          "friction type (expect 0 = Patch), enhanced determinism (expect 0 = off).");
		Debug.Log(sb.ToString(), this);
	}

	private static string Path(Transform t)
	{
		var p = "";
		while (t != null)
		{
			p = "/" + t.name + p;
			t = t.parent;
		}

		return p;
	}

	private int Check(string label, float live, float declared)
	{
		var tol = Mathf.Max(1e-6f, Mathf.Abs(declared) * 1e-4f);
		if (Mathf.Abs(live - declared) <= tol) return 0;
		Debug.LogError($"[Declared] ENGINE DISAGREES WITH DECLARATION — {label}: Unity is running " +
		               $"{live:0.#####}, this scene declares {declared:0.#####}.", this);
		return 1;
	}
}
