// 全屏剧情背景租约：大厅使用独立小屋材质，章节直接借当前GamePresentation已加载的原画与Shader。
// 只负责展示与等比铺满，不切场景/加载全部章节；关闭先解除UI引用，再释放自己加载的大厅材质。
using System;
using RevGame.Flow;
using Revolution;
using UnityEngine;
using UnityEngine.Sprites;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>剧情面板拥有的单张RawImage背景；章节资源仍由场景持有，本组件只借用。</summary>
    [RequireComponent(typeof(RawImage))]
    public sealed class StoryBackdrop : MonoBehaviour
    {
        /// <summary>背景UI和大厅资源句柄，0=大厅，正数=当前章；取消代次阻止迟到结果写到新故事。</summary>
        private RawImage _image;
        private RevResHandle _lobby;
        private int _generation;
        /// <summary>章节背景单面板材质副本，只复制一次以承接SpriteRenderer参数；关闭销毁，不逐帧或逐棋子克隆。</summary>
        private Material _chapterMaterial;
        /// <summary>原图完整UV与宽高比，用于等比裁切铺满；窗口改变只调整UV，不重新加载资源。</summary>
        private Rect _sourceUv;
        private float _sourceAspect;
        public bool Ready
        {
            get;
            private set;
        }
        /// <summary>当前原画的来源章节，0=大厅，只读用于验收；不负责切换游戏章节。</summary>
        public int Chapter
        {
            get;
            private set;
        }
        /// <summary>等待当前面板需要的背景，不打开其它章节；参数来自篇章配置。</summary>
        public async RevTask PrepareAsync(int chapter)
        {
            Release();
            int generation = _generation;
            _image = GetComponent<RawImage>();
            Chapter = chapter;
            if (chapter == 0)
            {
                _lobby = RevResManager.LoadAsync("Lobby", "MagicCottageMaterial", typeof(Material), null, RevResGroup.UI);
                while (_lobby != null && _lobby.IsLoading)
                {
                    await RevTask.Yield();
                    if (generation != _generation)
                    {
                        return;
                    }
                }
                Material material = _lobby?.Content as Material;
                if (material == null)
                {
                    throw new InvalidOperationException("大厅剧情背景加载失败：" + _lobby?.ErrorReason);
                }
                _image.material = material;
                _image.texture = material.GetTexture("_ChapterTex");
                _image.uvRect = new Rect(0, 0, 1, 1);
                _sourceAspect = (float)_image.texture.width / _image.texture.height;
            }
            else
            {
                // 流程在打开剧情前已等本章背景就绪，直接借用能保持和战斗相同的原画/Shader，而非复制六份材质。
                GamePresentation presentation = FindObjectOfType<GamePresentation>();
                SpriteRenderer renderer = presentation?.BackgroundRenderer;
                if (renderer == null || renderer.sprite == null || renderer.sharedMaterial == null || presentation.BackgroundChapter != chapter)
                {
                    throw new InvalidOperationException("剧情背景与当前章节未交付：" + chapter);
                }
                _image.texture = renderer.sprite.texture;
                _chapterMaterial = new Material(renderer.sharedMaterial);
                // SpriteRenderer会通过绘制数据绑定主纹理，RawImage改为显式绑定，防止材质副本仍采样默认白图。
                _chapterMaterial.SetTexture("_MainTex", _image.texture);
                _image.material = _chapterMaterial;
                Vector4 uv = DataUtility.GetOuterUV(renderer.sprite);
                _image.uvRect = new Rect(uv.x, uv.y, uv.z - uv.x, uv.w - uv.y);
                _sourceAspect = renderer.sprite.rect.width / renderer.sprite.rect.height;
                if (_chapterMaterial.HasProperty("_ArtUvRect"))
                {
                    _chapterMaterial.SetVector("_ArtUvRect", new Vector4(uv.x, uv.y, uv.z - uv.x, uv.w - uv.y));
                }
                if (_chapterMaterial.HasProperty("_BoardProtection"))
                {
                    _chapterMaterial.SetFloat("_BoardProtection", 0);
                }
                if (_chapterMaterial.HasProperty("_EffectTime"))
                {
                    _chapterMaterial.SetFloat("_EffectTime", -1);
                }
            }
            if (_image.texture == null)
            {
                throw new InvalidOperationException("剧情背景缺少实际纹理");
            }
            _image.color = Color.white;
            _sourceUv = _image.uvRect;
            Ready = true;
            FitCrop();
        }
        /// <summary>等比cover裁切，背景铺满屏幕；不改变人物或对话框布局，不让小屋随屏幕比例变形。</summary>
        private void FitCrop()
        {
            if (!Ready || _image == null || _image.rectTransform.rect.height <= 0)
            {
                return;
            }
            float aspect = _image.rectTransform.rect.width / _image.rectTransform.rect.height;
            float width = aspect < _sourceAspect ? aspect / _sourceAspect : 1;
            float height = aspect > _sourceAspect ? _sourceAspect / aspect : 1;
            _image.uvRect = new Rect(_sourceUv.x + _sourceUv.width * (1 - width) * .5f, _sourceUv.y + _sourceUv.height * (1 - height) * .5f, _sourceUv.width * width, _sourceUv.height * height);
        }
        /// <summary>窗口大小改变时重新裁切现有纹理，不产生加载或材质副本。</summary>
        private void OnRectTransformDimensionsChange()
        {
            FitCrop();
        }
        /// <summary>面板回池解除借用引用；只有自己加载过的大厅材质才释放，不能替场景卸章节背景。</summary>
        public void Release()
        {
            _generation++;
            Ready = false;
            if (_image != null)
            {
                _image.texture = null;
                _image.material = null;
            }
            if (_lobby != null)
            {
                RevResManager.Release("Lobby", "MagicCottageMaterial");
                _lobby = null;
            }
            if (_chapterMaterial != null)
            {
                Destroy(_chapterMaterial);
                _chapterMaterial = null;
            }
        }
        /// <summary>面板真正销毁时兜底释放，Release可重复调用不会多还引用。</summary>
        private void OnDestroy()
        {
            Release();
        }
    }
}
