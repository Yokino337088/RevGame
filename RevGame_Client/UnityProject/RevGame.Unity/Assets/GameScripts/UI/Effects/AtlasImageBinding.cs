using UnityEngine;
using UnityEngine.U2D;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>
    /// 补齐AB图集晚到时的UGUI刷新：Sprite引用存在，不代表Canvas已经提交了正确纹理和UV。
    /// 每张Image只监听启用期间的图集注册事件；不在Update扫描场景，不复制Sprite或材质。
    /// 正常路径先预载图集再打开UI，本组件同时保护动态奖励图片等晚绑定路径。
    /// </summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Image))]
    public sealed class AtlasImageBinding : MonoBehaviour
    {
        /// <summary>当前控件，Awake缓存一次；不是跨场景持有的资源租约。</summary>
        private Image _image;
        private void Awake() { _image = GetComponent<Image>(); }
        private void OnEnable()
        {
            SpriteAtlasManager.atlasRegistered += OnAtlasRegistered;
            _image.SetAllDirty();
        }
        private void OnDisable() { SpriteAtlasManager.atlasRegistered -= OnAtlasRegistered; }

        /// <summary>只刷新能够绑定到这张图集的Image；SetAllDirty同时重算九宫格UV和上传主纹理。</summary>
        private void OnAtlasRegistered(SpriteAtlas atlas)
        {
            Sprite sprite = _image.overrideSprite != null ? _image.overrideSprite : _image.sprite;
            if (sprite != null && atlas.CanBindTo(sprite))
            {
                _image.SetAllDirty();
            }
        }
    }
}
