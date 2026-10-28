// 使用正式PC包中的PieceView/格子/材质/预制体离屏绘制，不切换用户场景、不启动Play。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DG.Tweening;
using RevGame.Core;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>所有包、图集测试克隆、预览场景归夹具所有，finally完整释放；不冒充正式Play首次自动绑定测试。</summary>
    public static class GameBossVictoryPreview
    {
        private const string Folder = "Temp/BossVictoryChecks";
        private static readonly BindingFlags Private = BindingFlags.Instance|BindingFlags.NonPublic;
        public static string Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("离屏验证需要编辑态"); }
            foreach (AssetBundle existing in AssetBundle.GetAllLoadedAssetBundles()) { throw new InvalidOperationException("其它预览仍持有包："+existing.name); }
            Directory.CreateDirectory(Folder);
            var bundles = new Dictionary<string,AssetBundle>();
            var atlases = new List<SpriteAtlas>();
            var sprites = new Dictionary<string,Sprite>();
            var configs = new List<BossChapterArtConfig>();
            Scene scene = EditorSceneManager.NewPreviewScene();
            RenderTexture previous = RenderTexture.active, target = null;
            Texture2D readback = null;
            int checks = 0;
            Action<bool,string> require = (ok,message) => { if (!ok) { throw new InvalidOperationException(message); } checks++; };
            try
            {
                AssetBundle main = Load("PC",bundles);
                AssetBundleManifest manifest = main.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                require(manifest!=null,"真实PC主清单存在");
                foreach (string root in new[]{"battle_units","battle_board","ui_victory"})
                {
                    foreach (string dependency in manifest.GetAllDependencies(root)) { Load(dependency,bundles); }
                    Load(root,bundles);
                }
                foreach (AssetBundle bundle in bundles.Values) { atlases.AddRange(bundle.LoadAllAssets<SpriteAtlas>()); }
                GameObject prefab = bundles["battle_units"].LoadAsset<GameObject>("assets/gameres/pieces/unit/slimepiece.prefab");
                require(prefab!=null,"正式PieceView预制体存在");
                GameObject instance = UnityEngine.Object.Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(instance,scene);
                PieceView view = instance.GetComponent<PieceView>();
                typeof(PieceView).GetMethod("Awake",Private).Invoke(view,null);
                SpriteRenderer body = instance.transform.Find("Body").GetComponent<SpriteRenderer>();
                var cameraObject = new GameObject("Boss演出离屏相机",typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject,scene);
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.enabled=false; camera.orthographic=true; camera.orthographicSize=1.3f;
                camera.transform.position=new Vector3(0,0,-10); camera.nearClipPlane=.1f; camera.farClipPlane=100;
                camera.scene=scene; camera.backgroundColor=new Color(.075f,.12f,.17f); camera.clearFlags=CameraClearFlags.SolidColor;
                target = new RenderTexture(960,540,24,RenderTextureFormat.ARGB32); target.Create(); camera.targetTexture=target;
                readback = new Texture2D(960,540,TextureFormat.RGBA32,false);
                // 演出只借各章的独立包，检查不存在对其它章Boss包的依赖。
                for (int chapter=1;chapter<=6;chapter++)
                {
                    string bundleName="atlas_"+BossChapterArtConfig.AtlasName(chapter).ToLowerInvariant();
                    foreach (string dependency in manifest.GetAllDependencies(bundleName))
                    { require(!dependency.StartsWith("atlas_bosschapter",StringComparison.Ordinal),"单章Boss不能加载其它Boss章节"); Load(dependency,bundles); }
                    AssetBundle bundle=Load(bundleName,bundles);
                    SpriteAtlas atlas=bundle.LoadAsset<SpriteAtlas>(("assets/gameres/atlases/"+BossChapterArtConfig.AtlasName(chapter)+".spriteatlasv2").ToLowerInvariant());
                    BossChapterArtConfig asset=bundle.LoadAsset<BossChapterArtConfig>(("assets/gameres/config/boss/"+BossChapterArtConfig.ResourceName(chapter)+".asset").ToLowerInvariant());
                    require(atlas!=null&&atlas.spriteCount==8&&asset!=null,"本章图集/配置存在");
                    require(asset.DeathMaterial!=null&&asset.DeathAuraMaterial!=null&&asset.DeathMaterial.shader.isSupported,"本章真实死亡Shader可用");
                    BossChapterArtConfig config=UnityEngine.Object.Instantiate(asset); configs.Add(config);
                    config.Frames=new Sprite[8];
                    for (int frame=0;frame<8;frame++)
                    {
                        string key=asset.Frames[frame].name;
                        Sprite sprite=atlas.GetSprite(key);
                        require(sprite!=null&&sprite.packed&&sprite.texture!=null,"真实Boss图集像素存在");
                        sprites.Add(key,sprite); config.Frames[frame]=sprite;
                    }
                    view.OnPoolReturn(); view.Bind(chapter,PieceKind.HeavySword,StarTier.Three,GameSide.Enemy);
                    view.BindBossArt(config); view.SetBodySize(2,true,true);
                    foreach (SpriteRenderer renderer in instance.GetComponentsInChildren<SpriteRenderer>(true)) { renderer.enabled=renderer==body; }
                    Material resting=body.sharedMaterial;
                    Color32[] beforeHold = Capture(camera,target,readback,"Chapter"+chapter.ToString("D2")+"-BeforeHold.png");
                    view.HoldBossDefeat();
                    Color32[] afterHold = Capture(camera,target,readback,"Chapter"+chapter.ToString("D2")+"-AfterHold.png");
                    require(Difference(beforeHold,afterHold)<100,"主将保留前后贴图颜色不改变，不出现白色剪影");
                    view.BeginBossDeath();
                    Color32[] start=null,end=null; bool changed=false;
                    string prefix="Chapter"+chapter.ToString("D2");
                    for (int frame=0;frame<=20;frame++)
                    {
                        view.SetBossDeathProgress(frame/20f);
                        Color32[] pixels=Capture(camera,target,readback,prefix+"-Death-"+frame.ToString("D2")+".png");
                        if(frame==0){start=pixels;}
                        else if(Difference(start,pixels)>300){changed=true;}
                        if(frame==20){end=pixels;}
                    }
                    require(changed,"死亡GPU像素动态变化");
                    require(NonBackground(start,camera.backgroundColor)>1000,"死亡起始可见真实Boss");
                    require(NonBackground(end,camera.backgroundColor)<10,"死亡末帧身体/光晕完全消失");
                    view.OnPoolReturn();
                    require(!view.HasBossDeathVisual&&body.sharedMaterial==resting&&body.sprite==null,"死亡回池清帧/恢复共享材质");
                    view.Bind(chapter,PieceKind.HeavySword,StarTier.Three,GameSide.Enemy);view.BindBossArt(config);view.SetBodySize(2,true,true);
                    foreach(SpriteRenderer renderer in instance.GetComponentsInChildren<SpriteRenderer>(true)){renderer.enabled=renderer==body;}
                    view.BeginBossBirth();
                    for(int frame=0;frame<=20;frame++)
                    {
                        view.SetBossDeathProgress(1-frame/20f);
                        Color32[] pixels=Capture(camera,target,readback,prefix+"-Birth-"+frame.ToString("D2")+".png");
                        if(frame==0){require(NonBackground(pixels,camera.backgroundColor)<10,"出生从透明开始");}
                        if(frame==20){require(NonBackground(pixels,camera.backgroundColor)>1000,"逆向出生还原Boss");}
                    }
                    view.FinishBossBirth();
                    require(!view.HasBossDeathVisual&&view.IsKingVisual&&body.sharedMaterial==resting&&view.AnimationFrameCount==8,"出生恢复活棋/动画/主将标记");
                    Color32[] restored = Capture(camera,target,readback,prefix+"-BirthRestored.png");
                    require(Difference(beforeHold,restored)<100,"出生交还原材质后仍是原图，无白块");
                    view.BeginBossBirth(); view.SetBossDeathProgress(.45f);
                    var bodyBeforeRefresh = new MaterialPropertyBlock(); body.GetPropertyBlock(bodyBeforeRefresh);
                    view.SetEyeGarrison(true); view.StopTransientVisuals();
                    var bodyAfterRefresh = new MaterialPropertyBlock(); body.GetPropertyBlock(bodyAfterRefresh);
                    require(bodyBeforeRefresh.GetTexture("_MainTex") == bodyAfterRefresh.GetTexture("_MainTex")
                        && Mathf.Abs(bodyAfterRefresh.GetFloat("_Progress")-.45f)<.0001f,"旧驻眼/阶段刷新不覆盖Boss贴图与进度");
                    view.FinishBossBirth();
                    view.BeginBossBirth();view.SetBossDeathProgress(.45f);view.FinishBossBirth();
                    require(!view.HasBossDeathVisual&&body.sharedMaterial==resting,"出生中断恢复材质");
                    view.OnPoolReturn();
                }
                instance.SetActive(false);
                // 用正式81格和BoardView算法：仅夹具把名单装配到未启用的宿主，不触发服务/池开局。
                GameObject cellPrefab=bundles["battle_board"].LoadAsset<GameObject>("assets/gameres/board/tile/boardcell.prefab");
                require(cellPrefab!=null,"真实棋盘格预制体存在");
                var host=new GameObject("隔离波浪宿主");host.SetActive(false);SceneManager.MoveGameObjectToScene(host,scene);
                BoardView board=host.AddComponent<BoardView>();
                var cells=(List<BoardCellView>)typeof(BoardView).GetField("_cells",Private).GetValue(board);
                var positions=new List<Vector3>();
                for(int y=0;y<9;y++)
                {
                    for(int x=0;x<9;x++)
                    {
                        GameObject obj=UnityEngine.Object.Instantiate(cellPrefab);SceneManager.MoveGameObjectToScene(obj,scene);
                        BoardCellView cell=obj.GetComponent<BoardCellView>();cell.Bind(new BoardPos(x,y),false,null,null);
                        foreach(SpriteRenderer renderer in obj.GetComponentsInChildren<SpriteRenderer>(true))
                        {
                            if(renderer.sprite!=null){renderer.sprite=Packed(renderer.sprite,atlases,sprites);}
                            renderer.enabled=renderer.transform==obj.transform;
                        }
                        cells.Add(cell);positions.Add(cell.transform.position);
                    }
                }
                camera.orthographicSize=5.7f;camera.transform.position=new Vector3(0,0,-10);
                bool jumped=false;
                for(int frame=0;frame<=20;frame++)
                {
                    board.SetVictoryBoardWave(frame/20f,Vector3.zero);
                    foreach(BoardCellView cell in cells)
                    {Transform surface=cell.transform.Find("VictorySurface");if(surface!=null&&surface.localPosition.y>.05f){jumped=true;}}
                    Capture(camera,target,readback,"Board-Wave-"+frame.ToString("D2")+".png");
                }
                require(jumped,"81格真实渲染层有跳跃");
                for(int frame=0;frame<=20;frame++){board.SetBossIntroDance(frame/20f);Capture(camera,target,readback,"Board-Dance-"+frame.ToString("D2")+".png");}
                board.ClearVictoryBoardWave();
                for(int index=0;index<cells.Count;index++)
                {
                    require(cells[index].transform.position==positions[index],"格子根节点/碰撞位置不动");
                    Transform surface=cells[index].transform.Find("VictorySurface");
                    require(surface==null||surface.localPosition==Vector3.zero,"波浪和舞动结束归位");
                    cells[index].OnPoolReturn();cells[index].gameObject.SetActive(false);
                }
                cells.Clear();
                // 真实UI预制体/绑定/数据落屏；手动推进本实例的DOTween，不更新全局Tween。
                var canvasObject=new GameObject("Boss弹幕离屏画布",typeof(RectTransform),typeof(Canvas));SceneManager.MoveGameObjectToScene(canvasObject,scene);
                Canvas canvas=canvasObject.GetComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;canvas.worldCamera=camera;
                ((RectTransform)canvas.transform).sizeDelta=new Vector2(1920,1080);
                camera.orthographicSize=540;camera.transform.position=new Vector3(0,0,-100);camera.farClipPlane=500;
                foreach(string panelName in new[]{"BossVictoryPanel","BossIntroPanel"})
                {
                    GameObject panelPrefab=bundles["ui_victory"].LoadAsset<GameObject>(("assets/gameres/ui/panel/victory/"+panelName+".prefab").ToLowerInvariant());
                    require(panelPrefab!=null,"AB弹幕预制体存在");
                    GameObject obj=UnityEngine.Object.Instantiate(panelPrefab,canvas.transform,false);
                    Image bannerImage = obj.transform.Find("banner").GetComponent<Image>();
                    require(bannerImage.sprite != null && bannerImage.sprite.name == "UI_Tip_imgBK"
                        && bannerImage.material != null && bannerImage.material.name == "ArcanePanel", "实际AB弹幕使用HUD魔法提示图片和共享材质");
                    require(bannerImage.GetComponent<ArcaneImageCoordinates>() != null && bannerImage.GetComponent<AtlasImageBinding>() != null,
                        "实际AB弹幕拥有UV1与图集刷新组件");
                    // 原始资源名在测试克隆前检查；Atlas.GetSprite返回名带(Clone)，不能拿它冒充资源名错误。
                    foreach(Image image in obj.GetComponentsInChildren<Image>(true)){if(image.sprite!=null){image.sprite=Packed(image.sprite,atlases,sprites);image.SetAllDirty();}}
                    RevUIPanel panel=obj.GetComponent<RevUIPanel>();
                    // 编辑态不启动UI管理器；仅按公开绑定计划给这个固定面板装配真实控件，不调用框架内部Binder。
                    RevUIBindPlan plan = RevUIBindPlan.Get(panel.GetType(),typeof(RevUIPanel));
                    foreach (RevUIBindEntry binding in plan.Fields)
                    {
                        Transform node = obj.transform.Find("banner/" + binding.NodeName);
                        if (binding.NodeName == "banner") { node = obj.transform.Find("banner"); }
                        require(node != null,"弹幕绑定节点存在："+binding.NodeName);
                        binding.Field.SetValue(panel,node.GetComponent(binding.FieldType));
                    }
                    panel.SetData(new BossDefeatedInfo(3,15,1,new BoardPos(4,4),2,123456,false));panel.RefreshView();
                    Text headline=obj.transform.Find("banner/headline").GetComponent<Text>();
                    require(headline.text.Contains(panelName=="BossIntroPanel"?"已出现":"已击败"),"真实Panel数据落屏");
                    if(panel is BossVictoryPanel){require(obj.transform.Find("banner/chapterScore").GetComponent<Text>().text.Contains("123,456"),"总积分展示正确");}
                    bool done=false;
                    panel.GetType().GetMethod("PlayOpenTransition",Private).Invoke(panel,new object[]{(Action)(()=>done=true)});
                    Tween animation=(Tween)panel.GetType().GetField("_animation",Private).GetValue(panel);
                    animation.Pause();
                    for(int frame=0;frame<=15;frame++)
                    {
                        animation.Goto(frame/15f*.8f,false);
                        if(panel is BossIntroPanel intro){intro.SetFlashProgress(frame/15f);}
                        Canvas.ForceUpdateCanvases();Capture(camera,target,readback,panelName+"-"+frame.ToString("D2")+".png");
                    }
                    animation.Complete();require(done,"弹幕打开动画执行完成回调");
                    animation.Kill(false);UnityEngine.Object.DestroyImmediate(obj);
                }
                File.WriteAllText(Folder+"/result.txt",checks+"条真实PC AB/GPU断言通过；六章死亡/逆向出生/回池、81格波浪/舞动/碰撞归位、两种弹幕绑定/动画/积分。未运行正式Play或真机。");
                return checks+"条真实PC AB/GPU断言通过；"+Path.GetFullPath(Folder);
            }
            finally
            {
                RenderTexture.active=previous;EditorSceneManager.ClosePreviewScene(scene);
                foreach(BossChapterArtConfig config in configs){UnityEngine.Object.DestroyImmediate(config);}
                foreach(Sprite sprite in sprites.Values){UnityEngine.Object.DestroyImmediate(sprite);}
                if(readback!=null){UnityEngine.Object.DestroyImmediate(readback);}
                if(target!=null){target.Release();UnityEngine.Object.DestroyImmediate(target);}
                var owned=new List<AssetBundle>(bundles.Values);for(int index=owned.Count-1;index>=0;index--){owned[index].Unload(true);}
            }
        }
        private static AssetBundle Load(string name,Dictionary<string,AssetBundle> owned)
        {
            if(owned.TryGetValue(name,out AssetBundle existing)){return existing;}
            AssetBundle bundle=AssetBundle.LoadFromFile("Assets/StreamingAssets/PC/"+name);
            if(bundle==null){throw new InvalidOperationException("真实包加载失败："+name);}
            owned.Add(name,bundle);return bundle;
        }
        private static Sprite Packed(Sprite source,List<SpriteAtlas> atlases,Dictionary<string,Sprite> sprites)
        {
            if(sprites.TryGetValue(source.name,out Sprite cached)){return cached;}
            foreach(SpriteAtlas atlas in atlases)
            {Sprite sprite=atlas.GetSprite(source.name);if(sprite!=null){sprites.Add(source.name,sprite);return sprite;}}
            throw new InvalidOperationException("图集缺少Sprite："+source.name);
        }
        private static Color32[] Capture(Camera camera,RenderTexture target,Texture2D readback,string name)
        {
            camera.Render();RenderTexture.active=target;readback.ReadPixels(new Rect(0,0,target.width,target.height),0,0);readback.Apply(false,false);
            File.WriteAllBytes(Folder+"/"+name,readback.EncodeToPNG());return readback.GetPixels32();
        }
        private static int Difference(Color32[] a,Color32[] b)
        {int count=0;for(int i=0;i<a.Length;i++){if(Math.Abs(a[i].r-b[i].r)+Math.Abs(a[i].g-b[i].g)+Math.Abs(a[i].b-b[i].b)>12){count++;}}return count;}
        private static int NonBackground(Color32[] pixels,Color background)
        {
            // 实际背景像素读第一格；色空间转换不能用CPU线性色硬猜GPU背景值。
            Color32 sample=pixels[0];int count=0;
            foreach(Color32 pixel in pixels){if(Math.Abs(pixel.r-sample.r)+Math.Abs(pixel.g-sample.g)+Math.Abs(pixel.b-sample.b)>10){count++;}}
            return count;
        }
    }
}
