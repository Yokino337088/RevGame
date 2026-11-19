using System;
using System.Collections.Generic;
using RevGame.Data;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>一次剧情、对局或互动持有的立绘。每个图集只借一次，同名 Sprite 副本在收尾时销毁。</summary>
    public sealed class GamePortraitScope : IDisposable
    {
        private readonly Dictionary<string, GameSpriteAtlasService.ScopedAtlasLease> _atlases = new Dictionary<string, GameSpriteAtlasService.ScopedAtlasLease>(StringComparer.Ordinal);
        private readonly Dictionary<(string Atlas, string Sprite), Sprite> _sprites = new Dictionary<(string Atlas, string Sprite), Sprite>();
        private bool _disposed;

        public async RevTask WarmAtlasAsync(string name)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(GamePortraitScope));
            }
            if (!_atlases.TryGetValue(name, out var atlas))
            {
                atlas = RevServices.GetRequired<GameSpriteAtlasService>().AcquireScopedAtlas(name);
                _atlases.Add(name, atlas);
            }
            await atlas.WaitReadyAsync();
        }

        /// <summary>先确保所需图集就绪，再缓存一份可直接显示的 Sprite。关闭作用域会终止等待。</summary>
        public async RevTask<Sprite> WarmAsync(GamePortraitReference reference)
        {
            if (string.IsNullOrEmpty(reference.Atlas) || string.IsNullOrEmpty(reference.SpriteName))
            {
                throw new InvalidOperationException("立绘没有已配置的图集：" + reference.SpriteName);
            }
            await WarmAtlasAsync(reference.Atlas);
            if (_disposed)
            {
                throw new OperationCanceledException("立绘已经退出所属场景");
            }
            var key = (reference.Atlas, reference.SpriteName);
            if (!_sprites.TryGetValue(key, out var sprite))
            {
                sprite = _atlases[reference.Atlas].GetPackedSprite(reference.SpriteName);
                sprite.name = reference.SpriteName;
                _sprites.Add(key, sprite);
            }
            return sprite;
        }

        /// <summary>翻页与出招只读已交付的图片，避免在显示路径发起加载。</summary>
        public Sprite Get(GamePortraitReference reference)
        {
            return _sprites[(reference.Atlas, reference.SpriteName)];
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var sprite in _sprites.Values)
            {
                UnityEngine.Object.Destroy(sprite);
            }
            _sprites.Clear();
            foreach (var atlas in _atlases.Values)
            {
                atlas.Dispose();
            }
            _atlases.Clear();
        }
    }
}
