using System;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>一份章节背景的资源租约，由战斗场景或独立剧情面板持有。第一章走图集，其余章走原画纹理。</summary>
    internal sealed class ChapterBackgroundLease : IDisposable
    {
        private readonly string _materialName;
        private readonly string _shaderName;
        private RevResHandle _textureHandle;
        private RevResHandle _materialHandle;
        private GameSpriteAtlasService.TutorialSpriteLease _atlas;
        private bool _tutorialPending;
        private Exception _tutorialError;
        private bool _released;

        /// <summary>背景的逻辑图片名；第一章也是图集内 Sprite 名。</summary>
        public string Name
        {
            get;
        }
        public Texture2D Texture
        {
            get;
            private set;
        }
        public Sprite Sprite
        {
            get;
            private set;
        }
        public Material Material
        {
            get;
            private set;
        }

        public ChapterBackgroundLease(int chapter, GamePresentation.ChapterBackgroundStyle style)
        {
            if (style == null || string.IsNullOrWhiteSpace(style.textureName) || string.IsNullOrWhiteSpace(style.materialName) || string.IsNullOrWhiteSpace(style.shaderName))
            {
                throw new InvalidOperationException("章节背景的图片与材质逻辑名不能为空：" + chapter);
            }
            Name = style.textureName;
            _materialName = style.materialName;
            _shaderName = style.shaderName;
        }

        /// <summary>同一租约只加载一次。调用方先登记租约再等待，这样中途退场也能归还已发出的请求。</summary>
        public async RevTask LoadAsync(int chapter)
        {
            _materialHandle = RevResManager.LoadAsync(RevResPath.Material_Background, _materialName, typeof(Material), null, RevResGroup.Scene);
            if (chapter == 1)
            {
                _atlas = RevServices.GetRequired<GameSpriteAtlasService>().AcquireTutorialSprite();
                _tutorialPending = true;
                LoadTutorialSpriteAsync().Forget();
            }
            else
            {
                _textureHandle = RevResManager.LoadAsync(GamePresentation.ChapterBackgroundRoot, Name, typeof(Texture2D), null, RevResGroup.Scene);
            }

            // 加载预算使用现实时间。暂停战斗也要能报告超时，退出后的旧续体则直接返回。
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!_released && (_materialHandle.IsLoading || _textureHandle?.IsLoading == true || _tutorialPending))
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    throw new TimeoutException("章节背景加载超时：" + chapter);
                }
                await RevTask.Yield();
            }
            if (_released)
            {
                return;
            }
            if (_tutorialError != null || (chapter == 1 && Sprite == null))
            {
                throw new InvalidOperationException("第一章图集加载失败：" + Name, _tutorialError);
            }

            if (_textureHandle != null)
            {
                Texture = _textureHandle.Content as Texture2D;
                if (!_textureHandle.IsLoaded || Texture == null)
                {
                    throw new InvalidOperationException("背景图片加载失败：" + Name + " / " + _textureHandle.ErrorReason);
                }
            }
            Material = _materialHandle.Content as Material;
            if (!_materialHandle.IsLoaded || Material == null)
            {
                throw new InvalidOperationException("章节独立材质加载失败：" + _materialName + " / " + _materialHandle.ErrorReason);
            }
            if (Material.shader == null || !Material.shader.isSupported || Material.shader.name != _shaderName)
            {
                throw new InvalidOperationException("章节独立 Shader 未就绪、与材质不匹配或不支持当前平台：" + _materialName);
            }
        }

        private async RevTask LoadTutorialSpriteAsync()
        {
            try
            {
                Sprite sprite = await _atlas.LoadAsync(Name);
                if (_released)
                {
                    if (sprite != null)
                    {
                        UnityEngine.Object.Destroy(sprite);
                    }
                    return;
                }
                Sprite = sprite;
            }
            catch (Exception error)
            {
                _tutorialError = error;
            }
            finally
            {
                _tutorialPending = false;
            }
        }

        /// <summary>战斗 SpriteRenderer 需要完整矩形 Sprite。剧情 RawImage 可直接采样原画，只有第一章需要读取图集 Sprite。</summary>
        public Sprite GetRendererSprite()
        {
            if (Sprite == null)
            {
                Sprite = UnityEngine.Sprite.Create(Texture, new Rect(0, 0, Texture.width, Texture.height), new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect);
                Sprite.name = Name;
            }
            return Sprite;
        }

        /// <summary>幂等归还。调用方先清除可见对象引用，取消后晚到的图集 Sprite 由原请求销毁。</summary>
        public void Dispose()
        {
            if (_released)
            {
                return;
            }
            _released = true;
            if (Sprite != null)
            {
                UnityEngine.Object.Destroy(Sprite);
            }
            Sprite = null;
            Texture = null;
            Material = null;
            if (_textureHandle != null)
            {
                RevResManager.Release(GamePresentation.ChapterBackgroundRoot, Name);
                _textureHandle = null;
            }
            if (_materialHandle != null)
            {
                RevResManager.Release(RevResPath.Material_Background, _materialName);
                _materialHandle = null;
            }
            _atlas?.Dispose();
            _atlas = null;
        }
    }
}
