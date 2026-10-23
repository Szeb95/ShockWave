#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using ShockWave.ToyControl.Shared;
using UnityEditor;
using UnityEngine;

namespace ShockWave.ToyControl
{
    internal static class ToyControlShaderFactory
    {
        private const int TextScale=2;
        internal static Material CreatePointer(string folder)
        {
            var path=folder+"/Pointer.shader"; File.Copy(ToyControlSetupWindow.SourceRoot+"/Pointer.shader",path);
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            var shader=AssetDatabase.LoadAssetAtPath<Shader>(path);
            if(shader==null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Pointer shader failed to compile.");
            var material=new Material(shader) {name="Pointer green"}; AssetDatabase.CreateAsset(material,folder+"/Pointer.mat"); return material;
        }
        internal static Material CreateStrip(string panelMaterialPath)
        {
            var folder=panelMaterialPath.Substring(0,panelMaterialPath.LastIndexOf('/'));
            var path=folder+"/GripStrip.shader"; File.Copy(ToyControlSetupWindow.SourceRoot+"/GripStrip.shader",path);
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            var material=new Material(AssetDatabase.LoadAssetAtPath<Shader>(path)) {name="Floating grab strips"};
            AssetDatabase.CreateAsset(material,folder+"/GripStrip.mat"); return material;
        }
        internal static Material Create(string folder)
        {
            foreach(var file in new[] {"GraphCore.hlsli","TouchVisuals.hlsli","Panel.cginc"})
                File.Copy(ToyControlSetupWindow.SourceRoot+"/"+file,folder+"/"+file);
            var source=new StringBuilder("Shader \"ShockWave/Toy Control\" { Properties {\n_Labels (\"Labels\",2D)=\"black\" {}\n_Glyphs (\"Glyphs\",2D)=\"black\" {}\n_Status (\"Status\",2D)=\"black\" {}\n_LinkOnline (\"Backend fresh\",Float)=0\n_Clock (\"Clock\",Float)=0\n");
            foreach(var field in ToyControlContract.FeedbackFields()) source.Append("[HideInInspector] _"+field+" (\""+field+"\",Float)=0\n");
            source.Append("} SubShader { Tags {\"Queue\"=\"AlphaTest\" \"RenderType\"=\"TransparentCutout\" \"VRCFallback\"=\"Hidden\"} Cull Off ZWrite On Pass { CGPROGRAM\n#pragma target 4.5\n#pragma vertex vert\n#pragma fragment frag\n#pragma multi_compile_instancing\n#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON\n");
            foreach(var field in ToyControlContract.FeedbackFields()) source.Append("float _"+field+";\n");
            ArrayAccessor(source,"rowState",ToyControlContract.Rows,i=>"_"+ToyControlContract.Row(i,"State")); ArrayAccessor(source,"rowRG",ToyControlContract.Rows,i=>"_"+ToyControlContract.Row(i,"RG"));
            ArrayAccessor(source,"rowB",ToyControlContract.Rows,i=>"_"+ToyControlContract.Row(i,"B")); ArrayAccessor(source,"rowBattery",ToyControlContract.Rows,i=>"_"+ToyControlContract.Row(i,"Battery"));
            MatrixAccessor(source,"rowToy",ToyControlContract.Rows,9,(r,p)=>"_"+ToyControlContract.Row(r,ToyControlContract.Sample("Toy",p))); MatrixAccessor(source,"rowFunction",ToyControlContract.Rows,9,(r,p)=>"_"+ToyControlContract.Row(r,ToyControlContract.Sample("Function",p)));
            foreach(var field in new[] {"State","RG","B","Phase","Cycles"}) ArrayAccessor(source,"slot"+field,16,i=>"_"+ToyControlContract.Slot(i,field));
            ArrayAccessor(source,"curveWord",32,i=>"_"+ToyControlContract.Sample("Curve",i)); MatrixAccessor(source,"loopWord",16,16,(s,p)=>"_"+ToyControlContract.Slot(s,ToyControlContract.Sample("C",p)));
            source.Append("float levelByte(float word,int index) {float value=floor(saturate(word)*65535.0+0.5); return (index%2==0 ? fmod(value,256.0) : floor(value/256.0))/255.0;}\nfloat curveValue(int i) {return levelByte(curveWord(i/2),i);}\nfloat loopValue(int slot,int i) {return levelByte(loopWord(slot,i/2),i);}\n#include \"Panel.cginc\"\nENDCG } } }");
            source.Replace("_Glyphs (\"Glyphs\",2D)=\"black\" {}","_Glyphs (\"Glyphs\",2D)=\"black\" {}\n_GlyphMetrics (\"Glyph metrics\",2D)=\"black\" {}\n_ShockerLabels (\"Shocker labels\",2D)=\"black\" {}");
            var shaderPath=folder+"/Panel.shader"; File.WriteAllText(shaderPath,source.ToString()); AssetDatabase.ImportAsset(shaderPath,ImportAssetOptions.ForceSynchronousImport);
            var shader=AssetDatabase.LoadAssetAtPath<Shader>(shaderPath); if(shader==null || ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("VR shader failed to compile.");
            var material=new Material(shader) {name="VR OSC panel"};
            material.SetTexture("_Glyphs",Glyphs(folder)); material.SetTexture("_GlyphMetrics",GlyphMetrics(folder)); material.SetTexture("_Labels",Labels(folder)); material.SetTexture("_Status",Status(folder)); material.SetTexture("_ShockerLabels",ShockerLabels(folder));
            AssetDatabase.CreateAsset(material,folder+"/Panel.mat"); return material;
        }
        private static void ArrayAccessor(StringBuilder source,string name,int count,Func<int,string> field)
        {
            source.Append("float "+name+"(int i) {i=clamp(i,0,"+(count-1)+");");
            Lookup(source,"i",0,count,i=>"return "+field(i)+";"); source.Append("}\n");
        }
        private static void MatrixAccessor(StringBuilder source,string name,int rows,int columns,Func<int,int,string> field)
        {
            source.Append("float "+name+"(int row,int pair) {row=clamp(row,0,"+(rows-1)+");pair=clamp(pair,0,"+(columns-1)+");");
            Lookup(source,"row",0,rows,r=> {var nested=new StringBuilder(); Lookup(nested,"pair",0,columns,p=>"return "+field(r,p)+";"); return nested.ToString();}); source.Append("}\n");
        }
        private static void Lookup(StringBuilder source,string index,int start,int end,Func<int,string> leaf)
        {
            // Read only the requested uniform. A dynamically indexed local array
            // otherwise copies hundreds of curve words into per-pixel registers.
            if(end-start==1) {source.Append(leaf(start)); return;}
            var split=(start+end)/2; source.Append("[branch] if("+index+"<"+split+") {"); Lookup(source,index,start,split,leaf);
            source.Append("} else {"); Lookup(source,index,split,end,leaf); source.Append("}");
        }
        private static Texture2D Glyphs(string folder)
        {
            var texture=Blank(384*TextScale,192*TextScale); var font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var chars=new string(Enumerable.Range(32,95).Select(i=>(char)i).ToArray()); font.RequestCharactersInTexture(chars,24*TextScale,FontStyle.Normal);
            using(var atlas=new FontAtlas(font))
                for(var code=0;code<95;code++)
                {
                    CharacterInfo info; if(!font.GetCharacterInfo((char)(code+32),out info,24*TextScale,FontStyle.Normal)) continue;
                    Raster(texture,atlas.Texture,info,code%16*24*TextScale+(24*TextScale-info.advance)/2,code/16*32*TextScale+7*TextScale,Color.white);
                }
            return Save(texture,folder+"/Glyphs.png");
        }
        private static Texture2D GlyphMetrics(string folder)
        {
            var texture=Blank(95,1); var font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            font.RequestCharactersInTexture(new string(Enumerable.Range(32,95).Select(i=>(char)i).ToArray()),24*TextScale,FontStyle.Normal);
            for(var code=0;code<95;code++)
            {
                CharacterInfo info; var advance=font.GetCharacterInfo((char)(code+32),out info,24*TextScale,FontStyle.Normal) ? info.advance : 13*TextScale;
                texture.SetPixel(code,0,new Color(advance/255f,0,0,1));
            }
            var path=folder+"/GlyphMetrics.png"; var saved=Save(texture,path);
            var importer=(TextureImporter)AssetImporter.GetAtPath(path); importer.sRGBTexture=false; importer.filterMode=FilterMode.Point; importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        private static Texture2D Labels(string folder)
        {
            var t=Blank(1240*TextScale,800*TextScale); var white=new Color(242/255f,243/255f,245/255f); var faint=new Color(181/255f,186/255f,193/255f); var accent=new Color(16/255f,163/255f,127/255f);
            Text(t,"TOY FUNCTIONS",43,124,17,white,FontStyle.Bold); Text(t,"Choose functions to draw together",43,151,12,faint); Text(t,"VRC toy control",368,178,20,white,FontStyle.Bold);
            Centered(t,"Shockers",new Rect(1052,127,152,42),16,white,FontStyle.Bold);
            Centered(t,"Record",new Rect(365,127,130,42),14,white,FontStyle.Bold); Centered(t,"Play loop",new Rect(499,127,155,42),14,white,FontStyle.Bold);
            Centered(t,"Touch",new Rect(658,127,130,42),14,white,FontStyle.Bold);
            Centered(t,"Pointer",new Rect(658,758,130,42),14,white,FontStyle.Bold);
            Text(t,"loops running",870,178,13,faint); Text(t,"100%",350,222,13,faint); Text(t,"75",368,312,12,faint); Text(t,"50",368,400,12,faint); Text(t,"25",368,488,12,faint); Text(t,"0%",365,565,13,faint);
            Text(t,"<",58,652,20,faint); Text(t,">",274,652,20,faint);
            Text(t,"functions",66,687,11,faint);
            Centered(t,"Clear curve",new Rect(40,176,132,60),14,white,FontStyle.Bold); Centered(t,"STOP ALL",new Rect(176,176,132,60),14,white,FontStyle.Bold);
            Centered(t,"Select a function to draw",new Rect(405,720,765,48),20,faint,FontStyle.Bold);
            Centered(t,"Recording",new Rect(365,758,130,42),14,white,FontStyle.Bold);
            GestureHelp(t,faint);
            return Save(t,folder+"/Labels.png");
        }
        private static Texture2D ShockerLabels(string folder)
        {
            var t=Blank(1240*TextScale,800*TextScale); var white=new Color(242/255f,243/255f,245/255f); var faint=new Color(181/255f,186/255f,193/255f);
            Text(t,"SHOCKERS",43,124,17,white,FontStyle.Bold); Text(t,"Choose one shocker",43,151,12,faint);
            Text(t,"Shocker control",368,178,22,white,FontStyle.Bold);
            Centered(t,"Toys",new Rect(1052,127,152,42),16,white,FontStyle.Bold);
            Centered(t,"Touch",new Rect(658,127,130,42),14,white,FontStyle.Bold); Centered(t,"Pointer",new Rect(658,758,130,42),14,white,FontStyle.Bold);
            Centered(t,"STOP ALL",new Rect(176,176,132,60),14,white,FontStyle.Bold);
            Centered(t,"Intensity",new Rect(380,210,148,30),18,white,FontStyle.Bold); Centered(t,"Duration",new Rect(540,210,148,30),18,white,FontStyle.Bold);
            Centered(t,"SHOCK",new Rect(810,240,136,136),19,white,FontStyle.Bold); Centered(t,"ARM",new Rect(1020,240,136,136),19,white,FontStyle.Bold);
            Centered(t,"2 - confirm",new Rect(800,385,156,30),16,faint); Centered(t,"1 - arm",new Rect(1010,385,156,30),16,faint);
            Centered(t,"Vibrate",new Rect(810,430,346,64),22,white,FontStyle.Bold); Centered(t,"Sound",new Rect(810,516,346,64),22,white,FontStyle.Bold);
            Text(t,"Right > release > left within 3 seconds",800,600,15,faint); Text(t,"Two steps for every shock",800,626,16,faint);
            Text(t,"<",58,652,20,faint); Text(t,">",274,652,20,faint);
            GestureHelp(t,faint);
            return Save(t,folder+"/ShockerLabels.png");
        }
        private static void GestureHelp(Texture2D texture,Color color) => Centered(texture,"Fist = grab handle   |   Finger gun = point   |   Curl index (thumbs up) = click",new Rect(365,677,839,26),14,color);
        private static Texture2D Status(string folder)
        {
            var t=Blank(232*TextScale,160*TextScale); var accent=new Color(24/255f,185/255f,145/255f); var faint=new Color(181/255f,186/255f,193/255f);
            Text(t,"VRC CONNECTED",10,3,14,accent,FontStyle.Bold); Text(t,"ShockWave · live",10,23,11,faint);
            Text(t,"TEST MODE",10,43,14,accent,FontStyle.Bold); Text(t,"Preview · no toy output",10,63,11,faint);
            Text(t,"WAITING FOR SHOCKWAVE",10,83,14,faint,FontStyle.Bold); Text(t,"Open ShockWave / enable OSC",10,103,11,faint);
            Text(t,"REMOTE UNAVAILABLE",10,123,14,faint,FontStyle.Bold); Text(t,"Select Own for local toys",10,143,11,faint);
            return Save(t,folder+"/Status.png");
        }
        private static Texture2D Blank(int w,int h) {var t=new Texture2D(w,h,TextureFormat.RGBA32,false,true); t.SetPixels(Enumerable.Repeat(Color.clear,w*h).ToArray()); t.Apply(); return t;}
        private static Texture2D Save(Texture2D texture,string path)
        {
            texture.Apply(); File.WriteAllBytes(path,texture.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(texture); AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            var importer=(TextureImporter)AssetImporter.GetAtPath(path); importer.mipmapEnabled=false; importer.npotScale=TextureImporterNPOTScale.None; importer.sRGBTexture=false; importer.alphaIsTransparency=!path.EndsWith("GlyphMetrics.png",StringComparison.Ordinal); importer.filterMode=FilterMode.Bilinear; importer.textureCompression=TextureImporterCompression.Uncompressed; importer.maxTextureSize=4096; importer.alphaSource=TextureImporterAlphaSource.FromInput; importer.wrapMode=TextureWrapMode.Clamp; importer.SaveAndReimport(); return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        private static void Text(Texture2D t,string value,int x,int top,int size,Color color,FontStyle style=FontStyle.Normal)
        {
            x*=TextScale; top*=TextScale; size*=TextScale;
            var font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); font.RequestCharactersInTexture(value,size,style);
            using(var atlas=new FontAtlas(font)) foreach(var c in value)
            {CharacterInfo info; if(!font.GetCharacterInfo(c,out info,size,style)) continue; Raster(t,atlas.Texture,info,x,t.height-top-size,color); x+=info.advance;}
        }
        private static void Centered(Texture2D t,string value,Rect button,int size,Color color,FontStyle style=FontStyle.Normal)
        {
            size*=TextScale; var font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); font.RequestCharactersInTexture(value,size,style);
            var cursor=0; var minX=float.MaxValue; var maxX=float.MinValue; var minY=float.MaxValue; var maxY=float.MinValue;
            foreach(var c in value) {CharacterInfo info; if(!font.GetCharacterInfo(c,out info,size,style)) continue; minX=Mathf.Min(minX,cursor+info.minX); maxX=Mathf.Max(maxX,cursor+info.maxX); minY=Mathf.Min(minY,info.minY); maxY=Mathf.Max(maxY,info.maxY); cursor+=info.advance;}
            var x=Mathf.RoundToInt(button.center.x*TextScale-(minX+maxX)/2); var baseline=Mathf.RoundToInt(t.height-button.center.y*TextScale-(minY+maxY)/2);
            using(var atlas=new FontAtlas(font)) foreach(var c in value) {CharacterInfo info; if(!font.GetCharacterInfo(c,out info,size,style)) continue; Raster(t,atlas.Texture,info,x,baseline,color); x+=info.advance;}
        }
        private static void Raster(Texture2D t,Texture2D atlas,CharacterInfo info,int x,int baseline,Color color)
        {
            var w=info.maxX-info.minX; var h=info.maxY-info.minY;
            for(var yy=0;yy<h;yy++) for(var xx=0;xx<w;xx++)
            {
                var uv=Vector2.Lerp(Vector2.Lerp(info.uvBottomLeft,info.uvBottomRight,(xx+0.5f)/w),Vector2.Lerp(info.uvTopLeft,info.uvTopRight,(xx+0.5f)/w),(yy+0.5f)/h);
                var a=atlas.GetPixelBilinear(uv.x,uv.y).a; var dx=x+info.minX+xx; var dy=baseline+info.minY+yy;
                if(dx>=0 && dy>=0 && dx<t.width && dy<t.height && a>0)
                {
                    var before=t.GetPixel(dx,dy); var alpha=a+before.a*(1-a);
                    // Straight alpha: applying coverage again in the shader must
                    // not darken an already premultiplied glyph edge a second time.
                    var rgb=(new Vector3(color.r,color.g,color.b)*a+new Vector3(before.r,before.g,before.b)*before.a*(1-a))/alpha;
                    t.SetPixel(dx,dy,new Color(rgb.x,rgb.y,rgb.z,alpha));
                }
            }
        }
        private sealed class FontAtlas : IDisposable
        {
            public readonly Texture2D Texture;
            public FontAtlas(Font font)
            {
                var original=font.material.mainTexture; var rt=RenderTexture.GetTemporary(original.width,original.height,0,RenderTextureFormat.ARGB32); var previous=RenderTexture.active;
                try {Graphics.Blit(original,rt); RenderTexture.active=rt; Texture=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false); Texture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); Texture.Apply();}
                finally {RenderTexture.active=previous; RenderTexture.ReleaseTemporary(rt);}
            }
            public void Dispose() {UnityEngine.Object.DestroyImmediate(Texture);}
        }
    }
}
#endif
