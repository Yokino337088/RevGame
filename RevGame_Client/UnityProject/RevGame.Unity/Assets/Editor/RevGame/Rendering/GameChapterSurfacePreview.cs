// ============================================================
// GameChapterSurfacePreview —— 冰封/湖面材质的编辑态配置与GPU验收。
// 为什么隔离绘制：用户可能正在编辑未保存的场景，调背景不能切走、保存或丢弃这些工作。
// 只创建临时预览场景/相机/材质，所有临时对象在finally中回收；不进入Player或战斗热路径。
// ============================================================
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>
    /// 第三/五章背景的固定输入验收。所有材质、网格、预览相机与读回贴图只属于本次调用，finally统一回收。
    /// 这个工具证明桌面GPU实际出图与时间开关正确，不将像素差异或一次CPU计时当作手机性能达标。
    /// </summary>
    public static class GameChapterSurfacePreview
    {
        private const string MaterialPath = "Assets/GameRes/Material/Background/Chapter05_Ice_Material.mat"; // 编辑器AssetDatabase路径，保留原GUID。
        private const string OutputRoot = "Temp/IceShader"; // 编辑器生成证据，不是运行时资源路径，也不进入AB。

        /// <summary>
        /// 只配置第五章材质的冰封表现参数；原图、Shader引用、棋盘保护和其它章节不变。
        /// 记录Undo并仅保存此材质，失败抛出原因；不能用SaveAssets顺便保存用户其它未完成的资产。
        /// </summary>
        public static string Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("请退出Play后配置冰封材质"); }
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null || !material.HasProperty("_IceFlow")) { throw new InvalidOperationException("第五章新Shader尚未导入"); }
            Undo.RecordObject(material, "配置第五章极地冰封");
            // 这些数值控制色彩/UV/表现速度，属于美术材质；玩家数值仍必须从Excel读取。
            material.SetFloat("_Distortion", 0.0038f);
            material.SetFloat("_IceFlow", 1.15f);
            material.SetFloat("_IceDepth", 0.65f);
            material.SetFloat("_IceCracks", 1.15f);
            material.SetFloat("_IceScale", 6.5f);
            material.SetFloat("_IceCoverage", 0.85f);
            material.SetFloat("_ColdBreath", 1.1f);
            material.SetFloat("_Speed", 0.9f);
            material.SetFloat("_Glow", 1.05f);
            material.SetFloat("_Particles", 0.7f);
            material.SetFloat("_Frost", 0.95f);
            material.SetFloat("_ColdTone", 0.95f);
            ConfigureArtRect(material);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return "已保存第五章极地冰封参数，支持Undo；未修改场景或其它材质";
        }

        [MenuItem("RevGame/美术/配置第五章极地冰封")]
        public static void ConfigureMenu() { Configure(); }

        /// <summary>只配置第三章的表现参数；所有权/保存范围与冰封配置相同。</summary>
        public static string ConfigureLake()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("请退出Play后配置湖面材质"); }
            Material material = AssetDatabase.LoadAssetAtPath<Material>("Assets/GameRes/Material/Background/Chapter03_Lake_Material.mat");
            if (material == null || !material.HasProperty("_WaveHeight")) { throw new InvalidOperationException("第三章新Shader尚未导入"); }
            Undo.RecordObject(material, "配置第三章流动湖面");
            material.SetFloat("_Distortion", 0.012f);
            material.SetFloat("_Speed", 1.05f);
            material.SetFloat("_WaveHeight", 0.9f);
            material.SetFloat("_FlowDistance", 0.055f);
            material.SetFloat("_RippleStrength", 1.1f);
            material.SetFloat("_SpecularStrength", 1.25f);
            material.SetFloat("_SpecularPower", 100f);
            material.SetFloat("_Caustics", 0.8f);
            material.SetFloat("_ReflectionStrength", 0.4f);
            material.SetFloat("_ReflectionStretch", 0.85f);
            material.SetFloat("_Glow", 1.1f);
            material.SetFloat("_Particles", 0.35f);
            material.SetFloat("_BoardProtection", 0.65f);
            ConfigureArtRect(material);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return "已保存第三章连续流水/扩散波纹/波面反光参数，支持Undo";
        }

        [MenuItem("RevGame/美术/配置第三章流动湖面")]
        public static void ConfigureLakeMenu() { ConfigureLake(); }

        /// <summary>
        /// 两张指定原画均为1376×774，最下面6行完全透明；Unity UV原点在下方，因此跳过底部6/774。
        /// 这是原画内容边界，不是设备分辨率；换成无留边原画时把材质的有效UV改回(0,0,1,1)。
        /// </summary>
        private static void ConfigureArtRect(Material material)
        {
            material.SetVector("_ArtUVRect", new Vector4(0f, 6f / 774f, 1f, 768f / 774f));
        }

        /// <summary>固定白平衡/时间/1920×1080画幅；新增参数均为美术表现参数，不是策划战斗数值。</summary>
        [MenuItem("RevGame/美术/验收第五章冰封背景")]
        public static void RunMenu() { Run(); }

        /// <summary>
        /// 生成两个时间点与原画对照；强度0必须原画静止，速度0必须停止全部动画，左右四角必须仍可绘制。
        /// 预览不修改原材质：参数覆盖都写到临时副本，所以不会重置用户Inspector里的设置。
        /// </summary>
        public static string Run() { return RunSurface(MaterialPath, OutputRoot, "Ice"); }

        [MenuItem("RevGame/美术/验收第三章湖面背景")]
        public static void RunLakeMenu() { RunLake(); }

        /// <summary>与冰封使用同一个GPU夹具，避免两套测试的相机/采样条件不同而无法比较。</summary>
        public static string RunLake()
        {
            return RunSurface("Assets/GameRes/Material/Background/Chapter03_Lake_Material.mat", "Temp/LakeShader", "Lake");
        }

        private static string RunSurface(string materialPath, string outputRoot, string prefix)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("请退出Play后生成隔离预览"); }
            Material source = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (source == null || source.shader == null || !source.shader.isSupported || ShaderUtil.ShaderHasError(source.shader))
            {
                throw new InvalidOperationException(materialPath + "材质或Shader不可用，请先检查Console");
            }
            Directory.CreateDirectory(outputRoot);
            string resultPath = outputRoot + "/Result.txt";
            if (File.Exists(resultPath)) { File.Delete(resultPath); } // 清理旧验收状态，失败时不能留下上一次的“通过”。
            var material = new Material(source) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                material.SetFloat("_BoardProtection", 0f);
                material.SetFloat("_Transition", 1f);
                material.SetFloat("_Aspect", 16f / 9f);
                Color32[] first = Render(material, 0f, outputRoot + "/" + prefix + "-A.png");
                if (Difference(first, Render(material, 0f, null)) > 0.00001) { throw new InvalidOperationException("同一输入和时间未产生可重复画面"); }
                Color32[] second = Render(material, 2.5f, outputRoot + "/" + prefix + "-B.png");
                Render(material, 5f, outputRoot + "/" + prefix + "-C.png");
                float strength = material.GetFloat("_Strength");
                material.SetFloat("_Strength", 0f);
                Color32[] offA = Render(material, 0f, outputRoot + "/" + prefix + "-Off.png");
                Color32[] offB = Render(material, 2.5f, null);
                if (Difference(offA, offB) > 0.00001) { throw new InvalidOperationException("强度为0仍有动画"); }
                material.SetFloat("_Strength", strength);
                float speed = material.GetFloat("_Speed");
                material.SetFloat("_Speed", 0f);
                Color32[] pausedA = Render(material, 0f, null);
                Color32[] pausedB = Render(material, 2.5f, null);
                if (Difference(pausedA, pausedB) > 0.00001) { throw new InvalidOperationException("速度为0仍有动画"); }
                material.SetFloat("_Speed", speed);
                double movement = Difference(first, second);
                double effect = Difference(first, offA);
                if (movement < 0.001 || effect < 0.01) { throw new InvalidOperationException("动态或表面层未实际绘制，时间差=" + movement + "，原画差=" + effect); }
                // 明确证明新增的冰下流动本身在动，不能靠飘雪的像素变化冒充背景流动。
                double flow = 0;
                bool hasFlow = material.HasProperty("_IceFlow");
                if (hasFlow)
                {
                    material.SetFloat("_IceDepth", 0f);
                    material.SetFloat("_ColdTone", 0f);
                    material.SetFloat("_Particles", 0f);
                    material.SetFloat("_Frost", 0f);
                    material.SetFloat("_Glow", 0f);
                    material.SetFloat("_IceCracks", 0f);
                    material.SetFloat("_IceCoverage", 0f);
                    material.SetFloat("_ColdBreath", 0f);
                    flow = Difference(Render(material, 0f, outputRoot + "/Ice-Flow-A.png"), Render(material, 2.5f, outputRoot + "/Ice-Flow-B.png"));
                    if (flow < 0.0001) { throw new InvalidOperationException("关闭雪/霜/反光后，原画没有冰下流动"); }
                }
                if (material.HasProperty("_WaveHeight"))
                {
                    // 水流、涟漪、高光分别关闭其它层验证，不能让一层动画掩盖另一层没有接线。
                    material.CopyPropertiesFromMaterial(source);
                    material.SetFloat("_BoardProtection", 0f);
                    material.SetFloat("_Glow", 0f);
                    material.SetFloat("_Particles", 0f);
                    material.SetFloat("_ReflectionStrength", 0f);
                    material.SetFloat("_SpecularStrength", 0f);
                    material.SetFloat("_RippleStrength", 0f);
                    material.SetFloat("_WaveHeight", 0f);
                    flow = Difference(Render(material, 0f, outputRoot + "/Lake-Flow-A.png"), Render(material, 2.5f, outputRoot + "/Lake-Flow-B.png"));
                    if (flow < 0.0001) { throw new InvalidOperationException("关闭反光/波纹后湖水没有独立流动"); }
                    material.CopyPropertiesFromMaterial(source);
                    material.SetFloat("_BoardProtection", 0f);
                    material.SetFloat("_RippleStrength", 0f);
                    Color32[] noRipples = Render(material, 2.5f, null);
                    material.SetFloat("_RippleStrength", source.GetFloat("_RippleStrength"));
                    if (Difference(noRipples, Render(material, 2.5f, null)) < 0.0001) { throw new InvalidOperationException("涟漪强度未接入绘制"); }
                    Color32[] withSpecular = Render(material, 2.5f, null);
                    material.SetFloat("_SpecularStrength", 0f);
                    if (Difference(withSpecular, Render(material, 2.5f, null)) < 0.0001) { throw new InvalidOperationException("水面高光未接入绘制"); }
                }
                material.CopyPropertiesFromMaterial(source);
                material.SetFloat("_BoardProtection", 0f);
                material.SetFloat("_Transition", 1f);
                // 固定输入连续帧来自实际GPU输出，可做动画证据；这不是录制手机帧率或性能跑分。
                for (int frame = 0; frame < 24; frame++) { Render(material, frame * 0.15f, outputRoot + "/Frame-" + frame.ToString("D2") + ".png"); }
                // 复原临时材质再验棋盘保护；保护是画面局部衰减，不应该把整个背景关掉。
                material.CopyPropertiesFromMaterial(source);
                material.SetFloat("_Transition", 1f);
                material.SetFloat("_BoardProtection", source.GetFloat("_BoardProtection"));
                material.SetVector("_BoardRect", new Vector4(0.25f, 0.12f, 0.5f, 0.76f));
                Render(material, 2.5f, outputRoot + "/" + prefix + "-BoardProtected.png");
                string result = "通过：1920×1080实际GPU出图、0/2.5/5秒动态、强度0/速度0静止；时间像素差="
                    + movement.ToString("F6") + "，特效与原画差=" + effect.ToString("F6") + "，独立原画流动差=" + flow.ToString("F6");
                File.WriteAllText(resultPath, result);
                return result;
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        /// <summary>
        /// 使用Unity隔离预览场景绘制全屏四边形；UV是0~1，顶点色白色，和正式背景Sprite的Shader输入一致。
        /// 只给预览材质写时钟；不操作用户场景、主相机或全局RenderTexture引用。GPU读回只在编辑器验收时发生。
        /// </summary>
        private static Color32[] Render(Material material, float time, string file)
        {
            var preview = new PreviewRenderUtility();
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            Texture2D output = null;
            try
            {
                material.SetFloat("_PreviewTime", time);
                mesh.vertices = new[] { new Vector3(-8f / 9f, -0.5f, 0), new Vector3(8f / 9f, -0.5f, 0), new Vector3(-8f / 9f, 0.5f, 0), new Vector3(8f / 9f, 0.5f, 0) };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
                mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
                preview.camera.orthographic = true;
                preview.camera.orthographicSize = 0.5f;
                preview.camera.transform.position = new Vector3(0, 0, -2);
                preview.camera.transform.rotation = Quaternion.identity;
                preview.camera.nearClipPlane = 0.01f;
                preview.camera.farClipPlane = 10f;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = Color.magenta;
                preview.BeginStaticPreview(new Rect(0, 0, 1920, 1080));
                preview.DrawMesh(mesh, Matrix4x4.identity, material, 0);
                preview.Render();
                output = preview.EndStaticPreview();
                Color32[] pixels = output.GetPixels32();
                foreach (int index in new[] { 0, 1919, pixels.Length - 1920, pixels.Length - 1 })
                {
                    Color32 pixel = pixels[index];
                    if (pixel.r > 245 && pixel.b > 245 && pixel.g < 10) { throw new InvalidOperationException("预览未覆盖视口或Shader显示错误粉色"); }
                    if (pixel.r + pixel.g + pixel.b < 12) { throw new InvalidOperationException("四角采到了透明黑边，请检查原画有效UV"); }
                }
                if (file != null) { File.WriteAllBytes(file, output.EncodeToPNG()); }
                return pixels;
            }
            finally
            {
                if (output != null) { UnityEngine.Object.DestroyImmediate(output); }
                UnityEngine.Object.DestroyImmediate(mesh);
                preview.Cleanup();
            }
        }

        /// <summary>RGB平均绝对像素差，范围0~1；同一时间和输入应可重复，不将它解释为主观视觉评分。</summary>
        private static double Difference(Color32[] a, Color32[] b)
        {
            long sum = 0;
            for (int i = 0; i < a.Length; i++) { sum += Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b); }
            return sum / (a.Length * 3.0 * 255.0);
        }
    }
}
