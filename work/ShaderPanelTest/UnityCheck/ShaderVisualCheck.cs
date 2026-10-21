// Editor-only verifier. Copy to Assets/Editor in a NEW scratch Unity project.
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Rendering;

public static class ShaderVisualCheck
{
    public static void Run()
    {
        string folder = Path.Combine(Directory.GetCurrentDirectory(), "checks");
        Directory.CreateDirectory(folder);
        var checks = new List<string>();
        try
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/AvatarVisualProof.shader");
            if (shader == null || !shader.isSupported) throw new Exception("Unity proof shader missing/unsupported");
            var material = new Material(shader);
            material.SetFloat("_TouchX", 0.35f); material.SetFloat("_TouchY", 0.65f);
            material.SetFloat("_Touching", 1); material.SetFloat("_HeldEnabled0", 1);
            material.SetFloat("_HeldLevel0", 0.5f); material.SetFloat("_HeldEnabled1", 0);
            var target = new RenderTexture(765, 352, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            target.Create();
            Texture2D Capture()
            {
                Graphics.Blit(Texture2D.blackTexture, target, material);
                var old = RenderTexture.active; RenderTexture.active = target;
                var image = new Texture2D(765,352,TextureFormat.RGBA32,false,true);
                image.ReadPixels(new Rect(0,0,765,352),0,0); image.Apply(); RenderTexture.active = old;
                return image;
            }
            void Check(bool condition, string text) { if (!condition) throw new Exception(text); checks.Add(text); }
            bool BrightNear(Texture2D image, float u, float v)
            {
                int x = Mathf.RoundToInt(u * 765), y = Mathf.RoundToInt(v * 352);
                for (int dy=-2;dy<=2;dy++) for(int dx=-2;dx<=2;dx++)
                {
                    var c=image.GetPixel(Mathf.Clamp(x+dx,0,764),Mathf.Clamp(y+dy,0,351));
                    if (c.r>0.9f && c.g>0.9f && c.b>0.9f) return true;
                }
                return false;
            }
            using (var image = new ImageOwner(Capture()))
            {
                File.WriteAllBytes(Path.Combine(folder,"unity-visuals.png"),image.Value.EncodeToPNG());
                Check(BrightNear(image.Value,0.35f,0.65f),"Shared HLSL touch dot renders at supplied XY on Unity D3D11");
                var line=image.Value.GetPixel(30,176); var background=image.Value.GetPixel(30,160);
                Check(line.r>background.r+0.1f,"Shared HLSL colored held line renders at 50 percent");
            }
            material.SetFloat("_TouchX",0.70f);
            using(var image=new ImageOwner(Capture()))
            {
                Check(BrightNear(image.Value,0.70f,0.65f) && !BrightNear(image.Value,0.35f,0.65f),"Touch dot follows X without changing intensity");
            }
            material.SetFloat("_TouchY",0.30f);
            using(var image=new ImageOwner(Capture())) Check(BrightNear(image.Value,0.70f,0.30f),"Touch dot follows Y");
            material.SetFloat("_Touching",0);
            using(var image=new ImageOwner(Capture())) Check(!BrightNear(image.Value,0.70f,0.30f),"Touch release hides the dot");
            material.SetFloat("_HeldLevel0",0);
            using(var image=new ImageOwner(Capture())) Check(image.Value.GetPixel(30,176).r<0.1f,"Zero removes a held line");
            material.EnableKeyword("STEREO_INSTANCING_ON"); material.enableInstancing=true;
            using(var image=new ImageOwner(Capture())) Check(image.Value.GetPixel(30,176).r<0.1f,"Stereo-instancing shader variant compiles/renders");
            foreach(var message in ShaderUtil.GetShaderMessages(shader))
                if(message.severity==ShaderCompilerMessageSeverity.Error) throw new Exception(message.message);
            checks.Add("Unity reports no shader compile errors");
            UnityEngine.Object.DestroyImmediate(material); target.Release(); UnityEngine.Object.DestroyImmediate(target);
            File.WriteAllText(Path.Combine(folder,"report.txt"),"Unity "+Application.unityVersion+" / "+SystemInfo.graphicsDeviceType+"\n"+string.Join("\n",checks)+"\nNo VRChat client, real avatar project or hardware was used.\n");
            EditorApplication.Exit(0);
        }
        catch(Exception error)
        {
            File.WriteAllText(Path.Combine(folder,"failure.txt"),error.ToString()); Debug.LogException(error); EditorApplication.Exit(1);
        }
    }
    private sealed class ImageOwner : IDisposable
    {
        public Texture2D Value;
        public ImageOwner(Texture2D image) { Value=image; }
        public void Dispose() { UnityEngine.Object.DestroyImmediate(Value); }
    }
}
