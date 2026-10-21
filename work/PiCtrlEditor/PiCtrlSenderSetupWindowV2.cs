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
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;

namespace ShockWave.PiCtrl
{
    public sealed class PiCtrlSenderSetupWindow : EditorWindow
    {
        private const string HostName = "PiCtrl_TX";
        private const string AssetRoot = "Assets/SzebsToys/PiCtrl";
        private const string DataTag = "PiCtrl_DATA";
        private const string ClockTag = "PiCtrl_CLOCK";
        private const string Intensity = "PiCtrl_IntensityRadial";
        private const string Duration = "PiCtrl_DurationRadial";
        private const string Shock = "PiCtrl_Shock";
        private const string Vibrate = "PiCtrl_Vibrate";
        private const string Command = "PiCtrl_Command";
        private const int FramesPerSecond = 60;
        private const int StartGapFrames = 36;   // 0.60 seconds
        private const int DataSetupFrames = 15;  // 0.25 seconds
        private const int ClockHighFrames = 18;  // 0.30 seconds
        private const int ClockLowFrames = 18;   // 0.30 seconds
        private const int DuplicateGapFrames = 90; // 1.50 seconds
        private const int TailFrames = 18;

        private static readonly float[] DurationSeconds =
            { 0.5f, 1f, 2f, 3f, 5f, 8f, 10f, 13f, 15f };

        [SerializeField] private VRCAvatarDescriptor avatar;
        [SerializeField] private Transform contactAnchor;
        [SerializeField] private float contactRadius = 0.08f;

        [MenuItem("Tools/ShockWave/PiCtrl/Create or Replace Sender")]
        private static void Open()
        {
            var window = GetWindow<PiCtrlSenderSetupWindow>("PiCtrl Sender");
            window.minSize = new Vector2(500, 330);
            window.SetAvatarFromSelection();
            window.Show();
        }

        private void OnSelectionChange()
        {
            if (avatar == null) SetAvatarFromSelection();
            Repaint();
        }

        private void SetAvatarFromSelection()
        {
            if (Selection.activeGameObject == null) return;
            var selected = Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>();
            if (selected != null && selected != avatar)
            {
                avatar = selected;
                contactAnchor = DefaultAnchor(selected);
            }
        }

        private static Transform DefaultAnchor(VRCAvatarDescriptor selected)
        {
            if (selected == null) return null;
            var animator = selected.GetComponent<Animator>();
            return animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.RightHand)
                : selected.transform;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("PiCtrl sender", EditorStyles.boldLabel);
            var selected = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                "Avatar", avatar, typeof(VRCAvatarDescriptor), true);
            if (selected != avatar)
            {
                avatar = selected;
                contactAnchor = DefaultAnchor(selected);
            }
            contactAnchor = (Transform)EditorGUILayout.ObjectField(
                "Contact Anchor", contactAnchor, typeof(Transform), true);
            contactRadius = EditorGUILayout.Slider("Sender Radius (m)", contactRadius, 0.03f, 0.2f);

            EditorGUILayout.HelpBox(
                "Builds two custom-tag Contact Senders and a populated VRCFury FX Full Controller. " +
                "The two radial values are captured when Shock or Vibrate is pressed. The FX then " +
                "sends the same framed DATA/CLOCK packet twice. This replaces a previous PiCtrl_TX " +
                "on the selected avatar, including its generated PiCtrl assets.", MessageType.Info);
            EditorGUILayout.LabelField("Expression Parameters", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("2 local Float radials + 2 local Bool buttons");
            EditorGUILayout.LabelField("1 synced Int command (8 synced bits)");
            EditorGUILayout.LabelField("Intensity: nearest 10%, ties downward (15% -> 10%)");
            EditorGUILayout.LabelField("Duration: nearest listed preset, ties downward");
            EditorGUILayout.LabelField("Packet time: about 30 seconds for both copies");

            var error = Validate();
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);
            using (new EditorGUI.DisabledScope(error != null))
            {
                if (GUILayout.Button("Create / Replace PiCtrl Sender", GUILayout.Height(34)))
                    Generate();
            }
        }

        private string Validate()
        {
            if (avatar == null) return "Select an avatar in the scene.";
            if (EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid() ||
                !avatar.gameObject.scene.isLoaded)
                return "Select an avatar in a loaded scene, not a prefab asset.";
            if (contactAnchor == null || !contactAnchor.IsChildOf(avatar.transform))
                return "Choose a Contact Anchor inside the selected avatar.";
            var old = FindHost();
            if (old != null && !IsOurHost(old))
                return "A PiCtrl_TX object exists with unrelated content. It will not be replaced.";
            return null;
        }

        private GameObject FindHost()
        {
            return avatar == null ? null : avatar.GetComponentsInChildren<Transform>(true)
                .Where(t => t != avatar.transform && t.name == HostName)
                .Select(t => t.gameObject).FirstOrDefault();
        }

        private static bool IsOurHost(GameObject host)
        {
            if (host.GetComponents<Component>().Any(c => c == null ||
                !(c is Transform) && c.GetType().FullName != "VF.Model.VRCFury")) return false;
            foreach (Transform child in host.transform)
            {
                if (child.name != "TX_DATA" && child.name != "TX_CLOCK") return false;
                if (child.childCount != 0) return false;
                if (child.GetComponents<Component>().Any(c => c == null ||
                    !(c is Transform) && !(c is VRCContactSender))) return false;
            }
            return true;
        }

        private void Generate()
        {
            var error = Validate();
            if (error != null)
            {
                EditorUtility.DisplayDialog("PiCtrl", error, "OK");
                return;
            }

            var oldHost = FindHost();
            var stableFolder = AssetRoot + "/" + SafeName(avatar.name) + "_TX";
            string buildFolder = null;
            GameObject newHost = null;
            try
            {
                EnsureFolder(AssetRoot);
                buildFolder = AssetDatabase.GenerateUniqueAssetPath(stableFolder + "_Build");
                AssetDatabase.CreateFolder(AssetRoot, Path.GetFileName(buildFolder));
                AssetDatabase.CreateFolder(buildFolder, "Clips");

                var parameters = CreateParameters(buildFolder);
                var menu = CreateMenu(buildFolder);
                var controller = CreateController(buildFolder);

                newHost = new GameObject(HostName + "_Building");
                newHost.transform.SetParent(contactAnchor, false);
                newHost.transform.localPosition = Vector3.zero;
                newHost.transform.localRotation = Quaternion.identity;
                newHost.transform.localScale = Vector3.one;
                CreateSender(newHost.transform, "TX_DATA", DataTag);
                CreateSender(newHost.transform, "TX_CLOCK", ClockTag);

                var fullController = FuryComponents.CreateFullController(newHost);
                fullController.AddController(controller, VRCAvatarDescriptor.AnimLayerType.FX);
                fullController.AddMenu(menu);
                fullController.AddParams(parameters);
                AssetDatabase.SaveAssets();

                if (oldHost != null) DestroyImmediate(oldHost);
                newHost.name = HostName;
                if (AssetDatabase.IsValidFolder(stableFolder) && IsGeneratedFolder(stableFolder))
                    AssetDatabase.DeleteAsset(stableFolder);
                if (!AssetDatabase.IsValidFolder(stableFolder))
                {
                    var moveError = AssetDatabase.MoveAsset(buildFolder, stableFolder);
                    if (string.IsNullOrEmpty(moveError)) buildFolder = stableFolder;
                    else Debug.LogWarning("PiCtrl generated assets remain in " + buildFolder + ": " + moveError);
                }

                EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                AssetDatabase.SaveAssets();
                Selection.activeGameObject = newHost;
                Debug.Log("PiCtrl sender installed on " + avatar.name + " from " + buildFolder, newHost);
                EditorUtility.DisplayDialog("PiCtrl sender ready",
                    "The sender and FX assets are installed. Save the scene. " +
                    "The receiver avatar and external decoder still need to be built and tested.", "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                if (newHost != null && newHost.name.EndsWith("_Building")) DestroyImmediate(newHost);
                if (buildFolder != null && buildFolder != stableFolder &&
                    AssetDatabase.IsValidFolder(buildFolder)) AssetDatabase.DeleteAsset(buildFolder);
                EditorUtility.DisplayDialog("PiCtrl setup failed",
                    "The sender was not fully installed. See the Console for details.", "OK");
            }
        }

        private static bool IsGeneratedFolder(string folder)
        {
            var absolute = Application.dataPath + folder.Substring("Assets".Length);
            if (!File.Exists(Path.Combine(absolute, "PiCtrl_TX_FX.controller")) ||
                !File.Exists(Path.Combine(absolute, "PiCtrl_TX_Parameters.asset"))) return false;
            return Directory.GetFiles(absolute, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .All(path => Path.GetFileName(path).StartsWith("PiCtrl_TX_", StringComparison.Ordinal));
        }

        private void CreateSender(Transform parent, string name, string tag)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent, false);
            var sender = child.AddComponent<VRCContactSender>();
            sender.shapeType = ContactBase.ShapeType.Sphere;
            sender.radius = contactRadius;
            sender.position = Vector3.zero;
            sender.localOnly = false;
            sender.collisionTags = new List<string> { tag };
            child.SetActive(false);
        }

        private static VRCExpressionParameters CreateParameters(string folder)
        {
            var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            asset.name = "PiCtrl_TX_Parameters";
            asset.parameters = new[]
            {
                Param(Intensity, VRCExpressionParameters.ValueType.Float, 0.1f, false),
                Param(Duration, VRCExpressionParameters.ValueType.Float, 0.5f / 15f, false),
                Param(Shock, VRCExpressionParameters.ValueType.Bool, 0, false),
                Param(Vibrate, VRCExpressionParameters.ValueType.Bool, 0, false),
                Param(Command, VRCExpressionParameters.ValueType.Int, 0, true)
            };
            AssetDatabase.CreateAsset(asset, folder + "/PiCtrl_TX_Parameters.asset");
            return asset;
        }

        private static VRCExpressionParameters.Parameter Param(
            string name, VRCExpressionParameters.ValueType type, float defaultValue, bool synced)
        {
            return new VRCExpressionParameters.Parameter
            {
                name = name,
                valueType = type,
                defaultValue = defaultValue,
                saved = false,
                networkSynced = synced
            };
        }

        private static VRCExpressionsMenu CreateMenu(string folder)
        {
            var root = NewMenu(folder, "PiCtrl_TX_Menu");
            var main = NewMenu(folder, "PiCtrl_TX_Main");
            var modes = NewMenu(folder, "PiCtrl_TX_Modes");
            root.controls.Add(new VRCExpressionsMenu.Control
            {
                name = "PiCtrl", type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = main
            });
            main.controls.Add(Radial("Intensity", Intensity));
            main.controls.Add(Radial("Duration", Duration));
            main.controls.Add(Button("Shock", Shock));
            main.controls.Add(Button("Vibrate", Vibrate));
            main.controls.Add(new VRCExpressionsMenu.Control
            {
                name = "Modes", type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = modes
            });
            modes.controls.Add(Button("Shock", Shock));
            modes.controls.Add(Button("Vibrate", Vibrate));
            EditorUtility.SetDirty(root);
            EditorUtility.SetDirty(main);
            EditorUtility.SetDirty(modes);
            return root;
        }

        private static VRCExpressionsMenu NewMenu(string folder, string name)
        {
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.name = name;
            menu.controls = new List<VRCExpressionsMenu.Control>();
            AssetDatabase.CreateAsset(menu, folder + "/" + name + ".asset");
            return menu;
        }

        private static VRCExpressionsMenu.Control Radial(string name, string parameter)
        {
            return new VRCExpressionsMenu.Control
            {
                name = name,
                type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = parameter } }
            };
        }

        private static VRCExpressionsMenu.Control Button(string name, string parameter)
        {
            return new VRCExpressionsMenu.Control
            {
                name = name,
                type = VRCExpressionsMenu.Control.ControlType.Button,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter },
                value = 1
            };
        }

        private static AnimatorController CreateController(string folder)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(
                folder + "/PiCtrl_TX_FX.controller");
            controller.AddParameter(new AnimatorControllerParameter
                { name = Intensity, type = AnimatorControllerParameterType.Float, defaultFloat = 0.1f });
            controller.AddParameter(new AnimatorControllerParameter
                { name = Duration, type = AnimatorControllerParameterType.Float, defaultFloat = 0.5f / 15f });
            controller.AddParameter(Shock, AnimatorControllerParameterType.Bool);
            controller.AddParameter(Vibrate, AnimatorControllerParameterType.Bool);
            controller.AddParameter(Command, AnimatorControllerParameterType.Int);
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);

            var layers = controller.layers;
            layers[0].name = "PiCtrl Capture";
            layers[0].defaultWeight = 1f;
            controller.layers = layers;
            controller.AddLayer("PiCtrl Transmit");
            layers = controller.layers;
            layers[1].defaultWeight = 1f;
            controller.layers = layers;

            var capture = controller.layers[0].stateMachine;
            var transmit = controller.layers[1].stateMachine;
            var ready = capture.AddState("Ready", new Vector3(0, 0));
            var waitRelease = capture.AddState("Wait for release and completion", new Vector3(650, 0));
            ready.writeDefaultValues = waitRelease.writeDefaultValues = true;
            capture.defaultState = ready;
            var release = Transition(waitRelease, ready);
            release.AddCondition(AnimatorConditionMode.IfNot, 0, Shock);
            release.AddCondition(AnimatorConditionMode.IfNot, 0, Vibrate);
            release.AddCondition(AnimatorConditionMode.Equals, 0, Command);

            var offClip = CreateOffClip(folder + "/Clips");
            var idle = transmit.AddState("Idle - DATA 0 CLOCK 0", new Vector3(0, 0));
            var finish = transmit.AddState("Finish - clear command", new Vector3(650, 0));
            var waitZero = transmit.AddState("Wait for synced zero", new Vector3(850, 0));
            idle.motion = finish.motion = waitZero.motion = offClip;
            idle.writeDefaultValues = finish.writeDefaultValues = waitZero.writeDefaultValues = true;
            transmit.defaultState = idle;
            AddDriver(finish, 0);
            ExitTransition(finish, waitZero);
            Transition(waitZero, idle).AddCondition(AnimatorConditionMode.Equals, 0, Command);

            for (var mode = 1; mode <= 2; mode++)
            for (var durationIndex = 1; durationIndex <= 9; durationIndex++)
            for (var intensityIndex = 1; intensityIndex <= 10; intensityIndex++)
            {
                var code = (mode - 1) * 90 + (durationIndex - 1) * 10 + intensityIndex;
                var row = code - 1;
                var actionName = mode == 1 ? "Vibrate" : "Shock";
                var name = actionName + "_I" + intensityIndex * 10 + "_D" + durationIndex;

                var latch = capture.AddState("Latch " + name, new Vector3(220 + (row % 10) * 180, 120 + (row / 10) * 80));
                latch.writeDefaultValues = true;
                AddDriver(latch, code);
                var captureTransition = Transition(ready, latch);
                captureTransition.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
                captureTransition.AddCondition(AnimatorConditionMode.Equals, 0, Command);
                captureTransition.AddCondition(AnimatorConditionMode.If, 0, mode == 1 ? Vibrate : Shock);
                AddFloatRange(captureTransition, Intensity, IntensityLower(intensityIndex),
                    IntensityUpper(intensityIndex));
                AddFloatRange(captureTransition, Duration, DurationLower(durationIndex),
                    DurationUpper(durationIndex));
                Transition(latch, waitRelease).AddCondition(AnimatorConditionMode.Equals, code, Command);

                var clip = CreatePacketClip(folder + "/Clips", code);
                var send = transmit.AddState("Send twice " + name,
                    new Vector3(220 + (row % 10) * 180, 120 + (row / 10) * 80));
                send.motion = clip;
                send.writeDefaultValues = true;
                Transition(idle, send).AddCondition(AnimatorConditionMode.Equals, code, Command);
                ExitTransition(send, finish);
            }

            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static float IntensityLower(int index) => index == 1 ? -1f : (index - 0.5f) / 10f;
        private static float IntensityUpper(int index) => index == 10 ? 2f : (index + 0.5f) / 10f;
        private static float DurationLower(int index) => index == 1 ? -1f :
            (DurationSeconds[index - 2] + DurationSeconds[index - 1]) / 30f;
        private static float DurationUpper(int index) => index == 9 ? 2f :
            (DurationSeconds[index - 1] + DurationSeconds[index]) / 30f;

        private static void AddFloatRange(AnimatorStateTransition transition, string parameter,
            float lower, float upper)
        {
            if (lower >= 0f) transition.AddCondition(AnimatorConditionMode.Greater, lower, parameter);
            if (upper <= 1f) transition.AddCondition(AnimatorConditionMode.Less, upper + 0.0001f, parameter);
        }

        private static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0;
            transition.interruptionSource = TransitionInterruptionSource.None;
            return transition;
        }

        private static void ExitTransition(AnimatorState from, AnimatorState to)
        {
            var transition = Transition(from, to);
            transition.hasExitTime = true;
            transition.exitTime = 1f;
        }

        private static void AddDriver(AnimatorState state, int command)
        {
            var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.localOnly = true;
            driver.parameters = new List<VRCAvatarParameterDriver.Parameter>
            {
                new VRCAvatarParameterDriver.Parameter
                {
                    name = Command,
                    type = VRCAvatarParameterDriver.ChangeType.Set,
                    value = command,
                    chance = 1f
                }
            };
        }

        private static AnimationClip CreateOffClip(string folder)
        {
            var clip = new AnimationClip { name = "PiCtrl_TX_Off", frameRate = FramesPerSecond };
            var off = StepCurve(new List<Keyframe> { Key(0, 0), Key(6, 0) });
            clip.SetCurve("TX_DATA", typeof(GameObject), "m_IsActive", off);
            clip.SetCurve("TX_CLOCK", typeof(GameObject), "m_IsActive", off);
            AssetDatabase.CreateAsset(clip, folder + "/PiCtrl_TX_Off.anim");
            return clip;
        }

        private static AnimationClip CreatePacketClip(string folder, int command)
        {
            var clip = new AnimationClip
                { name = "PiCtrl_TX_Packet_" + command.ToString("D3"), frameRate = FramesPerSecond };
            var data = new List<Keyframe> { Key(0, 0) };
            var clock = new List<Keyframe> { Key(0, 0) };
            var bits = PacketBits(command);
            var frame = StartGapFrames;
            for (var copy = 0; copy < 2; copy++)
            {
                foreach (var bit in bits)
                {
                    data.Add(Key(frame, bit));
                    frame += DataSetupFrames;
                    clock.Add(Key(frame, 1));
                    frame += ClockHighFrames;
                    clock.Add(Key(frame, 0));
                    frame += ClockLowFrames;
                }
                data.Add(Key(frame, 0));
                if (copy == 0) frame += DuplicateGapFrames;
            }
            frame += TailFrames;
            data.Add(Key(frame, 0));
            clock.Add(Key(frame, 0));
            clip.SetCurve("TX_DATA", typeof(GameObject), "m_IsActive", StepCurve(data));
            clip.SetCurve("TX_CLOCK", typeof(GameObject), "m_IsActive", StepCurve(clock));
            AssetDatabase.CreateAsset(clip, folder + "/" + clip.name + ".anim");
            return clip;
        }

        private static int[] PacketBits(int command)
        {
            var checksum = ((command >> 4) ^ (command & 15) ^ 10) & 15;
            var bits = new int[16];
            bits[0] = 1; bits[1] = 1; bits[2] = 0; bits[3] = 1;
            for (var i = 0; i < 8; i++) bits[4 + i] = (command >> (7 - i)) & 1;
            for (var i = 0; i < 4; i++) bits[12 + i] = (checksum >> (3 - i)) & 1;
            return bits;
        }

        private static Keyframe Key(int frame, int value) =>
            new Keyframe(frame / (float)FramesPerSecond, value);

        private static AnimationCurve StepCurve(List<Keyframe> keys)
        {
            var curve = new AnimationCurve(keys.ToArray());
            for (var i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
            }
            return curve;
        }

        private static void EnsureFolder(string folder)
        {
            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string SafeName(string name)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "Avatar" : name.Trim();
        }
    }
}
#endif
