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
        /// <summary>背景原画纹理的具名引用。第一章用图集时为空。</summary>
        private RevResLease _textureLease;
        /// <summary>背景材质的具名引用，在实际渲染者退出后归还。</summary>
        private RevResLease _materialLease;
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

        /// <summary>登记本次等待的首屏资源并取得引用。调用方先保存此对象，再等待以支持中途退出。</summary>
        public async RevTask LoadAsync(int chapter, string holder = nameof(ChapterBackgroundLease), RevSceneHandoffResources trace = null)
        {
            trace?.Require(RevResPath.Material_Background, _materialName, holder, blocksFirstFrame: true);
            _materialLease = RevResManager.LoadOwnedAsync(RevResPath.Material_Background, _materialName, typeof(Material), holder,
                group: RevResGroup.Scene);
            if (chapter == 1)
            {
                trace?.Require(RevResPath.Atlases, GameSpriteAtlasService.TutorialAtlasName, holder, blocksFirstFrame: true);
                _atlas = RevServices.GetRequired<GameSpriteAtlasService>().AcquireTutorialSprite();
                _tutorialPending = true;
                LoadTutorialSpriteAsync().Forget();
            }
            else
            {
                trace?.Require(GamePresentation.ChapterBackgroundRoot, Name, holder, blocksFirstFrame: true);
                _textureLease = RevResManager.LoadOwnedAsync(GamePresentation.ChapterBackgroundRoot, Name, typeof(Texture2D), holder,
                    group: RevResGroup.Scene);
            }

            // 加载预算使用现实时间。暂停战斗也要能报告超时，退出后的旧续体则直接返回。
            RevResHandle materialHandle = _materialLease.Handle;
            RevResHandle textureHandle = _textureLease?.Handle;
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!_released && (materialHandle.IsLoading || textureHandle?.IsLoading == true || _tutorialPending))
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

            if (textureHandle != null)
            {
                Texture = textureHandle.Content as Texture2D;
                if (!textureHandle.IsLoaded || Texture == null)
                {
                    throw new InvalidOperationException("背景图片加载失败：" + Name + " / " + textureHandle.ErrorReason);
                }
            }
            Material = materialHandle.Content as Material;
            if (!materialHandle.IsLoaded || Material == null)
            {
                throw new InvalidOperationException("章节独立材质加载失败：" + _materialName + " / " + materialHandle.ErrorReason);
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
            _textureLease?.Dispose();
            _textureLease = null;
            _materialLease?.Dispose();
            _materialLease = null;
            _atlas?.Dispose();
            _atlas = null;
        }
    }
}
