//#if UNITY_EDITOR
//using UnityEditor;
//#endif
using UnityEngine;

namespace FIMSpace
{
    /// <summary> 
    /// FUVH - Fimpossible Unity Versions Helper. 
    /// It is implementing unity's #ifdefs so you can use methods from this class to avoid dirty code.
    /// </summary>
    public static class FUVH
    {
        /// <summary> Calling FindObjectOfType or FindAnyObjectByType depending on unity version </summary>
        public static T FindSceneObject<T>() where T : UnityEngine.Object
        {
#if UNITY_6000_4_OR_NEWER
            return UnityEngine.Object.FindAnyObjectByType<T>();
#elif UNITY_2022_3_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<T>();
#else
            return UnityEngine.Object.FindObjectOfType<T>();
#endif
        }

        /// <summary> Sets rigidbody2D.isKinematic or rigidbody2D.bodyType Kinematic / Dynamic depending on unity version </summary>
        public static void FsetIsKinematic( this Rigidbody2D rig2D, bool kinematic )
        {
#if UNITY_2022_3_OR_NEWER
            if( kinematic ) rig2D.bodyType = RigidbodyType2D.Kinematic; else rig2D.bodyType = RigidbodyType2D.Dynamic;
#else
            rig2D.isKinematic = kinematic;
#endif
        }

        /// <summary> True if rigidbody2D is kinematic </summary>
        public static bool FisKinematic( this Rigidbody2D rig2D )
        {
#if UNITY_2022_3_OR_NEWER
            if( rig2D.bodyType == RigidbodyType2D.Dynamic ) return false; else return true;
#else
            return rig2D.isKinematic;
#endif
        }

        /// <summary> In unity 6 using GetEntityId().GetHashCode(), in earlier versions using obj.GetInstanceID() </summary>
        public static int FGetObjectIDHash( this UnityEngine.Object obj )
        {
            if( obj == null ) return 0;

#if UNITY_6000_4_OR_NEWER
            return obj.GetEntityId().GetHashCode();
#else
            return obj.GetInstanceID();
#endif
        }

        #region OnOpenAssetAttribute template 

        //[OnOpenAssetAttribute(1)]
        //#if UNITY_6000_4_OR_NEWER
        //        public static bool OpenDesignerScriptableFile(EntityId instanceID, int line)
        //        {
        //            UnityEngine.Object obj = EditorUtility.EntityIdToObject(instanceID);
        //#else
        //        public static bool OpenDesignerScriptableFile(int instanceID, int line)
        //        {
        //            UnityEngine.Object obj = EditorUtility.InstanceIDToObject(instanceID);
        //#endif

        #endregion

    }
}