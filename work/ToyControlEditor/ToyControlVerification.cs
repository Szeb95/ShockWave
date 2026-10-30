#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using ShockWave.ToyControl.Shared;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;

namespace ShockWave.ToyControl
{
    public static class ToyControlVerification
    {
        public static void InspectSdk()
        {
            if(!Application.isBatchMode || Application.dataPath.Replace('\\','/').IndexOf("/ToyControlEditor/checks/unity-project/",StringComparison.OrdinalIgnoreCase)<0) throw new InvalidOperationException("Scratch project only.");
            var root=new GameObject("Temporary SDK inspection");
            try
            {
                var component=root.AddComponent<VRCParentConstraint>();
                var output=new System.Text.StringBuilder();
                foreach(var field in typeof(VRCParentConstraint).GetFields()) output.AppendLine(field.Name+" : "+field.FieldType.Name);
                var data=new SerializedObject(component); var property=data.GetIterator();
                while(property.Next(true)) output.AppendLine(property.propertyPath+" : "+property.propertyType);
                foreach(var binding in AnimationUtility.GetAnimatableBindings(root,root)) if(binding.type==typeof(VRCParentConstraint)) output.AppendLine("ANIM "+binding.propertyName);
                var flags=System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Static;
                foreach(var type in new[]{typeof(VRCParentConstraint).Assembly,typeof(VRCConstraintBase).Assembly}.Distinct().SelectMany(a=>a.GetTypes()).Where(t=>t.Name.Contains("Constraint"))) {
                    output.AppendLine("TYPE "+type.FullName);
                    foreach(var method in type.GetMethods(flags).Where(m=>m.DeclaringType==type)) output.AppendLine(method.ToString());
                }
                File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("SHOCKWAVE_VR_CHECKS"),"sdk-fields.txt"),output.ToString());
            }
            finally {UnityEngine.Object.DestroyImmediate(root);}
        }
        [Serializable] private class Entry {public string name; public float value;}
        [Serializable] private class Frame {public int version; public Entry[] values;}
        public static void Run()
        {
            if(!Application.isBatchMode || Application.dataPath.Replace('\\','/').IndexOf("/ToyControlEditor/checks/unity-project/",StringComparison.OrdinalIgnoreCase)<0)
                throw new InvalidOperationException("Use only the separate ToyControlEditor scratch project in batch mode.");
            var scene=EditorSceneManager.NewPreviewScene(); GameObject root=null; string folder=null; var passed=0;
            var output=Environment.GetEnvironmentVariable("SHOCKWAVE_VR_CHECKS");
            try
            {
                if(string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("SHOCKWAVE_VR_CHECKS needs an absolute artifact folder.");
                Directory.CreateDirectory(output);
                if(Environment.GetEnvironmentVariable("SHOCKWAVE_VR_INSPECT")=="1") InspectSdk();
                void Check(bool ok,string label) {if(!ok) throw new InvalidOperationException(label); passed++; Debug.Log("VR DEMO PASS "+label);}
                var requestedColor=Environment.GetEnvironmentVariable("SHOCKWAVE_VR_COLORSPACE");
                if(!string.IsNullOrWhiteSpace(requestedColor)) Check(QualitySettings.activeColorSpace.ToString()==requestedColor,"Expected active project color space: "+requestedColor);
                var frame=JsonUtility.FromJson<Frame>(File.ReadAllText(Environment.GetEnvironmentVariable("SHOCKWAVE_VR_FRAME")));
                Check(frame.version==ToyControlContract.Version && frame.values.Length==ToyControlContract.FeedbackFields().Length,"Host feedback fixture matches the shared contract");
                root=new GameObject("VR_Demo_Verification"); SceneManager.MoveGameObjectToScene(root,scene);
                var avatar=root.AddComponent<VRCAvatarDescriptor>(); avatar.ViewPosition=new Vector3(0,1.6f,0);
                VerifyConstraintRuntime(root.transform,Check);
                avatar.baseAnimationLayers=Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>(); avatar.specialAnimationLayers=Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
                var originalChild=new GameObject("Keep existing avatar object"); originalChild.transform.SetParent(root.transform,false);
                var leftHand=new GameObject("Synthetic left hand"); leftHand.transform.SetParent(root.transform,false); leftHand.transform.localPosition=new Vector3(-0.35f,1.1f,0);
                var rightHand=new GameObject("Synthetic right hand"); rightHand.transform.SetParent(root.transform,false); rightHand.transform.localPosition=new Vector3(0.35f,1.1f,0);
                leftHand.transform.localRotation=Quaternion.Euler(0,35,0); rightHand.transform.localRotation=Quaternion.Euler(0,-25,0);
                var leftIndex=new GameObject("Synthetic left index proximal"); leftIndex.transform.SetParent(leftHand.transform,false); leftIndex.transform.localPosition=Vector3.forward*.02f;
                var rightIndex=new GameObject("Synthetic right index proximal"); rightIndex.transform.SetParent(rightHand.transform,false); rightIndex.transform.localPosition=Vector3.forward*.02f;
                var rightLittle=new GameObject("Synthetic right little proximal");rightLittle.transform.SetParent(rightHand.transform,false);rightLittle.transform.localPosition=new Vector3(0,-.025f,.02f);
                var leftTip=new GameObject("Synthetic left index distal"); leftTip.transform.SetParent(leftIndex.transform,false); leftTip.transform.localPosition=Vector3.forward*0.06f;
                var rightTip=new GameObject("Synthetic right index distal"); rightTip.transform.SetParent(rightIndex.transform,false); rightTip.transform.localPosition=Vector3.forward*0.06f;
                Transform Bone(HumanBodyBones b) { return b==HumanBodyBones.RightHand ? rightHand.transform : b==HumanBodyBones.LeftHand ? leftHand.transform : b==HumanBodyBones.RightIndexProximal ? rightIndex.transform : b==HumanBodyBones.LeftIndexProximal ? leftIndex.transform : b==HumanBodyBones.RightIndexDistal ? rightTip.transform : b==HumanBodyBones.LeftIndexDistal ? leftTip.transform : b==HumanBodyBones.RightLittleProximal ? rightLittle.transform : null; }
                var host=ToyControlSetupWindow.Build(avatar,0.04f,0.10f,out folder,resolveBone:Bone,menuPath:" Utilities / ShockWave ");
                Check(!host.transform.Find("Panel").gameObject.activeSelf,"New panel is hidden before any Animator runs, including Unity preview");
                var fury=host.GetComponents<Component>().Single(c=>c!=null && c.GetType().FullName=="VF.Model.VRCFury");
                var flags=System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                var model=fury.GetType().GetField("content",flags).GetValue(fury);
                var entries=((System.Collections.IEnumerable)model.GetType().GetField("menus",flags).GetValue(model)).Cast<object>().ToArray();
                Check(entries.Length==1 && (string)entries[0].GetType().GetField("prefix",flags).GetValue(entries[0])=="Utilities/ShockWave","Chosen nested radial path is passed to VRCFury without replacing existing menus");
                Check(originalChild!=null && avatar.expressionParameters==null && avatar.expressionsMenu==null,"Installer preserves existing avatar children and descriptor assets");
                Check(host.GetComponents<Component>().Any(c=>c!=null && c.GetType().FullName=="VF.Model.VRCFury"),"VRCFury Full Controller attached");
                
                Check(ToyControlSetupWindow.ValidateAvatar(avatar)!=null,"Duplicate install refused");
                var parameters=AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder+"/Parameters.asset");
                Check(parameters.parameters.Length==618 && parameters.parameters.All(p=>p.saved==(p.name==ToyControlContract.Prefix+"Size") && !p.networkSynced && p.defaultValue==(p.name==ToyControlContract.Prefix+"Size" ? 0.2f : 0)),"618 local parameters; only size is saved, confirmation is never saved");
                Check(parameters.CalcTotalCost()==0,"No synced expression-parameter memory consumed");
                foreach(var path in AssetDatabase.FindAssets("t:VRCExpressionsMenu",new[] {folder}).Select(AssetDatabase.GUIDToAssetPath))
                    Check(AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(path).controls.Count<=8,"Menu page within eight controls: "+Path.GetFileName(path));
                var contacts=host.GetComponentsInChildren<VRCContactReceiver>(true);
                Check(!contacts.Any(c=>c.parameter.EndsWith("InZero") || c.parameter.EndsWith("InDemo")),"Removed Zero and Demo buttons have no contact receivers");
                Check(contacts.Length==41 && contacts.All(c=>c.localOnly && c.allowSelf && !c.allowOthers) && contacts.Count(c=>c.collisionTags.SequenceEqual(new[] {"FingerIndexL","FingerIndexR"}))==21,"Twenty-one index-only receivers plus separate self-only pointer/hand receivers");
                Check(contacts.Count(c=>c.collisionTags.SequenceEqual(new[] {"HandL"}))==2 && contacts.Count(c=>c.collisionTags.SequenceEqual(new[] {"HandR"}))==2,"Either hand can detect either edge separately");
                Check(contacts.Count(c=>c.receiverType==VRC.Dynamics.ContactReceiver.ReceiverType.Proximity)==16,"Graph, shocker surface and both pointer hands have independent horizontal/vertical proximity receivers");
                Check(host.GetComponentInChildren<ParticleSystem>(true)==null,"Shader graph has no particles");
                var panel=host.transform.Find("Panel"); var renderer=panel.Find("Size/Board").GetComponent<MeshRenderer>();
                Check(panel.Find("Size/Grips/LeftStrip")!=null && panel.Find("Size/Grips/RightStrip")!=null && host.GetComponentsInChildren<Renderer>(true).Length==7 && host.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length==2,"Two strips, both anchored beams and both markers accompany the original board");
                Check(contacts.Where(c=>c.parameter.Contains("Grab")).All(c=>Mathf.Abs(c.position.x)>ToyControlSetupWindow.BoardWidth/2+0.025f),"Grab receivers sit outside the board with a gap to its buttons");
                Check(contacts.Single(c=>c.parameter.EndsWith("InStop")).position.x<0 && contacts.Single(c=>c.parameter.EndsWith("InRecord")).position.y>contacts.Single(c=>c.parameter.EndsWith("InPage")).position.y-0.002f,"Stop is above the rows; page switch and pattern controls share the top row");
                var clearContact=contacts.Single(c=>c.parameter.EndsWith("InClear")); var stopContact=contacts.Single(c=>c.parameter.EndsWith("InStop"));
                var clearWidth=new SerializedObject(clearContact).FindProperty("size").vector3Value.x; var stopWidth=new SerializedObject(stopContact).FindProperty("size").vector3Value.x;
                Check(stopContact.position.x-clearContact.position.x-(clearWidth+stopWidth)/2>0.010f,"Inset button touch zones provide a physical gap between Clear and Stop");
                var placement=panel.GetComponent<VRCParentConstraint>();
                Check(placement.Sources.Count==6 && placement.Sources[1].SourceTransform==leftHand.transform && placement.Sources[2].SourceTransform==rightHand.transform && placement.Sources.All(s=>s.SourceTransform!=root.transform),"Hand sources and independent runtime fits have no avatar-root follow source");
                Check(Vector3.Distance(panel.position,host.transform.Find("HandAnchors/RightHandPosition/SpawnRight").position)<0.0001f && Vector3.Dot(panel.forward,(rightTip.transform.position-rightIndex.transform.position).normalized)>.999f,"Default in-hand alignment follows the actual extended-finger axes");
                Check(Vector3.Dot(panel.up,(rightIndex.transform.position-rightLittle.transform.position).normalized)>.999f,"In-hand up follows the index/thumb side rather than the hand's back, preventing a quarter-turn roll");
                var backAnchor=host.transform.Find("HandAnchors/RightHandPosition/BackRight");
                Check(Vector3.Dot(backAnchor.forward,-panel.right)>.999f && Vector3.Dot(backAnchor.up,panel.forward)>.999f,"Back-of-hand panel faces out of the hand's back with the fingers along its top");
                var mainMenu=AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder+"/Controls.asset");
                Check(mainMenu.controls.Any(m=>m.name=="Panel size" && m.type==VRCExpressionsMenu.Control.ControlType.RadialPuppet && m.subParameters.Single().name==ToyControlContract.Prefix+"Size"),"Panel size radial is in the main eight-control menu");
                Check(renderer.sharedMaterial.GetTexture("_Labels").width==2480 && renderer.sharedMaterial.GetTexture("_Glyphs").width==768,"Labels and glyph atlas have doubled text resolution");
                foreach(var file in new[] {"Labels.png","Glyphs.png","Status.png","GlyphMetrics.png"})
                {
                    var importer=(TextureImporter)AssetImporter.GetAtPath(folder+"/"+file);
                    Check(!importer.sRGBTexture && !importer.mipmapEnabled && importer.maxTextureSize==4096,"Authored display/data texture settings: "+file);
                }
                var rawLabels=new Texture2D(1,1,TextureFormat.RGBA32,false,true); rawLabels.LoadImage(File.ReadAllBytes(folder+"/Labels.png"));
                Check(rawLabels.GetPixels().Any(p=>p.a>0.1f && p.a<0.5f && p.r>0.9f && p.g>0.9f),"Antialiased text edges retain straight color without double darkening"); UnityEngine.Object.DestroyImmediate(rawLabels);
                Check(Mathf.Abs(panel.Find("Size/Board").localScale.y-ToyControlSetupWindow.BoardWidth*608/1240)<0.0001f,"VR board excludes the desktop footer");
                var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(folder+"/FX.controller");
                Check(controller.layers.Length==13 && controller.layers.All(l=>l.defaultWeight==1),"Existing layers plus pointer controls and explicit shocker-view acknowledgement");
                Check(controller.layers[1].stateMachine.states.Where(s=>s.state.name.StartsWith("Prepare")).All(s=>s.state.behaviours.OfType<VRCAvatarParameterDriver>().SelectMany(d=>d.parameters).Any(p=>p.name==ToyControlContract.Prefix+"InTouch" && p.value==0)),"Every grip/fit preparation clears touch before the stable held input state");
                var tree=(BlendTree)controller.layers[0].stateMachine.defaultState.motion;
                Check(tree.children.Length==ToyControlContract.FeedbackFields().Length && tree.children.All(t=>t.directBlendParameter==ToyControlContract.Prefix+"Weight"),"All numeric material properties have independent weighted bindings");
                foreach(var clip in controller.animationClips.Distinct()) foreach(var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var transform=host.transform.Find(binding.path);
                    if(transform==null || binding.type!=typeof(GameObject) && transform.GetComponent(binding.type)==null) throw new InvalidOperationException("Invalid generated binding "+binding.path+"/"+binding.propertyName);
                }
                Check(true,"Every generated animation binding resolves under its new host");
                var animator=host.AddComponent<Animator>(); animator.runtimeAnimatorController=controller; animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind(); animator.SetBool("IsLocal",true); animator.Update(0.02f); Check(!panel.gameObject.activeSelf,"Panel initially hidden");
                animator.SetBool(ToyControlContract.Prefix+"Visible",true);
                foreach(var item in frame.values) animator.SetFloat(ToyControlContract.ReceivePrefix+item.name,item.value);
                animator.SetBool(ToyControlContract.Prefix+"RxBeat",true);
                for(var i=0;i<20;i++) animator.Update(0.02f);
                Check(panel.gameObject.activeSelf && panel.GetComponent<VRCParentConstraint>().FreezeToWorld,"Show places then world-locks the panel without avatar/head following");
                var size=panel.Find("Size");
                animator.SetFloat(ToyControlContract.Prefix+"Size",0); for(var i=0;i<5;i++) animator.Update(0.02f);
                Check(Mathf.Abs(size.localScale.x-0.4f)<0.0001f && size.Find("Contacts").parent==size && size.Find("Grips").parent==size,"Minimum radial size scales Board, touch and grips as one child while placement stays frozen");
                animator.SetFloat(ToyControlContract.Prefix+"Size",1); for(var i=0;i<5;i++) animator.Update(0.02f);
                Check(Mathf.Abs(size.localScale.x-1.6f)<0.0001f && placement.FreezeToWorld,"Maximum radial size does not unfreeze a dropped panel");
                animator.SetFloat(ToyControlContract.Prefix+"Size",0.5f); for(var i=0;i<5;i++) animator.Update(0.02f);
                VerifyPlacements(host,animator,root.transform,rightHand.transform,Check);
                animator.SetFloat(ToyControlContract.Prefix+"Size",0.73f); animator.SetBool(ToyControlContract.Prefix+"InStop",true); animator.SetBool(ToyControlContract.Prefix+"Visible",false); for(var i=0;i<5;i++) animator.Update(0.02f);
                animator.SetBool(ToyControlContract.Prefix+"Visible",true); animator.SetBool(ToyControlContract.Prefix+"InStop",false); for(var i=0;i<20;i++) animator.Update(0.02f);
                Check(Mathf.Abs(size.localScale.x-(0.4f+1.2f*0.73f))<0.0001f && animator.GetFloat(ToyControlContract.Prefix+"Size")==0.73f,"Stop and hide/show cannot reset the size radial");
                animator.SetFloat(ToyControlContract.Prefix+"Size",0.5f); for(var i=0;i<5;i++) animator.Update(0.02f);
                animator.SetBool(ToyControlContract.Prefix+"Recall",true); for(var i=0;i<5;i++) animator.Update(0.02f);
                Check(!placement.FreezeToWorld && !placement.RebakeOffsetsWhenUnfrozen && placement.Sources[0].Weight==1 && placement.Sources[0].ParentPositionOffset==Vector3.zero,"Recall resets the spawn offset instead of retaining a previous grab offset");
                animator.SetBool(ToyControlContract.Prefix+"Recall",false); for(var i=0;i<20;i++) animator.Update(0.02f);
                VerifyPointers(host,animator,Check,output);
                var block=new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                var visibleWord=block.GetFloat("_R1_Toy00"); if(visibleWord==0) visibleWord=renderer.sharedMaterial.GetFloat("_R1_Toy00");
                Check(Mathf.Abs(visibleWord-frame.values.Single(v=>v.name=="R1_Toy00").value)<0.00002,"Animator preserves packed local glyph feedback");
                var liveCurveWord=block.GetFloat("_S00_C04"); if(liveCurveWord==0) liveCurveWord=renderer.sharedMaterial.GetFloat("_S00_C04");
                Check(Mathf.Abs(liveCurveWord-frame.values.Single(v=>v.name=="S00_C04").value)<0.00002,"Animator preserves packed recorded-curve feedback");
                for(var cycle=0;cycle<12;cycle++)
                {
                    animator.SetBool(ToyControlContract.Prefix+"RxBeat",cycle%2==0);
                    for(var step=0;step<10;step++) animator.Update(0.025f);
                }
                renderer.GetPropertyBlock(block); var pulsedOnline=block.GetFloat("_LinkOnline"); if(pulsedOnline==0) pulsedOnline=renderer.sharedMaterial.GetFloat("_LinkOnline");
                Check(controller.layers[2].stateMachine.states.All(s=>Mathf.Abs(s.state.motion.averageDuration-0.25f)<0.001f) && pulsedOnline>0.5f,"Quarter-second heartbeat clips and incoming app pulses keep the preview fresh across three seconds");
                panel.GetComponent<VRCParentConstraint>().IsActive=false;
                Render(panel,Path.Combine(output,"vr-panel.png"),(image,camera,board)=>
                {
                    int Count(Rect logical,Func<Color,bool> match)
                    {
                        Vector3 Screen(float x,float y) {return camera.WorldToScreenPoint(board.TransformPoint(new Vector3(x/1240-0.5f,0.5f-(y-100)/608,0)));}
                        var a=Screen(logical.xMin,logical.yMin); var b=Screen(logical.xMax,logical.yMax); var found=0;
                        for(var y=Mathf.FloorToInt(Mathf.Min(a.y,b.y));y<Mathf.CeilToInt(Mathf.Max(a.y,b.y));y++)
                            for(var x=Mathf.FloorToInt(Mathf.Min(a.x,b.x));x<Mathf.CeilToInt(Mathf.Max(a.x,b.x));x++)
                                if(match(image.GetPixel(Mathf.Clamp(x,0,image.width-1),Mathf.Clamp(y,0,image.height-1)))) found++;
                        return found;
                    }
                    bool Green(Color c) {return c.g>c.r+0.12f && c.g>c.b+0.12f;}
                    Color At(float x,float y) {var screen=camera.WorldToScreenPoint(board.TransformPoint(new Vector3(x/1240-0.5f,0.5f-(y-100)/608,0))); return image.GetPixel(Mathf.RoundToInt(screen.x),Mathf.RoundToInt(screen.y));}
                    var background=At(333,115); var card=At(800,115);
                    Check(At(2,102).a<0.01f && At(1238,102).a<0.01f && At(2,706).a<0.01f && At(1238,706).a<0.01f,"All four outer corners discard to transparent");
                    Check(At(5,400).a<0.01f && At(333,400).a<0.01f && At(1228,400).a<0.01f && At(24,105).a<0.01f && At(1216,105).a<0.01f,"Only the existing rounded sections are opaque; margins and middle gap have no extra frame");
                    Check(At(-78,400).a>0.9f && At(1318,400).a>0.9f && At(-30,400).a<0.01f && At(1270,400).a<0.01f && At(-92,222).a<0.01f && At(1304,222).a<0.01f,"Both rounded floating strips render with transparent gaps and clipped corners");
                    Check(Mathf.Abs(background.r-30/255f)<0.012f && Mathf.Abs(card.r-43/255f)<0.012f,"Rendered charcoal matches display palette in "+QualitySettings.activeColorSpace+" color space");
                    var minX=10000f; var maxX=-10000f; var minY=10000f; var maxY=-10000f;
                    for(var y=130f;y<166;y+=0.5f) for(var x=1058f;x<1198;x+=0.5f) {var c=At(x,y); if(c.r>0.65f && c.g>0.65f && c.b>0.65f) {minX=Mathf.Min(minX,x);maxX=Mathf.Max(maxX,x);minY=Mathf.Min(minY,y);maxY=Mathf.Max(maxY,y);}}
                    Check(maxX>minX && Mathf.Abs((minX+maxX)/2-1128)<=1.5f && Mathf.Abs((minY+maxY)/2-148)<=1.5f,"Shockers page-switch ink is centered horizontally and vertically");
                    Check(Count(new Rect(440,260,460,140),Green)>40 && Count(new Rect(440,440,460,115),Green)>40,"Deselected loop renders a waveform above and below its current held level");
                    Check(Count(new Rect(54,245,180,17),c=>c.r>0.45f && c.g>0.45f && c.b>0.45f)>30,"Packed toy name renders readable glyph pixels in relocated rows");
                });
                Check(!ShaderUtil.ShaderHasError(renderer.sharedMaterial.shader),"Full PC shader compiles/renders without errors");
                animator.SetFloat(ToyControlContract.ReceivePrefix+"ShockerPage",1); for(var i=0;i<5;i++) animator.Update(.02f);
                Check((animator.GetCurrentAnimatorStateInfo(10).IsName("Shocker controls visible") || animator.GetCurrentAnimatorStateInfo(10).IsName("Shocker controls pulse")) && controller.layers[10].stateMachine.states.Where(s=>s.state.name.StartsWith("Shocker controls")).All(s=>s.state.behaviours.OfType<VRCAvatarParameterDriver>().Any(d=>d.localOnly && d.parameters.Any(p=>p.name==ToyControlContract.Prefix+"InShockerReady" && p.value==1))),"Shocker page pulses an explicit local acknowledgement (driver execution requires VRChat)");
                animator.SetBool(ToyControlContract.Prefix+"InputReady",false); for(var i=0;i<3;i++) animator.Update(.02f);
                Check(animator.GetCurrentAnimatorStateInfo(10).IsName("Toys / unavailable"),"Grab/input guard revokes shocker acknowledgement");
                animator.SetBool(ToyControlContract.Prefix+"InputReady",true); animator.SetFloat(ToyControlContract.ReceivePrefix+"ShockerPage",0); for(var i=0;i<5;i++) animator.Update(.02f);
                Check(animator.GetCurrentAnimatorStateInfo(10).IsName("Toys / unavailable"),"Returning to toys revokes shocker acknowledgement");
                var shockerFixture=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SHOCKWAVE_VR_FRAME")),"shocker-feedback.json");
                var shockerFrame=JsonUtility.FromJson<Frame>(File.ReadAllText(shockerFixture));
                Check(shockerFrame.version==7 && shockerFrame.values.Length==ToyControlContract.FeedbackFields().Length,"Shocker fixture uses the production v7 contract");
                var toySaved=new MaterialPropertyBlock(); renderer.GetPropertyBlock(toySaved);
                var shockerBlock=new MaterialPropertyBlock(); foreach(var v in shockerFrame.values) shockerBlock.SetFloat("_"+v.name,v.value); shockerBlock.SetFloat("_LinkOnline",1);
                foreach(var armed in new[] {false,true})
                {
                    shockerBlock.SetFloat("_ShockArmed",armed ? 1 : 0); renderer.SetPropertyBlock(shockerBlock);
                    Render(panel,Path.Combine(output,armed ? "vr-shocker-armed.png" : "vr-shocker.png"),(image,camera,board)=>
                    {
                        Color At(float x,float y) {var screen=camera.WorldToScreenPoint(board.TransformPoint(new Vector3(x/1240-.5f,.5f-(y-100)/608,0)));return image.GetPixel(Mathf.RoundToInt(screen.x),Mathf.RoundToInt(screen.y));}
                        var green=At(armed ? 878 : 1088,360); var grey=At(armed ? 1088 : 878,360);
                        Check(green.g>green.r+.2f && Mathf.Abs(grey.g-grey.r)<.04f,"Shocker shader shows the correct green circle; armed="+armed);
                        Check(At(454,500).g>At(454,500).r+.15f && At(614,550).g>At(614,550).r+.15f,"Both vertical slider tracks render their current values");
                        Check(At(333,400).a<.01f && At(24,105).a<.01f,"Shocker page preserves the rounded transparent panel silhouette");
                    });
                }
                renderer.SetPropertyBlock(toySaved);
                var pointerPreview=new MaterialPropertyBlock(); renderer.GetPropertyBlock(pointerPreview); pointerPreview.SetFloat("_Pointer",1); renderer.SetPropertyBlock(pointerPreview);
                Render(panel,Path.Combine(output,"vr-panel-pointer.png"));
                pointerPreview.SetFloat("_Pointer",0); renderer.SetPropertyBlock(pointerPreview);
                var designSavedBlock=new MaterialPropertyBlock(); renderer.GetPropertyBlock(designSavedBlock);
                var sevenBlock=new MaterialPropertyBlock(); renderer.GetPropertyBlock(sevenBlock);
                sevenBlock.SetFloat("_Count",7/256f); sevenBlock.SetFloat("_Pages",1/43f); sevenBlock.SetFloat("_Page",0); sevenBlock.SetFloat("_Selected",0);
                sevenBlock.SetFloat("_R6_State",ToyControlContract.PackBytes(0,1)); sevenBlock.SetFloat("_R6_RG",ToyControlContract.PackBytes(16,163)); sevenBlock.SetFloat("_R6_B",127/255f);
                var seventhName=ToyControlContract.Name("Function 7");
                for(var pair=0;pair<seventhName.Length;pair++) sevenBlock.SetFloat("_R6_"+ToyControlContract.Sample("Function",pair),seventhName[pair]);
                renderer.SetPropertyBlock(sevenBlock);
                Render(panel,Path.Combine(output,"vr-panel-seven.png"),(image,camera,board)=>
                {
                    Color At(float x,float y) {var screen=camera.WorldToScreenPoint(board.TransformPoint(new Vector3(x/1240-0.5f,0.5f-(y-100)/608,0))); return image.GetPixel(Mathf.RoundToInt(screen.x),Mathf.RoundToInt(screen.y));}
                    var rowInk=0; for(var y=642f;y<663;y++) for(var x=54f;x<210;x++) {var c=At(x,y);if(c.g>c.r+0.1f && c.g>c.b+0.05f) rowInk++;}
                    Check(rowInk>20,"Seventh function name renders in the single-page pager area");
                    var emptyGraph=At(600,450); Check(Mathf.Abs(emptyGraph.r-43/255f)<0.012f && Mathf.Abs(emptyGraph.g-45/255f)<0.012f,"No selection removes the avatar grid and waveform");
                });
                renderer.SetPropertyBlock(designSavedBlock);
                var batterySaved=new MaterialPropertyBlock();renderer.GetPropertyBlock(batterySaved);
                foreach(var state in new[]{0,1,2}) {
                    var batteryBlock=new MaterialPropertyBlock();renderer.GetPropertyBlock(batteryBlock);
                    batteryBlock.SetFloat("_R0_Battery",state==0 ? 0 : 68/101f);
                    var packed=(int)Math.Round(frame.values.Single(v=>v.name=="R0_State").value*65535);
                    batteryBlock.SetFloat("_R0_State",ToyControlContract.PackBytes(packed&255,state==2 ? (packed>>8)&~1 : (packed>>8)|1));renderer.SetPropertyBlock(batteryBlock);
                    Render(panel,Path.Combine(output,"vr-battery-"+state+".png"),(image,camera,board)=> {
                        var ink=0;
                        for(var y=278;y<292;y++) for(var x=146;x<218;x++) {var p=camera.WorldToScreenPoint(board.TransformPoint(new Vector3(x/1240f-.5f,.5f-(y-100)/608f,0)));var c=image.GetPixel(Mathf.RoundToInt(p.x),Mathf.RoundToInt(p.y));if(c.r>.4f && c.g>.4f && c.b>.4f) ink++;}
                        Check(state==1 ? ink>10 : ink==0,"Battery text appears only for a connected toy with a known reading: "+state);
                    });
                }
                renderer.SetPropertyBlock(batterySaved);
                var savedBlock=new MaterialPropertyBlock(); renderer.GetPropertyBlock(savedBlock); var emptyBlock=new MaterialPropertyBlock(); renderer.GetPropertyBlock(emptyBlock); emptyBlock.SetFloat("_Count",0);emptyBlock.SetFloat("_Pages",0);emptyBlock.SetFloat("_Online",0);renderer.SetPropertyBlock(emptyBlock);
                Render(panel,Path.Combine(output,"vr-panel-empty.png"),(image,camera,board)=>
                {
                    var bright=0;
                    for(var row=0;row<ToyControlContract.Rows;row++) for(var y=278+row*64;y<292+row*64;y++) for(var x=146;x<187;x++) {var screen=camera.WorldToScreenPoint(board.TransformPoint(new Vector3(x/1240f-0.5f,0.5f-(y-100)/608f,0))); var c=image.GetPixel(Mathf.RoundToInt(screen.x),Mathf.RoundToInt(screen.y)); if(c.r>0.12f || c.g>0.12f || c.b>0.12f) bright++;}
                    Check(bright==0,"Empty toy list has no ghost battery labels");
                });
                renderer.SetPropertyBlock(savedBlock);
                var variants=new ShaderVariantCollection();
                foreach(var mesh in host.GetComponentsInChildren<Renderer>(true)) {mesh.sharedMaterial.enableInstancing=true; mesh.sharedMaterial.EnableKeyword("STEREO_INSTANCING_ON"); variants.Add(new ShaderVariantCollection.ShaderVariant(mesh.sharedMaterial.shader,UnityEngine.Rendering.PassType.Normal,new[] {"INSTANCING_ON","STEREO_INSTANCING_ON"}));} variants.WarmUp();
                Render(panel,Path.Combine(output,"vr-panel-stereo-variant.png"));
                Check(host.GetComponentsInChildren<Renderer>(true).All(m=>!ShaderUtil.ShaderHasError(m.sharedMaterial.shader)),"Panel, strips, dots and anchored beams compile/render their stereo-instancing variants");
                foreach(var mesh in host.GetComponentsInChildren<Renderer>(true)) mesh.sharedMaterial.DisableKeyword("STEREO_INSTANCING_ON"); UnityEngine.Object.DestroyImmediate(variants);
                if(Environment.GetEnvironmentVariable("SHOCKWAVE_VR_BENCHMARK")=="1") Benchmark(panel,folder,frame,output);
                var export=Path.Combine(output,"generated");Directory.CreateDirectory(export);
                foreach(var file in new[] {"Panel.shader","Panel.cginc","Labels.png","ShockerLabels.png","Glyphs.png","GlyphMetrics.png","Status.png"}) File.Copy(folder+"/"+file,Path.Combine(export,file),true);
                animator.SetInteger(ToyControlContract.Prefix+"HandMode",1); for(var i=0;i<5;i++) animator.Update(0.02f);
                Check(!panel.GetComponent<VRCParentConstraint>().FreezeToWorld,"Follow unfreezes placement");
                for(var i=0;i<120;i++) animator.Update(0.02f);
                renderer.GetPropertyBlock(block); var fresh=block.GetFloat("_LinkOnline"); if(fresh==0) fresh=renderer.sharedMaterial.GetFloat("_LinkOnline");
                Check(fresh<0.5f,"Backend heartbeat timeout switches visual state offline");
                animator.SetBool(ToyControlContract.Prefix+"RxBeat",!animator.GetBool(ToyControlContract.Prefix+"RxBeat")); for(var i=0;i<5;i++) animator.Update(0.02f);
                renderer.GetPropertyBlock(block); fresh=block.GetFloat("_LinkOnline"); if(fresh==0) fresh=renderer.sharedMaterial.GetFloat("_LinkOnline");
                Check(fresh>0.5f,"Fresh opposite backend pulse restores online state");
                animator.SetBool("IsLocal",false); for(var i=0;i<5;i++) animator.Update(0.02f); Check(!panel.gameObject.activeSelf,"Other viewers cannot see the panel");
                Check(!host.transform.Find("HandAnchors").gameObject.activeSelf,"Hidden/other-viewer state also disables hand-anchor solving");
                var assetHash=File.ReadAllBytes(folder+"/FX.controller"); var selected=Selection.activeObject; string updateFolder=null; GameObject updated=null;
                try
                {
                    host.name=ToyControlSetupWindow.LegacyHostName;
                    Check(ToyControlSetupWindow.ValidateAvatar(avatar,true)==null,"ShockWave recognizes the previous branded host for explicit update");
                    var captured=new ToyControlSetupWindow.HandPlacements {inHand=new ToyControlSetupWindow.HandPose {captured=true,position=new Vector3(-.18f,.02f,.06f),rotation=new Vector3(10,20,30)},backHand=new ToyControlSetupWindow.HandPose {captured=true,position=new Vector3(0,.06f,.08f),rotation=new Vector3(90,0,0)}};
                    updated=ToyControlSetupWindow.Build(avatar,0.04f,0.10f,out updateFolder,replaceExisting:true,resolveBone:Bone,placements:captured);
                    Check(Vector3.Distance(updated.transform.Find("HandAnchors/RightHandPosition/SpawnRight").localPosition,captured.inHand.position)<.00001f && Vector3.Distance(updated.transform.Find("HandAnchors/RightHandPosition/BackRight").localPosition,captured.backHand.position)<.00001f,"Explicit avatar update uses both captured hand-relative placements");
                    Check(File.ReadAllText(updateFolder+"/Placement.json").Contains("true"),"Captured placements persist in the generated content folder for future updates");
                    Check(updated.name==ToyControlSetupWindow.HostName && host.name==ToyControlSetupWindow.LegacyHostName,"Update prepares the ShockWave host without renaming the existing installation");
                    Check(updateFolder!=folder && host!=null && Selection.activeObject==selected && assetHash.SequenceEqual(File.ReadAllBytes(folder+"/FX.controller")),"Preparing a user-requested update preserves the old host, assets and scene selection until the explicit swap");
                }
                finally {if(updated!=null) UnityEngine.Object.DestroyImmediate(updated);if(updateFolder!=null && AssetDatabase.IsValidFolder(updateFolder)) AssetDatabase.DeleteAsset(updateFolder);}
                File.WriteAllText(Path.Combine(output,"report.json"),"{\"Passed\":"+passed+",\"PhysicalOutput\":false,\"UploadedAvatar\":false}");
                Debug.Log("VR DEMO VERIFICATION COMPLETE "+passed+" checks");
            }
            catch(Exception ex) {Debug.LogException(ex); if(!string.IsNullOrWhiteSpace(output)) File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString()); EditorApplication.Exit(1);}
            finally {if(root!=null) UnityEngine.Object.DestroyImmediate(root); if(folder!=null && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder); EditorSceneManager.ClosePreviewScene(scene);}
        }
        private static void VerifyConstraintRuntime(Transform avatar,Action<bool,string> check)
        {
            var manager=typeof(VRCConstraintBase).Assembly.GetType("VRC.Dynamics.VRCConstraintManager");
            var flags=System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
            manager.GetMethod("Initialize",flags).Invoke(null,null);
            var edit=manager.GetProperty("CanExecuteConstraintJobsInEditMode",flags);var prior=(bool)edit.GetValue(null);edit.SetValue(null,true);
            var parent=new GameObject("Scratch constraint runtime");parent.transform.SetParent(avatar,false);
            var hand=new GameObject("Wrist");hand.transform.SetParent(parent.transform,false);hand.transform.localPosition=new Vector3(.2f,1,.1f);
            var board=new GameObject("World board");board.transform.SetParent(parent.transform,false);board.transform.position=new Vector3(-.3f,1.2f,.5f);
            var c=board.AddComponent<VRCParentConstraint>();
            try {
                c.Sources.Add(new VRCConstraintSource(hand.transform,1,Vector3.zero,Vector3.zero));c.IsActive=true;c.Locked=true;
                if(!(bool)manager.GetMethod("IsConstraintRegistered",flags).Invoke(null,new object[]{c})) manager.GetMethod("RegisterConstraint",flags).Invoke(null,new object[]{c});
                manager.GetMethod("Sdk_ManuallyRefreshGroups",flags).Invoke(null,new object[]{new VRCConstraintBase[]{c}});
                void Solve() {c.ApplyConfigurationChanges();VRCDynamicsScheduler.UpdateConstraints(true);}
                Solve();c.FreezeToWorld=true;Solve();var position=board.transform.position;var rotation=board.transform.rotation;
                parent.transform.SetPositionAndRotation(new Vector3(1,.2f,-.8f),Quaternion.Euler(5,75,-12));Solve();
                check(Vector3.Distance(position,board.transform.position)<.0001f && Quaternion.Angle(rotation,board.transform.rotation)<.02f,"Actual SDK keeps a frozen board fixed under root translation and rotation");
                c.RebakeOffsetsWhenUnfrozen=true;c.FreezeToWorld=false;Solve();
                check(Vector3.Distance(position,board.transform.position)<.0001f && Quaternion.Angle(rotation,board.transform.rotation)<.02f,"Actual SDK rebakes a grip/fit without a position or rotation jump");
                var local=hand.transform.InverseTransformPoint(board.transform.position);var localRotation=Quaternion.Inverse(hand.transform.rotation)*board.transform.rotation;
                hand.transform.SetPositionAndRotation(hand.transform.position+new Vector3(.1f,.05f,.2f),Quaternion.Euler(-15,50,20)*hand.transform.rotation);Solve();
                check(Vector3.Distance(board.transform.position,hand.transform.TransformPoint(local))<.0001f && Quaternion.Angle(board.transform.rotation,hand.transform.rotation*localRotation)<.02f,"Actual SDK carries a rebaked panel with wrist translation and rotation");
                c.FreezeToWorld=true;Solve();position=board.transform.position;rotation=board.transform.rotation;
                parent.transform.SetPositionAndRotation(new Vector3(-1,0,1),Quaternion.Euler(0,-110,0));Solve();
                check(Vector3.Distance(position,board.transform.position)<.0001f && Quaternion.Angle(rotation,board.transform.rotation)<.02f,"Actual SDK release returns to world lock after moving the grip");
            }
            finally {manager.GetMethod("UnregisterConstraint",flags).Invoke(null,new object[]{c});UnityEngine.Object.DestroyImmediate(parent);edit.SetValue(null,prior);}
        }
        private static void VerifyPlacements(GameObject host,Animator animator,Transform avatar,Transform wrist,Action<bool,string> check)
        {
            var controller=(AnimatorController)animator.runtimeAnimatorController;
            var constraint=host.transform.Find("Panel").GetComponent<VRCParentConstraint>();
            check(AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(controller)).OfType<AnimationClip>().SelectMany(AnimationUtility.GetCurveBindings).All(b=>!((b.propertyName.StartsWith("Sources.source4.") || b.propertyName.StartsWith("Sources.source5.")) && b.propertyName.Contains("Offset"))),"Runtime wrist-fit offsets are never overwritten by animation curves");
            var contacts=host.transform.Find("Panel/Size/Contacts");
            var stateHashes=Enumerable.Repeat(int.MinValue,controller.layers.Length).ToArray();var frames=0;
            void Set(string name,bool value) => animator.SetBool(ToyControlContract.Prefix+name,value);
            void Mode(int value) => animator.SetInteger(ToyControlContract.Prefix+"HandMode",value);
            void Step(int count=20) {
                for(var i=0;i<count;i++) {
                    if(frames++%10==0) Set("RxBeat",!animator.GetBool(ToyControlContract.Prefix+"RxBeat"));
                    animator.Update(.02f);
                    // Unity does not execute VRC parameter drivers. Emulate their
                    // actual Set operations once on entry, without hardware/OSC.
                    for(var layer=0;layer<controller.layers.Length;layer++) {
                        var info=animator.GetCurrentAnimatorStateInfo(layer);if(stateHashes[layer]==info.fullPathHash) continue;stateHashes[layer]=info.fullPathHash;
                        var state=controller.layers[layer].stateMachine.states.First(s=>info.IsName(s.state.name)).state;
                        foreach(var p in state.behaviours.OfType<VRCAvatarParameterDriver>().SelectMany(d=>d.parameters)) {
                            var type=animator.parameters.Single(a=>a.name==p.name).type;
                            if(type==AnimatorControllerParameterType.Bool) animator.SetBool(p.name,p.value!=0);
                            else if(type==AnimatorControllerParameterType.Int) animator.SetInteger(p.name,(int)p.value);
                            else animator.SetFloat(p.name,p.value);
                        }
                    }
                }
            }
            bool At(string name) => animator.GetCurrentAnimatorStateInfo(1).IsName(name);
            foreach(var grip in ToyControlSetupWindow.GripInputs) Set(grip,false);
            Mode(0);Step();check(constraint.FreezeToWorld && At("Dropped"),"Both hand modes off remains world-locked");
            foreach(var hand in new[]{"L","R"}) foreach(var pose in new[]{2,6,7}) {
                Set("Grab"+hand+"Right",true);animator.SetInteger(hand=="L" ? "GestureLeft" : "GestureRight",pose);Step();
                check(constraint.FreezeToWorld && At("Dropped"),hand+" open/pistol/Thumbs Up cannot grab a handle");
                Set("Grab"+hand+"Right",false);animator.SetInteger(hand=="L" ? "GestureLeft" : "GestureRight",0);
            }
            foreach(var pointer in new[]{0f,1f}) foreach(var hand in new[]{"L","R"}) foreach(var edge in new[]{"Left","Right"}) {
                var gesture=hand=="L" ? "GestureLeft" : "GestureRight";var free=hand=="L" ? "R" : "L";var source=hand=="L" ? 1 : 2;
                animator.SetFloat(ToyControlContract.ReceivePrefix+"Pointer",pointer);Set("Grab"+hand+edge,true);animator.SetInteger(gesture,1);Step();
                check(At(hand=="L" ? "Grab left hand" : "Grab right hand") && !constraint.FreezeToWorld && constraint.Sources[source].Weight==1 && contacts.gameObject.activeSelf && animator.GetBool(ToyControlContract.Prefix+"InputReady"),"Stable "+hand+" "+edge+" grip allows free-hand input in mode "+pointer);
                if(pointer==1) {
                    foreach(var h in new[]{"L","R"}) animator.SetFloat(ToyControlContract.ReceivePrefix+"Pointer"+h+"Valid",1);
                    animator.SetInteger(free=="L" ? "GestureLeft" : "GestureRight",6);Step();animator.SetInteger(free=="L" ? "GestureLeft" : "GestureRight",7);Step();
                    check(animator.GetCurrentAnimatorStateInfo(free=="L" ? 6 : 7).IsName("Click / draw") && animator.GetCurrentAnimatorStateInfo(hand=="L" ? 6 : 7).IsName("Release before pointing"),"Only the free "+free+" hand can click/draw while holding with "+hand);
                    animator.SetInteger(free=="L" ? "GestureLeft" : "GestureRight",3);Step();
                    check(animator.GetCurrentAnimatorStateInfo(free=="L" ? 6 : 7).IsName("Aim"),"Free "+free+" Finger Point releases while the other hand holds the panel");
                    animator.SetInteger(free=="L" ? "GestureLeft" : "GestureRight",1);Step();
                    check(animator.GetCurrentAnimatorStateInfo(free=="L" ? 6 : 7).IsName("Click / draw") && animator.GetCurrentAnimatorStateInfo(hand=="L" ? 6 : 7).IsName("Release before pointing"),"Free "+free+" Fist clicks without turning the gripping "+hand+" Fist into a click");
                    animator.SetInteger(free=="L" ? "GestureLeft" : "GestureRight",0);
                } else check(contacts.Find("InTouch").gameObject.activeInHierarchy,"Free finger drawing remains enabled while holding in Touch mode");
                animator.SetInteger(gesture,0);Step();check(constraint.FreezeToWorld && At("Release touch guard") && !contacts.gameObject.activeSelf,"Releasing a world grip guards input until the hand leaves its strip");
                Set("Grab"+hand+edge,false);Step();check(At("Dropped") && constraint.FreezeToWorld && contacts.gameObject.activeSelf,"World release never resumes avatar/head following");
            }
            foreach(var pair in new[]{(1,0,"Follow right hand",4,"Adjusted in right hand","FitInHand"),(2,3,"Follow back of right hand",5,"Adjusted back of right hand","FitBackHand")}) {
                Mode(pair.Item1);Set("GrabRRight",true);animator.SetInteger("GestureRight",1);Step();
                check(At(pair.Item3) && constraint.Sources[pair.Item2].Weight==1,"Mounted right wrist cannot self-grab preset "+pair.Item1);
                animator.SetInteger("GestureRight",7);Step();check(animator.GetCurrentAnimatorStateInfo(7).IsName("Release before pointing"),"Mounted right hand cannot click its own panel");
                Set("GrabLLeft",true);animator.SetInteger("GestureLeft",1);Step();check(At("Grab left hand"),"Free left hand can reposition attached preset "+pair.Item1);
                check(!animator.GetBool(ToyControlContract.Prefix+"FitInHand") && !animator.GetBool(ToyControlContract.Prefix+"FitBackHand") && constraint.Sources[3].ParentPositionOffset==Vector3.zero && constraint.Sources[3].ParentRotationOffset==Vector3.zero,"New grips invalidate previous temporary fits and restore the back-hand preset offsets");
                animator.SetInteger("GestureLeft",0);Step(40);
                check(At(pair.Item5) && constraint.Sources[pair.Item4].Weight==1 && animator.GetBool(ToyControlContract.Prefix+pair.Item6),"Releasing after adjustment captures the selected wrist fit even while strip contact remains");
                Set("GrabLLeft",false);Set("GrabRRight",false);Mode(0);Step();check(At("Dropped") && constraint.FreezeToWorld,"Turning an adjusted attachment off world-locks it");
                Mode(pair.Item1);Step();check(At(pair.Item5),"The adjusted fit survives toggling its hand mode off and on");
                Set("Visible",false);Step();Set("Visible",true);Step(40);check(At(pair.Item5),"The adjusted fit survives hiding and showing this avatar session");
                Mode(0);animator.SetInteger("GestureRight",0);Step();
            }
            // A floating grip can become a right-wrist attachment without Unity.
            Set("GrabRRight",true);animator.SetInteger("GestureRight",1);Step();Mode(1);Step();
            check(At("Grab right hand"),"Selecting a hand mode while gripping does not jump to a preset");
            animator.SetInteger("GestureRight",0);Step(40);check(At("Adjusted in right hand"),"Releasing the positioned grip attaches to the right wrist on the fly");
            Set("GrabRRight",false);Mode(0);animator.SetFloat(ToyControlContract.ReceivePrefix+"Pointer",0);Step();
            var preview=new GameObject("Scratch capture pose");preview.transform.SetParent(host.transform,false);
            var offset=new Vector3(.02f,.04f,.08f);var rotation=Quaternion.Euler(25,-10,40);
            preview.transform.SetPositionAndRotation(wrist.position+wrist.rotation*offset,wrist.rotation*rotation);
            var captured=ToyControlSetupWindow.CapturePose(wrist,preview.transform);
            var position=avatar.position;var turn=avatar.rotation;
            avatar.SetPositionAndRotation(position+new Vector3(1.2f,.15f,-.4f),Quaternion.Euler(-38,100,40)*turn);
            var moved=ToyControlSetupWindow.CapturePose(wrist,preview.transform);
            check(Vector3.Distance(captured.position,offset)<.00001f && Quaternion.Angle(Quaternion.Euler(captured.rotation),rotation)<.01f && Vector3.Distance(moved.position,captured.position)<.00001f,"Pose capture preserves the adjusted placement across avatar position and rotation");
            avatar.SetPositionAndRotation(position,turn);UnityEngine.Object.DestroyImmediate(preview);
        }
        private static void VerifyPointers(GameObject host,Animator animator,Action<bool,string> check,string output)
        {
            var panel=host.transform.Find("Panel");var tracking=panel.Find("PoseTracking");var contacts=panel.Find("Size/Contacts");var hits=contacts.Find("PointerHits");var pointer=host.transform.Find("Pointer");
            check(host.GetComponentsInChildren<VRCRaycast>(true).Length==0 && host.GetComponentsInChildren<Collider>(true).Length==0,"Analytic pointer has no raycast or physical collider and cannot hit a floating scene box");
            check(tracking.parent==panel && tracking.GetComponentsInChildren<VRCContactReceiver>(true).Length==16,"Pose sensor volume is independent of panel radial size");
            check(host.GetComponentsInChildren<VRCContactSender>(true).Length==4 && host.GetComponentsInChildren<VRCContactSender>(true).All(s=>s.localOnly),"Both hands encode two local-only pose points");
            var controller=(AnimatorController)animator.runtimeAnimatorController;
            var menu=AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(AssetDatabase.GetAssetPath(controller).Replace("FX.controller","Controls.asset"));
            check(menu.controls.Select(c=>c.name).SequenceEqual(new[]{"Show panel","In right hand","Back of right hand","Panel size"}),"Radial menu contains the two hand presets and requested display controls");
            check(Mathf.Abs(animator.GetFloat(ToyControlContract.Prefix+"Size")-.5f)<.001f,"Explicit radial setting survives movement checks");
            var anchor=host.transform.Find("HandAnchors/RightHandPosition").GetComponent<VRCParentConstraint>();
            check(anchor.AffectsRotationX && anchor.AffectsRotationY && anchor.AffectsRotationZ,"Follow right hand includes all rotation axes");
            foreach(var r in contacts.GetComponentsInChildren<VRCContactReceiver>(true).Where(r=>r.parameter.Contains("InRow")))
            {
                var dimensions=new SerializedObject(r).FindProperty("size").vector3Value;
                check(dimensions.y<=40/1240f*.74f+.00001f && dimensions.z<=.0081f,"Inset row contact has a vertical dead zone and shallower depth: "+r.name);
            }
            void Step(){for(var i=0;i<8;i++) animator.Update(.02f);}
            check(controller.layers[3].stateMachine.states.All(s=>s.state.behaviours.OfType<VRCAvatarParameterDriver>().SelectMany(d=>d.parameters).Any(p=>p.name==ToyControlContract.Prefix+"AppReady" && p.value==(s.state.name.StartsWith("App pulse") ? 1 : 0))),"Existing backend freshness states explicitly gate pointer availability");
            // VRChat executes these drivers; scratch Animator evaluation supplies their result.
            animator.SetBool(ToyControlContract.Prefix+"AppReady",true);animator.SetBool(ToyControlContract.Prefix+"InputReady",true);animator.SetFloat(ToyControlContract.ReceivePrefix+"Pointer",1);
            animator.SetInteger("GestureLeft",7);animator.SetInteger("GestureRight",7);Step();
            check(pointer.gameObject.activeSelf && tracking.gameObject.activeSelf && !contacts.Find("InTouch").gameObject.activeSelf,"Pointer mode enables pose sensors and disables physical finger selection");
            foreach(var hand in new[]{"L","R"})
            {
                var layer=hand=="L" ? 6 : 7;var visual=hand=="L" ? 8 : 9;var gesture=hand=="L" ? "GestureLeft" : "GestureRight";
                check(animator.GetCurrentAnimatorStateInfo(layer).IsName("Release before pointing"),hand+" held Thumbs Up must release when pointer mode starts");
                animator.SetFloat(ToyControlContract.ReceivePrefix+"Pointer",0);Step();animator.SetInteger(gesture,1);animator.SetFloat(ToyControlContract.ReceivePrefix+"Pointer",1);Step();
                check(animator.GetCurrentAnimatorStateInfo(layer).IsName("Release before pointing"),hand+" held Fist must release when pointer mode starts");
                animator.SetInteger(gesture,3);Step();check(animator.GetCurrentAnimatorStateInfo(layer).IsName("Aim"),hand+" Finger Point arms aiming without clicking");
                animator.SetInteger(gesture,1);Step();check(animator.GetCurrentAnimatorStateInfo(layer).IsName("Click / draw"),hand+" fresh Fist enters the existing click/draw state");
                var heldTime=animator.GetCurrentAnimatorStateInfo(layer).normalizedTime;
                animator.SetInteger(gesture,7);Step();check(animator.GetCurrentAnimatorStateInfo(layer).IsName("Click / draw") && animator.GetCurrentAnimatorStateInfo(layer).normalizedTime>heldTime,hand+" Fist to Thumbs Up keeps the same state without a second press");
                heldTime=animator.GetCurrentAnimatorStateInfo(layer).normalizedTime;
                animator.SetInteger(gesture,1);Step();check(animator.GetCurrentAnimatorStateInfo(layer).IsName("Click / draw") && animator.GetCurrentAnimatorStateInfo(layer).normalizedTime>heldTime,hand+" Thumbs Up to Fist keeps the same state without a second press");
                animator.SetBool(ToyControlContract.Prefix+"AppReady",false);Step();animator.SetBool(ToyControlContract.Prefix+"AppReady",true);Step();
                check(animator.GetCurrentAnimatorStateInfo(layer).IsName("Release before pointing"),hand+" heartbeat recovery cannot replay a held Fist click");
                animator.SetInteger(gesture,3);Step();check(animator.GetCurrentAnimatorStateInfo(layer).IsName("Aim"),hand+" Finger Point releases the held click after recovery");
                var origin=pointer.Find(hand+"/Origin");var tip=origin.Find("Tip");
                check(Mathf.Abs(tip.localPosition.z-.2f)<.00001f && tip.parent==origin,hand+" calibrated aim uses a rigid 20cm baseline");
                var beam=pointer.Find(hand+"/Beam").GetComponent<SkinnedMeshRenderer>();var marker=hits.Find(hand+"/Marker").GetComponent<MeshRenderer>();
                check(beam.sharedMesh.vertexCount==8 && beam.bones.SequenceEqual(new[]{origin,hits.Find(hand)}),hand+" beam stays attached to the hand and panel cursor");
                check(beam.localBounds.size.x<1 && beam.localBounds.size.y<1 && beam.localBounds.size.z<2.5f,hand+" beam no longer reserves a 6.1 metre bounds cube");
                animator.SetInteger(gesture,6);animator.SetFloat(ToyControlContract.ReceivePrefix+"Pointer"+hand+"Valid",1);Step();
                check(marker.enabled && beam.enabled && animator.GetCurrentAnimatorStateInfo(visual).IsName("On panel"),hand+" valid surface cursor displays beam and dot together");
                foreach(var p in new[]{new Vector2(.1f,.2f),new Vector2(.85f,.75f)})
                {
                    animator.SetFloat(ToyControlContract.ReceivePrefix+"Pointer"+hand+"X",p.x);animator.SetFloat(ToyControlContract.ReceivePrefix+"Pointer"+hand+"Y",p.y);Step();
                    var cursor=hits.Find(hand).localPosition;
                    check(Mathf.Abs(cursor.x-(p.x-.5f)*.74f)<.0001f && Mathf.Abs(cursor.y-(p.y-.5f)*.74f*608/1240)<.0001f && Mathf.Abs(cursor.z+.0002f)<.00001f,hand+" animated cursor stays exactly on the board at "+p);
                    VerifyBeamEndpoints(beam,origin,hits.Find(hand),check,hand+" analytic cursor");
                }
                var initialDirection=origin.forward;var index=pointer.Find(hand).GetComponent<VRCParentConstraint>().Sources[0].SourceTransform.Find(hand=="L" ? "Synthetic left index proximal" : "Synthetic right index proximal");
                for(var pose=0;pose<8;pose++) {index.localRotation=Quaternion.Euler(pose*15,pose*4,pose*7);animator.SetInteger(gesture,pose);Step();check(Vector3.Dot(initialDirection,origin.forward)>.9999f && animator.GetCurrentAnimatorStateInfo(layer).IsName(pose==1 || pose==7 ? "Click / draw" : "Aim"),hand+" pose "+pose+" cannot steer the rigid aim; Fist and Thumbs Up click");}
                index.localRotation=Quaternion.identity;
                animator.SetFloat(ToyControlContract.ReceivePrefix+"Pointer"+hand+"Valid",0);Step();check(!marker.enabled && !beam.enabled,hand+" invalid/outside/grazing pointer hides both visuals");
            }
            VerifyPoseCoordinates(host,check);
            animator.SetBool(ToyControlContract.Prefix+"AppReady",false);Step();check(!pointer.gameObject.activeSelf && !tracking.gameObject.activeSelf && pointer.GetComponentsInChildren<SkinnedMeshRenderer>(true).All(r=>!r.enabled),"App freshness loss hides the last pointer and disables tracking");
            animator.SetBool(ToyControlContract.Prefix+"AppReady",true);Step();check(pointer.gameObject.activeSelf,"A fresh app heartbeat can restore aiming with release-before-click");
            animator.SetBool(ToyControlContract.Prefix+"InputReady",false);Step();check(!pointer.gameObject.activeSelf && !tracking.gameObject.activeSelf,"Placement guard disables pointer tracking and requires a fresh click");
            animator.SetBool(ToyControlContract.Prefix+"InputReady",true);animator.SetFloat(ToyControlContract.ReceivePrefix+"Pointer",0);Step();check(contacts.Find("InTouch").gameObject.activeSelf && !tracking.gameObject.activeSelf,"Touch mode restores finger controls without a scene collider");
            VerifyBeamRender(pointer.Find("L/Beam").GetComponent<SkinnedMeshRenderer>(),output,check);
            VerifySurfaceDot(hits.Find("L/Marker").GetComponent<MeshRenderer>(),panel.Find("Size/Board").GetComponent<MeshRenderer>(),output,check);
        }
        private static void VerifyPoseCoordinates(GameObject host,Action<bool,string> check)
        {
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance;
            var mgr=ContactManager.Inst;if(mgr==null){var o=new GameObject("Scratch pose contact manager");mgr=o.AddComponent<ContactManager>();typeof(ContactManager).GetMethod("Awake",flags).Invoke(mgr,null);}
            var all=host.GetComponentsInChildren<VRCContactSender>(true).Cast<ContactBase>().Concat(host.transform.Find("Panel/PoseTracking").GetComponentsInChildren<VRCContactReceiver>(true)).ToArray();
            foreach(var c in all) {typeof(ContactBase).GetField("manager",flags).SetValue(c,mgr);typeof(ContactBase).GetMethod("InitShape",flags).Invoke(c,new object[]{false});var collision=typeof(ContactManager).GetField("collision",flags).GetValue(mgr);collision.GetType().GetMethod("AddShape",flags).Invoke(collision,new[]{typeof(ContactBase).GetField("shape",flags).GetValue(c)});}
            var sender=host.GetComponentsInChildren<VRCContactSender>(true).First();var saved=sender.transform.position;var tag=sender.collisionTags[0];var tracking=host.transform.Find("Panel/PoseTracking");
            var receivers=tracking.GetComponentsInChildren<VRCContactReceiver>(true).Where(r=>r.collisionTags.Contains(tag)).ToArray();
            foreach(var rotation in new[]{Quaternion.identity,Quaternion.Euler(20,65,-12)})
            {
                var panel=host.transform.Find("Panel");var savedRot=panel.rotation;panel.rotation=rotation;
                foreach(var p in new[]{new Vector3(.25f,.15f,-.5f),new Vector3(-.5f,-.2f,.7f),new Vector3(1.1f,.3f,-.6f)})
                {
                    sender.transform.position=tracking.TransformPoint(p);foreach(var c in all) typeof(ContactBase).GetMethod("PerformShapeUpdate",flags).Invoke(c,null);
                    var collision=typeof(ContactManager).GetField("collision",flags).GetValue(mgr);collision.GetType().GetMethod("UpdateAndSchedule",flags).Invoke(collision,new object[]{.016f,false,new Unity.Jobs.JobHandle()});collision.GetType().GetMethod("Complete",flags).Invoke(collision,null);
                    var v=new Vector3(receivers.Single(r=>r.parameter.EndsWith("X")).CalculateProximity(sender),receivers.Single(r=>r.parameter.EndsWith("Y")).CalculateProximity(sender),receivers.Single(r=>r.parameter.EndsWith("Z")).CalculateProximity(sender));
                    check(Vector3.Distance(p,(v-Vector3.one*.5f)*3.5f)<.003f,"Actual SDK pose coordinates are panel-relative across position and rotation: "+p);
                }
                panel.rotation=savedRot;
            }
            sender.transform.position=saved;
        }

        private static void VerifyBeamRender(SkinnedMeshRenderer source,string output,Action<bool,string> check)
        {
            var beam=UnityEngine.Object.Instantiate(source.gameObject); var start=new GameObject("Scratch beam start"); var end=new GameObject("Scratch beam end"); var obj=new GameObject("Scratch beam camera");
            foreach(var item in new[] {beam,start,end,obj}) SceneManager.MoveGameObjectToScene(item,source.gameObject.scene);
            var center=new Vector3(100,100,100); start.transform.position=center+new Vector3(-.004f,-.002f,0); end.transform.position=center+new Vector3(.004f,.002f,0);
            start.transform.rotation=Quaternion.LookRotation(end.transform.position-start.transform.position); beam.transform.SetPositionAndRotation(center,Quaternion.identity); beam.transform.localScale=Vector3.one; beam.SetActive(true);
            var renderer=beam.GetComponent<SkinnedMeshRenderer>(); renderer.bones=new[] {start.transform,end.transform}; renderer.rootBone=start.transform; renderer.enabled=true;
            var camera=obj.AddComponent<Camera>(); camera.cameraType=CameraType.Preview; camera.scene=source.gameObject.scene;
            camera.transform.SetPositionAndRotation(center-Vector3.forward*.25f,Quaternion.identity); camera.orthographic=true; camera.orthographicSize=.006f; camera.nearClipPlane=.01f; camera.farClipPlane=2; camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.clear;
            var rt=RenderTexture.GetTemporary(128,128,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB); var old=RenderTexture.active; camera.targetTexture=rt; Texture2D image=null;
            try
            {
                image=new Texture2D(128,128,TextureFormat.RGBA32,false);
                camera.Render(); RenderTexture.active=rt; image.ReadPixels(new Rect(0,0,128,128),0,0); image.Apply(); var color=image.GetPixel(64,64);
                File.WriteAllBytes(Path.Combine(output,"pointer-anchored-beam.png"),image.EncodeToPNG());
                check(color.g>.58f && color.r<.12f && Mathf.Abs(color.b-127/255f)<.03f,"Actual generated skinned beam renders between its two endpoints on the GPU");
                renderer.enabled=false; camera.Render(); image.ReadPixels(new Rect(0,0,128,128),0,0); image.Apply();
                check(image.GetPixel(64,64).a<.01f,"Disabling the beam renderer removes its GPU pixels");
            }
            finally {RenderTexture.active=old; camera.targetTexture=null; RenderTexture.ReleaseTemporary(rt); if(image!=null) UnityEngine.Object.DestroyImmediate(image); foreach(var item in new[] {beam,start,end,obj}) UnityEngine.Object.DestroyImmediate(item);}
        }
        private static void VerifyBeamEndpoints(SkinnedMeshRenderer beam,Transform origin,Transform result,Action<bool,string> check,string label)
        {
            var mesh=new Mesh();
            try
            {
                beam.BakeMesh(mesh); var vertices=mesh.vertices; var start=Vector3.zero; var end=Vector3.zero;
                for(var i=0;i<4;i++) {start+=beam.transform.TransformPoint(vertices[i])/4; end+=beam.transform.TransformPoint(vertices[i+4])/4;}
                check(Vector3.Distance(start,origin.position)<.0001f && Vector3.Distance(end,result.position)<.0001f,"Actual baked beam endpoints match their live transforms: "+label);
            }
            finally {UnityEngine.Object.DestroyImmediate(mesh);}
        }
        private static void VerifySurfaceDot(MeshRenderer marker,MeshRenderer board,string output,Action<bool,string> check)
        {
            var dot=UnityEngine.Object.Instantiate(marker.gameObject); dot.name="Scratch surface dot render";
            SceneManager.MoveGameObjectToScene(dot,marker.gameObject.scene); dot.transform.SetPositionAndRotation(new Vector3(100,100,100),Quaternion.identity);
            dot.transform.localScale=Vector3.one*.006f; dot.SetActive(true); dot.GetComponent<MeshRenderer>().enabled=true;
            var obj=new GameObject("Scratch surface dot camera"); SceneManager.MoveGameObjectToScene(obj,marker.gameObject.scene);
            var camera=obj.AddComponent<Camera>(); camera.cameraType=CameraType.Preview; camera.scene=marker.gameObject.scene;
            camera.transform.SetPositionAndRotation(dot.transform.position-Vector3.forward*.25f,Quaternion.identity);
            camera.orthographic=true; camera.orthographicSize=.006f; camera.nearClipPlane=.01f; camera.farClipPlane=2; camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.clear;
            var rt=RenderTexture.GetTemporary(128,128,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB); var old=RenderTexture.active; camera.targetTexture=rt;
            Texture2D image=null; GameObject backing=null,occluder=null; Material cover=null;
            try
            {
                camera.Render(); RenderTexture.active=rt; image=new Texture2D(128,128,TextureFormat.RGBA32,false); image.ReadPixels(new Rect(0,0,128,128),0,0); image.Apply();
                File.WriteAllBytes(Path.Combine(output,"pointer-surface-dot.png"),image.EncodeToPNG());
                var color=image.GetPixel(64,64);
                check(Mathf.Abs(color.r-16/255f)<.02f && Mathf.Abs(color.g-163/255f)<.02f && Mathf.Abs(color.b-127/255f)<.02f,"Actual surface dot shader renders the green palette in "+QualitySettings.activeColorSpace+" ("+color+")");
                check(image.GetPixel(86,64).a>.9f && image.GetPixel(36,36).a<.01f && !ShaderUtil.ShaderHasError(marker.sharedMaterial.shader),"Surface dot clips the quad corners into a flat circle without shader errors");
                backing=UnityEngine.Object.Instantiate(board.gameObject); SceneManager.MoveGameObjectToScene(backing,marker.gameObject.scene);
                var center=dot.transform.position; backing.transform.SetPositionAndRotation(center,Quaternion.identity); backing.transform.localScale=new Vector3(.02f,.02f,1);
                var block=new MaterialPropertyBlock(); backing.GetComponent<MeshRenderer>().GetPropertyBlock(block); block.SetFloat("_Selected",0); block.SetFloat("_Count",0); backing.GetComponent<MeshRenderer>().SetPropertyBlock(block);
                check(marker.sharedMaterial.renderQueue>board.sharedMaterial.renderQueue && marker.sharedMaterial.GetFloat("_ZWrite")==0,"Surface dot draws after the real panel without writing depth");
                foreach(var side in new[] {1,-1}) foreach(var angle in new[] {0,35,65,80})
                {
                    var direction=new Vector3(Mathf.Sin(angle*Mathf.Deg2Rad),0,Mathf.Cos(angle*Mathf.Deg2Rad))*side;
                    dot.transform.position=center-Vector3.forward*(side*ToyControlSetupWindow.PointerTargetDepth/2);
                    camera.transform.SetPositionAndRotation(dot.transform.position-direction*.25f,Quaternion.LookRotation(direction));
                    camera.Render(); RenderTexture.active=rt; image.ReadPixels(new Rect(0,0,128,128),0,0); image.Apply(); color=image.GetPixel(64,64);
                    File.WriteAllBytes(Path.Combine(output,"pointer-on-panel-"+side+"-"+angle+".png"),image.EncodeToPNG());
                    check(color.g>.58f && color.r<.12f && Mathf.Abs(color.b-127/255f)<.03f,"Hover dot stays visible over the actual panel at "+angle+" degrees, side "+side+" ("+color+")");
                }
                dot.transform.position=center-Vector3.forward*(ToyControlSetupWindow.PointerTargetDepth/2); camera.transform.SetPositionAndRotation(dot.transform.position-Vector3.forward*.25f,Quaternion.identity);
                occluder=GameObject.CreatePrimitive(PrimitiveType.Quad); SceneManager.MoveGameObjectToScene(occluder,marker.gameObject.scene);
                occluder.transform.SetPositionAndRotation(center-Vector3.forward*.002f,Quaternion.identity); occluder.transform.localScale=new Vector3(.02f,.02f,1);
                cover=new Material(Shader.Find("Unlit/Color")); cover.color=Color.red; occluder.GetComponent<MeshRenderer>().sharedMaterial=cover;
                camera.Render(); RenderTexture.active=rt; image.ReadPixels(new Rect(0,0,128,128),0,0); image.Apply(); color=image.GetPixel(64,64);
                check(color.r>.9f && color.g<.1f,"A real foreground surface still occludes the dot; it does not draw through objects");
            }
            finally {RenderTexture.active=old; camera.targetTexture=null; RenderTexture.ReleaseTemporary(rt); if(image!=null) UnityEngine.Object.DestroyImmediate(image); if(backing!=null) UnityEngine.Object.DestroyImmediate(backing); if(occluder!=null) UnityEngine.Object.DestroyImmediate(occluder); if(cover!=null) UnityEngine.Object.DestroyImmediate(cover); UnityEngine.Object.DestroyImmediate(dot); UnityEngine.Object.DestroyImmediate(obj);}
        }
        private static void Render(Transform panel,string path,Action<Texture2D,Camera,Transform> inspect=null)
        {
            var obj=new GameObject("VR demo preview camera"); var camera=obj.AddComponent<Camera>(); SceneManager.MoveGameObjectToScene(obj,panel.gameObject.scene);
            camera.cameraType=CameraType.Preview; camera.scene=panel.gameObject.scene; camera.transform.position=panel.position-panel.forward*1.2f; camera.transform.rotation=panel.rotation;
            camera.orthographic=true; camera.orthographicSize=0.28f; camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(30/255f,31/255f,34/255f,0);
            var rt=RenderTexture.GetTemporary(1488,850,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB); camera.targetTexture=rt; var old=RenderTexture.active;
            Texture2D image=null; try {camera.Render(); RenderTexture.active=rt; image=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false); image.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); image.Apply(); File.WriteAllBytes(path,image.EncodeToPNG()); inspect?.Invoke(image,camera,panel.Find("Size/Board"));}
            finally {RenderTexture.active=old; camera.targetTexture=null; RenderTexture.ReleaseTemporary(rt); if(image!=null) UnityEngine.Object.DestroyImmediate(image); UnityEngine.Object.DestroyImmediate(obj);}
        }
        private static void Benchmark(Transform panel,string folder,Frame frame,string output)
        {
            // A blocking one-pixel readback drains each camera draw. Timings include
            // render submission and GPU completion; these are not isolated GPU timings.
            var export=Path.Combine(output,"baseline-shader"); Directory.CreateDirectory(export);
            foreach(var file in new[] {"Panel.shader","Panel.cginc","GraphCore.hlsli","TouchVisuals.hlsli"}) File.Copy(folder+"/"+file,Path.Combine(export,file),true);
            var obj=new GameObject("Performance preview camera"); var camera=obj.AddComponent<Camera>(); SceneManager.MoveGameObjectToScene(obj,panel.gameObject.scene);
            camera.cameraType=CameraType.Preview; camera.scene=panel.gameObject.scene; camera.transform.position=panel.position-panel.forward*1.2f; camera.transform.rotation=panel.rotation;
            camera.orthographic=true; camera.orthographicSize=0.23f; camera.clearFlags=CameraClearFlags.SolidColor;
            var rt=RenderTexture.GetTemporary(1488,850,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB); var old=RenderTexture.active; camera.targetTexture=rt;
            var image=new Texture2D(1,1,TextureFormat.RGBA32,false); var renderer=panel.Find("Size/Board").GetComponent<MeshRenderer>(); var original=renderer.sharedMaterial;
            var saved=new MaterialPropertyBlock(); renderer.GetPropertyBlock(saved); var values=new MaterialPropertyBlock(); renderer.GetPropertyBlock(values); Material baseline=null;
            var previousPictures=new System.Collections.Generic.Dictionary<bool,Color32[]>(); var visualComparisons=new System.Collections.Generic.List<string>();
            try
            {
                foreach(var item in frame.values) values.SetFloat("_"+item.name,item.value); values.SetFloat("_LinkOnline",1);
                var cases=new System.Collections.Generic.List<string>();
                string Measure(string label,Material material,bool dense,bool primaryPattern=false)
                {
                    renderer.sharedMaterial=material;
                    foreach(var item in frame.values) values.SetFloat("_"+item.name,item.value);
                    if(primaryPattern)
                    {
                        values.SetFloat("_Mode",1); values.SetFloat("_CurveStart",0); values.SetFloat("_Phase",0.43f);
                        values.SetFloat("_Touch",1); values.SetFloat("_TouchX",0.27f); values.SetFloat("_TouchY",0.70f);
                        for(var pair=0;pair<32;pair++) {var a=pair%3==0 ? 0 : 255; var b=pair%4==0 ? 255 : 32; values.SetFloat("_Curve"+pair.ToString("00"),(a+b*256)/65535f);}
                    }
                    if(dense) for(var slot=1;slot<16;slot++)
                    {
                        foreach(var item in frame.values.Where(v=>v.name.StartsWith("S00_"))) values.SetFloat("_S"+slot.ToString("00")+item.name.Substring(3),item.value);
                        values.SetFloat("_S"+slot.ToString("00")+"_Phase",slot*0.061f);
                        values.SetFloat("_S"+slot.ToString("00")+"_RG",((slot*47%200+40)+(slot*83%200+40)*256)/65535f);
                        values.SetFloat("_S"+slot.ToString("00")+"_B",(slot*31%200+40)/255f);
                    }
                    else foreach(var item in frame.values.Where(v=>v.name.StartsWith("S"))) values.SetFloat("_"+item.name,item.value);
                    renderer.SetPropertyBlock(values); var samples=new double[24];
                    for(var i=-4;i<samples.Length;i++) {var watch=System.Diagnostics.Stopwatch.StartNew(); camera.Render(); RenderTexture.active=rt; image.ReadPixels(new Rect(0,0,1,1),0,0,false); watch.Stop(); if(i>=0) samples[i]=watch.Elapsed.TotalMilliseconds;}
                    Array.Sort(samples); var median=(samples[11]+samples[12])/2;
                    var full=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);
                    try
                    {
                        full.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0,false); full.Apply(); var pixels=full.GetPixels32();
                        File.WriteAllBytes(Path.Combine(output,label+(dense ? "-16-loops" : "-fixture")+".png"),full.EncodeToPNG());
                        if(label.StartsWith("previous")) previousPictures[dense]=pixels;
                        else if(previousPictures.TryGetValue(dense,out var before))
                        {
                            long error=0; int different=0;
                            for(var pixel=0;pixel<pixels.Length;pixel++) {var a=pixels[pixel]; var b=before[pixel]; var delta=Math.Abs(a.r-b.r)+Math.Abs(a.g-b.g)+Math.Abs(a.b-b.b); error+=delta; if(delta>9) different++;}
                            var mean=error/(pixels.Length*3.0*255); var fraction=different/(double)pixels.Length;
                            if(mean>0.002 || fraction>0.005) throw new InvalidOperationException("Optimized waveform/name rendering changed: error="+mean+", different="+fraction);
                            visualComparisons.Add("{\"Dense16Loops\":"+(dense ? "true" : "false")+",\"PrimaryPatternAndTouch\":"+(primaryPattern ? "true" : "false")+",\"MeanChannelError\":"+mean.ToString("F8",System.Globalization.CultureInfo.InvariantCulture)+",\"ChangedPixelFraction\":"+fraction.ToString("F8",System.Globalization.CultureInfo.InvariantCulture)+"}");
                        }
                    }
                    finally {UnityEngine.Object.DestroyImmediate(full);}
                    return "{\"Shader\":\""+label+"\",\"Dense16Loops\":"+(dense ? "true" : "false")+",\"PrimaryPatternAndTouch\":"+(primaryPattern ? "true" : "false")+",\"MedianRenderAndReadbackMs\":"+median.ToString("F4",System.Globalization.CultureInfo.InvariantCulture)+"}";
                }
                var compare=Environment.GetEnvironmentVariable("SHOCKWAVE_VR_BASELINE_SHADER");
                if(!string.IsNullOrWhiteSpace(compare))
                {
                    var imported=folder+"/PerfBaseline"; Directory.CreateDirectory(imported);
                    foreach(var file in new[] {"Panel.shader","Panel.cginc","GraphCore.hlsli","TouchVisuals.hlsli"}) File.Copy(Path.Combine(compare,file),imported+"/"+file);
                    var shaderText=File.ReadAllText(imported+"/Panel.shader").Replace("ShockWave/VR OSC Demo","ShockWave/VR OSC Demo Performance Baseline"); File.WriteAllText(imported+"/Panel.shader",shaderText);
                    AssetDatabase.ImportAsset(imported+"/Panel.shader",ImportAssetOptions.ForceSynchronousImport); var shader=AssetDatabase.LoadAssetAtPath<Shader>(imported+"/Panel.shader");
                    baseline=new Material(shader); baseline.CopyPropertiesFromMaterial(original);
                    cases.Add(Measure("previous",baseline,false)); cases.Add(Measure("optimized",original,false));
                    cases.Add(Measure("previous",baseline,true)); cases.Add(Measure("optimized",original,true));
                    cases.Add(Measure("previous-primary-pattern",baseline,false,true)); cases.Add(Measure("optimized-primary-pattern",original,false,true));
                    if(ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Benchmark baseline compile error");
                }
                else {cases.Add(Measure("current",original,false)); cases.Add(Measure("current",original,true));}
                File.WriteAllText(Path.Combine(output,"performance.json"),"{\"Width\":1488,\"Height\":850,\"Device\":\""+SystemInfo.graphicsDeviceName+"\",\"Metric\":\"Camera render plus blocking one-pixel readback; includes CPU submission, not isolated GPU timing\",\"Cases\":["+string.Join(",",cases)+"],\"VisualComparisons\":["+string.Join(",",visualComparisons)+"]}");
            }
            finally {renderer.sharedMaterial=original; renderer.SetPropertyBlock(saved); if(baseline!=null) UnityEngine.Object.DestroyImmediate(baseline); UnityEngine.Object.DestroyImmediate(image); RenderTexture.active=old; camera.targetTexture=null; RenderTexture.ReleaseTemporary(rt); UnityEngine.Object.DestroyImmediate(obj);}
        }
    }
}
#endif
