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
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace ShockWave.PiCtrl
{
    // VRChat sends local avatar expression parameters to OSC. This component
    // prepares those parameters; it does not open a socket in the avatar.
    public sealed class PiCtrlSenderSetupWindow : EditorWindow
    {
        private const string HostName = "PiCtrl_OSC";
        private const string LegacyHostName = "PiCtrl_TX";
        private const string LegacyContactsName = "PiCtrl_TX_Contacts";
        private const string AssetRoot = "Assets/SzebsToys/PiCtrl";

        private const string IntensityRadial = "PiCtrl_IntensityRadial";
        private const string DurationRadial = "PiCtrl_DurationRadial";
        private const string ShockButton = "PiCtrl_ShockButton";
        private const string VibrateButton = "PiCtrl_VibrateButton";
        private const string Intensity = "PiCtrl_Intensity";
        private const string Duration = "PiCtrl_Duration";
        private const string Action = "PiCtrl_Action";

        [SerializeField] private VRCAvatarDescriptor avatar;

        [MenuItem("Tools/ShockWave/PiCtrl/Create or Replace OSC Sender")]
        private static void Open()
        {
            var window = GetWindow<PiCtrlSenderSetupWindow>("PiCtrl OSC Sender");
            window.minSize = new Vector2(540, 340);
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
            if (selected != null) avatar = selected;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("PiCtrl local OSC sender", EditorStyles.boldLabel);
            avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                "Avatar", avatar, typeof(VRCAvatarDescriptor), true);
            EditorGUILayout.HelpBox(
                "Creates PiCtrl_OSC directly under the selected avatar. It has a VRCFury Full Controller, " +
                "two 0-100 radials, and separate Shock/Vibrate buttons. Pressing a button captures both " +
                "radials as exact Int values and then changes PiCtrl_Action (1=Shock, 2=Vibrate). " +
                "All parameters are local/unsynced. There are no Contact Senders or DATA/CLOCK animations. " +
                "Run in Edit mode and save the scene afterward.", MessageType.Info);
            EditorGUILayout.LabelField("OSC output: /avatar/parameters/PiCtrl_Intensity  (Int 0-100)");
            EditorGUILayout.LabelField("OSC output: /avatar/parameters/PiCtrl_Duration   (Int 0-100)");
            EditorGUILayout.LabelField("OSC output: /avatar/parameters/PiCtrl_Action     (Int 0,1,2)");
            EditorGUILayout.LabelField("Only action transitions from 0 to 1 or 2 count as button presses.");
            EditorGUILayout.LabelField("Duration is a 0-100 setting; the external program decides its time units.");

            var error = Validate();
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);
            using (new EditorGUI.DisabledScope(error != null))
            {
                if (GUILayout.Button("Create / Replace PiCtrl OSC Sender", GUILayout.Height(36)))
                    Generate();
            }
        }

        private string Validate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return "Exit Play mode before replacing the sender.";
            if (avatar == null) return "Select an avatar in a loaded scene.";
            if (EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid() ||
                !avatar.gameObject.scene.isLoaded)
                return "Choose an avatar in a loaded scene, not a prefab asset.";

            var current = FindNamed(HostName);
            if (current != null && !IsOurOscHost(current))
                return "PiCtrl_OSC already exists with other content; it will not be replaced.";
            var legacy = FindNamed(LegacyHostName);
            if (legacy != null && !IsOurLegacyHost(legacy))
                return "PiCtrl_TX has other content; remove it manually before replacing it.";
            var contacts = FindNamed(LegacyContactsName);
            if (contacts != null && !IsOurLegacyContacts(contacts))
                return "PiCtrl_TX_Contacts has other content; remove it manually before replacing it.";
            return null;
        }

        private GameObject FindNamed(string name)
        {
            return avatar.GetComponentsInChildren<Transform>(true)
                .Where(t => t != avatar.transform && t.name == name)
                .Select(t => t.gameObject).FirstOrDefault();
        }

        private static bool HasOnlyFury(GameObject obj)
        {
            var components = obj.GetComponents<Component>();
            return components.Any(c => c != null && c.GetType().FullName == "VF.Model.VRCFury") &&
                   components.All(c => c != null && (c is Transform || c.GetType().FullName == "VF.Model.VRCFury"));
        }

        private static bool IsOurOscHost(GameObject obj) =>
            HasOnlyFury(obj) && obj.transform.childCount == 0;

        private static bool IsOurLegacyHost(GameObject obj)
        {
            if (!HasOnlyFury(obj)) return false;
            if (obj.transform.childCount != 0 && obj.transform.childCount != 2) return false;
            var names = new HashSet<string>();
            foreach (Transform child in obj.transform)
            {
                names.Add(child.name);
                if (child.name != "TX_DATA" && child.name != "TX_CLOCK") return false;
                if (child.childCount != 0 || !OnlyLegacyContactComponents(child.gameObject)) return false;
            }
            return obj.transform.childCount == 0 || names.SetEquals(new[] { "TX_DATA", "TX_CLOCK" });
        }

        private static bool IsOurLegacyContacts(GameObject obj)
        {
            if (obj.GetComponents<Component>().Any(c => c == null || !(c is Transform))) return false;
            if (obj.transform.childCount != 2) return false;
            var names = new HashSet<string>();
            foreach (Transform child in obj.transform)
            {
                names.Add(child.name);
                if (child.childCount != 0 || !OnlyLegacyContactComponents(child.gameObject)) return false;
            }
            return names.SetEquals(new[] { "TX_DATA", "TX_CLOCK" });
        }

        private static bool OnlyLegacyContactComponents(GameObject obj) =>
            obj.GetComponents<Component>().All(c => c != null &&
                (c is Transform || c.GetType().FullName ==
                 "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender"));

        private void Generate()
        {
            var error = Validate();
            if (error != null)
            {
                EditorUtility.DisplayDialog("PiCtrl", error, "OK");
                return;
            }

            var oldOsc = FindNamed(HostName);
            var oldTx = FindNamed(LegacyHostName);
            var oldContacts = FindNamed(LegacyContactsName);
            var stableFolder = AssetRoot + "/" + SafeName(avatar.name) + "_OSC";
            var legacyFolder = AssetRoot + "/" + SafeName(avatar.name) + "_TX";
            string buildFolder = null;
            GameObject newHost = null;

            try
            {
                EnsureFolder(AssetRoot);
                buildFolder = AssetDatabase.GenerateUniqueAssetPath(stableFolder + "_Build");
                AssetDatabase.CreateFolder(AssetRoot, Path.GetFileName(buildFolder));

                var parameters = CreateParameters(buildFolder);
                var menu = CreateMenu(buildFolder);
                var controller = CreateController(buildFolder);

                newHost = new GameObject(HostName + "_Building");
                newHost.transform.SetParent(avatar.transform, false);
                newHost.transform.localPosition = Vector3.zero;
                newHost.transform.localRotation = Quaternion.identity;
                newHost.transform.localScale = Vector3.one;
                var fullController = FuryComponents.CreateFullController(newHost);
                fullController.AddController(controller, VRCAvatarDescriptor.AnimLayerType.FX);
                fullController.AddMenu(menu);
                fullController.AddParams(parameters);
                fullController.AddGlobalParam(Intensity);
                fullController.AddGlobalParam(Duration);
                fullController.AddGlobalParam(Action);
                AssetDatabase.SaveAssets();

                if (oldOsc != null) DestroyImmediate(oldOsc);
                if (oldTx != null) DestroyImmediate(oldTx);
                if (oldContacts != null) DestroyImmediate(oldContacts);
                newHost.name = HostName;

                if (AssetDatabase.IsValidFolder(stableFolder) && IsOurAssetFolder(stableFolder, "PiCtrl_OSC_"))
                    AssetDatabase.DeleteAsset(stableFolder);
                if (!AssetDatabase.IsValidFolder(stableFolder))
                {
                    var moveError = AssetDatabase.MoveAsset(buildFolder, stableFolder);
                    if (string.IsNullOrEmpty(moveError)) buildFolder = stableFolder;
                    else Debug.LogWarning("PiCtrl OSC assets remain in " + buildFolder + ": " + moveError);
                }
                if (AssetDatabase.IsValidFolder(legacyFolder) && IsOurAssetFolder(legacyFolder, "PiCtrl_TX_"))
                    AssetDatabase.DeleteAsset(legacyFolder);

                EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                AssetDatabase.SaveAssets();
                Selection.activeGameObject = newHost;
                Debug.Log("PiCtrl OSC sender installed on " + avatar.name + " from " + buildFolder, newHost);
                EditorUtility.DisplayDialog("PiCtrl OSC sender ready",
                    "PiCtrl_OSC is directly under " + avatar.name + ". Save the scene. " +
                    "In the VRChat client, enable OSC and listen for PiCtrl_Intensity, " +
                    "PiCtrl_Duration and PiCtrl_Action. Unity Play mode does not emit VRChat OSC.", "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                if (newHost != null && newHost.name.EndsWith("_Building")) DestroyImmediate(newHost);
                if (buildFolder != null && buildFolder != stableFolder && AssetDatabase.IsValidFolder(buildFolder))
                    AssetDatabase.DeleteAsset(buildFolder);
                EditorUtility.DisplayDialog("PiCtrl setup failed", "See the Console for details.", "OK");
            }
        }

        private static bool IsOurAssetFolder(string folder, string prefix)
        {
            var absolute = Application.dataPath + folder.Substring("Assets".Length);
            if (!File.Exists(Path.Combine(absolute, prefix + "FX.controller")) ||
                !File.Exists(Path.Combine(absolute, prefix + "Parameters.asset"))) return false;
            var directories = Directory.GetDirectories(absolute, "*", SearchOption.TopDirectoryOnly);
            if (prefix == "PiCtrl_OSC_" && directories.Length != 0) return false;
            if (prefix == "PiCtrl_TX_")
            {
                if (directories.Any(path => Path.GetFileName(path) != "Clips")) return false;
                foreach (var directory in directories)
                    if (Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                        .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        .Any(path => !Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)))
                        return false;
            }
            return Directory.GetFiles(absolute, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .All(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal));
        }

        private static VRCExpressionParameters CreateParameters(string folder)
        {
            var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            asset.name = "PiCtrl_OSC_Parameters";
            asset.parameters = new[]
            {
                Param(IntensityRadial, VRCExpressionParameters.ValueType.Float, 0.1f),
                Param(DurationRadial, VRCExpressionParameters.ValueType.Float, 0.1f),
                Param(ShockButton, VRCExpressionParameters.ValueType.Bool, 0),
                Param(VibrateButton, VRCExpressionParameters.ValueType.Bool, 0),
                Param(Intensity, VRCExpressionParameters.ValueType.Int, 0),
                Param(Duration, VRCExpressionParameters.ValueType.Int, 0),
                Param(Action, VRCExpressionParameters.ValueType.Int, 0)
            };
            AssetDatabase.CreateAsset(asset, folder + "/PiCtrl_OSC_Parameters.asset");
            return asset;
        }

        private static VRCExpressionParameters.Parameter Param(
            string name, VRCExpressionParameters.ValueType type, float defaultValue)
        {
            return new VRCExpressionParameters.Parameter
            {
                name = name, valueType = type, defaultValue = defaultValue,
                saved = false, networkSynced = false
            };
        }

        private static VRCExpressionsMenu CreateMenu(string folder)
        {
            var root = NewMenu(folder, "PiCtrl_OSC_Menu");
            var main = NewMenu(folder, "PiCtrl_OSC_Main");
            var modes = NewMenu(folder, "PiCtrl_OSC_Modes");
            root.controls.Add(new VRCExpressionsMenu.Control
            {
                name = "PiCtrl", type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = main
            });
            main.controls.Add(Radial("Intensity 0-100", IntensityRadial));
            main.controls.Add(Radial("Duration 0-100", DurationRadial));
            main.controls.Add(Button("Shock", ShockButton));
            main.controls.Add(Button("Vibrate", VibrateButton));
            main.controls.Add(new VRCExpressionsMenu.Control
            {
                name = "Modes", type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = modes
            });
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

        private static VRCExpressionsMenu.Control Radial(string label, string parameter) =>
            new VRCExpressionsMenu.Control
            {
                name = label,
                type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = parameter } }
            };

        private static VRCExpressionsMenu.Control Button(string label, string parameter) =>
            new VRCExpressionsMenu.Control
            {
                name = label,
                type = VRCExpressionsMenu.Control.ControlType.Button,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter },
                value = 1
            };

        private static AnimatorController CreateController(string folder)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(
                folder + "/PiCtrl_OSC_FX.controller");
            controller.AddParameter(new AnimatorControllerParameter
                { name = IntensityRadial, type = AnimatorControllerParameterType.Float, defaultFloat = 0.1f });
            controller.AddParameter(new AnimatorControllerParameter
                { name = DurationRadial, type = AnimatorControllerParameterType.Float, defaultFloat = 0.1f });
            controller.AddParameter(ShockButton, AnimatorControllerParameterType.Bool);
            controller.AddParameter(VibrateButton, AnimatorControllerParameterType.Bool);
            controller.AddParameter(Intensity, AnimatorControllerParameterType.Int);
            controller.AddParameter(Duration, AnimatorControllerParameterType.Int);
            controller.AddParameter(Action, AnimatorControllerParameterType.Int);
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);

            var layers = controller.layers;
            layers[0].name = "PiCtrl OSC Capture";
            layers[0].defaultWeight = 1f;
            controller.layers = layers;
            var stateMachine = controller.layers[0].stateMachine;
            var ready = stateMachine.AddState("Ready - action 0", new Vector3(0, 0));
            var shock = stateMachine.AddState("Capture Shock", new Vector3(320, -120));
            var vibrate = stateMachine.AddState("Capture Vibrate", new Vector3(320, 120));
            ready.writeDefaultValues = shock.writeDefaultValues = vibrate.writeDefaultValues = true;
            stateMachine.defaultState = ready;
            AddResetDriver(ready);
            AddCaptureDriver(shock, 1);
            AddCaptureDriver(vibrate, 2);
            var shockPress = Transition(ready, shock);
            shockPress.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
            shockPress.AddCondition(AnimatorConditionMode.If, 0, ShockButton);
            var vibratePress = Transition(ready, vibrate);
            vibratePress.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
            vibratePress.AddCondition(AnimatorConditionMode.If, 0, VibrateButton);
            Transition(shock, ready).AddCondition(AnimatorConditionMode.IfNot, 0, ShockButton);
            Transition(vibrate, ready).AddCondition(AnimatorConditionMode.IfNot, 0, VibrateButton);
            EditorUtility.SetDirty(controller);
            return controller;
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

        private static void AddResetDriver(AnimatorState state)
        {
            var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.localOnly = true;
            driver.parameters = new List<VRCAvatarParameterDriver.Parameter>
            {
                new VRCAvatarParameterDriver.Parameter
                {
                    name = Action, type = VRCAvatarParameterDriver.ChangeType.Set,
                    value = 0, chance = 1f
                }
            };
        }

        private static void AddCaptureDriver(AnimatorState state, int action)
        {
            var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.localOnly = true;
            // Parameter Driver operations execute in order on state entry.
            driver.parameters = new List<VRCAvatarParameterDriver.Parameter>
            {
                CopyRadial(IntensityRadial, Intensity),
                CopyRadial(DurationRadial, Duration),
                new VRCAvatarParameterDriver.Parameter
                {
                    name = Action, type = VRCAvatarParameterDriver.ChangeType.Set,
                    value = action, chance = 1f
                }
            };
        }

        private static VRCAvatarParameterDriver.Parameter CopyRadial(string source, string destination) =>
            new VRCAvatarParameterDriver.Parameter
            {
                name = destination, source = source,
                type = VRCAvatarParameterDriver.ChangeType.Copy,
                convertRange = true,
                sourceMin = 0, sourceMax = 1,
                destMin = 0.5f, destMax = 100.5f
            };

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
