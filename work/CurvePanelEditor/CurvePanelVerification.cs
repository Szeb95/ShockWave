#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;

namespace ShockWave.CurvePanel
{
    // Explicit batch-only verification. No InitializeOnLoad or actual avatar
    // selection, scene saving, upload, OSC socket, account or hardware access.
    public static class CurvePanelVerification
    {
        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Run this verifier in a separate Unity batch session.");
            var scene = EditorSceneManager.NewPreviewScene();
            string folder = null;
            GameObject root = null;
            Exception failure = null;
            var passed = 0;
            try
            {
                root = new GameObject("CurvePanel_Verification_" + Guid.NewGuid().ToString("N"));
                SceneManager.MoveGameObjectToScene(root, scene);
                var avatar = root.AddComponent<VRCAvatarDescriptor>(); avatar.ViewPosition = new Vector3(0, 1.6f, 0);
                var diagnostic = new GameObject("SDK fields").AddComponent<VRCContactReceiver>();
                var serialized = new SerializedObject(diagnostic);
                var iterator = serialized.GetIterator();
                while (iterator.NextVisible(true))
                    if (iterator.propertyPath.IndexOf("size", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        iterator.propertyPath.IndexOf("face", StringComparison.OrdinalIgnoreCase) >= 0 || iterator.name == "shapeType")
                        Debug.Log("CURVE SDK FIELD " + iterator.propertyPath + " " + iterator.propertyType +
                            (iterator.propertyType == SerializedPropertyType.Enum ? " " + string.Join(",", iterator.enumNames) : ""));
                UnityEngine.Object.DestroyImmediate(diagnostic.gameObject);
                var host = CurvePanelSetupWindow.Build(avatar, 0.6f, 0.18f, out folder);
                void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); passed++; Debug.Log("CURVE PASS " + message); }
                Check(host.transform.parent == root.transform, "Root installed under selected avatar");
                Check(host.GetComponents<Component>().Any(c => c != null && c.GetType().FullName == "VF.Model.VRCFury"), "VRCFury Full Controller attached");
                Check(CurvePanelSetupWindow.ValidateAvatar(avatar) != null, "Duplicate installation refused without replacing existing content");
                var parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder + "/Parameters.asset");
                Check(parameters.parameters.Length == 9 && parameters.parameters.All(p => !p.saved && !p.networkSynced && p.defaultValue == 0), "All nine parameters unsaved/local with idle defaults");
                var menu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Controls.asset");
                Check(menu.controls.Count <= 8 && menu.controls.Any(control => control.name == "Show panel" &&
                    control.type == VRCExpressionsMenu.Control.ControlType.Toggle), "Panel toggle and menu capacity");
                Check(AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(folder + "/Destination.asset").controls.Count == 3, "Own/Remote/Both selectors");
                var receivers = host.GetComponentsInChildren<VRCContactReceiver>(true);
                Check(receivers.Length == 2 && receivers.All(r => r.localOnly && r.allowSelf && !r.allowOthers && r.collisionTags.SequenceEqual(new[] { "Finger" })), "Self-only local finger contacts");
                var panel = host.transform.Find("Panel");
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(folder + "/FX.controller");
                Check(controller.layers.Length == 5 && controller.layers.All(layer => layer.defaultWeight == 1), "Five active controller layers");
                foreach (var clip in controller.animationClips.Distinct()) foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var target = host.transform.Find(binding.path);
                    Check(target != null && (binding.type == typeof(GameObject) || target.GetComponent(binding.type) != null), "Animation binding " + binding.path + "/" + binding.propertyName);
                }
                var animator = host.AddComponent<Animator>(); animator.runtimeAnimatorController = controller;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind(); animator.SetBool("IsLocal", true); animator.Update(0.02f);
                Check(!panel.gameObject.activeSelf, "Panel initially hidden");
                animator.SetBool(CurvePanelSetupWindow.Prefix + "Visible", true);
                for (var i = 0; i < 25; i++) animator.Update(0.02f);
                Check(panel.gameObject.activeSelf, "Show toggle reveals panel");
                Check(panel.GetComponent<VRCParentConstraint>().FreezeToWorld, "Placement freezes after settling");
                animator.SetFloat(CurvePanelSetupWindow.Prefix + "Preview", 0.4f);
                for (var i = 0; i < 5; i++) animator.Update(0.02f);
                var point = panel.Find("CurrentPoint");
                Check(Mathf.Abs(point.localPosition.y - (CurvePanelSetupWindow.GraphBottom + 0.4f * CurvePanelSetupWindow.GraphHeight)) < 0.001f, "Preview radial positions drawing point");
                animator.SetBool(CurvePanelSetupWindow.Prefix + "Touch", true);
                animator.SetFloat(CurvePanelSetupWindow.Prefix + "Level", 0.8f);
                for (var i = 0; i < 5; i++) animator.Update(0.02f);
                Check(Mathf.Abs(point.localPosition.y - (CurvePanelSetupWindow.GraphBottom + 0.8f * CurvePanelSetupWindow.GraphHeight)) < 0.001f, "Touch level overrides menu preview");
                Check(panel.Find("ContactLamp").gameObject.activeSelf, "Contact feedback lamp");
                animator.SetInteger(CurvePanelSetupWindow.Prefix + "Route", 2);
                for (var i = 0; i < 5; i++) animator.Update(0.02f);
                Check(panel.Find("Route2").gameObject.activeSelf && !panel.Find("Route0").gameObject.activeSelf, "Both selection indicator");
                animator.SetBool(CurvePanelSetupWindow.Prefix + "Clear", true); animator.Update(0.02f); animator.Update(0.02f);
                Check(!panel.Find("History").gameObject.activeSelf, "Clear resets particle history");
                animator.SetBool(CurvePanelSetupWindow.Prefix + "Clear", false); animator.Update(0.02f); animator.Update(0.02f);
                animator.SetBool(CurvePanelSetupWindow.Prefix + "Stop", true); animator.Update(0.02f); animator.Update(0.02f);
                Check(!panel.Find("History").gameObject.activeSelf, "Stop clears history while pressed");
                animator.SetBool(CurvePanelSetupWindow.Prefix + "Stop", false); animator.Update(0.02f); animator.Update(0.02f);
                animator.SetBool(CurvePanelSetupWindow.Prefix + "Follow", true);
                for (var i = 0; i < 5; i++) animator.Update(0.02f);
                Check(!panel.GetComponent<VRCParentConstraint>().FreezeToWorld, "Follow unfreezes panel");
                animator.SetBool("IsLocal", false); animator.Update(0.02f); animator.Update(0.02f);
                Check(!panel.gameObject.activeSelf, "Remote viewer panel hidden");
                UnityEngine.Object.DestroyImmediate(animator);
                panel.gameObject.SetActive(true);
                panel.GetComponent<VRCParentConstraint>().IsActive = false;
                panel.localRotation = Quaternion.identity;
                var particles = panel.Find("History").GetComponent<ParticleSystem>();
                particles.gameObject.SetActive(true);
                particles.Simulate(0, false, true);
                for (var i = 0; i < 240; i++)
                {
                    var y = CurvePanelSetupWindow.GraphBottom + CurvePanelSetupWindow.GraphHeight * (0.45f + 0.35f * Mathf.Sin(i / 17f));
                    particles.transform.localPosition = new Vector3(CurvePanelSetupWindow.GraphWidth / 2, y, -0.009f);
                    particles.Simulate(1f / 30f, false, false);
                }
                var points = new ParticleSystem.Particle[particles.main.maxParticles];
                var count = particles.GetParticles(points);
                Check(count >= 220 && count <= 260, "Bounded eight-second particle history");
                Check(points.Take(count).Max(p => p.position.y) - points.Take(count).Min(p => p.position.y) > 0.1f, "Earlier heights retained while emitter moves");
                Check(points.Take(count).Min(p => p.position.x) < -0.20f && points.Take(count).Max(p => p.position.x) <= 0.28f, "History scrolls left within graph");
                point.localPosition = particles.transform.localPosition;
                Render(panel);
                Debug.Log("CURVE VERIFICATION COMPLETE " + passed + " checks");
            }
            catch (Exception ex) { failure = ex; Debug.LogException(ex); }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (folder != null && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                EditorSceneManager.ClosePreviewScene(scene);
            }
            if (failure != null) EditorApplication.Exit(1);
        }

        private static void Render(Transform panel)
        {
            var path = Environment.GetEnvironmentVariable("SHOCKWAVE_CURVE_PREVIEW");
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("SHOCKWAVE_CURVE_PREVIEW must be an absolute output path.");
            var obj = new GameObject("Verification camera"); var camera = obj.AddComponent<Camera>();
            SceneManager.MoveGameObjectToScene(obj, panel.gameObject.scene);
            camera.cameraType = CameraType.Preview;
            camera.scene = panel.gameObject.scene;
            camera.transform.position = panel.position - Vector3.forward * 1.2f; camera.transform.rotation = Quaternion.identity;
            camera.orthographic = true; camera.orthographicSize = 0.27f;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.04f, 0.06f, 0.08f);
            var rt = RenderTexture.GetTemporary(1400, 980, 24);
            camera.targetTexture = rt;
            var previous = RenderTexture.active;
            var image = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            try
            {
                camera.Render(); RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); image.Apply();
                if (image.GetPixels32().Count(pixel => pixel.r > 45 || pixel.g > 65 || pixel.b > 80) < 20000)
                    throw new InvalidOperationException("Preview must visibly render the panel, labels and trace, not a blank camera.");
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally { camera.targetTexture = null; RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); UnityEngine.Object.DestroyImmediate(image); UnityEngine.Object.DestroyImmediate(obj); }
        }
    }
}
#endif
