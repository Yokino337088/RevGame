// ============================================================
// RevPooledMember.cs —— 池实例身上的"身份证"组件
//
// 位置：Runtime\ObjectPool\Core\
//
// 【它解决两件事】
//   ① 归还时不用查全局表：拿到对象就能知道它属于哪个池（读自己的 PoolId）。
//      王者用 CPooledGameObjectScript 干这件事，这里同理。
//      （另一条路是维护"实例 ID → 池"的全局字典，代价是外部销毁时会留脏记录，
//        而且每个归还都要查一次字典。）
//   ② 替业务缓存 IRevPoolable 组件：取出/归还时自动回调 OnPoolGet / OnPoolReturn，
//      让**预制体自己的脚本**能重置动画、粒子、计时器等状态 ——
//      不必为了池化去改预制体结构，也不用在业务代码里到处写"取出后手动初始化"。
//
// 【为什么 PoolId 不序列化】
//   如果它被序列化，一旦有人把池里的实例另存成预制体，实例就会带着**过期的 PoolId**，
//   归还时可能被送到别的池里去（那个池会把它当自己 prefab 的实例发出去 —— 灾难）。
//   不序列化 → 复制出来的东西 PoolId 为 0 → 归还时被明确拒绝并报错，安全。
//
// 【组件查找只在"第一次绑定"时做一次】
//   GetComponents 是有开销的（要分配数组）。放进池的对象只创建一次、复用无数次，
//   所以把这次开销摊在创建时，取出/归还路径上是零查找、零分配。
// ============================================================
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>池实例的标记组件（由对象池自动添加，业务不要自己加）。</summary>
    [DisallowMultipleComponent]
    public sealed class RevPooledMember : MonoBehaviour
    {
        private static readonly IRevPoolable[] Empty = new IRevPoolable[0];

        private int _poolId;
        private IRevPoolable[] _poolables;

        /// <summary>所属池的编号（0 = 不属于任何池）。</summary>
        public int PoolId => _poolId;

        /// <summary>绑定到某个池；顺便把实例上的 IRevPoolable 组件缓存起来。</summary>
        internal void Bind(int poolId)
        {
            _poolId = poolId;
            CachePoolables();
        }

        internal void Unbind(int poolId)
        {
            // 池重绑或销毁时，Unity 的 Destroy 可能要等到帧末才真正删除对象；这段时间对象仍可能被业务代码传回 Return。
            // 先把旧池编号清零，Return 就会拒绝把这个旧 prefab 对象塞进新池；poolId 校验也避免误清已绑定到别的池的编号。
            if (_poolId == poolId) _poolId = 0;
        }

        /// <summary>取出时回调（由池调用）。</summary>
        internal void NotifyPoolGet()
        {
            IRevPoolable[] list = _poolables;
            if (list == null) return;

            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    list[i].OnPoolGet();
                }
                catch (System.Exception e)
                {
                    // 隔离：一个组件出错不该影响这个对象的其它组件，也不该让池卡住
                    RevPoolLog.Error($"OnPoolGet 抛异常（已隔离）：{list[i].GetType().Name} - {e}");
                }
            }
        }

        /// <summary>归还时回调（由池调用，在对象失活之前）。</summary>
        internal void NotifyPoolReturn()
        {
            IRevPoolable[] list = _poolables;
            if (list == null) return;

            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    list[i].OnPoolReturn();
                }
                catch (System.Exception e)
                {
                    RevPoolLog.Error($"OnPoolReturn 抛异常（已隔离）：{list[i].GetType().Name} - {e}");
                }
            }
        }

        private void CachePoolables()
        {
            if (_poolables != null) return;

            MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
            List<IRevPoolable> found = null;

            for (int i = 0; i < behaviours.Length; i++)
            {
                if (!(behaviours[i] is IRevPoolable poolable)) continue;

                if (found == null) found = new List<IRevPoolable>(2);
                found.Add(poolable);
            }

            _poolables = found != null ? found.ToArray() : Empty;
        }
    }
}
