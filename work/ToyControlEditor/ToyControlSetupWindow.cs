#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using com.vrcfury.api;
using ShockWave.ToyControl.Shared;
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

namespace ShockWave.ToyControl
{
    public sealed partial class ToyControlSetupWindow : EditorWindow
    {
        internal const string HostName="ShockWaveToyControl", LegacyHostName="SzebsToyControl", AssetRoot="Assets/SzebsToys/ToyControl";
        internal static string SourceRoot
        {
            get {var package=UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ToyControlSetupWindow).Assembly); return package!=null ? "Packages/"+package.name+"/Editor" : "Assets/Editor/SzebsToysToyControl";}
        }
        internal const float BoardWidth=0.74f, LogicalWidth=1240, LogicalHeight=608, LogicalTop=100;
        internal const float MinimumSize=0.4f, MaximumSize=1.6f;
        internal static readonly int[] GrabGestures={1}; // Fist only; pistol aim / Thumbs Up must never grab.
        internal static Rect Strip(bool right) => new Rect(right ? 1304 : -92,222,28,352);
        internal const string BoardPath="Panel/Size/Board";
        internal static readonly string[] GripInputs={"GrabLLeft","GrabLRight","GrabRLeft","GrabRRight"};
        [SerializeField] private VRCAvatarDescriptor avatar;
        [SerializeField] private float handGap=0.04f,handForward=0.10f;
        [SerializeField] private string menuPath="ShockWave";
        [SerializeField] private Vector2 windowScroll;
        [MenuItem("Tools/ShockWave/Panel")]
        public static void Open()
        {
            var window=GetWindow<ToyControlSetupWindow>("ShockWave Panel"); window.minSize=new Vector2(520,330);
            if(Selection.activeGameObject!=null) window.avatar=Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>();
            if(!window.placementEdited) window.LoadPlacements();
            window.Show();
        }
        private void OnGUI()
        {
            using(var scrolling=new EditorGUILayout.ScrollViewScope(windowScroll)) {
            windowScroll=scrolling.scrollPosition;
            EditorGUILayout.LabelField("VRC toy control",EditorStyles.boldLabel);
            avatar=(VRCAvatarDescriptor)EditorGUILayout.ObjectField("Avatar",avatar,typeof(VRCAvatarDescriptor),true);
            menuPath=EditorGUILayout.TextField("Radial menu path",menuPath);
            handGap=EditorGUILayout.Slider("Gap beside right hand (m)",handGap,0.01f,0.15f);
            handForward=EditorGUILayout.Slider("Forward from hand (m)",handForward,0,0.25f);
            PlacementGUI();
            EditorGUILayout.HelpBox("Choose a radial menu path, for example Utilities/ShockWave. The menu contains Show panel, In right hand, Back of right hand and Panel size (20% by default). Both hand toggles off locks the panel in the world. The panel starts hidden.",MessageType.Info);
            EditorGUILayout.HelpBox("Keep ShockWave running and enable OSC in VRChat. Connected toys appear automatically. Toy panel sensitivity and Max % apply to all input. Use Test mode first; recordings stay in the program.",MessageType.Info);
            EditorGUILayout.HelpBox("A Fist on either handle grabs in Touch or Pointer mode. Draw with your free hand while holding. Refit an attached right-hand panel using the left handle grip; release to retain its new wrist offset. Pistol/Thumbs Up never grab. Update replaces only this tool's host, preserves old assets and can be undone.",MessageType.Info);
            var error=ValidateAvatar(avatar,true); if(error!=null) EditorGUILayout.HelpBox(error,MessageType.Warning);
            using(new EditorGUI.DisabledScope(error!=null)) if(GUILayout.Button(Existing(avatar)==null ? "Add Toy Control to Avatar" : "Update Toy Control on Avatar",GUILayout.Height(38))) Install();
            }
        }
        private static GameObject Existing(VRCAvatarDescriptor target) => target==null ? null : target.transform.Cast<Transform>().FirstOrDefault(t=>t.name==HostName || t.name==LegacyHostName)?.gameObject;
        internal static string ValidateAvatar(VRCAvatarDescriptor target,bool allowUpdate=false)
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode) return "Exit Play mode before installing.";
            if(target==null || EditorUtility.IsPersistent(target) || !target.gameObject.scene.IsValid() || !target.gameObject.scene.isLoaded) return "Choose an avatar in a loaded scene.";
            var installed=target.GetComponentsInChildren<Transform>(true).Where(t=>t!=target.transform && (t.name==HostName || t.name==LegacyHostName)).ToArray();
            if(installed.Length>0 && (!allowUpdate || installed.Length!=1 || installed[0].parent!=target.transform || !installed[0].GetComponents<Component>().Any(c=>c!=null && c.GetType().FullName=="VF.Model.VRCFury"))) return "An existing Toy control host cannot be replaced automatically. Choose the avatar with this tool's single direct-child installation.";
            return null;
        }
        private void Install()
        {
            try
            {
                if(!placementEdited) LoadPlacements();
                var old=Existing(avatar); string folder; var host=Build(avatar,handGap,handForward,out folder,replaceExisting:old!=null,menuPath:menuPath,placements:handPlacements);
                Undo.IncrementCurrentGroup(); var undo=Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Add/update ShockWave Toy Control");
                Undo.RegisterCreatedObjectUndo(host,"Add/update ShockWave Toy Control"); if(old!=null) Undo.DestroyObjectImmediate(old); Undo.CollapseUndoOperations(undo); EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                placementEdited=false;
                Debug.Log("VRC toy control added. Save the scene when ready. Assets: "+folder,host);
                EditorUtility.DisplayDialog("Toy Control added","The panel starts hidden. Save your scene and upload when ready. Keep ShockWave running, enable OSC in VRChat and use Show panel in your chosen radial menu path. Start in Test mode. Existing avatar/controller/menu assets were preserved.","OK");
            }
            catch(Exception ex) { Debug.LogException(ex); EditorUtility.DisplayDialog("Toy control could not be added","Only this invocation's partial new object/assets were removed. See the Console.","OK"); }
        }
        internal static GameObject Build(VRCAvatarDescriptor target,float gap,float forward,out string folder,bool replaceExisting=false,Func<HumanBodyBones,Transform> resolveBone=null,string menuPath="ShockWave",HandPlacements placements=null)
        {
            var error=ValidateAvatar(target,replaceExisting); if(error!=null) throw new InvalidOperationException(error);
            menuPath=string.Join("/",(menuPath ?? "").Split('/').Select(s=>s.Trim()).Where(s=>s.Length>0));
            if(menuPath.Length>128 || menuPath.Any(char.IsControl)) throw new ArgumentException("Use a menu path up to 128 characters without line breaks.");
            if(float.IsNaN(gap) || float.IsInfinity(gap) || float.IsNaN(forward) || float.IsInfinity(forward) || gap<0.01f || gap>0.15f || forward<0 || forward>0.25f) throw new ArgumentOutOfRangeException();
            placements=placements ?? new HandPlacements(); ValidatePlacements(placements);
            folder=null; GameObject host=null;
            try
            {
                EnsureFolder(AssetRoot);
                var safe=string.Concat(target.name.Select(c=>Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                folder=AssetDatabase.GenerateUniqueAssetPath(AssetRoot+"/"+(string.IsNullOrWhiteSpace(safe) ? "Avatar" : safe));
                AssetDatabase.CreateFolder(AssetRoot,Path.GetFileName(folder));
                var material=ToyControlShaderFactory.Create(folder);
                host=Child(HostName,target.transform); var panel=Child("Panel",host.transform);
                var scale=target.transform.lossyScale; if(scale.x<=0 || scale.y<=0 || scale.z<=0) throw new InvalidOperationException("Avatar scale must be positive.");
                panel.transform.localScale=new Vector3(1/scale.x,1/scale.y,1/scale.z);
                var animator=target.GetComponent<Animator>();
                Transform Bone(HumanBodyBones bone) => resolveBone!=null ? resolveBone(bone) : animator!=null && animator.isHuman ? animator.GetBoneTransform(bone) : null;
                var right=Bone(HumanBodyBones.RightHand); var left=Bone(HumanBodyBones.LeftHand);
                if(right==null || left==null) throw new InvalidOperationException("A humanoid avatar with left/right hand bones is required for hand placement.");
                var anchors=Child("HandAnchors",host.transform); anchors.transform.localScale=new Vector3(1/scale.x,1/scale.y,1/scale.z);
                var rightPosition=Child("RightHandPosition",anchors.transform);rightPosition.transform.SetPositionAndRotation(right.position,right.rotation);
                var anchorConstraint=rightPosition.AddComponent<VRCParentConstraint>(); anchorConstraint.Sources.Add(new VRCConstraintSource(right,1,Vector3.zero,Vector3.zero));
                anchorConstraint.AffectsRotationX=anchorConstraint.AffectsRotationY=anchorConstraint.AffectsRotationZ=true;anchorConstraint.IsActive=true;anchorConstraint.Locked=true;
                var handFrame=HandFrame(right,Bone(HumanBodyBones.RightIndexProximal),Bone(HumanBodyBones.RightIndexDistal),Bone(HumanBodyBones.RightLittleProximal),target.transform.up);
                var spawn=Child("SpawnRight",rightPosition.transform);
                spawn.transform.localPosition=placements.inHand.captured ? placements.inHand.position : handFrame*new Vector3(-BoardWidth/2-gap,0,forward);
                spawn.transform.localRotation=placements.inHand.captured ? Quaternion.Euler(placements.inHand.rotation) : handFrame;
                var back=Child("BackRight",rightPosition.transform);
                back.transform.localPosition=placements.backHand.captured ? placements.backHand.position : handFrame*new Vector3(.04f,0,.065f);
                back.transform.localRotation=placements.backHand.captured ? Quaternion.Euler(placements.backHand.rotation) : handFrame*Quaternion.LookRotation(Vector3.left,Vector3.forward);
                panel.transform.SetPositionAndRotation(spawn.transform.position,spawn.transform.rotation);
                var constraint=panel.AddComponent<VRCParentConstraint>(); constraint.Sources.Add(new VRCConstraintSource(spawn.transform,1,Vector3.zero,Vector3.zero));
                constraint.Sources.Add(new VRCConstraintSource(left,0,Vector3.zero,Vector3.zero)); constraint.Sources.Add(new VRCConstraintSource(right,0,Vector3.zero,Vector3.zero));
                constraint.Sources.Add(new VRCConstraintSource(back.transform,0,Vector3.zero,Vector3.zero));
                // Separate unanimated offsets retain each in-game wrist fit.
                constraint.Sources.Add(new VRCConstraintSource(right,0,Vector3.zero,Vector3.zero));
                constraint.Sources.Add(new VRCConstraintSource(right,0,Vector3.zero,Vector3.zero));
                constraint.IsActive=true; constraint.Locked=true;
                var size=Child("Size",panel.transform);
                var board=GameObject.CreatePrimitive(PrimitiveType.Quad); board.name="Board"; DestroyImmediate(board.GetComponent<Collider>()); board.transform.SetParent(panel.transform,false);
                board.transform.SetParent(size.transform,false);
                board.transform.localScale=new Vector3(BoardWidth,BoardWidth*LogicalHeight/LogicalWidth,1);
                var renderer=board.GetComponent<MeshRenderer>(); renderer.sharedMaterial=material; renderer.shadowCastingMode=ShadowCastingMode.Off; renderer.receiveShadows=false;
                Contacts(size.transform); Grips(size.transform); BuildPointers(host.transform,size.transform,Bone,folder,scale);
                var parameters=Parameters(folder); var menu=Menu(folder); var controller=Controller(folder,scale,gap,forward,handFrame,placements.inHand.captured);
                var full=FuryComponents.CreateFullController(host); full.AddController(controller,VRCAvatarDescriptor.AnimLayerType.FX); full.AddMenu(menu,menuPath); full.AddParams(parameters);
                foreach(var p in parameters.parameters) full.AddGlobalParam(p.name);
                foreach(var name in GripInputs) full.AddGlobalParam(ToyControlContract.Prefix+name);
                full.AddGlobalParam(ToyControlContract.Prefix+"InputReady");
                foreach(var path in AssetDatabase.FindAssets("",new[] {folder}).Select(AssetDatabase.GUIDToAssetPath).Distinct()) AssetDatabase.SaveAssetIfDirty(AssetDatabase.LoadMainAssetAtPath(path));
                File.WriteAllText(folder+"/Placement.json",JsonUtility.ToJson(placements,true)); AssetDatabase.ImportAsset(folder+"/Placement.json");
                panel.SetActive(false); // Hidden in Unity until explicitly shown by the avatar menu.
                return host;
            }
            catch { if(host!=null) DestroyImmediate(host); if(folder!=null && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder); throw; }
        }
        internal static Vector3 Position(float x,float y) { return new Vector3((x/LogicalWidth-0.5f)*BoardWidth,(0.5f-(y-LogicalTop)/LogicalHeight)*BoardWidth*LogicalHeight/LogicalWidth,-0.015f); }
        private static GameObject Child(string name,Transform parent) { var obj=new GameObject(name); obj.transform.SetParent(parent,false); return obj; }
        private static VRCContactReceiver Contact(Transform parent,string suffix,Rect bounds,bool proximity=false,Quaternion? rotation=null,string tag="Finger",float depth=0.018f)
        {
            var obj=Child(suffix,parent); var r=obj.AddComponent<VRCContactReceiver>(); r.parameter=ToyControlContract.Prefix+suffix;
            r.receiverType=proximity ? ContactReceiver.ReceiverType.Proximity : ContactReceiver.ReceiverType.Constant;
            r.allowSelf=true; r.allowOthers=false; r.localOnly=true; r.collisionTags=tag=="Finger" ? new List<string> {"FingerIndexL","FingerIndexR"} : new List<string> {tag};
            r.position=Position(bounds.center.x,bounds.center.y); r.rotation=rotation ?? Quaternion.identity;
            var serialized=new SerializedObject(r); var shape=serialized.FindProperty("shapeType"); var dimensions=serialized.FindProperty("size"); var face=serialized.FindProperty("useFaceProximity");
            var box=shape==null ? -1 : Array.IndexOf(shape.enumNames,"Box");
            if(box<0 || dimensions==null || proximity && face==null) throw new InvalidOperationException("VRChat SDK with Box and Use Face Proximity is required.");
            shape.enumValueIndex=box;
            var w=bounds.width/LogicalWidth*BoardWidth; var h=bounds.height/LogicalWidth*BoardWidth;
            dimensions.vector3Value=!proximity ? new Vector3(w,h,depth) : suffix.EndsWith("X",StringComparison.Ordinal) ? new Vector3(0.018f,h,w) : new Vector3(w,0.018f,h);
            if(face!=null) face.boolValue=proximity; serialized.ApplyModifiedPropertiesWithoutUndo(); return r;
        }
        private static void Contacts(Transform panel)
        {
            var contacts=Child("Contacts",panel).transform; var graph=new Rect(405,230,765,352);
            Contact(contacts,"InTouch",graph); Contact(contacts,"InX",graph,true,Quaternion.Euler(0,90,0)); Contact(contacts,"InY",graph,true,Quaternion.Euler(-90,0,0));
            // Broad finger senders can overlap neighbouring full-height cards.
            // Keep a 24px vertical dead zone between the actual touch targets.
            for(var row=0;row<ToyControlContract.Rows;row++) Contact(contacts,"InRow"+row,new Rect(50,250+row*64,248,40),depth:.008f);
            ButtonContact(contacts,"InRecord",new Rect(365,127,130,42)); ButtonContact(contacts,"InPlay",new Rect(499,127,155,42));
            ButtonContact(contacts,"InInputMode",new Rect(658,127,130,42));
            ButtonContact(contacts,"InClear",new Rect(40,176,132,60)); ButtonContact(contacts,"InStop",new Rect(176,176,132,60));
            ButtonContact(contacts,"InPrev",new Rect(40,648,52,36)); ButtonContact(contacts,"InNext",new Rect(256,648,52,36));
            ButtonContact(contacts,"InPage",new Rect(1052,127,152,42));
            Contact(contacts,"InPanelTouch",new Rect(390,230,790,375));
            var full=new Rect(0,100,1240,608);
            Contact(contacts,"InPanelX",full,true,Quaternion.Euler(0,90,0)); Contact(contacts,"InPanelY",full,true,Quaternion.Euler(-90,0,0));
        }
        private static void ButtonContact(Transform parent,string name,Rect bounds)
        {
            // Keep finger spheres away from neighbouring rounded button edges.
            bounds.xMin+=8; bounds.xMax-=8; bounds.yMin+=6; bounds.yMax-=6;
            Contact(parent,name,bounds);
        }
        private static void Grips(Transform panel)
        {
            var grips=Child("Grips",panel).transform;
            var material=ToyControlShaderFactory.CreateStrip(AssetDatabase.GetAssetPath(panel.Find("Board").GetComponent<MeshRenderer>().sharedMaterial));
            foreach(var right in new[] {false,true})
            {
                var rect=Strip(right); var strip=GameObject.CreatePrimitive(PrimitiveType.Quad); strip.name=right ? "RightStrip" : "LeftStrip";
                DestroyImmediate(strip.GetComponent<Collider>()); strip.transform.SetParent(grips,false); strip.transform.localPosition=Position(rect.center.x,rect.center.y);
                strip.transform.localScale=new Vector3(rect.width/LogicalWidth*BoardWidth,rect.height/LogicalWidth*BoardWidth,1);
                var renderer=strip.GetComponent<MeshRenderer>(); renderer.sharedMaterial=material; renderer.shadowCastingMode=ShadowCastingMode.Off; renderer.receiveShadows=false;
            }
            foreach(var hand in new[] {"L","R"})
            {
                foreach(var right in new[] {false,true}) {var rect=Strip(right); rect.xMin-=8; rect.xMax+=8; Contact(grips,"Grab"+hand+(right ? "Right" : "Left"),rect,tag:"Hand"+hand,depth:0.06f);}
            }
        }
        private static VRCExpressionParameters Parameters(string folder)
        {
            var list=new List<VRCExpressionParameters.Parameter>();
            void Add(string name,VRCExpressionParameters.ValueType type) { list.Add(new VRCExpressionParameters.Parameter {name=name,valueType=type,networkSynced=false,saved=false,defaultValue=0}); }
            foreach(var name in ToyControlContract.BoolInputs) Add(ToyControlContract.Prefix+name,VRCExpressionParameters.ValueType.Bool);
            foreach(var name in ToyControlContract.FloatInputs) Add(ToyControlContract.Prefix+name,VRCExpressionParameters.ValueType.Float);
            list.Add(new VRCExpressionParameters.Parameter {name=ToyControlContract.Prefix+"Size",valueType=VRCExpressionParameters.ValueType.Float,networkSynced=false,saved=true,defaultValue=0.2f});
            Add(ToyControlContract.Prefix+"HandMode",VRCExpressionParameters.ValueType.Int);
            Add(ToyControlContract.Prefix+"RxBeat",VRCExpressionParameters.ValueType.Bool);
            foreach(var name in ToyControlContract.FeedbackFields()) Add(ToyControlContract.ReceivePrefix+name,VRCExpressionParameters.ValueType.Float);
            var asset=ScriptableObject.CreateInstance<VRCExpressionParameters>(); asset.parameters=list.ToArray(); AssetDatabase.CreateAsset(asset,folder+"/Parameters.asset"); return asset;
        }
        private static VRCExpressionsMenu Menu(string folder)
        {
            var menu=ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            VRCExpressionsMenu.Control Toggle(string label,string parameter) => new VRCExpressionsMenu.Control {name=label,type=VRCExpressionsMenu.Control.ControlType.Toggle,parameter=new VRCExpressionsMenu.Control.Parameter {name=ToyControlContract.Prefix+parameter},value=1};
            menu.controls=new List<VRCExpressionsMenu.Control> {Toggle("Show panel","Visible"),new VRCExpressionsMenu.Control {name="In right hand",type=VRCExpressionsMenu.Control.ControlType.Toggle,parameter=new VRCExpressionsMenu.Control.Parameter {name=ToyControlContract.Prefix+"HandMode"},value=1},new VRCExpressionsMenu.Control {name="Back of right hand",type=VRCExpressionsMenu.Control.ControlType.Toggle,parameter=new VRCExpressionsMenu.Control.Parameter {name=ToyControlContract.Prefix+"HandMode"},value=2},new VRCExpressionsMenu.Control {name="Panel size",type=VRCExpressionsMenu.Control.ControlType.RadialPuppet,subParameters=new[] {new VRCExpressionsMenu.Control.Parameter {name=ToyControlContract.Prefix+"Size"}}}};
            AssetDatabase.CreateAsset(menu,folder+"/Controls.asset"); return menu;
        }
        private static AnimatorController Controller(string folder,Vector3 avatarScale,float gap,float forward,Quaternion handFrame,bool capturedInHand)
        {
            var c=AnimatorController.CreateAnimatorControllerAtPath(folder+"/FX.controller");
            foreach(var name in ToyControlContract.BoolInputs) c.AddParameter(ToyControlContract.Prefix+name,AnimatorControllerParameterType.Bool);
            foreach(var name in ToyControlContract.FloatInputs) c.AddParameter(ToyControlContract.Prefix+name,AnimatorControllerParameterType.Float);
            c.AddParameter(new AnimatorControllerParameter {name=ToyControlContract.Prefix+"Size",type=AnimatorControllerParameterType.Float,defaultFloat=0.2f});
            foreach(var name in GripInputs) c.AddParameter(ToyControlContract.Prefix+name,AnimatorControllerParameterType.Bool);
            c.AddParameter(ToyControlContract.Prefix+"HandMode",AnimatorControllerParameterType.Int);
            c.AddParameter(ToyControlContract.Prefix+"HoldingHand",AnimatorControllerParameterType.Int);
            c.AddParameter(ToyControlContract.Prefix+"FitInHand",AnimatorControllerParameterType.Bool);
            c.AddParameter(ToyControlContract.Prefix+"FitBackHand",AnimatorControllerParameterType.Bool);
            c.AddParameter("GestureLeft",AnimatorControllerParameterType.Int); c.AddParameter("GestureRight",AnimatorControllerParameterType.Int);
            c.AddParameter(ToyControlContract.Prefix+"RxBeat",AnimatorControllerParameterType.Bool); c.AddParameter("IsLocal",AnimatorControllerParameterType.Bool);
            c.AddParameter(ToyControlContract.Prefix+"InputReady",AnimatorControllerParameterType.Bool);
            c.AddParameter(ToyControlContract.Prefix+"AppReady",AnimatorControllerParameterType.Bool);
            var weightName=ToyControlContract.Prefix+"Weight"; c.AddParameter(new AnimatorControllerParameter {name=weightName,type=AnimatorControllerParameterType.Float,defaultFloat=1});
            foreach(var name in ToyControlContract.FeedbackFields()) c.AddParameter(ToyControlContract.ReceivePrefix+name,AnimatorControllerParameterType.Float);
            var layers=c.layers; layers[0].name="Shader feedback"; layers[0].defaultWeight=1; c.layers=layers;
            var tree=new BlendTree {name="Numeric shader feedback",blendType=BlendTreeType.Direct}; AssetDatabase.AddObjectToAsset(tree,c);
            var normalized=new SerializedObject(tree); var flag=normalized.FindProperty("m_NormalizedBlendValues"); if(flag!=null) {flag.boolValue=false; normalized.ApplyModifiedPropertiesWithoutUndo();}
            foreach(var field in ToyControlContract.FeedbackFields())
            {
                var blend=new BlendTree {name=field,blendType=BlendTreeType.Simple1D,blendParameter=ToyControlContract.ReceivePrefix+field,useAutomaticThresholds=false}; AssetDatabase.AddObjectToAsset(blend,c);
                for(var value=0;value<=1;value++) {var clip=Clip(c,field+value,0.1f); Curve(clip,BoardPath,typeof(MeshRenderer),"material._"+field,value); blend.AddChild(clip,value);}
                tree.AddChild(blend); var children=tree.children; children[children.Length-1].directBlendParameter=weightName; tree.children=children;
            }
            var data=State(c.layers[0].stateMachine,"Feedback",tree); data.writeDefaultValues=true;
            PlacementLayer(c);
            var beat=Layer(c,"Avatar heartbeat"); var dormant=State(beat,"Hidden",Clip(c,"No heartbeat",0.25f)); var a=State(beat,"Pulse A",Clip(c,"Beat A",0.25f)); var b=State(beat,"Pulse B",Clip(c,"Beat B",0.25f)); beat.defaultState=dormant;
            Driver(dormant,"InBeat",0); Driver(dormant,"InTestTouch",0); Driver(a,"InBeat",1); Driver(b,"InBeat",0);
            var start=Transition(dormant,a); Bool(start,"Visible",true); start.AddCondition(AnimatorConditionMode.If,0,"IsLocal");
            foreach(var s in new[] {a,b}) {Bool(Transition(s,dormant),"Visible",false); Transition(s,dormant).AddCondition(AnimatorConditionMode.IfNot,0,"IsLocal");}
            var ab=Transition(a,b); ab.hasExitTime=true; ab.exitTime=1; var ba=Transition(b,a); ba.hasExitTime=true; ba.exitTime=1;
            var watch=Layer(c,"Backend freshness");
            var offline=State(watch,"Waiting for ShockWave",Material(c,"Offline","_LinkOnline",0,0.1f)); var onA=State(watch,"App pulse A",Material(c,"Online A","_LinkOnline",1,2)); var onB=State(watch,"App pulse B",Material(c,"Online B","_LinkOnline",1,2)); watch.defaultState=offline;
            Bool(Transition(offline,onA),"RxBeat",true); Bool(Transition(offline,onB),"RxBeat",false);
            Bool(Transition(onA,onB),"RxBeat",false); Bool(Transition(onB,onA),"RxBeat",true);
            foreach(var state in new[] {onA,onB}) {var timeout=Transition(state,offline); timeout.hasExitTime=true; timeout.exitTime=1;}
            // Offline must not immediately re-enter the same stale bit: separate
            // wait states remember which pulse expired, then require its opposite.
            var waitA=State(watch,"Expired A",Material(c,"Expired A","_LinkOnline",0,0.1f)); var waitB=State(watch,"Expired B",Material(c,"Expired B","_LinkOnline",0,0.1f));
            foreach(var state in new[] {offline,waitA,waitB}) Driver(state,"AppReady",0);
            foreach(var state in new[] {onA,onB}) Driver(state,"AppReady",1);
            foreach(var s in new[] {onA,onB}) foreach(var t in s.transitions.Where(t=>t.destinationState==offline).ToArray()) s.RemoveTransition(t);
            var ta=Transition(onA,waitA); ta.hasExitTime=true; ta.exitTime=1; var tb=Transition(onB,waitB); tb.hasExitTime=true; tb.exitTime=1;
            Bool(Transition(waitA,onB),"RxBeat",false); Bool(Transition(waitB,onA),"RxBeat",true);
            var sizing=Layer(c,"Panel size"); var sizes=new BlendTree {name="Display and touch size",blendType=BlendTreeType.Simple1D,blendParameter=ToyControlContract.Prefix+"Size",useAutomaticThresholds=false}; AssetDatabase.AddObjectToAsset(sizes,c);
            for(var i=0;i<2;i++)
            {
                var value=i==0 ? MinimumSize : MaximumSize; var clip=Clip(c,"Size "+value,0.1f);
                foreach(var axis in new[] {"x","y","z"}) Curve(clip,"Panel/Size",typeof(Transform),"m_LocalScale."+axis,value);
                if(!capturedInHand) {
                    var position=handFrame*new Vector3(-BoardWidth*value/2-gap,0,forward);
                    Curve(clip,"HandAnchors/RightHandPosition/SpawnRight",typeof(Transform),"m_LocalPosition.x",position.x);
                    Curve(clip,"HandAnchors/RightHandPosition/SpawnRight",typeof(Transform),"m_LocalPosition.y",position.y);
                    Curve(clip,"HandAnchors/RightHandPosition/SpawnRight",typeof(Transform),"m_LocalPosition.z",position.z);
                }
                sizes.AddChild(clip,i);
            }
            State(sizing,"Size",sizes); PointerLayers(c); ShockerViewLayer(c); PointerPositionLayer(c); EditorUtility.SetDirty(c); return c;
        }
        private static AnimatorStateMachine Layer(AnimatorController c,string name) {c.AddLayer(name); var layers=c.layers; layers[layers.Length-1].defaultWeight=1; c.layers=layers; return layers[layers.Length-1].stateMachine;}
        private static AnimatorState State(AnimatorStateMachine sm,string name,Motion motion) {var s=sm.AddState(name); s.motion=motion; s.writeDefaultValues=false; return s;}
        private static AnimatorStateTransition Transition(AnimatorState a,AnimatorState b) {var t=a.AddTransition(b); t.hasExitTime=false; t.duration=0; t.hasFixedDuration=true; return t;}
        private static void Bool(AnimatorStateTransition t,string suffix,bool value) {t.AddCondition(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,0,ToyControlContract.Prefix+suffix);}
        private static AnimationClip Clip(AnimatorController c,string name,float duration)
        {
            var clip=new AnimationClip {name=name,frameRate=60};
            // A harmless material property gives heartbeat/timeout clips a
            // duration without animating the panel transform or input values.
            Curve(clip,BoardPath,typeof(MeshRenderer),"material._Clock",0,duration);
            AssetDatabase.AddObjectToAsset(clip,c); return clip;
        }
        private static void Curve(AnimationClip clip,string path,Type type,string property,float value,float duration=0.1f) {AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(path,type,property),AnimationCurve.Constant(0,duration,value));}
        private static AnimationClip Placement(AnimatorController c,string name,bool visible,bool freeze,float duration,int source=0,bool rebake=false,bool contacts=true)
        {
            var clip=Clip(c,name,duration); Curve(clip,"Panel",typeof(GameObject),"m_IsActive",visible ? 1 : 0,duration); Curve(clip,"Panel",typeof(VRCParentConstraint),"FreezeToWorld",freeze ? 1 : 0,duration);
            Curve(clip,"HandAnchors",typeof(GameObject),"m_IsActive",visible ? 1 : 0,duration);
            Curve(clip,"Panel",typeof(VRCParentConstraint),"RebakeOffsetsWhenUnfrozen",rebake ? 1 : 0,duration);
            Curve(clip,"Panel/Size/Contacts",typeof(GameObject),"m_IsActive",visible && contacts ? 1 : 0,duration);
            for(var i=0;i<6;i++) Curve(clip,"Panel",typeof(VRCParentConstraint),"Sources.source"+i+".Weight",i==source ? 1 : 0,duration);
            foreach(var kind in new[] {"ParentPositionOffset","ParentRotationOffset"}) foreach(var axis in new[] {"x","y","z"}) Curve(clip,"Panel",typeof(VRCParentConstraint),"Sources.source0."+kind+"."+axis,0,duration);
            return clip;
        }
        private static AnimationClip Material(AnimatorController c,string name,string property,float value,float duration) {var clip=Clip(c,name,duration); Curve(clip,BoardPath,typeof(MeshRenderer),"material."+property,value,duration); return clip;}
        private static void Driver(AnimatorState state,string suffix,float value) {var driver=state.behaviours.OfType<VRCAvatarParameterDriver>().FirstOrDefault() ?? state.AddStateMachineBehaviour<VRCAvatarParameterDriver>(); driver.localOnly=true; if(driver.parameters==null) driver.parameters=new List<VRCAvatarParameterDriver.Parameter>(); driver.parameters.Add(new VRCAvatarParameterDriver.Parameter {name=ToyControlContract.Prefix+suffix,type=VRCAvatarParameterDriver.ChangeType.Set,value=value});}
        private static void EnsureFolder(string folder) {var parts=folder.Split('/'); var current=parts[0]; foreach(var part in parts.Skip(1)) {if(!AssetDatabase.IsValidFolder(current+"/"+part)) AssetDatabase.CreateFolder(current,part); current+="/"+part;}}
    }
}
#endif
