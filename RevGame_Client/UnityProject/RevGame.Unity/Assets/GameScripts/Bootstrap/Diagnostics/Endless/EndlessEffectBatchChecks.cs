#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RevGame.Endless
{
    /// <summary>通过真实 Unity 网格验证特效通道布局和索引范围，防止复用后读到旧画面。</summary>
    internal static class EndlessEffectBatchChecks
    {
        /// <summary>验证从空网格到部分更新、缩短和重开的完整顺序，结束时销毁临时网格。</summary>
        internal static object Run()
        {
            int passed = 0;
            // 失败中止并给出对应的网格契约，成功时返回断言数。
            void Check(bool condition, string detail)
            {
                if (!condition)
                {
                    throw new InvalidOperationException(detail);
                }
                passed++;
            }

            var batch = new EndlessEffectBatch(3, "测试无尽局部效果网格");
            try
            {
                Mesh mesh = batch.Mesh;
                int quadBytes = EndlessEffectBatch.DynamicBytesPerQuad;
                Check(mesh.GetVertexBufferStride(0) == 36 && mesh.GetVertexBufferStride(1) == 8
                    && mesh.GetVertexAttributeStream(VertexAttribute.TexCoord0) == 1
                    && mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord1) == 28, "着色器主纹理坐标与效果参数分别取正确的流");
                Check(mesh.GetIndexCount(0) == 0 && batch.LastUploadBytes == 0, "新网格预留容量但不画任何方块");

                var tint = new Color(.33f, .75f, .84f, .448f);
                var parameters = new Vector2(.125f, 4);
                var second = new Vector2(2, 3);
                batch.WriteSquare(0, Vector2.zero, .5f, -.4f, tint, parameters);
                batch.WriteSquare(1, second, .5f, -.4f, Color.white, Vector2.one);
                batch.Submit(2);
                var indices = mesh.GetIndices(0);
                Check(indices.Length == 12 && indices[0] == 0 && indices[5] == 3 && indices[6] == 4 && indices[11] == 7
                    && batch.LastUploadBytes == 2 * quadBytes, "首帧只上传两个方块，沿用原三角形绕序");
                Check(mesh.vertices[0].Equals(new Vector3(-.5f, -.5f, -.4f)) && mesh.colors[3].Equals(tint)
                    && mesh.uv[2].Equals(Vector2.one) && mesh.uv2[3].Equals(parameters), "读取真实网格可还原位置、浮点颜色、主纹理坐标和效果参数");

                batch.WriteSquare(0, Vector2.zero, .5f, -.4f, tint, parameters);
                batch.WriteSquare(1, second, .5f, -.4f, Color.white, Vector2.one);
                batch.Submit(2);
                Check(batch.LastUploadBytes == 0 && mesh.GetIndexCount(0) == 12, "画面输入相同则留用上一帧的网格");
                const float faintChange = .0000001f;
                second.x += faintChange * 4;
                batch.WriteSquare(0, Vector2.zero, .5f, -.4f, tint, parameters);
                batch.WriteSquare(1, second, .5f, -.4f, Color.white, Vector2.one);
                batch.Submit(2);
                Check(batch.LastUploadBytes == quadBytes && mesh.vertices[4].x == second.x - .5f
                    && mesh.colors[0].Equals(tint) && mesh.uv2[0].Equals(parameters), "细小移动只写对应槽，前一个方块的输入保持正确");

                batch.Submit(1);
                Check(mesh.GetIndexCount(0) == 6 && batch.LastUploadBytes == 0, "减少方块只缩短索引范围");
                batch.WriteSquare(1, second, .5f, -.4f, Color.white, Vector2.one);
                batch.Submit(2);
                Check(mesh.GetIndexCount(0) == 12 && batch.LastUploadBytes == quadBytes, "原值回到活动槽时重新交给网格");
                batch.Submit(0);
                Check(mesh.GetIndexCount(0) == 0 && batch.LastUploadBytes == 0, "最后一道特效结束后立即撤空");
                batch.WriteSquare(0, Vector2.one, .5f, -.4f, Color.red, Vector2.right);
                batch.Submit(1);
                Check(mesh.GetIndexCount(0) == 6 && batch.LastUploadBytes == quadBytes
                    && mesh.colors[0].Equals(Color.red) && mesh.uv2[0].Equals(Vector2.right), "复用第一槽时画新特效，没有旧局的颜色或参数");
            }
            finally
            {
                UnityEngine.Object.Destroy(batch.Mesh);
            }
            return new Dictionary<string, object> { ["success"] = true, ["passed"] = passed };
        }
    }
}
#endif
