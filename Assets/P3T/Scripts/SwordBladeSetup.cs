using System.Collections.Generic;
using UnityEngine;

/// <summary>
///     Sets up a sword's blade physics: a Rigidbody on the sword root + a convex MeshCollider on the child
///     "Blade" carrying a high-friction PhysicsMaterial. Lives ON the sword prefab so (a) both blades are
///     provably identical across stations (boundary identity, 04_MeasurementAndFindings §5) and (b) a
///     standalone dummy's sword works without any station protocol object. Originally extracted from the
///     Week 4/5 controller's SetupSword so behaviour was unchanged; that controller is deleted, and this is
///     now the sole writer of a sword's physics.
///
///     Runs on Awake, or call <see cref="Setup"/> explicitly. Exposes the built <see cref="Body"/> +
///     <see cref="Blade"/> for other components to consume.
///     PILOT / DEMO scope: measured thesis data are the BLADE rigidbody poses, never the dummy meshes.
/// </summary>
[DisallowMultipleComponent]
public class SwordBladeSetup : MonoBehaviour
{
    [Header("Rigidbody (sole writer - the Week 4/5 controller's SetupSword is deleted)")]
    public float mass = 1.0f;

    [Header("Centre of mass (measured point of balance)")]
    [Tooltip("Apply the measured point of balance as the rigidbody centre of mass. When false, Unity auto-" +
             "computes it from the colliders — which is WRONG for a sword here, because the only collider is " +
             "the blade: it carries no grip and no pommel, so the auto-COM lands far too far toward the tip " +
             "(measured 181.5 mm from the guard against a true ~93 mm).")]
    public bool applyMeasuredCentreOfMass = true;

    [Tooltip("MARKED centre of mass, mm from the guard along the blade toward the tip.\n\n" +
             "PROVENANCE. Specimen_Measurements.md [removed - see git history] originally recorded '92 cm'/'90.5 cm'; that is a cm/mm " +
             "transcription typo, confirmed by Noah, who was present at measurement. The balance was also " +
             "measured WITHOUT markers, while `mass` here is the MARKED assembly, so the balance point is " +
             "corrected for them: SW-A1 92 -> 93.29 mm, SW-B1 90.5 -> 91.44 mm. That correction divides the " +
             "marker mass equally three ways — a DECLARED ASSUMPTION, not a measurement. Bounded: all 8 g at " +
             "the tip gives 96.6 mm, all at the quillons 91.6 mm, a ~5 mm span against the ±20 mm marker " +
             "floor, so it is not load-bearing.\n\n" +
             "The inertia tensor is deliberately left as Unity's auto value: the marker assemblies were never " +
             "individually weighed and the grip and pommel carry no collider, so there is no measured mass " +
             "distribution to build a tensor from. That is a declared approximation.")]
    public float centreOfMassFromGuardMm = 93.29f;

    /// <summary>Local-space COM actually applied, for auditing. Zero when <see cref="applyMeasuredCentreOfMass"/> is off.</summary>
    public Vector3 EffectiveCentreOfMass { get; private set; }

    [Tooltip("Gravity for this sword. THIS FIELD IS NOW LIVE EVERYWHERE, including on stations. It used to be " +
             "dead on a station sword because the Week 4/5 controller's ApplyContactMaintenance() wrote " +
             "!zeroGravityBlades at Start and on every ARM and always won — that controller was deactivated on " +
             "2026-08-21 and deleted on 2026-08-24, so nothing overrides this any more. Measured 2026-08-21: " +
             "the field is false, and the Rigidbody is false, on ALL 33 swords that have one — stations, bay " +
             "dummies and free-play grabbables alike. So a dropped free-play sword does not fall. That is a " +
             "consequence of the station-era serialization, not a decision anyone made for free play.")]
    public bool useGravity = true;

    [Header("Blade collider + contact material")]
    public bool addBladeCollider = true;

    public string bladeChildName = "Blade";

    // FROZEN 2026-08-22 at 0.45 static / 0.35 dynamic, combine Maximum. See
    // Workspace/Week7/FREEZE_BladeFriction_Declaration.md for the ratified declaration and its reasoning.
    //
    // WHY 0.45 AND NOT THE OLD 0.3. The declared value here is a SINGLE SCALAR; the quantity is a PAIR, and
    // BladeGrip.physicMaterial holds it (0.45/0.35). BuildRuntimeMaterial seeds the clone from that asset and
    // then overwrites this field with the asset static value, by design - so the asset always won and the 0.3
    // declared here NEVER APPLIED. That single disagreement was the sole cause of 35 [Tuner] STALE DECLARATION
    // errors per play, one per instance. The asset is the operating point; this now records it rather than
    // contradicting it.
    //
    // The us:ud split is real and load-bearing: ApplyMaterial preserves the asset ratio (0.778) instead of
    // collapsing both coefficients onto one scalar, because us > ud is what gives the model a depinning
    // threshold at all (Baumberger & Caroli 2006, Week1/ReferencePDFs/Proposal/).
    [Tunable("Contact material", 0f, 3f, "Blade friction",
        "FROZEN nominal operating point, not a measurement: static 0.45 / dynamic 0.35, combine Maximum. " +
        "No measured blade-on-blade value exists and the compiled handbook sources disagree by ~5x, so this " +
        "is declared, not asserted. It is anchored to the one reproducible physical observation - flat-on-flat " +
        "always slips - which it reproduced at every pair across that whole 5x span. mu_s > mu_d holds, so the " +
        "depinning threshold exists, and the slider moves magnitude while the asset keeps the ratio. " +
        "See Workspace/Week7/FREEZE_BladeFriction_Declaration.md.",
        0, Disposition.Exposed, 0.45f)]
    public float friction = 0.45f;

    [Tunable("Contact material", 0f, 1f, "Blade bounciness",
        "Physics-material restitution. Expected ~0 for steel; exposed so a reviewer can confirm it is not a factor.")]
    public float bounce = 0f;

    [Header("Contact robustness (declared/frozen per 02_AttachmentSpec checklist + 04 §6)")]
    [Tooltip("Both blades are DYNAMIC; ContinuousDynamic does not sweep dynamic-vs-dynamic. ContinuousSpeculative is Unity's recommended mode for thin fast dynamic contact. NOTE (Session 7): CCD is linear-only and does NOT stop the flat-on-flat ROTATIONAL tunnel - the real fix was BladeContactStabilizer (contact-normal pinning) on the station root, but that script was DELETED 2026-08-21 as unused - it was disabled on every station and never ran. This mode is still the better baseline.")]
    public CollisionDetectionMode collisionDetection = CollisionDetectionMode.ContinuousSpeculative;

    // RETIRED 2026-08-21 (Noah): `bladeContactOffset` and its [Tunable] are gone. It was declared when
    // this rig had no extra ragdoll generators, and it is no longer considered worth tuning.
    //
    // EVERY contact collider now simply inherits Physics.defaultContactOffset and nothing here writes the
    // value. One path, one value, project-wide. Anyone wanting to test other offsets sets the PROJECT
    // default, which applies to the generated proxies as well.
    //
    // WHY IT HAD TO GO RATHER THAN BE FIXED. The declared value never reached the collider that actually
    // collides. ContactColliders() enumerates this sword's authored hierarchy, but Ragdoll Animator hosts a
    // worn sword on GENERATED proxy colliders, and those were never in the list -- so a station sword ran
    // Unity's default 0.01 while this component declared 0.005, and the declaration described a value that
    // was not in force. Same class of divergence as the CoM/inertia drift fixed in MBC 0e783ef.
    //
    // Measured before removal: 91 serialized colliders at 0.005 and 13 at 0.01, with every runtime proxy at
    // 0.01. Normalising to the project default therefore CHANGES NOTHING for station swords, whose proxies
    // were already at 0.01, and DOES change the non-proxy swords (XRI/freeplay) from 0.005 to 0.01.
    //
    // See PHYSICS_SETTINGS_DECLARATION F6 for the retirement record and the prior sensitivity screen.

    [Tooltip("Rigidbody solver POSITION iterations for the blade. Pinned to the project default 25 as part of the 2026-08-09 freeze. It was 16 with nothing recording why; heterogeneous solver quality across the bodies of one apparatus is indefensible noise, not a setting. See VERIFY_ProposedFreeze.md [removed - see git history] section 4.")]
    public int solverIterations = 25;

    [Tooltip("Rigidbody solver VELOCITY iterations for the blade. Pinned to the project default 15 by the 2026-08-09 freeze. This field did not exist before that: the blade declared a position count and silently inherited its velocity count, so the pair could never be declared together — which is exactly what the standing retraction is about. Velocity iterations are NOT a passenger (25/15 holds contact where 25/25 crosses at the same rate), so the blade must name its own.")]
    public int solverVelocityIterations = 15;

    [Tooltip("DECLARED baseline contact material. Assign BladeGrip.physicMaterial. This is the citable record and is NEVER mutated: at startup it is cloned once, the clone is assigned to every non-trigger collider on this sword, and the runtime sliders edit only the clone. Assigning an asset here means SEED THE RUNTIME MATERIAL FROM THIS, not DISABLE TUNING. Leaving it null leaves the contact material UNDECLARED and Verify() reports it.")]
    [UnityEngine.Serialization.FormerlySerializedAs("sharedBladeMaterial")]
    public PhysicsMaterial physicsMaterial;

    /// <summary>
    ///     The working copy the sliders edit. Instantiated ONCE from <see cref="physicsMaterial" /> and assigned
    ///     to every blade collider through <c>sharedMaterial</c>.
    ///     <para>
    ///         <b>Why one explicit clone rather than <c>Collider.material</c>.</b> Reading
    ///         <c>Collider.material</c> silently duplicates the shared material <i>for that collider</i>, so a
    ///         blade with more than one collider would end up with several independent runtime copies and a
    ///         slider would move only one of them. Assigning our own single clone through
    ///         <c>sharedMaterial</c> keeps every collider on the blade synchronised, and — unlike writing to the
    ///         asset directly — never mutates the project asset that other colliders also reference.
    ///     </para>
    /// </summary>
    private PhysicsMaterial _runtimeMaterial;

    /// <summary>
    ///     Frozen snapshot of the declared asset, taken before the clone is ever touched. Verification compares
    ///     against this rather than against the asset, so a check stays trustworthy even if someone edits the
    ///     asset mid-session.
    /// </summary>
    private PhysicsMaterialValues _declaredRecord;

    /// <summary>Plain value copy of a material — no Unity object, so it cannot be mutated behind our back.</summary>
    public struct PhysicsMaterialValues
    {
        public float StaticFriction, DynamicFriction, Bounciness;
        public PhysicsMaterialCombine FrictionCombine, BounceCombine;
        public bool Captured;

        public static PhysicsMaterialValues From(PhysicsMaterial m)
        {
            return new PhysicsMaterialValues
            {
                StaticFriction = m.staticFriction, DynamicFriction = m.dynamicFriction,
                Bounciness = m.bounciness, FrictionCombine = m.frictionCombine,
                BounceCombine = m.bounceCombine, Captured = true
            };
        }

        public override string ToString()
        {
            return $"mu_s {StaticFriction:0.###} / mu_d {DynamicFriction:0.###} / bounce {Bounciness:0.###} " +
                   $"/ {FrictionCombine}";
        }
    }

    /// <summary>The declared baseline as captured at startup. <c>Captured</c> is false if no asset was assigned.</summary>
    public PhysicsMaterialValues DeclaredRecord => _declaredRecord;

    /// <summary>The live working copy. Never the project asset.</summary>
    public PhysicsMaterial RuntimeMaterial => _runtimeMaterial;

    public Rigidbody Body { get; private set; }
    public Collider Blade { get; private set; }

    private void Awake()
    {
        Setup();
    }

    /// <summary>
    ///     Keep the SERIALIZED collider in step with the authored fields while in the editor, so the inspector,
    ///     the gizmos and the runtime all report one number.
    ///     <para>
    ///         <b>Why this exists.</b> <c>Collider.contactOffset</c> is itself a serialized value, so there
    ///         were once two persisted sources of truth for one physical quantity: the collider said 0.01
    ///         while the now-retired <c>bladeContactOffset</c> field said 0.005, with the field silently
    ///         winning at <see cref="Setup" />. Every edit-mode inspection — including a collider-skin
    ///         gizmo — read the wrong one, and it already produced one false measurement this project had to
    ///         withdraw.
    ///     </para>
    ///     <para>
    ///         <b>contactOffset is no longer one of the fields this syncs.</b> It is the project default on
    ///         every collider and nothing overrides it, so there is only one source of truth left and
    ///         nothing to keep in step. The remaining fields here are still synced.
    ///     </para>
    ///     <para>
    ///         <b>Runtime is deliberately untouched.</b> This returns immediately in play mode, so
    ///         <c>SweepRunner</c> (DELETED 2026-08-21) kept full authority over the live value for a sweep: runtime
    ///         overrides remain a sweep mechanism, not a permanent second truth.
    ///     </para>
    /// </summary>
    private void OnValidate()
    {
        if (Application.isPlaying) return;
        SyncSerialized();
    }

    /// <summary>
    ///     Edit-mode only: push every declared field onto the component it configures, so the inspector and the
    ///     runtime can never disagree.
    ///     <para>
    ///         <b>Why this is the fix rather than "stop writing at runtime".</b> <see cref="Setup" /> has to
    ///         apply these on Awake — a prefab instantiated at runtime has no other chance to be configured. The
    ///         problem was never the write; it was that the write happened <i>only</i> at runtime, so the
    ///         serialized Rigidbody said one thing, this component said another, and the simulation quietly used
    ///         the second. Selecting the object told a reviewer the wrong number. Syncing in edit mode collapses
    ///         the two sources of truth into one: by the time play begins they already agree, and the runtime
    ///         write becomes a confirmation instead of an override.
    ///     </para>
    ///     <para>
    ///         The blade contact material is deliberately NOT synced here — it is a runtime clone by design (see
    ///         <see cref="BuildRuntimeMaterial" />), and the declared record for it is the
    ///         <see cref="physicsMaterial" /> asset, which is directly selectable.
    ///     </para>
    /// </summary>
    public void SyncSerialized()
    {
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            if (!Mathf.Approximately(rb.mass, mass)) rb.mass = mass;
            if (rb.collisionDetectionMode != collisionDetection) rb.collisionDetectionMode = collisionDetection;
            if (rb.solverIterations != solverIterations) rb.solverIterations = solverIterations;
            if (rb.solverVelocityIterations != solverVelocityIterations)
                rb.solverVelocityIterations = solverVelocityIterations;
            // useGravity is intentionally not synced back from the Rigidbody. The reason it was not synced --
            // that the Week 4/5 controller overwrote it on every ARM -- is gone with that controller, but
            // syncing an authored declaration FROM the runtime body is the wrong direction regardless: it would
            // let whatever the body happens to hold overwrite what the sword declares.
        }

        // contactOffset is deliberately NOT written here any more. The mismatch this used to correct -- a
        // crossguard on Unity's default while the blade ran a declared override, generating contact at the
        // SUM and so firing a crossguard-vs-blade pair earlier than a blade pair -- cannot arise once
        // nothing overrides the default at all. Uniformity by not intervening rather than by correcting.
    }

    /// <summary>
    ///     Compare what this component declares against what the engine is actually running. Returns false only
    ///     when something genuinely diverged; <paramref name="report" /> names each difference.
    ///     <para>
    ///         This is the per-object half of the scripted comparison that
    ///         <see cref="DeclaredPhysicsSettings" /> does for project-wide settings. Between them, no declared
    ///         value in the apparatus can silently drift from the value in effect.
    ///     </para>
    /// </summary>
    public bool VerifyAgainstLive(out string report)
    {
        var problems = new List<string>();
        var rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            problems.Add("no Rigidbody");
        }
        else
        {
            if (!Approx(rb.mass, mass)) problems.Add($"mass live {rb.mass} vs declared {mass}");
            if (rb.collisionDetectionMode != collisionDetection)
                problems.Add($"collisionDetection live {rb.collisionDetectionMode} vs declared {collisionDetection}");
            if (rb.solverIterations != solverIterations)
                problems.Add($"solverIterations live {rb.solverIterations} vs declared {solverIterations}");
            if (rb.solverVelocityIterations != solverVelocityIterations)
                problems.Add(
                    $"solverVelocityIterations live {rb.solverVelocityIterations} vs declared {solverVelocityIterations}");
            if (applyMeasuredCentreOfMass)
            {
                var wantMm = centreOfMassFromGuardMm;
                var haveMm = -rb.centerOfMass.x * 1000f;
                if (Mathf.Abs(haveMm - wantMm) > 0.5f)
                    problems.Add($"centreOfMass live {haveMm:0.##} mm vs declared {wantMm:0.##} mm");
            }
        }

        // every non-trigger collider must be on this sword's ONE runtime clone, at the declared offset --
        // except the QuickDirty comparator's two derived hulls, whose whole purpose is to carry one fixed
        // material each. They are still required to carry SOME material and the declared contact offset;
        // they are simply not expected to be on this sword's clone. See BuildRuntimeMaterial.
        var quickDirty = GetComponent<RqQuickDirtyBlade>();
        var cols = ContactColliders();
        if (cols.Length == 0) problems.Add("no non-trigger colliders");
        foreach (var c in cols)
        {
            bool derivedHull = quickDirty != null && (c == quickDirty.EdgeHull || c == quickDirty.FlatHull);

            // Still checked, against the PROJECT default rather than a per-sword field, so a stray override
            // reintroduced by hand or by a generator is still caught.
            if (!Approx(c.contactOffset, Physics.defaultContactOffset))
                problems.Add($"{c.gameObject.name}.contactOffset live {c.contactOffset} vs project default " +
                             $"{Physics.defaultContactOffset} — nothing should override it");
            if (c.sharedMaterial == null)
                problems.Add($"{c.gameObject.name} has NO physics material — it is running PhysX's built-in 0.6/0.6");
            else if (!derivedHull && Application.isPlaying && _runtimeMaterial != null &&
                     c.sharedMaterial != _runtimeMaterial)
                problems.Add($"{c.gameObject.name} is not on this sword's runtime material ({c.sharedMaterial.name})");
        }

        string matDetail;
        if (!MatchesDeclared(out matDetail)) problems.Add("material: " + matDetail);

        report = problems.Count == 0
            ? "OK"
            : string.Join("; ", problems.ToArray());
        return problems.Count == 0;
    }

    private static bool Approx(float a, float b)
    {
        return Mathf.Abs(a - b) <= Mathf.Max(1e-6f, Mathf.Abs(b) * 1e-4f);
    }

    [ContextMenu("Setup blade physics")]
    public void Setup()
    {
        var rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.useGravity = useGravity;
        rb.linearDamping = 0.05f;
        rb.angularDamping = 0.05f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = collisionDetection;
        rb.solverIterations = solverIterations;
        rb.solverVelocityIterations = solverVelocityIterations;
        Body = rb;

        // Measured point of balance -> centre of mass. Must come AFTER mass is set.
        //
        // UNIT TRAP (cost the other branch a 4.09 m error, caught only by empirical readback):
        // Rigidbody.centerOfMass is expressed in the body's local ROTATIONAL frame but in METRES, not in
        // scaled local units. The V2 swords are unit-scale so the two coincide today — but the pre-V2 swords
        // carried a 0.0253 root scale, where assuming local units would place the COM ~4 m from the guard.
        // The conversion is written out explicitly and the achieved distance is asserted below, so a future
        // rescale surfaces as an error rather than as grossly wrong rotational dynamics that read as a result.
        if (applyMeasuredCentreOfMass)
        {
            // METRES, not scaled local units — so no lossyScale factor belongs here. The sword root origin is
            // the guard / grip junction and the blade runs along local -X.
            rb.centerOfMass = new Vector3(-centreOfMassFromGuardMm * 0.001f, 0f, 0f);
            EffectiveCentreOfMass = rb.centerOfMass;

            // Assert on the WORLD distance, not the local value: a local round-trip would still pass under a
            // rescaled root, which is exactly the case this guard exists to catch.
            var achievedMm = Vector3.Distance(rb.worldCenterOfMass, transform.position) * 1000f;
            if (Mathf.Abs(achievedMm - centreOfMassFromGuardMm) > 0.5f)
                Debug.LogError(string.Format(
                    "[SwordBladeSetup] {0}: centre of mass landed {1:F3} mm from the guard in WORLD space, " +
                    "declared {2:F3} mm (root lossyScale {3}). Check the Rigidbody.centerOfMass unit " +
                    "convention before trusting any rotational result.",
                    name, achievedMm, centreOfMassFromGuardMm, transform.lossyScale), this);
        }

        if (!addBladeCollider) return;
        var bt = transform.Find(bladeChildName);
        if (bt == null) return;
        var col = bt.GetComponent<Collider>();
        if (col == null)
        {
            var mf = bt.GetComponent<MeshFilter>();
            var mc = bt.gameObject.AddComponent<MeshCollider>();
            mc.convex = true;
            if (mf != null) mc.sharedMesh = mf.sharedMesh;
            col = mc;
        }

        Blade = col;
        // No contactOffset write: a freshly added collider already carries Physics.defaultContactOffset,
        // which is now the single value for the whole project.

        BuildRuntimeMaterial();
        FreezeInertiaExcludingMarkers(rb);
    }

    /// <summary>The inertia tensor actually in force, for auditing. Zero until <see cref="Setup" /> has run.</summary>
    public Vector3 EffectiveInertiaTensor { get; private set; }

    [Header("Inertia (declared — see PHYSICS_SETTINGS_DECLARATION.md [removed - see git history] F9/F10)")]
    [Tooltip("DECLARED inertia tensor, kg·m², applied on Setup. Computed from the MASS-BEARING STEEL ONLY — " +
             "blade and guard — with the tracking markers excluded.\n\n" +
             "WHY IT MUST BE DECLARED. Unity's auto tensor distributes the rigidbody's mass over its colliders " +
             "BY VOLUME. A 40 mm marker sphere is 33,510 mm3 against a blade of roughly 183,000 mm3, so three " +
             "markers are ~55% of the blade's volume and the auto tensor hands them about 35% of the sword's " +
             "mass — one of them 1010 mm out at the tip. The real markers weigh 8 g of 1582 g, i.e. 0.5%. " +
             "Measured directly: adding the marker colliders moved the auto tensor +218% roll / +85% " +
             "transverse on blade A and +286% / +140% on blade B. That is a different sword, not a " +
             "perturbation.\n\n" +
             "Rotation coupling gain is the primary outcome measure and it is measured THROUGH this number, so " +
             "it is declared rather than left emergent. Same standing approximation as before the markers " +
             "existed: the grip and pommel carry no colliders, so the steel-only tensor is itself an " +
             "approximation — a declared one.\n\n" +
             "Zero disables the override and raises a console error rather than silently reverting to auto.")]
    public Vector3 declaredInertiaTensor;

    /// <summary>
    ///     Compute the inertia tensor from the **mass-bearing steel only** — blade and guard — and freeze it, so
    ///     the tracking markers contribute contact without contributing inertia.
    ///     <para>
    ///         <b>Why this is necessary and not a nicety.</b> Unity's auto tensor distributes the rigidbody's
    ///         mass over its colliders <i>by volume</i>. A 40 mm marker sphere is 33 510 mm³ against a blade of
    ///         roughly 183 000 mm³, so the three markers are ~55 % of the blade's volume and the auto tensor
    ///         hands them about <b>35 % of the sword's mass</b> — one of them 1010 mm out at the tip. The real
    ///         markers weigh <b>8 g of 1582 g, 0.5 %</b>. Measured directly: adding the sphere colliders moved
    ///         the tensor by <b>+218 % roll / +85 % transverse</b> on blade A and <b>+286 % / +140 %</b> on
    ///         blade B. That is not a perturbation, it is a different sword.
    ///     </para>
    ///     <para>
    ///         So the markers are excluded from the tensor by construction: disable them, let PhysX recompute
    ///         from the remaining colliders, re-enable, and freeze the result. The blade and guard still carry
    ///         the whole mass, which is the same declared approximation as before the markers existed —
    ///         <see cref="centreOfMassFromGuardMm" />'s tooltip already records that the grip and pommel have no
    ///         colliders and so no measured distribution to build a tensor from.
    ///     </para>
    ///     <para>
    ///         ⚠️ This makes the tensor a <b>declared</b> quantity rather than an emergent one, which is what
    ///         <c>PHYSICS_SETTINGS_DECLARATION.md [removed - see git history]</c> F9 concluded it had to become: a 2.3 mm change in an
    ///         unmeasured guard dimension had already moved it 39 % on the axis that governs the primary outcome
    ///         measure. Rotation gain is measured through this number.
    ///     </para>
    /// </summary>
    private void FreezeInertiaExcludingMarkers(Rigidbody rb)
    {
        if (rb == null) return;

        // DECLARED, not computed. The first implementation disabled the marker colliders, called
        // ResetInertiaTensor() and re-enabled them — and it silently did nothing, because a collider's
        // enabled-state change is not reflected in the rigidbody's mass distribution within the same frame.
        // Measured after that "fix": still +217% roll. A declared value cannot fail that way, and F9 had
        // already concluded the tensor has to become declared rather than emergent.
        if (declaredInertiaTensor.sqrMagnitude > 0f)
        {
            rb.inertiaTensor = declaredInertiaTensor;
            EffectiveInertiaTensor = declaredInertiaTensor;
            return;
        }

        EffectiveInertiaTensor = rb.inertiaTensor;
        Debug.LogError($"[SwordBladeSetup] {name}: declaredInertiaTensor is unset, so the inertia tensor is " +
                       "Unity's AUTO value — which distributes mass over colliders BY VOLUME and therefore " +
                       "hands the three 40 mm markers about 35% of this sword's mass, one of them at the tip. " +
                       $"Measured effect: +218% roll. Live value {rb.inertiaTensor}. Rotation gain is measured " +
                       "through this number — declare it. See PHYSICS_SETTINGS_DECLARATION.md [removed - see git history] F9/F10.", this);
    }

    /// <summary>
    ///     Declared asset → one runtime clone → runtime overrides. The three roles are deliberately separate:
    ///     <list type="bullet">
    ///         <item><see cref="physicsMaterial" /> — the declared baseline. Inspector-assigned, citable, and
    ///             <b>never mutated</b>. This is the value a reviewer reads by selecting the asset.</item>
    ///         <item><see cref="_declaredRecord" /> — a frozen value snapshot of that asset, so verification
    ///             stays trustworthy even if the asset is edited mid-session.</item>
    ///         <item><see cref="_runtimeMaterial" /> — the working copy the sliders edit, shared by every
    ///             collider on this blade.</item>
    ///     </list>
    ///     <para>
    ///         <b>An assigned asset means "seed the runtime material from this", not "disable tuning."</b> The
    ///         previous implementation early-returned out of <see cref="ApplyMaterial" /> whenever an asset was
    ///         present, which would have silently disabled the reviewer-drivable friction slider the moment the
    ///         declared asset was wired up — removing the one thing Bill asked for in exchange for the one thing
    ///         Noah asked for. Both are available; they are just different objects.
    ///     </para>
    ///     <para>
    ///         With no asset assigned the clone is seeded from the serialized <see cref="friction" /> /
    ///         <see cref="bounce" /> fields instead, and <see cref="_declaredRecord" /> is left uncaptured so
    ///         verification can report the apparatus as undeclared rather than silently passing.
    ///     </para>
    /// </summary>
    private void BuildRuntimeMaterial()
    {
        if (_runtimeMaterial == null)
        {
            if (physicsMaterial != null)
            {
                _declaredRecord = PhysicsMaterialValues.From(physicsMaterial);
                _runtimeMaterial = Instantiate(physicsMaterial);
                _runtimeMaterial.name = physicsMaterial.name + " (runtime)";
                // keep the authored fields in step with the declared asset, so the sliders start where the
                // declaration says they start rather than at whatever the prefab happened to hold
                friction = _declaredRecord.StaticFriction;
                bounce = _declaredRecord.Bounciness;
            }
            else
            {
                _declaredRecord = default; // Captured stays false: nothing was declared
                _runtimeMaterial = MakeMaterial();
                _runtimeMaterial.name = "BladeGrip (runtime, UNDECLARED)";
            }

            // a runtime clone must never be written into the scene or the asset database
            _runtimeMaterial.hideFlags = HideFlags.DontSave;
        }

        // EVERY collider on the whole sword points at the SAME clone — not just the blade child. Each sword
        // carries at least a Blade and a Crossguard, and walking only the blade left the crossguard on a null
        // material, i.e. silently on PhysX's built-in 0.6/0.6 while the blade ran the declared 0.3.
        //
        // Deliberately sharedMaterial and not Collider.material: reading .material duplicates the material FOR
        // THAT COLLIDER, so a two-collider sword would end up with two independent runtime copies and a slider
        // would move only one of them.
        // The QuickDirty comparator is the one exception, and it is not an exception to the rule above so
        // much as a different object: its two DERIVED hulls exist only to carry one fixed material each, and
        // that pairing IS the treatment being compared. Overwriting them here would silently turn the whole
        // QuickDirty arm back into the baseline while still reporting three live shapes. The blade's own
        // cooked hull is untouched by this and stays on the declared material with everything else, so a
        // baseline sword -- which carries no RqQuickDirtyBlade -- behaves exactly as before.
        var quickDirty = GetComponent<RqQuickDirtyBlade>();

        foreach (var c in GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger) continue; // measurement/probe volumes are not contact surfaces
            if (quickDirty != null && (c == quickDirty.EdgeHull || c == quickDirty.FlatHull)) continue;
            c.sharedMaterial = _runtimeMaterial;
        }
    }

    /// <summary>
    ///     Every non-trigger collider on this sword, for verification. Blade + Crossguard at minimum.
    /// </summary>
    public Collider[] ContactColliders()
    {
        var all = GetComponentsInChildren<Collider>(true);
        var keep = new List<Collider>();
        foreach (var c in all)
            if (!c.isTrigger)
                keep.Add(c);
        return keep.ToArray();
    }

    private void OnDestroy()
    {
        // the clone is ours; nothing else references it
        if (_runtimeMaterial != null) Destroy(_runtimeMaterial);
    }

    /// <summary>
    ///     Does the live working copy still match the declared asset it was seeded from? Returns false only when
    ///     something actually diverged, so a caller can report drift rather than discover it in a draft. A
    ///     deliberate slider sweep will report false — that is correct, and the caller decides whether it matters.
    /// </summary>
    public bool MatchesDeclared(out string detail)
    {
        if (!_declaredRecord.Captured)
        {
            detail = "no declared PhysicsMaterial asset is assigned — the contact material is UNDECLARED, and " +
                     "anything bypassing this component gets PhysX's built-in 0.6/0.6";
            return false;
        }

        if (_runtimeMaterial == null)
        {
            detail = "runtime material has not been built yet";
            return false;
        }

        var live = PhysicsMaterialValues.From(_runtimeMaterial);
        const float tol = 1e-5f;
        var same = Mathf.Abs(live.StaticFriction - _declaredRecord.StaticFriction) <= tol
                   && Mathf.Abs(live.DynamicFriction - _declaredRecord.DynamicFriction) <= tol
                   && Mathf.Abs(live.Bounciness - _declaredRecord.Bounciness) <= tol
                   && live.FrictionCombine == _declaredRecord.FrictionCombine
                   && live.BounceCombine == _declaredRecord.BounceCombine;
        detail = same ? "matches declared: " + _declaredRecord
            : "DIVERGED — declared [" + _declaredRecord + "]  live [" + live + "]";
        return same;
    }

    /// <summary>
    ///     Push the current friction/bounce onto the <b>runtime clone</b>. Needed because those fields are
    ///     runtime-tunable: writing the field alone never reaches PhysX.
    ///     <para>
    ///         <b>Safe by construction, and it no longer opts out.</b> It writes to
    ///         <see cref="_runtimeMaterial" /> — a clone owned by this component and flagged
    ///         <c>HideFlags.DontSave</c> — so the declared <see cref="physicsMaterial" /> asset is never
    ///         mutated no matter how far a reviewer drags a slider. The earlier version early-returned whenever
    ///         an asset was assigned, which would have disabled runtime tuning the moment the declared asset was
    ///         wired up.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>mu_s and mu_d are still driven from ONE scalar here.</b> That is finding F1: with them equal
    ///         the model has no depinning threshold and cannot represent stick-slip, so a null bind result is
    ///         confounded. Splitting them is a Gate B prerequisite (TODO #12) and this is the method that has to
    ///         change.
    ///     </para>
    /// </summary>
    public void ApplyMaterial()
    {
        if (_runtimeMaterial == null) return;

        // PRESERVE THE DECLARED us:ud RATIO. This used to write `friction` into BOTH coefficients, which
        // collapsed static onto dynamic and DESTROYED THE DEPINNING THRESHOLD the moment anyone touched the
        // Tuner slider — silently, mid-session, after startup had correctly seeded the split from the asset.
        //
        // Why that matters rather than being tidiness: Baumberger & Caroli (2006), in the verified corpus at
        // Week1/ReferencePDFs/Proposal/, define us as a threshold below which no sliding occurs, and it exists
        // only while us > ud. With us == ud there is no stick-slip and no depinning, so a null bind result is
        // uninterpretable rather than informative. A slider must not be able to remove the mechanism under
        // measurement.
        //
        // So the slider now moves MAGNITUDE and the declared asset keeps authority over the RATIO. That also
        // matches the recommended reporting form in the Week 6 friction research: sweep the ratio deliberately,
        // do not change it by accident.
        //
        // Fallback: with no declared asset the ratio is 1 and the old collapsing behaviour returns. That case
        // is already reported separately as an undeclared contact surface, so it stays visible rather than
        // being papered over here.
        var ratio = 1f;
        if (_declaredRecord.Captured && _declaredRecord.StaticFriction > 1e-6f)
            ratio = _declaredRecord.DynamicFriction / _declaredRecord.StaticFriction;

        _runtimeMaterial.staticFriction = friction;
        _runtimeMaterial.dynamicFriction = friction * ratio;
        _runtimeMaterial.bounciness = bounce;
    }

    /// <summary>Put the runtime clone back to the declared asset's values, discarding any slider changes.</summary>
    [ContextMenu("Reset material to declared")]
    public void ResetMaterialToDeclared()
    {
        if (_runtimeMaterial == null || !_declaredRecord.Captured) return;
        _runtimeMaterial.staticFriction = _declaredRecord.StaticFriction;
        _runtimeMaterial.dynamicFriction = _declaredRecord.DynamicFriction;
        _runtimeMaterial.bounciness = _declaredRecord.Bounciness;
        _runtimeMaterial.frictionCombine = _declaredRecord.FrictionCombine;
        _runtimeMaterial.bounceCombine = _declaredRecord.BounceCombine;
        friction = _declaredRecord.StaticFriction;
        bounce = _declaredRecord.Bounciness;
    }

    // ApplyContactOffset() removed with the field it applied. Nothing writes Collider.contactOffset now.


    private PhysicsMaterial MakeMaterial()
    {
        return new PhysicsMaterial("BladeGrip")
        {
            staticFriction = friction, dynamicFriction = friction,
            frictionCombine = PhysicsMaterialCombine.Maximum, bounciness = bounce
        };
    }
}
