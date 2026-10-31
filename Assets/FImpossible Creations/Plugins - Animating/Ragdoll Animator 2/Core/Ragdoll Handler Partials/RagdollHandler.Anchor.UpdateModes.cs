using UnityEngine;

namespace FIMSpace.FProceduralAnimation
{
    public partial class RagdollHandler
    {
        public enum ERagdollAnchorUpdateMode { None = -1, Default = 0, PID, Smoothing, Joint }

        /// <summary> The way how anchor bone is being updated to match the keyframed animation positioning. Skipped if using User_ProvideAnchorVelocity. </summary>
        public ERagdollAnchorUpdateMode AnchorUpdateMode = ERagdollAnchorUpdateMode.Default;

        void UpdateAnchorMode( float anchorSpring )
        {
            var anchor = _playmodeAnchorBone;
            anchor.BoneProcessor.UpdateFixedPositionDelta();

            if( AnchorUpdateMode != ERagdollAnchorUpdateMode.Joint && _anchor_jointParent != null )
            {
                anchor.Joint_SetAngularMotionLock( ConfigurableJointMotion.Free );
                anchor.Joint_SetMotionLock( ConfigurableJointMotion.Free );
                GameObject.Destroy( _anchor_jointParent.gameObject );
            }

            if( AnchorUpdateMode == ERagdollAnchorUpdateMode.None ) return; // -------------------------------------------------------- NONE

            // When hips is far away from target position, the power is lower - needs to pull back body rather than precisely match (avoid wobbling towards target position)
            float power = Mathf.LerpUnclamped( 0f, 1f, anchorSpring );
            float invPow = 1f - power; invPow *= invPow; invPow = 1f - invPow;
            float minMultiplier = invPow;

            if( AnchorUpdateMode == ERagdollAnchorUpdateMode.Default ) // -------------------------------------------------------- DEFAULT
            {
                // ::: Rotate Anchor :::
                if( LockAnchorRotation )
                {
                    // Since rigidbody.freezeRotation logic changed in Unity 2023 (without mentioning it in the release notes -_-) rotation needs to be calculated with slerp
                    anchor.GameRigidbody.rotation = Quaternion.Slerp( anchor.GameRigidbody.rotation, anchor.BoneProcessor.AnimatorRotation, Time.fixedDeltaTime * 60f );
                }
                else
                    RagdollHandlerUtilities.AddRigidbodyTorqueToRotateTowards( anchor.GameRigidbody, anchor.BoneProcessor.AnimatorRotation, /*( 0.5f + ( mass * connectedMass ) ) **/ minMultiplier );

                if( Anchor_ApplyUserCustomVelocity() ) return; // If custom velocity applied - skip anchor update mode velocity update

                // ::: Translate Anchor :::

                // Anchor bone spring operation
                RagdollHandlerUtilities.AddAccelerationTowardsWorldPosition( anchor.GameRigidbody, anchor.BoneProcessor.LastMatchingRigidodyOrigin, anchor.BoneProcessor.FixedPositionDelta, ( power * power * power ) * anchorBoneSpringPositionMultiplier, UnscaledTime ? Time.fixedUnscaledDeltaTime : Time.fixedDeltaTime );
                //RagdollHandlerUtilities.AddRigidbodyForceToMoveTowards( anchor.GameRigidbody, anchor.BoneProcessor.LastMatchingRigidodyOrigin, power * anchorBoneSpringPositionMultiplier );
            }
            else if( AnchorUpdateMode == ERagdollAnchorUpdateMode.PID ) // -------------------------------------------------------- PID (Proportional-Integral-Derivative)
            {
                // ::: Rotate Anchor :::
                if( LockAnchorRotation )
                    anchor.GameRigidbody.rotation = Quaternion.Slerp( anchor.GameRigidbody.rotation, anchor.BoneProcessor.AnimatorRotation, Time.fixedDeltaTime * 60f );
                else
                    RagdollHandlerUtilities.AddRigidbodyTorqueToRotateTowards( anchor.GameRigidbody, anchor.BoneProcessor.AnimatorRotation, minMultiplier );

                if( Anchor_ApplyUserCustomVelocity() ) return; // If custom velocity applied - skip anchor update mode velocity update

                if( _anchor_PID == null )
                {
                    _anchor_PID = new HelperPIDController();
                    _anchor_PID.Reset();
                }

                _anchor_PID.responseGain = Mathf.Lerp( 100f, 500f, anchorSpring );
                _anchor_PID.integralGain = Mathf.Lerp( 0.5f, 3f, anchorSpring );
                _anchor_PID.dampGain = Mathf.Lerp( 10f, 30f, anchorSpring );

                // ::: Translate Anchor :::
                Vector3 force = _anchor_PID.Update( anchor.GameRigidbody.position, anchor.BoneProcessor.LastMatchingRigidodyOrigin, anchor.GameRigidbody.linearVelocity, Time.fixedDeltaTime );
                anchor.GameRigidbody.AddForce( force, ForceMode.Acceleration );
            }
            else if( AnchorUpdateMode == ERagdollAnchorUpdateMode.Smoothing ) // -------------------------------------------------------- SMOOTHING
            {
                // ::: Rotate Anchor :::
                if( LockAnchorRotation )
                    anchor.GameRigidbody.rotation = Quaternion.Slerp( anchor.GameRigidbody.rotation, anchor.BoneProcessor.AnimatorRotation, Time.fixedDeltaTime * 60f );
                else
                    RagdollHandlerUtilities.AddRigidbodyTorqueToRotateTowards( anchor.GameRigidbody, anchor.BoneProcessor.AnimatorRotation, minMultiplier );

                if( Anchor_ApplyUserCustomVelocity() ) return; // If custom velocity applied - skip anchor update mode velocity update

                if( Time.unscaledTime - LastStandingModeAtTime < 0.25f ) _anchor_smoothTarget = anchor.BoneProcessor.LastMatchingRigidodyOrigin;

                if( _anchor_wasSmooth )
                    RagdollHandlerUtilities.AddAccelerationTowardsWorldPosition( anchor.GameRigidbody, _anchor_smoothTarget, anchor.BoneProcessor.FixedPositionDelta, ( power * power * power ) * anchorBoneSpringPositionMultiplier, UnscaledTime ? Time.fixedUnscaledDeltaTime : Time.fixedDeltaTime );
            }
            else if( AnchorUpdateMode == ERagdollAnchorUpdateMode.Joint ) // -------------------------------------------------------- USING CONFIGURABLE JOINT
            {
                // ::: Rotate Anchor :::
                if( LockAnchorRotation )
                    anchor.GameRigidbody.rotation = Quaternion.Slerp( anchor.GameRigidbody.rotation, anchor.BoneProcessor.AnimatorRotation, Time.fixedDeltaTime * 60f );
                else
                    RagdollHandlerUtilities.AddRigidbodyTorqueToRotateTowards( anchor.GameRigidbody, anchor.BoneProcessor.AnimatorRotation, minMultiplier );

                if( Anchor_ApplyUserCustomVelocity() ) return; // If custom velocity applied - skip anchor update mode velocity update

                var hipsJoint = anchor.Joint;

                if( _anchor_jointParent == null )
                {
                    var _dummyObject = new GameObject( "AnchorJointParent" );
                    _dummyObject.transform.SetParent( dummyContainer, true );
                    _dummyObject.transform.position = anchor.GameRigidbody.position;
                    _dummyObject.transform.rotation = anchor.GameRigidbody.rotation;

                    _anchor_jointParent = _dummyObject.AddComponent<Rigidbody>();
                    _anchor_jointParent.isKinematic = true;
                    _anchor_jointParent.useGravity = false;

                    hipsJoint.connectedBody = _anchor_jointParent;
                    hipsJoint.angularXMotion = ConfigurableJointMotion.Locked;
                    hipsJoint.angularYMotion = ConfigurableJointMotion.Locked;
                    hipsJoint.angularZMotion = ConfigurableJointMotion.Locked;
                }

                if( hipsJoint.connectedBody == null )
                {
                    _anchor_jointParent.transform.position = anchor.GameRigidbody.position;
                    _anchor_jointParent.transform.rotation = anchor.GameRigidbody.rotation;

                    hipsJoint.connectedBody = _anchor_jointParent;
                    hipsJoint.angularXMotion = ConfigurableJointMotion.Locked;
                    hipsJoint.angularYMotion = ConfigurableJointMotion.Locked;
                    hipsJoint.angularZMotion = ConfigurableJointMotion.Locked;
                }

                float jSpring = 1000f + SpringsValue * anchorSpring; // 1500
                float jDamp = 75f + DampingValue * anchorSpring; // 150

                var drive = new JointDrive
                {
                    positionSpring = jSpring,
                    positionDamper = jDamp,
                    maximumForce = 10000f
                };

                hipsJoint.xDrive = drive;
                hipsJoint.yDrive = drive;
                hipsJoint.zDrive = drive;

                _anchor_jointParent.MovePosition( anchor.BoneProcessor.AnimatorPosition );
                _anchor_jointParent.MoveRotation( anchor.BoneProcessor.AnimatorRotation );

                hipsJoint.targetPosition = Vector3.zero;
                hipsJoint.targetRotation = Quaternion.identity; 
            }
        }

        void Anchor_LateUpdate()
        {
            if( AnchorUpdateMode == ERagdollAnchorUpdateMode.None ) return;

            var anchor = _playmodeAnchorBone;

            if( AnchorUpdateMode == ERagdollAnchorUpdateMode.Smoothing )
            {
                float anchorSpring = AnchorBoneSpring * AnchorBoneSpringMultiplier;

                if( _anchor_wasSmooth == false ) { _anchor_smoothTarget = anchor.BoneProcessor.AnimatorPosition; _anchor_smtVelo = Vector3.zero; _anchor_wasSmooth = true; }

                float duration = Mathf.Lerp( 0.11f, 0.0001f, anchorSpring );

                _anchor_smoothTarget = Vector3.SmoothDamp( _anchor_smoothTarget, anchor.BoneProcessor.LastMatchingRigidodyOrigin, ref _anchor_smtVelo, duration, 100000f, Time.deltaTime );
            }

        }

        /// <summary> Reset for anchor update modes </summary>
        void Anchor_Reset()
        {
            _anchor_wasSmooth = false;
            _providedAnchorVelocity = null;
            _anchor_PID = null;

            if( AnchorUpdateMode == ERagdollAnchorUpdateMode.Joint )
            {
                if( IsInStandingMode == false )
                {
                    var anchor = GetAnchorBoneController;

                    if( anchor.Joint.connectedBody != null )
                    {
                        anchor.Joint.connectedBody = null;
                        var drive = new JointDrive { positionSpring = 0f, positionDamper = 0f, maximumForce = 0f };
                        anchor.Joint.xDrive = drive;
                        anchor.Joint.yDrive = drive;
                        anchor.Joint.zDrive = drive;

                        anchor.Joint_SetAngularMotionLock( ConfigurableJointMotion.Free );
                        anchor.Joint_SetMotionLock( ConfigurableJointMotion.Free );
                    }
                }
            }
            else if( AnchorUpdateMode != ERagdollAnchorUpdateMode.Joint && _anchor_jointParent != null ) GameObject.Destroy( _anchor_jointParent.gameObject );

        }


        /// <summary> Returning true if custom velocity was used. Applies user custom anchor velocity. </summary>
        bool Anchor_ApplyUserCustomVelocity()
        {
            var anchor = _playmodeAnchorBone;

            if( _providedAnchorVelocity != null )
            {
                if( Vector3.Distance( anchor.PhysicalDummyBone.position, anchor.SourceBone.position ) < anchor.BaseColliderSetup.CalculateSize().magnitude * 0.05f )
                {
                    anchor.GameRigidbody.linearVelocity = _providedAnchorVelocity.Value;
                    _providedAnchorVelocity = null;

                    return true; // Don't apply rigidbody forces calculated below
                }
                else
                {
                    _providedAnchorVelocity = null;
                }
            }

            return false;
        }


        Vector3 _anchor_smoothTarget = Vector3.zero;
        Vector3 _anchor_smtVelo = Vector3.zero;
        bool _anchor_wasSmooth = false;
        HelperPIDController _anchor_PID = null;
        Rigidbody _anchor_jointParent = null;

        /// <summary> Basic implementation of Proportional-Integral-Derivative controller </summary>
        class HelperPIDController
        {
            public float responseGain = 100f;
            public float integralGain = 0.5f;
            public float dampGain = 10f;

            private Vector3 _integral;
            private Vector3 _previousError;

            public Vector3 Update( Vector3 current, Vector3 target, Vector3 currentVelocity, float dt )
            {
                Vector3 error = target - current;
                _integral += error * dt;
                Vector3 derivative = ( error - _previousError ) / dt;
                _previousError = error;

                return ( error * responseGain ) + ( _integral * integralGain ) + ( derivative * dampGain );
            }

            public void Reset()
            {
                _integral = Vector3.zero;
                _previousError = Vector3.zero;
            }
        }

    }

}