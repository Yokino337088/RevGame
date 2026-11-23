#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RevGame.Endless
{
    /// <summary>编辑器 Play 验收入口，使用独立组件和临时 Mesh，专测显示快照和顶点流。</summary>
    internal static class EndlessHordePresentationChecks
    {
        /// <summary>从空局核对位移、打断、换槽和回收，再检查 CPU 与 GPU 输入布局及中间帧上传。始终销毁测试网格。</summary>
        internal static object Run()
        {
            int passed = 0;
            // 单个断言通过后累计计数，让调用方能区分完整验证和中途异常。
            void Check(bool condition, string detail)
            {
                if (!condition)
                {
                    throw new InvalidOperationException(detail);
                }
                passed++;
            }

            var world = new EndlessHordeComponents(1);
            var run = new EndlessRun { phase = EndlessPhase.Fighting };
            var display = new EndlessHordePresentation(1, .1f);
            world[0] = new EndlessHorde { active = true, id = 1, x = 1, hp = 10, maxHp = 10 };
            Check(display.Capture(world, run, 0) && display.Position(0, display.Blend(0)).x == 1f, "新敌人直接对齐");
            Check(!display.Capture(world, run, .05), "同一逻辑样本不重复采集");

            // 逻辑以 10 Hz 提交。帧间坐标与已结算位置分别核对。
            var enemy = world[0];
            enemy.x = 2;
            world[0] = enemy;
            run.seconds = .1f;
            display.Capture(world, run, .1);
            Check(Mathf.Abs(display.Position(0, display.Blend(.15)).x - 1.5f) < .0001f && world[0].x == 2f, "表现取中间值且权威坐标不变");
            Check(display.Position(0, display.Blend(.5)).x == 2f, "没有后续样本时停在确认位置");
            enemy.hp = 5;
            world[0] = enemy;
            display.Capture(world, run, .15);
            Check(Mathf.Abs(display.Position(0, display.Blend(.16)).x - 1.6f) < .0001f && display.Read(0).hp == 5, "中途掉血不重启位置插值");

            // 下一步提前可见时，以同一播放时刻的姿态续上。
            float before = display.Position(0, display.Blend(.17)).x;
            enemy.x = 3;
            world[0] = enemy;
            run.seconds = .2f;
            display.Capture(world, run, .17);
            Check(display.Position(0, display.Blend(.17)).x == before, "背靠背逻辑更新时位置连续");
            enemy.x = 0;
            world[0] = enemy;
            world.MarkPositionCut(0);
            run.seconds = .3f;
            display.Capture(world, run, .3);
            Check(display.Position(0, display.Blend(.3)).x == 0f, "冲退跳位当步对齐");

            world[0] = new EndlessHorde { active = true, id = 2, x = 5 };
            display.Capture(world, run, .33);
            Check(display.Position(0, display.Blend(.33)).x == 5f, "复用槽位后对齐新敌人");
            run.phase = EndlessPhase.Paused;
            display.Capture(world, run, .35);
            Check(display.Position(0, display.Blend(.6)).x == 5f && display.Blend(.6) == 1f, "暂停时停止补间");
            world.Clear();
            display.Capture(world, run, .6);
            Check(!display.Read(0).active, "清盘后没有旧身份");

            var batch = new EndlessQuadBatch(1, "测试外围敌人顶点流", gpuCapable: true);
            try
            {
                batch.Write(0, Vector2.zero, new Vector2(4, 6), .5f, .5f, 0, new Vector4(.25f, 0, .75f, 1), new Color(0, 1, 0, .5f));
                batch.Submit(1, blend: 0, appearanceChanged: true);
                batch.Submit(1, blend: .5f);
                Check(batch.Mesh.vertices[0] == new Vector3(1.5f, 2.5f, 0) &&
                    batch.Mesh.uv[0] == new Vector2(.25f, 0) && batch.Mesh.colors[0].a == .5f, "只更新位置流仍保留 UV 和血条比例");
                batch.Submit(1, blend: 0, onGpu: true);
                // 直接解码传给顶点 Shader 的数据，核对中心与局部四角，保留原血量和图集采样。
                var endpoints = new List<Vector4>(4);
                batch.Mesh.GetUVs(1, endpoints);
                var origin = batch.Mesh.vertices[0];
                var endpoint = endpoints[0];
                Vector2 reconstructed = Vector2.Lerp(new Vector2(origin.x, origin.y), new Vector2(endpoint.x, endpoint.y), .5f)
                    + new Vector2(endpoint.z, endpoint.w);
                Check(reconstructed == new Vector2(1.5f, 2.5f) && batch.Mesh.colors[0].a == .5f
                    && batch.LastPositionBytes == 112, "GPU 端点布局复用外观并重建与 CPU 相同的中点四角");
                batch.Submit(1, blend: .5f, onGpu: true);
                Check(batch.LastPositionBytes == 0 && batch.LastAppearanceBytes == 0, "相同快照的 GPU 中间帧不上传顶点");
                batch.Submit(1, blend: .5f);
                Check(batch.Mesh.vertices[0] == new Vector3(1.5f, 2.5f, 0)
                    && batch.LastPositionBytes == EndlessQuadBatch.PositionBytesPerQuad, "退出 GPU 路径后按原布局立即重写坐标");
                batch.Submit(0);
                Check(batch.Mesh.GetIndexCount(0) == 0, "回收后索引范围立即清空");
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
