#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using ShockWave.ToyControl.Shared;

namespace ShockWave.ToyControl
{
    public sealed partial class ToyControlSetupWindow
    {
        internal const float PointerDistance=2f, PointerPoseSpan=3.5f;
        internal const float PointerTargetDepth=.0002f, PointerContactRadius=.0001f, PointerContactDepth=.0004f;
        internal const string PointerTag="ShockWaveToyPointer";
        private static void BuildPointers(Transform host,Transform size,Func<HumanBodyBones,Transform> bone,string folder,Vector3 scale)
        {
            var sensors=Child("PoseTracking",size.parent);
            var hits=Child("PointerHits",size.Find("Contacts"));
            var pointers=Child("Pointer",host); pointers.transform.localScale=new Vector3(1/scale.x,1/scale.y,1/scale.z);
            var material=ToyControlShaderFactory.CreatePointer(folder);
            var dotMaterial=new Material(material) {name="Pointer surface dot"}; dotMaterial.SetFloat("_Disc",1);
            dotMaterial.renderQueue=(int)UnityEngine.Rendering.RenderQueue.AlphaTest+1;
            dotMaterial.SetFloat("_ZWrite",0); dotMaterial.SetFloat("_OffsetFactor",-1); dotMaterial.SetFloat("_OffsetUnits",-1);
            AssetDatabase.CreateAsset(dotMaterial,folder+"/PointerDot.mat");
            foreach(var left in new[] {true,false})
            {
                var hand=left ? "L" : "R";
                var wrist=bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                var proximal=bone(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
                var distal=bone(left ? HumanBodyBones.LeftIndexDistal : HumanBodyBones.RightIndexDistal);
                if(wrist==null || proximal==null || distal==null || proximal==distal) throw new InvalidOperationException("Pointer mode requires both humanoid hands and index-finger bones.");
                var group=Child(hand,pointers.transform); var origin=Child("Origin",group.transform);
                var direction=Quaternion.Inverse(wrist.rotation)*(distal.position-proximal.position);
                if(direction.sqrMagnitude<.000001f) throw new InvalidOperationException("Index finger is too short to calibrate pointing.");
                direction.Normalize(); group.transform.SetPositionAndRotation(wrist.position,wrist.rotation);
                origin.transform.localPosition=Quaternion.Inverse(wrist.rotation)*(proximal.position-wrist.position);
                origin.transform.localRotation=Quaternion.LookRotation(direction,Mathf.Abs(Vector3.Dot(direction,Vector3.up))>.99f ? Vector3.forward : Vector3.up);
                var follow=group.AddComponent<VRCParentConstraint>(); follow.Sources.Add(new VRCConstraintSource(wrist,1,Vector3.zero,Vector3.zero));
                follow.AffectsRotationX=follow.AffectsRotationY=follow.AffectsRotationZ=true; follow.IsActive=true; follow.Locked=true;
                var tip=Child("Tip",origin.transform); tip.transform.localPosition=Vector3.forward*.2f;
                foreach(var end in new[] {"O","T"})
                {
                    var sender=(end=="O" ? origin : tip).AddComponent<VRCContactSender>(); sender.radius=.0001f; sender.localOnly=true; sender.collisionTags=new List<string>{PointerTag+hand+end};
                    foreach(var axis in new[] {"Hit","X","Y","Z"})
                    {
                        var obj=Child("InPose"+hand+end+axis,sensors.transform); var r=obj.AddComponent<VRCContactReceiver>();
                        r.parameter=ToyControlContract.Prefix+obj.name; r.receiverType=axis=="Hit" ? ContactReceiver.ReceiverType.Constant : ContactReceiver.ReceiverType.Proximity;
                        r.allowSelf=true; r.allowOthers=false; r.localOnly=true; r.collisionTags=new List<string>{PointerTag+hand+end};
                        r.rotation=axis=="X" ? Quaternion.Euler(0,90,0) : axis=="Y" ? Quaternion.Euler(-90,0,0) : Quaternion.identity;
                        var so=new SerializedObject(r); var shape=so.FindProperty("shapeType"); shape.enumValueIndex=Array.IndexOf(shape.enumNames,"Box");
                        so.FindProperty("size").vector3Value=Vector3.one*PointerPoseSpan; so.FindProperty("useFaceProximity").boolValue=axis!="Hit"; so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
                var result=Child(hand,hits.transform); result.transform.localPosition=new Vector3(0,0,-.0002f);
                var marker=GameObject.CreatePrimitive(PrimitiveType.Quad); marker.name="Marker"; DestroyImmediate(marker.GetComponent<Collider>());
                marker.transform.SetParent(result.transform,false); marker.transform.localScale=Vector3.one*.006f;
                var dot=marker.GetComponent<MeshRenderer>(); dot.sharedMaterial=dotMaterial; dot.enabled=false; dot.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off; dot.receiveShadows=false;
                Beam(group.transform,origin.transform,result.transform,hand);
            }
            pointers.SetActive(false); hits.SetActive(false); sensors.SetActive(false);
            void Beam(Transform parent,Transform start,Transform end,string hand)
            {
                var obj=Child("Beam",parent); obj.transform.rotation=start.rotation;
                var mesh=new Mesh {name="Contact pointer "+hand+" beam"}; var vertices=new Vector3[8]; var weights=new BoneWeight[8]; var uv=new Vector2[8];
                for(var i=0;i<8;i++) {vertices[i]=new Vector3((i%4==0 || i%4==3 ? -1 : 1)*.00075f,(i%4<2 ? -1 : 1)*.00075f,0);weights[i]=new BoneWeight {boneIndex0=i/4,weight0=1};uv[i]=new Vector2(i%2,i/4);}
                mesh.vertices=vertices;mesh.uv=uv;mesh.boneWeights=weights;mesh.bindposes=new[] {Matrix4x4.identity,Matrix4x4.identity};
                mesh.triangles=new[] {0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7};mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,folder+"/Pointer"+hand+"Beam.asset");
                var renderer=obj.AddComponent<SkinnedMeshRenderer>();renderer.sharedMesh=mesh;renderer.bones=new[] {start,end};renderer.rootBone=parent;
                renderer.localBounds=new Bounds(Vector3.forward, new Vector3(.65f,.65f,2.4f));renderer.updateWhenOffscreen=true;
                renderer.sharedMaterial=material;renderer.enabled=false;renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;renderer.receiveShadows=false;
            }
        }
        private static void PointerLayers(AnimatorController c)
        {
            foreach(var entry in c.layers[1].stateMachine.states)
            {
                var ready=entry.state.name=="Place beside right hand" || entry.state.name=="Dropped" || entry.state.name=="Follow right hand" || entry.state.name=="Follow back of right hand" || entry.state.name=="Follow avatar";
                Driver(entry.state,"InputReady",ready ? 1 : 0);
                if(!ready) foreach(var name in new[] {"InPointerLPress","InPointerRPress","InPointerReady","InInputMode","InPanelTouch","InPage"}) Driver(entry.state,name,0);
            }
            var mode=Layer(c,"Pointer visibility"); var off=State(mode,"Touch / unavailable",Clip(c,"Pointer off",.1f)); var on=State(mode,"Pointer",Clip(c,"Pointer on",.1f));mode.defaultState=off;
            Driver(off,"InPointerReady",0);Driver(on,"InPointerReady",1);
            foreach(var path in new[] {"Pointer","Panel/Size/Contacts/PointerHits","Panel/PoseTracking"})
            {Curve((AnimationClip)off.motion,path,typeof(GameObject),"m_IsActive",0);Curve((AnimationClip)on.motion,path,typeof(GameObject),"m_IsActive",1);}
            foreach(var name in ToyControlContract.BoolInputs)
                if(name=="InTouch" || name=="InPanelTouch" || name=="InPage" || name.StartsWith("InRow",StringComparison.Ordinal) || name=="InRecord" || name=="InPlay" || name=="InClear" || name=="InStop" || name=="InPrev" || name=="InNext")
                {Curve((AnimationClip)off.motion,"Panel/Size/Contacts/"+name,typeof(GameObject),"m_IsActive",1);Curve((AnimationClip)on.motion,"Panel/Size/Contacts/"+name,typeof(GameObject),"m_IsActive",0);}
            PointerReady(Transition(off,on));
            foreach(var suffix in new[] {"Visible","InputReady","AppReady"}) Bool(Transition(on,off),suffix,false);
            Transition(on,off).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");Transition(on,off).AddCondition(AnimatorConditionMode.Less,.5f,ToyControlContract.ReceivePrefix+"Pointer");
            foreach(var hand in new[] {"L","R"})
            {
                var sm=Layer(c,"Pointer "+hand+" click");var inactive=State(sm,"Release before pointing",Clip(c,"Pointer "+hand+" idle",.1f));sm.defaultState=inactive;
                var ready=State(sm,"Aim",Clip(c,"Pointer "+hand+" aim",.1f));var down=State(sm,"Click / draw",Clip(c,"Pointer "+hand+" press",.1f));
                Driver(inactive,"InPointer"+hand+"Press",0);Driver(ready,"InPointer"+hand+"Press",0);Driver(down,"InPointer"+hand+"Press",1);
                var gesture=hand=="L" ? "GestureLeft" : "GestureRight";var arm=Transition(inactive,ready);PointerReady(arm);arm.AddCondition(AnimatorConditionMode.NotEqual,7,gesture);
                foreach(var state in new[] {ready,down})
                {foreach(var suffix in new[] {"Visible","InputReady","AppReady"}) Bool(Transition(state,inactive),suffix,false);Transition(state,inactive).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");Transition(state,inactive).AddCondition(AnimatorConditionMode.Less,.5f,ToyControlContract.ReceivePrefix+"Pointer");}
                Transition(ready,down).AddCondition(AnimatorConditionMode.Equals,7,gesture);Transition(down,ready).AddCondition(AnimatorConditionMode.NotEqual,7,gesture);
            }
            foreach(var hand in new[] {"L","R"})
            {
                var sm=Layer(c,"Pointer "+hand+" visuals");var hidden=State(sm,"Off panel",Clip(c,"Pointer "+hand+" hidden",.1f));var shown=State(sm,"On panel",Clip(c,"Pointer "+hand+" shown",.1f));sm.defaultState=hidden;
                var marker="Panel/Size/Contacts/PointerHits/"+hand+"/Marker";
                Curve((AnimationClip)hidden.motion,marker,typeof(MeshRenderer),"m_Enabled",0);Curve((AnimationClip)shown.motion,marker,typeof(MeshRenderer),"m_Enabled",1);
                Curve((AnimationClip)hidden.motion,"Pointer/"+hand+"/Beam",typeof(SkinnedMeshRenderer),"m_Enabled",0);Curve((AnimationClip)shown.motion,"Pointer/"+hand+"/Beam",typeof(SkinnedMeshRenderer),"m_Enabled",1);
                var show=Transition(hidden,shown);PointerReady(show);show.AddCondition(AnimatorConditionMode.Greater,.5f,ToyControlContract.ReceivePrefix+"Pointer"+hand+"Valid");
                foreach(var suffix in new[] {"Visible","InputReady","AppReady"}) Bool(Transition(shown,hidden),suffix,false);
                Transition(shown,hidden).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");Transition(shown,hidden).AddCondition(AnimatorConditionMode.Less,.5f,ToyControlContract.ReceivePrefix+"Pointer");Transition(shown,hidden).AddCondition(AnimatorConditionMode.Less,.5f,ToyControlContract.ReceivePrefix+"Pointer"+hand+"Valid");
            }
        }
        private static void PointerPositionLayer(AnimatorController c)
        {
            // Separate nested 1D trees keep each hand's XYZ position independent.
            // A Direct tree adds default transform/constraint channels per child.
            foreach(var hand in new[] {"L","R"})
            {
                var layer=Layer(c,"Panel pointer "+hand+" position");
                var xTree=new BlendTree {name=hand+" cursor X",blendType=BlendTreeType.Simple1D,blendParameter=ToyControlContract.ReceivePrefix+"Pointer"+hand+"X",useAutomaticThresholds=false};AssetDatabase.AddObjectToAsset(xTree,c);
                for(var x=0;x<2;x++)
                {
                    var yTree=new BlendTree {name=hand+" cursor Y at X"+x,blendType=BlendTreeType.Simple1D,blendParameter=ToyControlContract.ReceivePrefix+"Pointer"+hand+"Y",useAutomaticThresholds=false};AssetDatabase.AddObjectToAsset(yTree,c);
                    for(var y=0;y<2;y++)
                    {
                        var clip=Clip(c,hand+" cursor "+x+y,.1f);var path="Panel/Size/Contacts/PointerHits/"+hand;
                        Curve(clip,path,typeof(Transform),"m_LocalPosition.x",(x-.5f)*BoardWidth);
                        Curve(clip,path,typeof(Transform),"m_LocalPosition.y",(y-.5f)*BoardWidth*LogicalHeight/LogicalWidth);
                        Curve(clip,path,typeof(Transform),"m_LocalPosition.z",-.0002f);
                        yTree.AddChild(clip,y);
                    }
                    xTree.AddChild(yTree,x);
                }
                State(layer,"Cursor position",xTree);
            }
        }
        private static void ShockerViewLayer(AnimatorController c)
        {
            var layer=Layer(c,"Shocker view acknowledgement");var off=State(layer,"Toys / unavailable",Clip(c,"Shocker view off",.1f));layer.defaultState=off;
            var on=State(layer,"Shocker controls visible",Clip(c,"Shocker view ready",.25f));var pulse=State(layer,"Shocker controls pulse",Clip(c,"Shocker view pulse",.25f));
            Driver(off,"InShockerReady",0);Driver(off,"InShockerBeat",0);
            Driver(on,"InShockerReady",1);Driver(on,"InShockerBeat",1);Driver(pulse,"InShockerReady",1);Driver(pulse,"InShockerBeat",0);
            var enter=Transition(off,on);Bool(enter,"Visible",true);Bool(enter,"InputReady",true);Bool(enter,"AppReady",true);enter.AddCondition(AnimatorConditionMode.If,0,"IsLocal");enter.AddCondition(AnimatorConditionMode.Greater,.5f,ToyControlContract.ReceivePrefix+"ShockerPage");
            foreach(var state in new[] {on,pulse}) {
                foreach(var suffix in new[] {"Visible","InputReady","AppReady"}) Bool(Transition(state,off),suffix,false);
                Transition(state,off).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");Transition(state,off).AddCondition(AnimatorConditionMode.Less,.5f,ToyControlContract.ReceivePrefix+"ShockerPage");
            }
            foreach(var pair in new[] {(on,pulse),(pulse,on)}) {var t=Transition(pair.Item1,pair.Item2);t.hasExitTime=true;t.exitTime=1;}
        }
        private static void PointerReady(AnimatorStateTransition t)
        {Bool(t,"Visible",true);Bool(t,"InputReady",true);Bool(t,"AppReady",true);t.AddCondition(AnimatorConditionMode.If,0,"IsLocal");t.AddCondition(AnimatorConditionMode.Greater,.5f,ToyControlContract.ReceivePrefix+"Pointer");}
    }
}
#endif
