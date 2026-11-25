// 全屏剧情背景：小屋和围棋神殿可独立加载，章节原画优先借用当前场景。
// 只负责展示与等比铺满；关闭先解除UI引用，再归还本面板的资源。
using System;
using RevGame.Data;
using Revolution;
using UnityEngine;
using UnityEngine.Sprites;
using UnityEngine.UI;

namespace RevGame.View
{
    /// <summary>剧情面板的全屏 RawImage。持有当前独立背景的租约，战斗中优先借用已经交付的同章原画。</summary>
    [RequireComponent(typeof(RawImage))]
    public sealed class StoryBackdrop : MonoBehaviour
    {
        private RawImage _image;
        /// <summary>大厅或九路神殿共用的独立材质句柄，面板关闭时按当前编号归还。</summary>
        private RevResHandle _standaloneMaterial;
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
        /// <summary>当前背景编号，-1 为九路神殿，0 为大厅，正数为战役章节。</summary>
        public int Chapter
        {
            get;
            private set;
        }
        /// <summary>当前背景 Shader 的原画采样属性，准备与视觉验收都用此属性确认实际纹理。</summary>
        public string TextureProperty => Chapter == 1 || Chapter == StoryBook.PvpBackgroundChapter ? "_MainTex" : "_ChapterTex";

        /// <summary>选用正式大厅和九路棋盘已有材质的逻辑路径，独立背景无需创建场景对象。</summary>
        private static (string Root, string Name) StandaloneMaterial(int chapter)
        {
            return chapter == StoryBook.PvpBackgroundChapter
                ? ("Material/Pvp/Board", "PvpTemple")
                : ("Material/Lobby", "MagicCottageMaterial");
        }

        /// <summary>等待当前面板需要的背景，不打开其它章节；参数来自篇章配置。</summary>
        public async RevTask PrepareAsync(int chapter, bool preview)
        {
            Release();
            int generation = _generation;
            _image = GetComponent<RawImage>();
            Chapter = chapter;
            if (chapter <= 0)
            {
                // 与场景复用材质和原画。剧情可能在大厅开始，独立租约由这个面板归还。
                var (root, name) = StandaloneMaterial(chapter);
                _standaloneMaterial = RevResManager.LoadAsync(root, name, typeof(Material), null, RevResGroup.UI, lifetime: RevResLifetime.Manual);
                while (_standaloneMaterial != null && _standaloneMaterial.IsLoading)
                {
                    await RevTask.Yield();
                    if (generation != _generation)
                    {
                        return;
                    }
                }
                Material material = _standaloneMaterial?.Content as Material;
                if (material == null || !material.HasProperty(TextureProperty))
                {
                    throw new InvalidOperationException("剧情背景材质未就绪：" + name + " " + _standaloneMaterial?.ErrorReason);
                }
                Texture texture = material.GetTexture(TextureProperty);
                if (texture == null)
                {
                    throw new InvalidOperationException("剧情背景缺少原画：" + name);
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
            if (_standaloneMaterial != null)
            {
                var (root, name) = StandaloneMaterial(Chapter);
                RevResManager.Release(root, name, RevResLifetime.Manual);
                _standaloneMaterial = null;
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
