// 全屏剧情背景：大厅使用小屋材质，正式篇章借用场景原画，开发预览也可单独租用配置对应的章节资源。
// 只负责展示与等比铺满；关闭先解除UI引用，再归还本面板的资源。
using System;
using Revolution;
using UnityEngine;
using UnityEngine.Sprites;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>剧情面板的全屏 RawImage，最多持有一章独立背景；同章战斗中优先借用已经交付的原画。</summary>
    [RequireComponent(typeof(RawImage))]
    public sealed class StoryBackdrop : MonoBehaviour
    {
        /// <summary>背景UI和大厅资源句柄，0=大厅，正数=当前章；取消代次阻止迟到结果写到新故事。</summary>
        private RawImage _image;
        private RevResHandle _lobby;
        private ChapterBackgroundLease _chapterLease;
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
        public async RevTask PrepareAsync(int chapter, bool preview)
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
                Texture texture = material.GetTexture("_ChapterTex");
                if (texture == null)
                {
                    throw new InvalidOperationException("大厅剧情背景缺少实际纹理");
                }
                _image.material = material;
                _image.texture = texture;
                _image.uvRect = new Rect(0, 0, 1, 1);
                _sourceAspect = (float)texture.width / texture.height;
            }
            else
            {
                GamePresentation presentation = FindObjectOfType<GamePresentation>();
                SpriteRenderer renderer = presentation?.BackgroundRenderer;
                if (renderer != null && renderer.sprite != null && renderer.sharedMaterial != null && presentation.BackgroundChapter == chapter)
                {
                    BindChapter(renderer.sharedMaterial, renderer.sprite, renderer.sprite.texture, chapter);
                }
                else if (preview)
                {
                    var style = presentation == null ? GamePresentation.DefaultBackgroundStyle(chapter) : presentation.BackgroundStyle(chapter);
                    var lease = new ChapterBackgroundLease(chapter, style);
                    _chapterLease = lease;
                    await lease.LoadAsync(chapter);
                    if (generation != _generation)
                    {
                        return;
                    }
                    Texture texture = lease.Sprite == null ? lease.Texture : lease.Sprite.texture;
                    BindChapter(lease.Material, lease.Sprite, texture, chapter);
                }
                else
                {
                    throw new InvalidOperationException("剧情背景与当前章节未交付：" + chapter);
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

        /// <summary>把战斗 Shader 需要的采样数据交给 RawImage。图集需局部 UV，其余原画使用完整纹理。</summary>
        private void BindChapter(Material source, Sprite sprite, Texture texture, int chapter)
        {
            Vector4 uv = sprite == null ? new Vector4(0, 0, 1, 1) : DataUtility.GetOuterUV(sprite);
            _sourceAspect = sprite == null ? (float)texture.width / texture.height : sprite.rect.width / sprite.rect.height;
            _image.texture = texture;
            _image.uvRect = new Rect(uv.x, uv.y, uv.z - uv.x, uv.w - uv.y);
            _chapterMaterial = new Material(source);
            // SpriteRenderer 的属性块不会带到 UI。第二章起 Shader 从 _ChapterTex 采样，第一章从 _MainTex 采样图集。
            _chapterMaterial.SetTexture("_MainTex", texture);
            if (chapter == 1)
            {
                _chapterMaterial.SetVector("_ArtUvRect", new Vector4(uv.x, uv.y, uv.z - uv.x, uv.w - uv.y));
            }
            else
            {
                _chapterMaterial.SetTexture("_ChapterTex", texture);
                _chapterMaterial.SetFloat("_Aspect", _sourceAspect);
                _chapterMaterial.SetFloat("_Transition", 1);
                _chapterMaterial.SetFloat("_PreviewTime", -1);
            }
            if (_chapterMaterial.HasProperty("_BoardProtection"))
            {
                _chapterMaterial.SetFloat("_BoardProtection", 0);
            }
            if (_chapterMaterial.HasProperty("_EffectTime"))
            {
                _chapterMaterial.SetFloat("_EffectTime", -1);
            }
            _image.material = _chapterMaterial;
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
        /// <summary>面板回池先解除 RawImage 的借用，再归还独立租约。场景本身的引用仍由场景归还。</summary>
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
            _chapterLease?.Dispose();
            _chapterLease = null;
        }
        /// <summary>面板真正销毁时兜底释放，Release可重复调用不会多还引用。</summary>
        private void OnDestroy()
        {
            Release();
        }
    }
}
