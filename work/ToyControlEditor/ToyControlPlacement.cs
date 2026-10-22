#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace ShockWave.ToyControl
{
    public sealed partial class ToyControlSetupWindow
    {
        [Serializable] internal sealed class HandPose
        { public bool captured; public Vector3 position,rotation; }
        [Serializable] internal sealed class HandPlacements
        { public HandPose inHand=new HandPose(),backHand=new HandPose(); }
        [SerializeField] private HandPlacements handPlacements=new HandPlacements();
        [SerializeField] private bool placementEdited;
        private void PlacementGUI()
        {
            EditorGUILayout.LabelField("Right-hand placements",EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("In hand and Back of hand are separate radial toggles. For an exact fit, pause Play mode, adjust the panel as desired and capture its pose below. Exit Play mode before Update. Capture reads the current panel; it does not move or save the avatar.",MessageType.Info);
            using(new EditorGUI.DisabledScope(avatar==null))
            {
                using(new EditorGUILayout.HorizontalScope())
                {
                    if(GUILayout.Button("Capture In hand")) CapturePlacement(false);
                    if(GUILayout.Button("Capture Back of hand")) CapturePlacement(true);
                }
                if(GUILayout.Button("Load saved placements from avatar")) LoadPlacements();
            }
            EditorGUILayout.LabelField("In hand",handPlacements.inHand.captured ? "Captured" : "Automatic hand alignment");
            EditorGUILayout.LabelField("Back of hand",handPlacements.backHand.captured ? "Captured" : "Automatic hand alignment");
            if(GUILayout.Button("Use automatic placements")) {handPlacements=new HandPlacements();placementEdited=true;}
        }
        private void CapturePlacement(bool back)
        {
            try
            {
                var host=Existing(avatar);var panel=host==null ? null : host.transform.Find("Panel");
                var animator=avatar.GetComponent<Animator>();var hand=animator!=null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
                if(panel==null || hand==null) throw new InvalidOperationException("Choose the avatar with the panel and a humanoid right hand. Capture the visible Panel pose after adjusting it.");
                var pose=CapturePose(hand,panel);
                if(back) handPlacements.backHand=pose;else handPlacements.inHand=pose;
                placementEdited=true;
                ShowNotification(new GUIContent((back ? "Back of hand" : "In hand")+" captured. Exit Play mode, then Update."));
            }
            catch(Exception ex) {EditorUtility.DisplayDialog("Placement not captured",ex.Message,"OK");}
        }
        internal static HandPose CapturePose(Transform hand,Transform panel)
        {
            // World metres relative to wrist rotation: independent of avatar root
            // position, arbitrary bone axes and positive parent scale.
            return new HandPose {captured=true,position=Quaternion.Inverse(hand.rotation)*(panel.position-hand.position),rotation=(Quaternion.Inverse(hand.rotation)*panel.rotation).eulerAngles};
        }
        private void LoadPlacements()
        {
            handPlacements=new HandPlacements();placementEdited=false;
            var host=Existing(avatar);var board=host==null ? null : host.transform.Find("Panel/Size/Board");
            var renderer=board==null ? null : board.GetComponent<MeshRenderer>();
            var material=renderer==null ? "" : AssetDatabase.GetAssetPath(renderer.sharedMaterial);
            if(!material.StartsWith(AssetRoot+"/",StringComparison.Ordinal)) return;
            var path=Path.GetDirectoryName(material)+"/Placement.json";
            if(File.Exists(path)) handPlacements=JsonUtility.FromJson<HandPlacements>(File.ReadAllText(path)) ?? new HandPlacements();
        }
        internal static Quaternion HandFrame(Transform wrist,Transform proximal,Transform distal,Transform little,Vector3 avatarUp)
        {
            if(proximal==null || distal==null || (distal.position-proximal.position).sqrMagnitude<.000001f) throw new InvalidOperationException("A humanoid right index finger is required for hand alignment.");
            var forward=(distal.position-proximal.position).normalized;
            // Up points toward the index/thumb side, so a finger pistol holds
            // the panel upright. The right axis then points out of the hand's back.
            var up=Vector3.ProjectOnPlane(little!=null ? proximal.position-little.position : avatarUp,forward);
            if(up.sqrMagnitude<.000001f) up=Vector3.ProjectOnPlane(wrist.up,forward);
            if(up.sqrMagnitude<.000001f) up=Vector3.ProjectOnPlane(wrist.right,forward);
            return Quaternion.Inverse(wrist.rotation)*Quaternion.LookRotation(forward,up.normalized);
        }
        internal static void ValidatePlacements(HandPlacements placements)
        {
            foreach(var pose in new[] {placements.inHand,placements.backHand})
            {
                if(pose==null) throw new ArgumentException("Both hand placements are required.");
                foreach(var v in new[] {pose.position.x,pose.position.y,pose.position.z,pose.rotation.x,pose.rotation.y,pose.rotation.z})
                    if(float.IsNaN(v) || float.IsInfinity(v)) throw new ArgumentException("Hand placement contains an invalid value.");
                if(pose.captured && pose.position.magnitude>2) throw new ArgumentException("Capture the panel within two metres of the right hand.");
            }
        }
    }
}
#endif
