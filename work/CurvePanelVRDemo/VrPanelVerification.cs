#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using ShockWave.VrDemo.Shared;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;

namespace ShockWave.VrDemo
{
    public static class VrPanelVerification
    {
        [Serializable] private class Entry {public string name; public float value;}
        [Serializable] private class Frame {public int version; public Entry[] values;}
        public static void Run()
        {
            if(!Application.isBatchMode || Application.dataPath.Replace('\\','/').IndexOf("/CurvePanelVRDemo/checks/unity-project/",StringComparison.OrdinalIgnoreCase)<0)
                throw new InvalidOperationException("Use only the separate CurvePanelVRDemo scratch project in batch mode.");
            var scene=EditorSceneManager.NewPreviewScene(); GameObject root=null; string folder=null; var passed=0;
            var output=Environment.GetEnvironmentVariable("SHOCKWAVE_VR_CHECKS");
            try
            {
                if(string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("SHOCKWAVE_VR_CHECKS needs an absolute artifact folder.");
                Directory.CreateDirectory(output);
                void Check(bool ok,string label) {if(!ok) throw new InvalidOperationException(label); passed++; Debug.Log("VR DEMO PASS "+label);}
                var requestedColor=Environment.GetEnvironmentVariable("SHOCKWAVE_VR_COLORSPACE");
                if(!string.IsNullOrWhiteSpace(requestedColor)) Check(QualitySettings.activeColorSpace.ToString()==requestedColor,"Expected active project color space: "+requestedColor);
                var frame=JsonUtility.FromJson<Frame>(File.ReadAllText(Environment.GetEnvironmentVariable("SHOCKWAVE_VR_FRAME")));
                Check(frame.version==1 && frame.values.Length==518,"Host feedback fixture matches the shared contract");
                root=new GameObject("VR_Demo_Verification"); SceneManager.MoveGameObjectToScene(root,scene);
                var avatar=root.AddComponent<VRCAvatarDescriptor>(); avatar.ViewPosition=new Vector3(0,1.6f,0);
                var originalChild=new GameObject("Keep existing avatar object"); originalChild.transform.SetParent(root.transform,false);
                var host=VrPanelSetupWindow.Build(avatar,0.65f,0.16f,out folder);
                Check(originalChild!=null && avatar.expressionParameters==null && avatar.expressionsMenu==null,"Installer preserves existing avatar children and descriptor assets");
                Check(host.GetComponents<Component>().Any(c=>c!=null && c.GetType().FullName=="VF.Model.VRCFury"),"VRCFury Full Controller attached");
                Check(VrPanelSetupWindow.ValidateAvatar(avatar)!=null,"Duplicate install refused");
                var parameters=AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(folder+"/Parameters.asset");
                Check(parameters.parameters.Length==549 && parameters.parameters.All(p=>!p.saved && !p.networkSynced && p.defaultValue==0),"All 549 expression parameters local, unsaved and idle by default");
                Check(parameters.CalcTotalCost()==0,"No synced expression-parameter memory consumed");
                foreach(var path in AssetDatabase.FindAssets("t:VRCExpressionsMenu",new[] {folder}).Select(AssetDatabase.GUIDToAssetPath))
                    Check(AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(path).controls.Count<=8,"Menu page within eight controls: "+Path.GetFileName(path));
                var contacts=host.GetComponentsInChildren<VRCContactReceiver>(true);
                Check(contacts.Length==21 && contacts.All(c=>c.localOnly && c.allowSelf && !c.allowOthers && c.collisionTags.SequenceEqual(new[] {"Finger"})),"Twenty-one self-only local finger receivers");
                Check(contacts.Count(c=>c.receiverType==VRC.Dynamics.ContactReceiver.ReceiverType.Proximity)==2,"Independent horizontal and vertical proximity receivers");
                Check(host.GetComponentInChildren<ParticleSystem>(true)==null,"Shader graph has no particles");
                var panel=host.transform.Find("Panel"); var renderer=panel.Find("Board").GetComponent<MeshRenderer>();
                Check(renderer.sharedMaterial.GetTexture("_Labels").width==2480 && renderer.sharedMaterial.GetTexture("_Glyphs").width==768,"Labels and glyph atlas have doubled text resolution");
                foreach(var file in new[] {"Labels.png","Glyphs.png","Status.png","GlyphMetrics.png"})
                {
                    var importer=(TextureImporter)AssetImporter.GetAtPath(folder+"/"+file);
                    Check(!importer.sRGBTexture && !importer.mipmapEnabled && importer.maxTextureSize==4096,"Authored display/data texture settings: "+file);
                }
                var rawLabels=new Texture2D(1,1,TextureFormat.RGBA32,false,true); rawLabels.LoadImage(File.ReadAllBytes(folder+"/Labels.png"));
                Check(rawLabels.GetPixels().Any(p=>p.a>0.1f && p.a<0.5f && p.r>0.9f && p.g>0.9f),"Antialiased text edges retain straight color without double darkening"); UnityEngine.Object.DestroyImmediate(rawLabels);
                Check(Mathf.Abs(panel.Find("Board").localScale.y-VrPanelSetupWindow.BoardWidth*708/1240)<0.0001f,"VR board excludes the desktop footer");
                var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(folder+"/FX.controller");
                Check(controller.layers.Length==4 && controller.layers.All(l=>l.defaultWeight==1),"Feedback, placement, avatar heartbeat and backend freshness FX layers");
                var tree=(BlendTree)controller.layers[0].stateMachine.defaultState.motion;
                Check(tree.children.Length==518 && tree.children.All(t=>t.directBlendParameter==VrPanelContract.Prefix+"Weight"),"All numeric material properties have independent weighted bindings");
                foreach(var clip in controller.animationClips.Distinct()) foreach(var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var transform=host.transform.Find(binding.path);
                    if(transform==null || binding.type!=typeof(GameObject) && transform.GetComponent(binding.type)==null) throw new InvalidOperationException("Invalid generated binding "+binding.path+"/"+binding.propertyName);
                }
                Check(true,"Every generated animation binding resolves under its new host");
                var animator=host.AddComponent<Animator>(); animator.runtimeAnimatorController=controller; animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind(); animator.SetBool("IsLocal",true); animator.Update(0.02f); Check(!panel.gameObject.activeSelf,"Panel initially hidden");
                animator.SetBool(VrPanelContract.Prefix+"Visible",true);
                foreach(var item in frame.values) animator.SetFloat(VrPanelContract.ReceivePrefix+item.name,item.value);
                animator.SetBool(VrPanelContract.Prefix+"RxBeat",true);
                for(var i=0;i<20;i++) animator.Update(0.02f);
                Check(panel.gameObject.activeSelf && panel.GetComponent<VRCParentConstraint>().FreezeToWorld,"Show places the panel then drops it");
                var block=new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                var visibleWord=block.GetFloat("_R1_Toy00"); if(visibleWord==0) visibleWord=renderer.sharedMaterial.GetFloat("_R1_Toy00");
                Check(Mathf.Abs(visibleWord-frame.values.Single(v=>v.name=="R1_Toy00").value)<0.00002,"Animator preserves packed local glyph feedback");
                var liveCurveWord=block.GetFloat("_S00_C04"); if(liveCurveWord==0) liveCurveWord=renderer.sharedMaterial.GetFloat("_S00_C04");
                Check(Mathf.Abs(liveCurveWord-frame.values.Single(v=>v.name=="S00_C04").value)<0.00002,"Animator preserves packed recorded-curve feedback");
                panel.GetComponent<VRCParentConstraint>().IsActive=false;
                Render(panel,Path.Combine(output,"vr-panel.png"),(image,camera,board)=>
                {
                    int Count(Rect logical,Func<Color,bool> match)
                    {
                        Vector3 Screen(float x,float y) {return camera.WorldToScreenPoint(board.TransformPoint(new Vector3(x/1240-0.5f,0.5f-y/708,0)));}
                        var a=Screen(logical.xMin,logical.yMin); var b=Screen(logical.xMax,logical.yMax); var found=0;
                        for(var y=Mathf.FloorToInt(Mathf.Min(a.y,b.y));y<Mathf.CeilToInt(Mathf.Max(a.y,b.y));y++)
                            for(var x=Mathf.FloorToInt(Mathf.Min(a.x,b.x));x<Mathf.CeilToInt(Mathf.Max(a.x,b.x));x++)
                                if(match(image.GetPixel(Mathf.Clamp(x,0,image.width-1),Mathf.Clamp(y,0,image.height-1)))) found++;
                        return found;
                    }
                    bool Green(Color c) {return c.g>c.r+0.12f && c.g>c.b+0.12f;}
                    Color At(float x,float y) {var screen=camera.WorldToScreenPoint(board.TransformPoint(new Vector3(x/1240-0.5f,0.5f-y/708,0))); return image.GetPixel(Mathf.RoundToInt(screen.x),Mathf.RoundToInt(screen.y));}
                    var background=At(550,90); var card=At(800,115);
                    Check(Mathf.Abs(background.r-30/255f)<0.012f && Mathf.Abs(card.r-43/255f)<0.012f,"Rendered charcoal matches display palette in "+QualitySettings.activeColorSpace+" color space");
                    var minX=10000f; var maxX=-10000f; var minY=10000f; var maxY=-10000f;
                    for(var y=130f;y<164;y+=0.5f) for(var x=837f;x<927;x+=0.5f) {var c=At(x,y); if(c.r>0.65f && c.g>0.65f && c.b>0.65f) {minX=Mathf.Min(minX,x);maxX=Mathf.Max(maxX,x);minY=Mathf.Min(minY,y);maxY=Mathf.Max(maxY,y);}}
                    Check(maxX>minX && Mathf.Abs((minX+maxX)/2-882)<=1.5f && Mathf.Abs((minY+maxY)/2-147)<=1.5f,"OWN ink is centered horizontally and vertically in its button");
                    Check(Count(new Rect(440,260,460,140),Green)>40 && Count(new Rect(440,440,460,115),Green)>40,"Deselected loop renders a waveform above and below its current held level");
                    Check(Count(new Rect(54,177,180,17),c=>c.r>0.45f && c.g>0.45f && c.b>0.45f)>30,"Packed toy name renders readable glyph pixels");
                });
                Check(!ShaderUtil.ShaderHasError(renderer.sharedMaterial.shader),"Full PC shader compiles/renders without errors");
                var savedBlock=new MaterialPropertyBlock(); renderer.GetPropertyBlock(savedBlock); var emptyBlock=new MaterialPropertyBlock(); renderer.GetPropertyBlock(emptyBlock); emptyBlock.SetFloat("_Count",0);emptyBlock.SetFloat("_Pages",0);emptyBlock.SetFloat("_Online",0);renderer.SetPropertyBlock(emptyBlock);
                Render(panel,Path.Combine(output,"vr-panel-empty.png"),(image,camera,board)=>
                {
                    var bright=0;
                    for(var row=0;row<6;row++) for(var y=216+row*72;y<230+row*72;y++) for(var x=146;x<187;x++) {var screen=camera.WorldToScreenPoint(board.TransformPoint(new Vector3(x/1240f-0.5f,0.5f-y/708f,0))); var c=image.GetPixel(Mathf.RoundToInt(screen.x),Mathf.RoundToInt(screen.y)); if(c.r>0.12f || c.g>0.12f || c.b>0.12f) bright++;}
                    Check(bright==0,"Empty toy list has no ghost battery labels in any of six rows");
                });
                renderer.SetPropertyBlock(savedBlock);
                renderer.sharedMaterial.enableInstancing=true; renderer.sharedMaterial.EnableKeyword("STEREO_INSTANCING_ON");
                var variants=new ShaderVariantCollection(); variants.Add(new ShaderVariantCollection.ShaderVariant(renderer.sharedMaterial.shader,UnityEngine.Rendering.PassType.Normal,new[] {"INSTANCING_ON","STEREO_INSTANCING_ON"})); variants.WarmUp();
                Render(panel,Path.Combine(output,"vr-panel-stereo-variant.png"));
                Check(!ShaderUtil.ShaderHasError(renderer.sharedMaterial.shader),"Full panel stereo-instancing variant compiles and renders");
                renderer.sharedMaterial.DisableKeyword("STEREO_INSTANCING_ON"); UnityEngine.Object.DestroyImmediate(variants);
                if(Environment.GetEnvironmentVariable("SHOCKWAVE_VR_BENCHMARK")=="1") Benchmark(panel,folder,frame,output);
                var export=Path.Combine(output,"generated");Directory.CreateDirectory(export);
                foreach(var file in new[] {"Panel.shader","Panel.cginc","Labels.png","Glyphs.png","GlyphMetrics.png","Status.png"}) File.Copy(folder+"/"+file,Path.Combine(export,file),true);
                animator.SetBool(VrPanelContract.Prefix+"Follow",true); for(var i=0;i<5;i++) animator.Update(0.02f);
                Check(!panel.GetComponent<VRCParentConstraint>().FreezeToWorld,"Follow unfreezes placement");
                for(var i=0;i<120;i++) animator.Update(0.02f);
                renderer.GetPropertyBlock(block); var fresh=block.GetFloat("_LinkOnline"); if(fresh==0) fresh=renderer.sharedMaterial.GetFloat("_LinkOnline");
                Check(fresh<0.5f,"Backend heartbeat timeout switches visual state offline");
                animator.SetBool(VrPanelContract.Prefix+"RxBeat",false); for(var i=0;i<5;i++) animator.Update(0.02f);
                renderer.GetPropertyBlock(block); fresh=block.GetFloat("_LinkOnline"); if(fresh==0) fresh=renderer.sharedMaterial.GetFloat("_LinkOnline");
                Check(fresh>0.5f,"Fresh opposite backend pulse restores online state");
                animator.SetBool("IsLocal",false); for(var i=0;i<5;i++) animator.Update(0.02f); Check(!panel.gameObject.activeSelf,"Other viewers cannot see the panel");
                File.WriteAllText(Path.Combine(output,"report.json"),"{\"Passed\":"+passed+",\"PhysicalOutput\":false,\"UploadedAvatar\":false}");
                Debug.Log("VR DEMO VERIFICATION COMPLETE "+passed+" checks");
            }
            catch(Exception ex) {Debug.LogException(ex); if(!string.IsNullOrWhiteSpace(output)) File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString()); EditorApplication.Exit(1);}
            finally {if(root!=null) UnityEngine.Object.DestroyImmediate(root); if(folder!=null && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder); EditorSceneManager.ClosePreviewScene(scene);}
        }
        private static void Render(Transform panel,string path,Action<Texture2D,Camera,Transform> inspect=null)
        {
            var obj=new GameObject("VR demo preview camera"); var camera=obj.AddComponent<Camera>(); SceneManager.MoveGameObjectToScene(obj,panel.gameObject.scene);
            camera.cameraType=CameraType.Preview; camera.scene=panel.gameObject.scene; camera.transform.position=panel.position-panel.forward*1.2f; camera.transform.rotation=panel.rotation;
            camera.orthographic=true; camera.orthographicSize=0.23f; camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(0.08f,0.09f,0.10f);
            var rt=RenderTexture.GetTemporary(1488,850,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB); camera.targetTexture=rt; var old=RenderTexture.active;
            Texture2D image=null; try {camera.Render(); RenderTexture.active=rt; image=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false); image.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); image.Apply(); File.WriteAllBytes(path,image.EncodeToPNG()); inspect?.Invoke(image,camera,panel.Find("Board"));}
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
            var image=new Texture2D(1,1,TextureFormat.RGBA32,false); var renderer=panel.Find("Board").GetComponent<MeshRenderer>(); var original=renderer.sharedMaterial;
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
