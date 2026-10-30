// ============================================================
// GameBackgroundFieldBuild.cs —— 构建时绑定GPU数据，避免战斗中生成纹理。
// 静态冰胞与周期噪声是GPU数据纹理，不是Sprite；各自只进入所属背景包。
// 生成源由tools/performance-fields.py维护，纹理接入通过材质引用交给RevResManager随章节租约装卸。
// ============================================================
using System;
using UnityEditor;
using UnityEngine;
namespace RevGame.Editor
{
    public static class GameBackgroundFieldBuild
    {
        /// <summary>构建时显式分类，不允许绕回散图Sprite或在战斗Update创建/克隆纹理材质。</summary>
        public static void Configure()
        {
            ConfigureTexture("Chapter05_IceField",512,false);
            ConfigureTexture("Chapter04_SwampNoise",256,true);
            Bind("Chapter05_Ice","_IceField","_UseIceField","Chapter05_IceField");
            Bind("Chapter04_Swamp","_FlowNoise","_UseFlowNoise","Chapter04_SwampNoise");
        }
        /// <summary>size是算法数据精度的像素边长；repeat只对周期噪声开启，冰胞场到边界需Clamp。</summary>
        private static void ConfigureTexture(string name,int size,bool repeat)
        {
            string path="Assets/GameRes/ChapterBackground/"+name+".png";
            var importer=AssetImporter.GetAtPath(path) as TextureImporter;
            if(importer==null) { throw new InvalidOperationException("缺少GPU预计算数据，先运行tools/performance-fields.py："+path); }
            importer.textureType=TextureImporterType.Default;
            // 最小meta导入时可能保留Cube形状；必须显式指定二维数据，否则LoadAssetAtPath<Texture2D>取不到。
            importer.textureShape=TextureImporterShape.Texture2D;
            // 距离/斜面必须线性读取，有损压缩和sRGB会改变解码值；禁止readable避免CPU像素副本常驻。
            importer.sRGBTexture=false;
            importer.mipmapEnabled=false;importer.isReadable=false;importer.npotScale=TextureImporterNPOTScale.None;
            importer.filterMode=FilterMode.Bilinear;importer.wrapMode=repeat?TextureWrapMode.Repeat:TextureWrapMode.Clamp;
            importer.textureCompression=TextureImporterCompression.Uncompressed;
            importer.maxTextureSize=size;
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings{name="Standalone",overridden=true,maxTextureSize=size,format=TextureImporterFormat.RGBA32});
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings{name="Android",overridden=true,maxTextureSize=size,format=TextureImporterFormat.RGBA32});
            importer.SaveAndReimport();
        }
        /// <summary>绑定现有章节材质，租约仍归所属AB；缺材质/纹理/Shader属性直接拒绝构建。</summary>
        private static void Bind(string chapter,string property,string enabled,string texture)
        {
            var material=AssetDatabase.LoadAssetAtPath<Material>("Assets/GameRes/Material/Background/"+chapter+"_Material.mat");
            var image=AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/GameRes/ChapterBackground/"+texture+".png");
            if(material==null||image==null||!material.HasProperty(property))
            { throw new InvalidOperationException("背景场纹理/材质契约不完整："+chapter+"；材质="+(material!=null)+"；纹理="+(image!=null)+"；Shader="+(material==null?"无":material.shader.name)+"；属性="+(material!=null&&material.HasProperty(property))); }
            material.SetTexture(property,image);material.SetFloat(enabled,1);EditorUtility.SetDirty(material);
        }
    }
}
