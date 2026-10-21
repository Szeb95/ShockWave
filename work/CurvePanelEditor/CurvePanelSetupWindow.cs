#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using com.vrcfury.api;
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

namespace ShockWave.CurvePanel
{
    // Editor installer only. The uploaded avatar uses constraints, contacts,
    // animation and particles, with no custom runtime MonoBehaviour or socket.
    public sealed class CurvePanelSetupWindow : EditorWindow
    {
        internal const string HostName = "SzebsCurvePanel";
        internal const string Prefix = "SzebsPanel_";
        internal const string AssetRoot = "Assets/SzebsToys/CurvePanel";
        internal const float GraphWidth = 0.54f, GraphHeight = 0.19f, GraphBottom = -0.035f;
        internal const float HistorySeconds = 8f;
        internal static readonly string[] BoolNames = { "Visible", "Follow", "Recall", "Touch", "Stop", "Clear" };
        [SerializeField] private VRCAvatarDescriptor avatar;
        [SerializeField] private float distance = 0.60f;
        [SerializeField] private float belowEyes = 0.18f;

        [MenuItem("Tools/ShockWave/Curve Panel/Add to Avatar")]
        public static void Open()
        {
            var window = GetWindow<CurvePanelSetupWindow>("ShockWave Curve Panel");
            window.minSize = new Vector2(510, 360);
            window.UseSelection();
            window.Show();
        }

        private void OnSelectionChange() { if (avatar == null) UseSelection(); Repaint(); }
        private void UseSelection()
        {
            if (Selection.activeGameObject != null)
                avatar = Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Avatar curve panel · first touch test", EditorStyles.boldLabel);
            avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField("Avatar", avatar, typeof(VRCAvatarDescriptor), true);
            distance = EditorGUILayout.Slider("Distance in front (m)", distance, 0.35f, 1.2f);
            belowEyes = EditorGUILayout.Slider("Below eye level (m)", belowEyes, 0, 0.4f);
            EditorGUILayout.HelpBox("Adds a ShockWave Curve Panel root with a VRCFury Full Controller, " +
                "a local panel, and a Curve Panel menu with a Show panel toggle. Touch higher for a higher curve; " +
                "the last eight seconds scroll left. The panel places itself in front of your head and stays " +
                "there until recalled. Follow head is optional. Run in Edit mode and save the scene.", MessageType.Info);
            EditorGUILayout.HelpBox("This is a visual/OSC prototype. Preview level changes only the graph. " +
                "Own / Remote / Both exports a selection; toy and remote routing are not connected yet. " +
                "No shock or toy command is sent by this installer.", MessageType.Info);
            var error = ValidateAvatar(avatar);
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);
            using (new EditorGUI.DisabledScope(error != null))
                if (GUILayout.Button("Add Curve Panel to Avatar", GUILayout.Height(36))) Install();
        }

        internal static string ValidateAvatar(VRCAvatarDescriptor target)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Exit Play mode before installing.";
            if (target == null || EditorUtility.IsPersistent(target) || !target.gameObject.scene.IsValid() ||
                !target.gameObject.scene.isLoaded) return "Select an avatar in a loaded scene, not a prefab asset.";
            if (target.GetComponentsInChildren<Transform>(true).Any(t => t != target.transform && t.name == HostName))
                return "This avatar already has the Curve Panel. Existing content is kept; undo/remove that installation before adding another.";
            return null;
        }

        private void Install()
        {
            var error = ValidateAvatar(avatar);
            if (error != null) return;
            GameObject host = null;
            string folder = null;
            try
            {
                host = Build(avatar, distance, belowEyes, out folder);
                Undo.RegisterCreatedObjectUndo(host, "Add ShockWave Curve Panel");
                EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                Selection.activeGameObject = host;
                SceneView.lastActiveSceneView?.Frame(new Bounds(host.transform.Find("Panel").position, Vector3.one * 0.85f), false);
                Debug.Log("ShockWave curve panel installed. Save the scene; in VRChat use Curve Panel > Show panel. Assets: " + folder, host);
                EditorUtility.DisplayDialog("Curve panel added", "The ShockWave Curve Panel was added under your avatar. " +
                    "Save the scene. The panel is visible here for positioning, but starts hidden in VRChat. " +
                    "Use Curve Panel > Show panel, then touch the graph. Preview level provides a menu-only visual test.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("Curve panel could not be added", "The partial new installation was removed. See the Console for the cause.", "OK");
            }
        }

        internal static GameObject Build(VRCAvatarDescriptor target, float panelDistance, float verticalOffset, out string folder)
        {
            var error = ValidateAvatar(target);
            if (error != null) throw new InvalidOperationException(error);
            if (float.IsNaN(panelDistance) || float.IsInfinity(panelDistance) || float.IsNaN(verticalOffset) || float.IsInfinity(verticalOffset) || panelDistance < 0.35f ||
                panelDistance > 1.2f || verticalOffset < 0 || verticalOffset > 0.4f)
                throw new ArgumentOutOfRangeException(nameof(panelDistance));
            folder = null;
            GameObject host = null;
            try
            {
                EnsureFolder(AssetRoot);
                folder = AssetDatabase.GenerateUniqueAssetPath(AssetRoot + "/" + SafeName(target.name));
                AssetDatabase.CreateFolder(AssetRoot, Path.GetFileName(folder));
                var flatShader = CreateShader(folder, "Panel", FlatShader);
                var dotShader = CreateShader(folder, "Dot", DotShader);
                var background = CreateBackground(folder);
                var board = MaterialAsset(folder, "Board", flatShader, Color.white, background);
                var ink = MaterialAsset(folder, "Ink", dotShader, new Color(0.25f, 1f, 0.75f));
                var current = MaterialAsset(folder, "Current", flatShader, new Color(0.45f, 1f, 0.82f));
                var route = MaterialAsset(folder, "Route", flatShader, new Color(0.35f, 0.95f, 0.75f));
                var contactLamp = MaterialAsset(folder, "Contact", flatShader, new Color(0.25f, 1f, 0.65f));

                host = Child(HostName, target.transform);
                var panel = Child("Panel", host.transform);
                var eyePosition = target.transform.TransformPoint(target.ViewPosition);
                panel.transform.position = eyePosition + target.transform.forward * panelDistance - target.transform.up * verticalOffset;
                panel.transform.rotation = target.transform.rotation;
                // Keep panel dimensions in meters when the avatar root is scaled.
                var scale = target.transform.lossyScale;
                if (scale.x <= 0 || scale.y <= 0 || scale.z <= 0) throw new InvalidOperationException("The avatar must have a positive, nonzero scale.");
                panel.transform.localScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
                var animator = target.GetComponent<Animator>();
                var head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : target.transform;
                if (head == null) head = target.transform;
                var constraint = panel.AddComponent<VRCParentConstraint>();
                constraint.Sources.Add(new VRCConstraintSource(head, 1,
                    head.InverseTransformPoint(panel.transform.position),
                    (Quaternion.Inverse(head.rotation) * panel.transform.rotation).eulerAngles));
                constraint.IsActive = true;
                constraint.Locked = true;
                constraint.FreezeToWorld = false;

                Quad("Board", panel.transform, new Vector3(0, 0, 0), new Vector2(0.64f, 0.45f), board);
                Quad("CurrentPoint", panel.transform, new Vector3(GraphWidth / 2, GraphBottom, -0.012f),
                    new Vector2(0.009f, 0.009f), current);
                var lamp = Quad("ContactLamp", panel.transform, new Vector3(0.27f, 0.198f, -0.012f), new Vector2(0.009f, 0.009f), contactLamp);
                lamp.SetActive(false);
                for (var i = 0; i < 3; i++)
                    Quad("Route" + i, panel.transform, new Vector3(-0.164f + i * 0.164f, -0.103f, -0.012f),
                        new Vector2(0.125f, 0.0025f), route).SetActive(i == 0);

                var history = Child("History", panel.transform);
                history.transform.localPosition = new Vector3(GraphWidth / 2, GraphBottom, -0.009f);
                CreateHistory(history, panel.transform, ink);
                CreateContacts(panel.transform);
                var parameters = CreateParameters(folder);
                var menu = CreateMenu(folder);
                var controller = CreateController(folder);
                var full = FuryComponents.CreateFullController(host);
                full.AddController(controller, VRCAvatarDescriptor.AnimLayerType.FX);
                full.AddMenu(menu);
                full.AddParams(parameters);
                foreach (var param in parameters.parameters) full.AddGlobalParam(param.name);
                AssetDatabase.SaveAssets();
                return host;
            }
            catch
            {
                if (host != null) DestroyImmediate(host);
                // Only the unique folder created by this invocation is removed.
                if (folder != null && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                throw;
            }
        }

        private static GameObject Child(string name, Transform parent)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            return obj;
        }

        private static GameObject Quad(string name, Transform parent, Vector3 position, Vector2 size, Material material)
        {
            var obj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            obj.name = name;
            DestroyImmediate(obj.GetComponent<Collider>());
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            obj.transform.localScale = new Vector3(size.x, size.y, 1);
            var renderer = obj.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return obj;
        }

        private static void CreateHistory(GameObject history, Transform panel, Material ink)
        {
            var particles = history.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.duration = HistorySeconds;
            main.loop = true;
            main.startLifetime = HistorySeconds;
            main.startSpeed = 0;
            main.startSize = 0.005f;
            main.startColor = Color.white;
            main.maxParticles = 260;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.Custom;
            main.customSimulationSpace = panel;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var emission = particles.emission;
            emission.rateOverTime = 30f;
            var shape = particles.shape; shape.enabled = false;
            var velocity = particles.velocityOverLifetime;
            velocity.enabled = true; velocity.space = ParticleSystemSimulationSpace.Local;
            velocity.x = -GraphWidth / HistorySeconds;
            velocity.y = 0; velocity.z = 0;
            var renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = ink;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            particles.useAutoRandomSeed = false;
            particles.randomSeed = 1234;
        }

        private static void CreateContacts(Transform panel)
        {
            var contacts = Child("Contacts", panel);
            var touch = Child("TouchArea", contacts.transform).AddComponent<VRCContactReceiver>();
            SetContact(touch, "Touch", ContactReceiver.ReceiverType.Constant);
            touch.position = new Vector3(0, GraphBottom + GraphHeight / 2, -0.022f);
            var level = Child("Height", contacts.transform).AddComponent<VRCContactReceiver>();
            SetContact(level, "Level", ContactReceiver.ReceiverType.Proximity);
            // Box/face proximity is required: do not silently replace a screen
            // coordinate with radial distance on an older SDK.
            SetBox(touch, new Vector3(GraphWidth, GraphHeight, 0.045f), false);
            level.position = touch.position;
            level.rotation = Quaternion.Euler(-90, 0, 0); // positive Z face becomes the graph's top edge
            SetBox(level, new Vector3(GraphWidth, 0.045f, GraphHeight), true);
        }

        private static void SetContact(VRCContactReceiver receiver, string suffix, ContactReceiver.ReceiverType type)
        {
            receiver.parameter = Prefix + suffix;
            receiver.receiverType = type;
            receiver.allowSelf = true;
            receiver.allowOthers = false;
            receiver.localOnly = true;
            receiver.collisionTags = new List<string> { "Finger" };
        }

        private static void SetBox(VRCContactReceiver receiver, Vector3 size, bool face)
        {
            var data = new SerializedObject(receiver);
            var shape = data.FindProperty("shapeType");
            var dimensions = data.FindProperty("size");
            var proximity = data.FindProperty("useFaceProximity");
            var box = shape == null ? -1 : Array.IndexOf(shape.enumNames, "Box");
            if (box < 0 || dimensions == null || face && proximity == null)
                throw new InvalidOperationException("This panel needs VRChat SDK contact boxes with Use Face Proximity. Update the Avatars SDK before installing.");
            shape.enumValueIndex = box;
            dimensions.vector3Value = size;
            if (proximity != null) proximity.boolValue = face;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private static VRCExpressionParameters CreateParameters(string folder)
        {
            var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            var list = BoolNames.Select(name => Parameter(name, VRCExpressionParameters.ValueType.Bool)).ToList();
            list.Add(Parameter("Level", VRCExpressionParameters.ValueType.Float));
            list.Add(Parameter("Preview", VRCExpressionParameters.ValueType.Float));
            list.Add(Parameter("Route", VRCExpressionParameters.ValueType.Int));
            asset.parameters = list.ToArray();
            AssetDatabase.CreateAsset(asset, folder + "/Parameters.asset");
            return asset;
        }

        private static VRCExpressionParameters.Parameter Parameter(string suffix, VRCExpressionParameters.ValueType type) =>
            new VRCExpressionParameters.Parameter { name = Prefix + suffix, valueType = type, defaultValue = 0, saved = false, networkSynced = false };

        private static VRCExpressionsMenu CreateMenu(string folder)
        {
            var root = NewMenu(folder, "Menu");
            var main = NewMenu(folder, "Controls");
            var targets = NewMenu(folder, "Destination");
            root.controls.Add(Submenu("Curve Panel", main));
            main.controls.Add(Control("Show panel", "Visible", VRCExpressionsMenu.Control.ControlType.Toggle));
            main.controls.Add(Control("Follow head", "Follow", VRCExpressionsMenu.Control.ControlType.Toggle));
            main.controls.Add(Control("Recall in front", "Recall", VRCExpressionsMenu.Control.ControlType.Button));
            main.controls.Add(Control("Clear curve", "Clear", VRCExpressionsMenu.Control.ControlType.Button));
            main.controls.Add(Control("Stop", "Stop", VRCExpressionsMenu.Control.ControlType.Button));
            main.controls.Add(new VRCExpressionsMenu.Control { name = "Preview level", type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = Prefix + "Preview" } } });
            main.controls.Add(Submenu("Destination", targets));
            targets.controls.Add(Control("Own toys", "Route", VRCExpressionsMenu.Control.ControlType.Toggle, 0));
            targets.controls.Add(Control("Remote toys", "Route", VRCExpressionsMenu.Control.ControlType.Toggle, 1));
            targets.controls.Add(Control("Both", "Route", VRCExpressionsMenu.Control.ControlType.Toggle, 2));
            EditorUtility.SetDirty(root); EditorUtility.SetDirty(main); EditorUtility.SetDirty(targets);
            return root;
        }

        private static VRCExpressionsMenu NewMenu(string folder, string name)
        {
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.controls = new List<VRCExpressionsMenu.Control>();
            AssetDatabase.CreateAsset(menu, folder + "/" + name + ".asset");
            return menu;
        }

        private static VRCExpressionsMenu.Control Submenu(string name, VRCExpressionsMenu menu) =>
            new VRCExpressionsMenu.Control { name = name, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = menu };
        private static VRCExpressionsMenu.Control Control(string name, string param, VRCExpressionsMenu.Control.ControlType type, float value = 1) =>
            new VRCExpressionsMenu.Control { name = name, type = type, value = value, parameter = new VRCExpressionsMenu.Control.Parameter { name = Prefix + param } };

        private static AnimatorController CreateController(string folder)
        {
            var c = AnimatorController.CreateAnimatorControllerAtPath(folder + "/FX.controller");
            foreach (var suffix in BoolNames) c.AddParameter(Prefix + suffix, AnimatorControllerParameterType.Bool);
            c.AddParameter(Prefix + "Level", AnimatorControllerParameterType.Float);
            c.AddParameter(Prefix + "Preview", AnimatorControllerParameterType.Float);
            c.AddParameter(Prefix + "Route", AnimatorControllerParameterType.Int);
            c.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            var layers = c.layers; layers[0].name = "Panel placement"; layers[0].defaultWeight = 1; c.layers = layers;
            var sm = c.layers[0].stateMachine;
            var hidden = State(sm, "Hidden", PlacementClip(folder, "Hidden", false, false, 0.01f));
            var placing = State(sm, "Place in front", PlacementClip(folder, "Place", true, false, 0.15f));
            var pinned = State(sm, "Placed in world", PlacementClip(folder, "Pinned", true, true, 0.01f));
            var following = State(sm, "Follow head", PlacementClip(folder, "Follow", true, false, 0.01f));
            sm.defaultState = hidden;
            ResetInputs(hidden);
            var show = Transition(hidden, placing); Bool(show, "Visible", true); Local(show, true);
            foreach (var state in new[] { placing, pinned, following })
            {
                Bool(Transition(state, hidden), "Visible", false);
                Local(Transition(state, hidden), false);
            }
            Bool(Transition(pinned, following), "Follow", true);
            Bool(Transition(following, placing), "Follow", false);
            Bool(Transition(pinned, placing), "Recall", true);
            var follow = Transition(placing, following); follow.hasExitTime = true; follow.exitTime = 1; Bool(follow, "Follow", true);
            var freeze = Transition(placing, pinned); freeze.hasExitTime = true; freeze.exitTime = 1;
            Bool(freeze, "Follow", false); Bool(freeze, "Recall", false);

            c.AddLayer("Touch graph");
            sm = WeightedLayer(c, c.layers.Length - 1);
            var touch = State(sm, "Finger height", HeightTree(folder, "Level"));
            var preview = State(sm, "Menu preview", HeightTree(folder, "Preview"));
            sm.defaultState = preview;
            Bool(Transition(preview, touch), "Touch", true);
            Bool(Transition(touch, preview), "Touch", false);

            c.AddLayer("History reset");
            sm = WeightedLayer(c, c.layers.Length - 1);
            var drawing = State(sm, "Draw", ActiveClip(folder, "Draw", "Panel/History", true));
            var cleared = State(sm, "Clear or stop", ActiveClip(folder, "Clear", "Panel/History", false));
            sm.defaultState = drawing;
            Bool(Transition(drawing, cleared), "Clear", true);
            Bool(Transition(drawing, cleared), "Stop", true);
            var resume = Transition(cleared, drawing); Bool(resume, "Clear", false); Bool(resume, "Stop", false);

            c.AddLayer("Contact indicator");
            sm = WeightedLayer(c, c.layers.Length - 1);
            var idle = State(sm, "No touch", ActiveClip(folder, "IdleLamp", "Panel/ContactLamp", false));
            var active = State(sm, "Touch", ActiveClip(folder, "TouchLamp", "Panel/ContactLamp", true));
            sm.defaultState = idle;
            Bool(Transition(idle, active), "Touch", true);
            Bool(Transition(active, idle), "Touch", false);

            c.AddLayer("Destination indicator");
            sm = WeightedLayer(c, c.layers.Length - 1);
            for (var i = 0; i < 3; i++)
            {
                var clip = Clip(folder, "Route" + i);
                for (var j = 0; j < 3; j++) Curve(clip, "Panel/Route" + j, typeof(GameObject), "m_IsActive", i == j ? 1 : 0);
                var state = State(sm, new[] { "Own", "Remote", "Both" }[i], clip);
                if (i == 0) sm.defaultState = state;
                var transition = sm.AddAnyStateTransition(state);
                transition.hasExitTime = false; transition.duration = 0; transition.canTransitionToSelf = false;
                transition.AddCondition(AnimatorConditionMode.Equals, i, Prefix + "Route");
            }
            EditorUtility.SetDirty(c);
            return c;
        }

        private static AnimatorStateMachine WeightedLayer(AnimatorController c, int index)
        {
            var layers = c.layers; layers[index].defaultWeight = 1; c.layers = layers;
            return c.layers[index].stateMachine;
        }
        private static AnimatorState State(AnimatorStateMachine sm, string name, Motion motion)
        {
            var s = sm.AddState(name); s.motion = motion; s.writeDefaultValues = false; return s;
        }
        private static AnimatorStateTransition Transition(AnimatorState a, AnimatorState b)
        {
            var t = a.AddTransition(b); t.hasExitTime = false; t.duration = 0; t.hasFixedDuration = true; return t;
        }
        private static void Bool(AnimatorStateTransition t, string suffix, bool value) =>
            t.AddCondition(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0, Prefix + suffix);
        private static void Local(AnimatorStateTransition t, bool value) =>
            t.AddCondition(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0, "IsLocal");
        private static AnimationClip Clip(string folder, string name)
        {
            var clip = new AnimationClip { name = "SzebsPanel_" + name, frameRate = 60 };
            AssetDatabase.CreateAsset(clip, folder + "/" + name + ".anim");
            return clip;
        }
        private static void Curve(AnimationClip clip, string path, Type type, string property, float value, float duration = 0.01f) =>
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, duration, value));
        private static AnimationClip PlacementClip(string folder, string name, bool visible, bool freeze, float duration)
        {
            var clip = Clip(folder, name);
            Curve(clip, "Panel", typeof(GameObject), "m_IsActive", visible ? 1 : 0, duration);
            Curve(clip, "Panel", typeof(VRCParentConstraint), "FreezeToWorld", freeze ? 1 : 0, duration);
            return clip;
        }
        private static AnimationClip ActiveClip(string folder, string name, string path, bool active)
        {
            var clip = Clip(folder, name); Curve(clip, path, typeof(GameObject), "m_IsActive", active ? 1 : 0); return clip;
        }
        private static BlendTree HeightTree(string folder, string suffix)
        {
            var tree = new BlendTree { name = "Height " + suffix, blendType = BlendTreeType.Simple1D,
                blendParameter = Prefix + suffix, useAutomaticThresholds = false };
            for (var i = 0; i < 2; i++)
            {
                var clip = Clip(folder, suffix + i);
                foreach (var path in new[] { "Panel/History", "Panel/CurrentPoint" })
                    Curve(clip, path, typeof(Transform), "m_LocalPosition.y", GraphBottom + GraphHeight * i);
                tree.AddChild(clip, i);
            }
            AssetDatabase.CreateAsset(tree, folder + "/" + suffix + "Height.asset");
            return tree;
        }
        private static void ResetInputs(AnimatorState state)
        {
            var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>(); driver.localOnly = true;
            driver.parameters = new List<VRCAvatarParameterDriver.Parameter>();
            foreach (var suffix in new[] { "Touch", "Level", "Preview" }) driver.parameters.Add(new VRCAvatarParameterDriver.Parameter
                { name = Prefix + suffix, type = VRCAvatarParameterDriver.ChangeType.Set, value = 0 });
        }

        private static Shader CreateShader(string folder, string name, string text)
        {
            var path = folder + "/" + name + ".shader";
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException(name + " shader could not compile.");
            return shader;
        }
        private static Material MaterialAsset(string folder, string name, Shader shader, Color color, Texture texture = null)
        {
            var material = new Material(shader) { name = name, color = color };
            if (texture != null) material.mainTexture = texture;
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        private static Texture2D CreateBackground(string folder)
        {
            const int width = 1536, height = 1080;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = Enumerable.Repeat(new Color32(17, 24, 34, 255), width * height).ToArray();
            void Rect(int x, int y, int w, int h, Color32 color)
            {
                for (var yy = Math.Max(0, y); yy < Math.Min(height, y + h); yy++)
                    for (var xx = Math.Max(0, x); xx < Math.Min(width, x + w); xx++) pixels[yy * width + xx] = color;
            }
            Rect(0, 0, width, 5, new Color32(51, 202, 157, 255));
            Rect(0, height - 5, width, 5, new Color32(51, 202, 157, 255));
            // Pixel mapping matches the physical graph and route indicators.
            int X(float x) => Mathf.RoundToInt((x / 0.64f + 0.5f) * width);
            int Y(float y) => Mathf.RoundToInt((y / 0.45f + 0.5f) * height);
            var left = X(-GraphWidth / 2); var right = X(GraphWidth / 2);
            var bottom = Y(GraphBottom); var top = Y(GraphBottom + GraphHeight);
            Rect(left, bottom, right - left, top - bottom, new Color32(10, 16, 24, 255));
            for (var i = 0; i <= 4; i++) Rect(left, bottom + (top - bottom) * i / 4, right - left, 2, new Color32(40, 55, 68, 255));
            for (var i = 0; i <= 8; i++) Rect(left + (right - left) * i / 8, bottom, 2, top - bottom, new Color32(35, 47, 59, 255));
            Rect(right - 2, bottom, 3, top - bottom, new Color32(75, 211, 169, 255));
            texture.SetPixels32(pixels); texture.Apply();
            BakeLabels(texture, new[] {
                new Label("SHOCKWAVE CURVE PANEL", 75, 1005, 42, new Color(0.86f, 0.94f, 1)),
                new Label("LIVE TOUCH TEST", 78, 938, 25, new Color(0.4f, 0.88f, 0.73f)),
                new Label("100", 15, top - 10, 20, new Color(0.55f, 0.65f, 0.74f)),
                new Label("0", 45, bottom, 20, new Color(0.55f, 0.65f, 0.74f)),
                new Label("-8s", left, bottom - 50, 21, new Color(0.55f, 0.65f, 0.74f)),
                new Label("NOW", right - 70, bottom - 50, 21, new Color(0.4f, 0.88f, 0.73f)),
                new Label("OWN", X(-0.164f) - 43, Y(-0.087f), 26, Color.white),
                new Label("REMOTE", X(0) - 71, Y(-0.087f), 26, Color.white),
                new Label("BOTH", X(0.164f) - 51, Y(-0.087f), 26, Color.white),
                new Label("TOUCH HIGHER TO DRAW A STRONGER CURVE", 105, 180, 26, new Color(0.76f, 0.85f, 0.91f)),
                new Label("MENU: RECALL / CLEAR / STOP / DESTINATION", 105, 120, 24, new Color(0.55f, 0.65f, 0.74f)),
                new Label("VISUAL TEST  |  TOY ROUTING NOT CONNECTED", 105, 58, 22, new Color(0.4f, 0.88f, 0.73f))
            });
            var path = folder + "/Board.png";
            File.WriteAllBytes(path, texture.EncodeToPNG()); DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.mipmapEnabled = false; importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048; importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        private readonly struct Label
        {
            public readonly string Text; public readonly int X, Y, Size; public readonly Color Color;
            public Label(string text, int x, int y, int size, Color color) { Text = text; X = x; Y = y; Size = size; Color = color; }
        }
        private static void BakeLabels(Texture2D destination, Label[] labels)
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            foreach (var group in labels.GroupBy(label => label.Size))
            {
                font.RequestCharactersInTexture(string.Concat(group.Select(label => label.Text)), group.Key, FontStyle.Normal);
                var atlasTexture = font.material.mainTexture;
                var rt = RenderTexture.GetTemporary(atlasTexture.width, atlasTexture.height, 0, RenderTextureFormat.ARGB32);
                var previous = RenderTexture.active;
                Texture2D atlas = null;
                try
                {
                    Graphics.Blit(atlasTexture, rt); RenderTexture.active = rt;
                    atlas = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                    atlas.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); atlas.Apply();
                    foreach (var label in group)
                    {
                        var x = label.X;
                        foreach (var character in label.Text)
                        {
                            if (!font.GetCharacterInfo(character, out var info, label.Size, FontStyle.Normal)) continue;
                            var w = info.maxX - info.minX; var h = info.maxY - info.minY;
                            for (var yy = 0; yy < h; yy++) for (var xx = 0; xx < w; xx++)
                            {
                                var uv = Vector2.Lerp(Vector2.Lerp(info.uvBottomLeft, info.uvBottomRight, (xx + 0.5f) / w),
                                    Vector2.Lerp(info.uvTopLeft, info.uvTopRight, (xx + 0.5f) / w), (yy + 0.5f) / h);
                                var alpha = atlas.GetPixelBilinear(uv.x, uv.y).a;
                                var dx = x + info.minX + xx; var dy = label.Y + info.minY + yy;
                                if (dx < 0 || dy < 0 || dx >= destination.width || dy >= destination.height) continue;
                                destination.SetPixel(dx, dy, Color.Lerp(destination.GetPixel(dx, dy), label.Color, alpha));
                            }
                            x += info.advance;
                        }
                    }
                }
                finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); if (atlas != null) DestroyImmediate(atlas); }
            }
            destination.Apply();
        }
        private static void EnsureFolder(string folder)
        {
            var parts = folder.Split('/'); var current = parts[0];
            foreach (var part in parts.Skip(1)) { if (!AssetDatabase.IsValidFolder(current + "/" + part)) AssetDatabase.CreateFolder(current, part); current += "/" + part; }
        }
        private static string SafeName(string name)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "Avatar" : name.Trim();
        }
        private const string FlatShader = @"Shader ""ShockWave/CurvePanel/Unlit"" {
Properties { _MainTex (""Texture"", 2D) = ""white"" {} _Color (""Color"", Color) = (1,1,1,1) }
SubShader { Tags { ""RenderType""=""Opaque"" } Cull Off ZWrite On
Pass { CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma multi_compile_instancing
#include ""UnityCG.cginc""
sampler2D _MainTex; fixed4 _Color;
struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
v2f vert(appdata v) { v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_OUTPUT(v2f,o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o); o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
fixed4 frag(v2f i):SV_Target { return tex2D(_MainTex,i.uv)*_Color; }
ENDCG } } }";
        private const string DotShader = @"Shader ""ShockWave/CurvePanel/HistoryDot"" {
Properties { _Color (""Color"", Color) = (0.2,1,0.7,1) }
SubShader { Tags { ""Queue""=""Transparent"" ""RenderType""=""Transparent"" } Blend SrcAlpha OneMinusSrcAlpha Cull Off ZWrite Off
Pass { CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma multi_compile_instancing
#include ""UnityCG.cginc""
fixed4 _Color;
struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; UNITY_VERTEX_OUTPUT_STEREO };
v2f vert(appdata v) { v2f o; UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_OUTPUT(v2f,o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o); o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; o.color=v.color*_Color; return o; }
fixed4 frag(v2f i):SV_Target { float d=length(i.uv*2-1); fixed4 c=i.color; c.a*=1-smoothstep(0.65,1,d); return c; }
ENDCG } } }";
    }
}
#endif
