using UnityEngine;

namespace RevGame.Data
{
    /// <summary>原画各图层使用同一画布坐标，按远景、人物与前景叠放。</summary>
    public sealed class YayaMemoryPageArt : ScriptableObject
    {
        public Texture2D Backdrop;
        public Texture2D Atmosphere;
        public Texture2D ContactShadow;
        public Texture2D Cast;
        public Texture2D Foreground;
    }
}
