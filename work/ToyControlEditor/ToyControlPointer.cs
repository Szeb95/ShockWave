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
        internal const float PointerDistance=3f;
        internal const float PointerTargetDepth=0.0002f;
        internal const float PointerContactRadius=0.0001f,PointerContactDepth=0.0004f;
        internal const string PointerTag="ShockWaveToyPointer";
        private static void BuildPointers(Transform host,Transform size,Func<HumanBodyBones,Transform> bone,string folder,Vector3 scale)
        {
            // Only this generated panel supplies the physical ray target. No
            // world settings, avatar colliders or existing bones are modified.
            var target=Child("PointerTarget",size); target.layer=10;
            var box=target.AddComponent<BoxCollider>(); box.center=Vector3.zero;
            box.size=new Vector3(BoardWidth,BoardWidth*LogicalHeight/LogicalWidth,PointerTargetDepth); box.isTrigger=false;
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
                // Bake the extended index direction once in wrist space. During
                // play only the hand bone is followed: curling/splaying finger
                // bones for a gesture must never steer the ray or move its origin.
                var direction=Quaternion.Inverse(wrist.rotation)*(distal.position-proximal.position);
                if(direction.sqrMagnitude<0.000001f) throw new InvalidOperationException("The index finger is too short to determine a pointer direction.");
                direction.Normalize();
                group.transform.SetPositionAndRotation(wrist.position,wrist.rotation);
                origin.transform.localPosition=Quaternion.Inverse(wrist.rotation)*(proximal.position-wrist.position);
                origin.transform.localRotation=Quaternion.LookRotation(direction,Mathf.Abs(Vector3.Dot(direction,Vector3.up))>.99f ? Vector3.forward : Vector3.up);
                var follow=group.AddComponent<VRCParentConstraint>(); follow.Sources.Add(new VRCConstraintSource(wrist,1,Vector3.zero,Vector3.zero));
                follow.AffectsRotationX=follow.AffectsRotationY=follow.AffectsRotationZ=true; follow.IsActive=true; follow.Locked=true;
                // Result positions belong to the panel, independently of moving
                // wrist anchors. Keep visible hits on the actual board plane;
                // only the invisible contact sender uses the finger-plane offset.
                var result=Child(hand,hits.transform); result.transform.position=origin.transform.position+origin.transform.forward*PointerDistance;
                var ray=origin.AddComponent<VRCRaycast>(); ray.RaycastDirection=Vector3.forward; ray.Distance=PointerDistance; ray.ApplyTransformScale=true;
                ray.RaycastCollisionMode=VRCRaycast.CollisionMode.HitCustomLayers; ray.CustomCollisionLayers=(1<<9)|(1<<10);
                ray.ResultTransform=result.transform; ray.BehaviorOnMiss=VRCRaycast.MissBehavior.SnapToEnd; ray.Parameter=ToyControlContract.Prefix+"Ray"+hand;
                var sender=result.AddComponent<VRCContactSender>(); sender.position=new Vector3(0,0,-0.015f); sender.radius=PointerContactRadius; sender.localOnly=true; sender.collisionTags=new List<string> {PointerTag+hand};
                Beam(group.transform,origin.transform,result.transform,hand);
                Visual(PrimitiveType.Quad,"Marker",result.transform,Vector3.zero,Vector3.one*0.006f,dot:true);
                var contacts=size.Find("Contacts"); var canvas=new Rect(0,LogicalTop,LogicalWidth,LogicalHeight);
                Contact(contacts,"InPointer"+hand+"Hit",canvas,tag:PointerTag+hand,depth:PointerContactDepth);
                Contact(contacts,"InPointer"+hand+"X",canvas,true,Quaternion.Euler(0,90,0),PointerTag+hand);
                Contact(contacts,"InPointer"+hand+"Y",canvas,true,Quaternion.Euler(-90,0,0),PointerTag+hand);
            }
            pointers.SetActive(false);
            hits.SetActive(false);
            void Beam(Transform parent,Transform start,Transform end,string hand)
            {
                // Skin each end directly to its live transform. A length driven
                // by Animator's ray ratio can lag a wrist constraint and leave
                // the visible endpoint floating away from the real ray result.
                var obj=Child("Beam",parent); var mesh=new Mesh {name="Pointer "+hand+" anchored beam"};
                var vertices=new Vector3[8]; var weights=new BoneWeight[8]; var uv=new Vector2[8];
                for(var i=0;i<8;i++)
                {
                    vertices[i]=new Vector3((i%4==0 || i%4==3 ? -1 : 1)*.00075f,(i%4<2 ? -1 : 1)*.00075f,0);
                    weights[i]=new BoneWeight {boneIndex0=i/4,weight0=1}; uv[i]=new Vector2(i%2,i/4);
                }
                mesh.vertices=vertices; mesh.uv=uv; mesh.boneWeights=weights; mesh.bindposes=new[] {Matrix4x4.identity,Matrix4x4.identity};
                mesh.triangles=new[] {0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7}; mesh.RecalculateBounds();
                AssetDatabase.CreateAsset(mesh,folder+"/Pointer"+hand+"Beam.asset");
                var renderer=obj.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh=mesh; renderer.bones=new[] {start,end}; renderer.rootBone=parent;
                renderer.localBounds=new Bounds(Vector3.zero,Vector3.one*(PointerDistance*2+.1f)); renderer.updateWhenOffscreen=true;
                renderer.sharedMaterial=material; renderer.enabled=false; renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows=false;
            }
            void Visual(PrimitiveType primitive,string name,Transform parent,Vector3 position,Vector3 dimensions,bool dot=false)
            {
                var visual=GameObject.CreatePrimitive(primitive); visual.name=name; DestroyImmediate(visual.GetComponent<Collider>());
                visual.transform.SetParent(parent,false); visual.transform.localPosition=position; visual.transform.localScale=dimensions;
                var renderer=visual.GetComponent<MeshRenderer>(); renderer.sharedMaterial=dot ? dotMaterial : material;
                if(dot) renderer.enabled=false;
                renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows=false;
            }
        }
        private static void PointerLayers(AnimatorController c)
        {
            var placement=c.layers[1].stateMachine;
            foreach(var entry in placement.states)
            {
                var ready=entry.state.name=="Place beside right hand" || entry.state.name=="Dropped" || entry.state.name=="Follow right hand";
                Driver(entry.state,"InputReady",ready ? 1 : 0);
                if(!ready) foreach(var name in new[] {"InPointerLPress","InPointerRPress","InPointerLHit","InPointerRHit","InInputMode","InPanelTouch","InPage"}) Driver(entry.state,name,0);
            }
            var mode=Layer(c,"Pointer visibility");
            var off=State(mode,"Touch / unavailable",Clip(c,"Pointer off",0.1f));
            var on=State(mode,"Pointer",Clip(c,"Pointer on",0.1f)); mode.defaultState=off;
            Curve((AnimationClip)off.motion,"Pointer",typeof(GameObject),"m_IsActive",0);
            Curve((AnimationClip)on.motion,"Pointer",typeof(GameObject),"m_IsActive",1);
            Curve((AnimationClip)off.motion,"Panel/Size/Contacts/PointerHits",typeof(GameObject),"m_IsActive",0);
            Curve((AnimationClip)on.motion,"Panel/Size/Contacts/PointerHits",typeof(GameObject),"m_IsActive",1);
            Curve((AnimationClip)off.motion,"Panel/Size/PointerTarget",typeof(GameObject),"m_IsActive",0);
            Curve((AnimationClip)on.motion,"Panel/Size/PointerTarget",typeof(GameObject),"m_IsActive",1);
            foreach(var name in ToyControlContract.BoolInputs)
            {
                if(name=="InInputMode" || name.StartsWith("InPointer",StringComparison.Ordinal) || !name.StartsWith("In",StringComparison.Ordinal)) continue;
                // These are the physical finger receivers only; expression-menu
                // actions keep working in either input mode.
                if(name=="InTouch" || name=="InPanelTouch" || name=="InPage" || name.StartsWith("InRow",StringComparison.Ordinal) || name=="InRecord" || name=="InPlay" || name=="InClear" || name=="InStop" || name=="InPrev" || name=="InNext")
                {
                    Curve((AnimationClip)off.motion,"Panel/Size/Contacts/"+name,typeof(GameObject),"m_IsActive",1);
                    Curve((AnimationClip)on.motion,"Panel/Size/Contacts/"+name,typeof(GameObject),"m_IsActive",0);
                }
            }
            var enable=Transition(off,on); PointerReady(enable);
            foreach(var suffix in new[] {"Visible","InputReady"}) Bool(Transition(on,off),suffix,false);
            Transition(on,off).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");
            Transition(on,off).AddCondition(AnimatorConditionMode.Less,0.5f,ToyControlContract.ReceivePrefix+"Pointer");
            foreach(var hand in new[] {"L","R"})
            {
                var ray=ToyControlContract.Prefix+"Ray"+hand;
                c.AddParameter(ray+"_Hit",AnimatorControllerParameterType.Bool); c.AddParameter(ray+"_Ratio",AnimatorControllerParameterType.Float); c.AddParameter(ray+"_Distance",AnimatorControllerParameterType.Float);
                var sm=Layer(c,"Pointer "+hand+" click");
                var inactive=State(sm,"Release before pointing",Clip(c,"Pointer "+hand+" idle",0.1f)); sm.defaultState=inactive;
                var ready=State(sm,"Aim",Clip(c,"Pointer "+hand+" aim",.1f)); var down=State(sm,"Click / draw",Clip(c,"Pointer "+hand+" press",.1f));
                Driver(inactive,"InPointer"+hand+"Press",0); Driver(ready,"InPointer"+hand+"Press",0); Driver(down,"InPointer"+hand+"Press",1);
                var gesture=hand=="L" ? "GestureLeft" : "GestureRight";
                var arm=Transition(inactive,ready); PointerReady(arm); arm.AddCondition(AnimatorConditionMode.NotEqual,7,gesture);
                foreach(var state in new[] {ready,down})
                {
                    foreach(var suffix in new[] {"Visible","InputReady"}) Bool(Transition(state,inactive),suffix,false);
                    Transition(state,inactive).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");
                    Transition(state,inactive).AddCondition(AnimatorConditionMode.Less,0.5f,ToyControlContract.ReceivePrefix+"Pointer");
                }
                // Placement/hide/mode exits must win if the click gesture rises
                // on the same frame as grabbing or disabling the panel.
                Transition(ready,down).AddCondition(AnimatorConditionMode.Equals,7,gesture);
                Transition(down,ready).AddCondition(AnimatorConditionMode.NotEqual,7,gesture);
            }
            foreach(var hand in new[] {"L","R"})
            {
                var sm=Layer(c,"Pointer "+hand+" visuals");
                var hidden=State(sm,"Off panel",Clip(c,"Pointer "+hand+" dot hidden",.1f));
                var shown=State(sm,"On panel",Clip(c,"Pointer "+hand+" dot shown",.1f)); sm.defaultState=hidden;
                var marker="Panel/Size/Contacts/PointerHits/"+hand+"/Marker";
                Curve((AnimationClip)hidden.motion,marker,typeof(MeshRenderer),"m_Enabled",0);
                Curve((AnimationClip)shown.motion,marker,typeof(MeshRenderer),"m_Enabled",1);
                Curve((AnimationClip)hidden.motion,"Pointer/"+hand+"/Beam",typeof(SkinnedMeshRenderer),"m_Enabled",0);
                Curve((AnimationClip)shown.motion,"Pointer/"+hand+"/Beam",typeof(SkinnedMeshRenderer),"m_Enabled",1);
                var show=Transition(hidden,shown); PointerReady(show); Bool(show,"InPointer"+hand+"Hit",true); Bool(show,"Ray"+hand+"_Hit",true);
                foreach(var suffix in new[] {"Visible","InputReady","InPointer"+hand+"Hit","Ray"+hand+"_Hit"}) Bool(Transition(shown,hidden),suffix,false);
                Transition(shown,hidden).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");
                Transition(shown,hidden).AddCondition(AnimatorConditionMode.Less,.5f,ToyControlContract.ReceivePrefix+"Pointer");
            }
        }
        private static void ShockerViewLayer(AnimatorController c)
        {
            // A v4 avatar has no acknowledgement. Its graph coordinates must
            // never be reinterpreted as shock buttons by a newer desktop app.
            var layer=Layer(c,"Shocker view acknowledgement");
            var off=State(layer,"Toys / unavailable",Clip(c,"Shocker view off",.1f)); layer.defaultState=off;
            var on=State(layer,"Shocker controls visible",Clip(c,"Shocker view ready",.1f));
            Driver(off,"InShockerReady",0); Driver(on,"InShockerReady",1);
            var enter=Transition(off,on); Bool(enter,"Visible",true); Bool(enter,"InputReady",true); enter.AddCondition(AnimatorConditionMode.If,0,"IsLocal");
            enter.AddCondition(AnimatorConditionMode.Greater,.5f,ToyControlContract.ReceivePrefix+"ShockerPage");
            foreach(var suffix in new[] {"Visible","InputReady"}) Bool(Transition(on,off),suffix,false);
            Transition(on,off).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");
            Transition(on,off).AddCondition(AnimatorConditionMode.Less,.5f,ToyControlContract.ReceivePrefix+"ShockerPage");
        }
        private static void PointerReady(AnimatorStateTransition transition)
        {
            Bool(transition,"Visible",true); Bool(transition,"InputReady",true);
            transition.AddCondition(AnimatorConditionMode.If,0,"IsLocal");
            transition.AddCondition(AnimatorConditionMode.Greater,0.5f,ToyControlContract.ReceivePrefix+"Pointer");
        }
    }
}
#endif
