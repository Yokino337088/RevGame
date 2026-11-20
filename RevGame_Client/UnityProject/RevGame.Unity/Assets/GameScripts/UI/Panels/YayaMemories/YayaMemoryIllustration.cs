using System;
using RevGame.Data;
using Revolution;
using UnityEngine;

namespace RevGame.UI
{
    /// <summary>单章原画租约。相邻章可先加载，只有当前页和正在翻到的一页才分配合成图。</summary>
    internal sealed class YayaMemoryIllustration : IDisposable
    {
        internal const string Root = "YayaMemories/";
        internal const string AssetName = "Illustration";
        private readonly int _chapter;
        private readonly string _root;
        private readonly float _deadline;
        private RevResHandle _handle;
        private RenderTexture _image;
        private float _nextDraw;

        public YayaMemoryIllustration(int chapter, bool unlocked)
        {
            _chapter = chapter;
            _root = Root + "Chapter" + chapter.ToString("D2") + "/";
            Unlocked = unlocked;
            if (unlocked)
            {
                // 插画跟随这一页的临时持有，翻页时提前归还。
                _handle = RevResManager.LoadAsync(_root, AssetName, typeof(YayaMemoryPageArt), null, RevResGroup.UI, lifetime: RevResLifetime.Manual);
                _deadline = Time.realtimeSinceStartup + 10f;
            }
        }

        public bool Unlocked { get; }
        public bool Waiting => _handle?.IsLoading == true && Time.realtimeSinceStartup < _deadline;
        public Texture Image => _image;

        /// <summary>保持同一画幅、同一人物坐标。只有环境和各特效图层由 Shader 取样移动。</summary>
        public Texture Draw(Material compositor, float now)
        {
            if (_handle?.Content is not YayaMemoryPageArt art || art.Backdrop == null || art.Cast == null)
            {
                return null;
            }
            if (_image != null && now < _nextDraw)
            {
                return _image;
            }
            if (_image == null)
            {
                int width = Application.isMobilePlatform ? 960 : 1440;
                _image = new RenderTexture(width, width * 9 / 16, 0, RenderTextureFormat.ARGB32)
                {
                    name = "Yaya memories " + _chapter, filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
            }
            _nextDraw = now + 1f / 30f;
            compositor.SetTexture("_Atmosphere", art.Atmosphere);
            compositor.SetTexture("_Contact", art.ContactShadow);
            compositor.SetTexture("_Cast", art.Cast);
            compositor.SetTexture("_Foreground", art.Foreground);
            compositor.SetVector("_Layers", new Vector4(art.Atmosphere != null ? 1f : 0f,
                art.ContactShadow != null ? 1f : 0f, art.Foreground != null ? 1f : 0f, 0f));
            compositor.SetFloat("_Chapter", _chapter);
            compositor.SetFloat("_EffectClock", now);
            Graphics.Blit(art.Backdrop, _image, compositor, 0);
            // 共享合成材质不留上一次的资源引用，离页时租约即可独立回收。
            compositor.SetTexture("_Atmosphere", null);
            compositor.SetTexture("_Contact", null);
            compositor.SetTexture("_Cast", null);
            compositor.SetTexture("_Foreground", null);
            return _image;
        }

        public void ReleaseImage()
        {
            if (_image != null)
            {
                _image.Release();
                UnityEngine.Object.Destroy(_image);
                _image = null;
            }
        }

        public void Dispose()
        {
            ReleaseImage();
            if (_handle != null)
            {
                RevResManager.Release(_root, AssetName, RevResLifetime.Manual);
                _handle = null;
            }
        }
    }
}
