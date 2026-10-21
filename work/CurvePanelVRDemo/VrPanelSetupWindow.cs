#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using com.vrcfury.api;
using ShockWave.VrDemo.Shared;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;

namespace ShockWave.VrDemo
{
    public sealed class VrPanelSetupWindow : EditorWindow
    {
        internal const string HostName="SzebsCurvePanelVRDemo", AssetRoot="Assets/SzebsToys/CurvePanelVRDemo", SourceRoot="Assets/Editor/SzebsToysCurvePanelVRDemo";
        internal const float BoardWidth=0.74f, LogicalWidth=1240, LogicalHeight=708;
        [SerializeField] private VRCAvatarDescriptor avatar;
        [SerializeField] private float distance=0.65f,belowEyes=0.16f;
        [MenuItem("Tools/ShockWave/Curve Panel/VR OSC Demo")]
        public static void Open()
        {
            var window=GetWindow<VrPanelSetupWindow>("ShockWave VR OSC Demo"); window.minSize=new Vector2(520,330);
            if(Selection.activeGameObject!=null) window.avatar=Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>();
            window.Show();
        }
        private void OnGUI()
        {
            EditorGUILayout.LabelField("Shader panel · first VR OSC demo",EditorStyles.boldLabel);
            avatar=(VRCAvatarDescriptor)EditorGUILayout.ObjectField("Avatar",avatar,typeof(VRCAvatarDescriptor),true);
            distance=EditorGUILayout.Slider("Distance in front (m)",distance,0.35f,1.2f);
            belowEyes=EditorGUILayout.Slider("Below eyes (m)",belowEyes,0,0.4f);
            EditorGUILayout.HelpBox("Adds a separate ShockWave VR Curve Demo with shader, self-only finger contacts and VRCFury Full Controller (FX, menu and parameters). Your earlier panel and avatar settings are kept. The panel starts hidden locally in VRChat; use VR Curve Demo > Show panel.",MessageType.Info);
            EditorGUILayout.HelpBox("Keep ShockWave Shader Lab open and click Start VR OSC. Enable OSC in VRChat, show the panel, then press Demo OSC. This version controls simulated values only. Recordings stay in the test program; the avatar contains no runtime script or socket.",MessageType.Info);
            var error=ValidateAvatar(avatar); if(error!=null) EditorGUILayout.HelpBox(error,MessageType.Warning);
            using(new EditorGUI.DisabledScope(error!=null)) if(GUILayout.Button("Add VR OSC Demo to Avatar",GUILayout.Height(38))) Install();
        }
        internal static string ValidateAvatar(VRCAvatarDescriptor target)
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode) return "Exit Play mode before installing.";
            if(target==null || EditorUtility.IsPersistent(target) || !target.gameObject.scene.IsValid() || !target.gameObject.scene.isLoaded) return "Choose an avatar in a loaded scene.";
            if(target.GetComponentsInChildren<Transform>(true).Any(t=>t!=target.transform && t.name==HostName)) return "This VR demo is already installed. Existing content is preserved; remove/undo that demo before installing another.";
            return null;
        }
        private void Install()
        {
            try
            {
                string folder; var host=Build(avatar,distance,belowEyes,out folder);
                Undo.RegisterCreatedObjectUndo(host,"Add ShockWave VR OSC Demo"); EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene); Selection.activeGameObject=host;
                Debug.Log("VR OSC demo added. Save the scene when ready. Assets: "+folder,host);
                EditorUtility.DisplayDialog("VR OSC demo added","Save your scene when ready. In VRChat: enable OSC, open VR Curve Demo > Show panel, and press Demo OSC with the test program's VR OSC enabled. Original avatar/controller/menu assets were preserved.","OK");
            }
            catch(Exception ex) { Debug.LogException(ex); EditorUtility.DisplayDialog("Demo could not be added","Only this invocation's partial new object/assets were removed. See the Console.","OK"); }
        }
        internal static GameObject Build(VRCAvatarDescriptor target,float panelDistance,float offset,out string folder)
        {
            var error=ValidateAvatar(target); if(error!=null) throw new InvalidOperationException(error);
            if(float.IsNaN(panelDistance) || float.IsInfinity(panelDistance) || float.IsNaN(offset) || float.IsInfinity(offset) || panelDistance<0.35f || panelDistance>1.2f || offset<0 || offset>0.4f) throw new ArgumentOutOfRangeException();
            folder=null; GameObject host=null;
            try
            {
                EnsureFolder(AssetRoot);
                var safe=string.Concat(target.name.Select(c=>Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                folder=AssetDatabase.GenerateUniqueAssetPath(AssetRoot+"/"+(string.IsNullOrWhiteSpace(safe) ? "Avatar" : safe));
                AssetDatabase.CreateFolder(AssetRoot,Path.GetFileName(folder));
                var material=VrPanelShaderFactory.Create(folder);
                host=Child(HostName,target.transform); var panel=Child("Panel",host.transform);
                panel.transform.position=target.transform.TransformPoint(target.ViewPosition)+target.transform.forward*panelDistance-target.transform.up*offset;
                panel.transform.rotation=target.transform.rotation;
                var scale=target.transform.lossyScale; if(scale.x<=0 || scale.y<=0 || scale.z<=0) throw new InvalidOperationException("Avatar scale must be positive.");
                panel.transform.localScale=new Vector3(1/scale.x,1/scale.y,1/scale.z);
                var animator=target.GetComponent<Animator>(); var head=animator!=null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : target.transform;
                if(head==null) head=target.transform;
                var constraint=panel.AddComponent<VRCParentConstraint>(); constraint.Sources.Add(new VRCConstraintSource(head,1,head.InverseTransformPoint(panel.transform.position),(Quaternion.Inverse(head.rotation)*panel.transform.rotation).eulerAngles));
                constraint.IsActive=true; constraint.Locked=true;
                var board=GameObject.CreatePrimitive(PrimitiveType.Quad); board.name="Board"; DestroyImmediate(board.GetComponent<Collider>()); board.transform.SetParent(panel.transform,false);
                board.transform.localScale=new Vector3(BoardWidth,BoardWidth*LogicalHeight/LogicalWidth,1);
                var renderer=board.GetComponent<MeshRenderer>(); renderer.sharedMaterial=material; renderer.shadowCastingMode=ShadowCastingMode.Off; renderer.receiveShadows=false;
                Contacts(panel.transform); var parameters=Parameters(folder); var menu=Menu(folder); var controller=Controller(folder);
                var full=FuryComponents.CreateFullController(host); full.AddController(controller,VRCAvatarDescriptor.AnimLayerType.FX); full.AddMenu(menu); full.AddParams(parameters);
                foreach(var p in parameters.parameters) full.AddGlobalParam(p.name);
                foreach(var path in AssetDatabase.FindAssets("",new[] {folder}).Select(AssetDatabase.GUIDToAssetPath).Distinct()) AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath(path));
                return host;
            }
            catch { if(host!=null) DestroyImmediate(host); if(folder!=null && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder); throw; }
        }
        internal static Vector3 Position(float x,float y) { return new Vector3((x/LogicalWidth-0.5f)*BoardWidth,(0.5f-y/LogicalHeight)*BoardWidth*LogicalHeight/LogicalWidth,-0.015f); }
        private static GameObject Child(string name,Transform parent) { var obj=new GameObject(name); obj.transform.SetParent(parent,false); return obj; }
        private static VRCContactReceiver Contact(Transform parent,string suffix,Rect bounds,bool proximity=false,Quaternion? rotation=null)
        {
            var obj=Child(suffix,parent); var r=obj.AddComponent<VRCContactReceiver>(); r.parameter=VrPanelContract.Prefix+suffix;
            r.receiverType=proximity ? ContactReceiver.ReceiverType.Proximity : ContactReceiver.ReceiverType.Constant;
            r.allowSelf=true; r.allowOthers=false; r.localOnly=true; r.collisionTags=new List<string> {"Finger"};
            r.position=Position(bounds.center.x,bounds.center.y); r.rotation=rotation ?? Quaternion.identity;
            var serialized=new SerializedObject(r); var shape=serialized.FindProperty("shapeType"); var dimensions=serialized.FindProperty("size"); var face=serialized.FindProperty("useFaceProximity");
            var box=shape==null ? -1 : Array.IndexOf(shape.enumNames,"Box");
            if(box<0 || dimensions==null || proximity && face==null) throw new InvalidOperationException("VRChat SDK with Box and Use Face Proximity is required.");
            shape.enumValueIndex=box;
            var w=bounds.width/LogicalWidth*BoardWidth; var h=bounds.height/LogicalWidth*BoardWidth;
            dimensions.vector3Value=!proximity ? new Vector3(w,h,0.018f) : suffix=="InX" ? new Vector3(0.018f,h,w) : new Vector3(w,0.018f,h);
            if(face!=null) face.boolValue=proximity; serialized.ApplyModifiedPropertiesWithoutUndo(); return r;
        }
        private static void Contacts(Transform panel)
        {
            var contacts=Child("Contacts",panel).transform; var graph=new Rect(405,230,765,352);
            Contact(contacts,"InTouch",graph); Contact(contacts,"InX",graph,true,Quaternion.Euler(0,90,0)); Contact(contacts,"InY",graph,true,Quaternion.Euler(-90,0,0));
            for(var row=0;row<6;row++) Contact(contacts,"InRow"+row,new Rect(40,176+row*72,268,62));
            Contact(contacts,"InRecord",new Rect(365,175,130,42)); Contact(contacts,"InPlay",new Rect(510,175,155,42)); Contact(contacts,"InHold",new Rect(680,175,140,42));
            Contact(contacts,"InClear",new Rect(365,630,135,45)); Contact(contacts,"InZero",new Rect(520,630,170,45)); Contact(contacts,"InStop",new Rect(710,630,150,45)); Contact(contacts,"InDemo",new Rect(950,630,232,45));
            Contact(contacts,"InPrev",new Rect(40,648,52,36)); Contact(contacts,"InNext",new Rect(256,648,52,36));
            for(var i=0;i<3;i++) Contact(contacts,new[] {"InOwn","InRemote","InBoth"}[i],new Rect(832+i*116,127,100,40));
        }
        private static VRCExpressionParameters Parameters(string folder)
        {
            var list=new List<VRCExpressionParameters.Parameter>();
            void Add(string name,VRCExpressionParameters.ValueType type) { list.Add(new VRCExpressionParameters.Parameter {name=name,valueType=type,networkSynced=false,saved=false,defaultValue=0}); }
            foreach(var name in VrPanelContract.BoolInputs) Add(VrPanelContract.Prefix+name,VRCExpressionParameters.ValueType.Bool);
            foreach(var name in VrPanelContract.FloatInputs) Add(VrPanelContract.Prefix+name,VRCExpressionParameters.ValueType.Float);
            Add(VrPanelContract.Prefix+"RxBeat",VRCExpressionParameters.ValueType.Bool);
            foreach(var name in VrPanelContract.FeedbackFields()) Add(VrPanelContract.ReceivePrefix+name,VRCExpressionParameters.ValueType.Float);
            var asset=ScriptableObject.CreateInstance<VRCExpressionParameters>(); asset.parameters=list.ToArray(); AssetDatabase.CreateAsset(asset,folder+"/Parameters.asset"); return asset;
        }
        private static VRCExpressionsMenu Menu(string folder)
        {
            VRCExpressionsMenu New(string name) { var m=ScriptableObject.CreateInstance<VRCExpressionsMenu>(); m.controls=new List<VRCExpressionsMenu.Control>(); AssetDatabase.CreateAsset(m,folder+"/"+name+".asset"); return m; }
            VRCExpressionsMenu.Control Button(string label,string param,bool toggle=false) { return new VRCExpressionsMenu.Control {name=label,type=toggle ? VRCExpressionsMenu.Control.ControlType.Toggle : VRCExpressionsMenu.Control.ControlType.Button,parameter=new VRCExpressionsMenu.Control.Parameter {name=VrPanelContract.Prefix+param},value=1}; }
            VRCExpressionsMenu.Control Sub(string label,VRCExpressionsMenu m) { return new VRCExpressionsMenu.Control {name=label,type=VRCExpressionsMenu.Control.ControlType.SubMenu,subMenu=m}; }
            var root=New("Menu"); var main=New("Controls"); var patterns=New("Patterns"); var rows=New("Rows"); var test=New("TouchTest"); var route=New("Destination");
            root.controls.Add(Sub("VR Curve Demo",main));
            main.controls.Add(Button("Show panel","Visible",true)); main.controls.Add(Button("Follow head","Follow",true)); main.controls.Add(Button("Recall in front","Recall")); main.controls.Add(Button("Demo OSC","InDemo"));
            main.controls.Add(Sub("Pattern controls",patterns)); main.controls.Add(Sub("Select visible rows",rows)); main.controls.Add(Sub("Touch test / calibration",test)); main.controls.Add(Sub("Destination",route));
            foreach(var item in new[] {("Record next touch","InRecord"),("Play loop","InPlay"),("Hold selected","InHold"),("Zero selected","InZero"),("Stop all","InStop"),("Clear curve","InClear"),("Previous page","InPrev"),("Next page","InNext")}) patterns.controls.Add(Button(item.Item1,item.Item2));
            for(var i=0;i<6;i++) rows.controls.Add(Button("Toggle row "+(i+1),"InRow"+i)); rows.controls.Add(Button("Previous page","InPrev")); rows.controls.Add(Button("Next page","InNext"));
            test.controls.Add(Button("Emulate touch","InTestTouch",true));
            foreach(var axis in new[] {"X","Y"}) test.controls.Add(new VRCExpressionsMenu.Control {name="Test "+axis,type=VRCExpressionsMenu.Control.ControlType.RadialPuppet,subParameters=new[] {new VRCExpressionsMenu.Control.Parameter {name=VrPanelContract.Prefix+"InTest"+axis}}});
            test.controls.Add(Button("Invert finger X","InFlipX",true)); test.controls.Add(Button("Invert finger Y","InFlipY",true));
            foreach(var target in new[] {"Own","Remote","Both"}) route.controls.Add(Button(target,"In"+target));
            foreach(var m in new[] {root,main,patterns,rows,test,route}) EditorUtility.SetDirty(m); return root;
        }
        private static AnimatorController Controller(string folder)
        {
            var c=AnimatorController.CreateAnimatorControllerAtPath(folder+"/FX.controller");
            foreach(var name in VrPanelContract.BoolInputs) c.AddParameter(VrPanelContract.Prefix+name,AnimatorControllerParameterType.Bool);
            foreach(var name in VrPanelContract.FloatInputs) c.AddParameter(VrPanelContract.Prefix+name,AnimatorControllerParameterType.Float);
            c.AddParameter(VrPanelContract.Prefix+"RxBeat",AnimatorControllerParameterType.Bool); c.AddParameter("IsLocal",AnimatorControllerParameterType.Bool);
            var weightName=VrPanelContract.Prefix+"Weight"; c.AddParameter(new AnimatorControllerParameter {name=weightName,type=AnimatorControllerParameterType.Float,defaultFloat=1});
            foreach(var name in VrPanelContract.FeedbackFields()) c.AddParameter(VrPanelContract.ReceivePrefix+name,AnimatorControllerParameterType.Float);
            var layers=c.layers; layers[0].name="Shader feedback"; layers[0].defaultWeight=1; c.layers=layers;
            var tree=new BlendTree {name="Numeric shader feedback",blendType=BlendTreeType.Direct}; AssetDatabase.AddObjectToAsset(tree,c);
            var normalized=new SerializedObject(tree); var flag=normalized.FindProperty("m_NormalizedBlendValues"); if(flag!=null) {flag.boolValue=false; normalized.ApplyModifiedPropertiesWithoutUndo();}
            foreach(var field in VrPanelContract.FeedbackFields())
            {
                var blend=new BlendTree {name=field,blendType=BlendTreeType.Simple1D,blendParameter=VrPanelContract.ReceivePrefix+field,useAutomaticThresholds=false}; AssetDatabase.AddObjectToAsset(blend,c);
                for(var value=0;value<=1;value++) {var clip=Clip(c,field+value,0.1f); Curve(clip,"Panel/Board",typeof(MeshRenderer),"material._"+field,value); blend.AddChild(clip,value);}
                tree.AddChild(blend); var children=tree.children; children[children.Length-1].directBlendParameter=weightName; tree.children=children;
            }
            var data=State(c.layers[0].stateMachine,"Feedback",tree); data.writeDefaultValues=true;
            var place=Layer(c,"Local placement");
            var hidden=State(place,"Hidden",Placement(c,"Hidden",false,false,0.2f)); var settling=State(place,"Place in front",Placement(c,"Place",true,false,0.2f));
            var pinned=State(place,"Dropped",Placement(c,"Dropped",true,true,0.1f)); var follow=State(place,"Follow head",Placement(c,"Follow",true,false,0.1f)); place.defaultState=hidden;
            var show=Transition(hidden,settling); Bool(show,"Visible",true); show.AddCondition(AnimatorConditionMode.If,0,"IsLocal");
            foreach(var state in new[] {settling,pinned,follow}) {Bool(Transition(state,hidden),"Visible",false); Transition(state,hidden).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");}
            Bool(Transition(pinned,follow),"Follow",true); Bool(Transition(follow,settling),"Follow",false); Bool(Transition(pinned,settling),"Recall",true);
            var drop=Transition(settling,pinned); drop.hasExitTime=true; drop.exitTime=1; Bool(drop,"Follow",false); Bool(drop,"Recall",false);
            var following=Transition(settling,follow); following.hasExitTime=true; following.exitTime=1; Bool(following,"Follow",true);
            var beat=Layer(c,"Avatar heartbeat"); var dormant=State(beat,"Hidden",Clip(c,"No heartbeat",0.5f)); var a=State(beat,"Pulse A",Clip(c,"Beat A",0.5f)); var b=State(beat,"Pulse B",Clip(c,"Beat B",0.5f)); beat.defaultState=dormant;
            Driver(dormant,"InBeat",0); Driver(dormant,"InTestTouch",0); Driver(a,"InBeat",1); Driver(b,"InBeat",0);
            var start=Transition(dormant,a); Bool(start,"Visible",true); start.AddCondition(AnimatorConditionMode.If,0,"IsLocal");
            foreach(var s in new[] {a,b}) {Bool(Transition(s,dormant),"Visible",false); Transition(s,dormant).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");}
            var ab=Transition(a,b); ab.hasExitTime=true; ab.exitTime=1; var ba=Transition(b,a); ba.hasExitTime=true; ba.exitTime=1;
            var watch=Layer(c,"Backend freshness");
            var offline=State(watch,"Waiting for test app",Material(c,"Offline","_LinkOnline",0,0.1f)); var onA=State(watch,"App pulse A",Material(c,"Online A","_LinkOnline",1,2)); var onB=State(watch,"App pulse B",Material(c,"Online B","_LinkOnline",1,2)); watch.defaultState=offline;
            Bool(Transition(offline,onA),"RxBeat",true); Bool(Transition(offline,onB),"RxBeat",false);
            Bool(Transition(onA,onB),"RxBeat",false); Bool(Transition(onB,onA),"RxBeat",true);
            foreach(var state in new[] {onA,onB}) {var timeout=Transition(state,offline); timeout.hasExitTime=true; timeout.exitTime=1;}
            // Offline must not immediately re-enter the same stale bit: separate
            // wait states remember which pulse expired, then require its opposite.
            var waitA=State(watch,"Expired A",Material(c,"Expired A","_LinkOnline",0,0.1f)); var waitB=State(watch,"Expired B",Material(c,"Expired B","_LinkOnline",0,0.1f));
            foreach(var s in new[] {onA,onB}) foreach(var t in s.transitions.Where(t=>t.destinationState==offline).ToArray()) s.RemoveTransition(t);
            var ta=Transition(onA,waitA); ta.hasExitTime=true; ta.exitTime=1; var tb=Transition(onB,waitB); tb.hasExitTime=true; tb.exitTime=1;
            Bool(Transition(waitA,onB),"RxBeat",false); Bool(Transition(waitB,onA),"RxBeat",true);
            EditorUtility.SetDirty(c); return c;
        }
        private static AnimatorStateMachine Layer(AnimatorController c,string name) {c.AddLayer(name); var layers=c.layers; layers[layers.Length-1].defaultWeight=1; c.layers=layers; return layers[layers.Length-1].stateMachine;}
        private static AnimatorState State(AnimatorStateMachine sm,string name,Motion motion) {var s=sm.AddState(name); s.motion=motion; s.writeDefaultValues=false; return s;}
        private static AnimatorStateTransition Transition(AnimatorState a,AnimatorState b) {var t=a.AddTransition(b); t.hasExitTime=false; t.duration=0; t.hasFixedDuration=true; return t;}
        private static void Bool(AnimatorStateTransition t,string suffix,bool value) {t.AddCondition(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,0,VrPanelContract.Prefix+suffix);}
        private static AnimationClip Clip(AnimatorController c,string name,float duration)
        {
            var clip=new AnimationClip {name=name,frameRate=60};
            // A harmless material property gives heartbeat/timeout clips a
            // duration without animating the panel transform or input values.
            Curve(clip,"Panel/Board",typeof(MeshRenderer),"material._Clock",0,duration);
            AssetDatabase.AddObjectToAsset(clip,c); return clip;
        }
        private static void Curve(AnimationClip clip,string path,Type type,string property,float value,float duration=0.1f) {AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(path,type,property),AnimationCurve.Constant(0,duration,value));}
        private static AnimationClip Placement(AnimatorController c,string name,bool visible,bool freeze,float duration) {var clip=Clip(c,name,duration); Curve(clip,"Panel",typeof(GameObject),"m_IsActive",visible ? 1 : 0,duration); Curve(clip,"Panel",typeof(VRCParentConstraint),"FreezeToWorld",freeze ? 1 : 0,duration); return clip;}
        private static AnimationClip Material(AnimatorController c,string name,string property,float value,float duration) {var clip=Clip(c,name,duration); Curve(clip,"Panel/Board",typeof(MeshRenderer),"material."+property,value,duration); return clip;}
        private static void Driver(AnimatorState state,string suffix,float value) {var driver=state.AddStateMachineBehaviour<VRCAvatarParameterDriver>(); driver.localOnly=true; driver.parameters=new List<VRCAvatarParameterDriver.Parameter> {new VRCAvatarParameterDriver.Parameter {name=VrPanelContract.Prefix+suffix,type=VRCAvatarParameterDriver.ChangeType.Set,value=value}};}
        private static void EnsureFolder(string folder) {var parts=folder.Split('/'); var current=parts[0]; foreach(var part in parts.Skip(1)) {if(!AssetDatabase.IsValidFolder(current+"/"+part)) AssetDatabase.CreateFolder(current,part); current+="/"+part;}}
    }
}
#endif
