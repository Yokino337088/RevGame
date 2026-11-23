#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Runtime.InteropServices;
using RevGame.Core;

namespace RevGame.Bootstrap
{
    /// <summary>从 Windows DXCore 匹配 Unity 实际使用的适配器。启动查询一次，所有原生接口在本次调用内释放。</summary>
    internal static class GameWindowsGpu
    {
        /// <summary>DXCore 工厂的 COM 接口 ID。</summary>
        private static readonly Guid FactoryId = new Guid("78ee5945-c36e-4b13-a669-005dd11c0f06");
        /// <summary>DXCore 适配器清单的 COM 接口 ID。</summary>
        private static readonly Guid ListId = new Guid("526c7776-40e9-459b-b711-f32ad76dfc28");
        /// <summary>清单中单个适配器的 COM 接口 ID。</summary>
        private static readonly Guid AdapterId = new Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
        /// <summary>筛选 D3D11 和 D3D12 图形适配器。较早驱动可能只在 D3D12 属性下报告。</summary>
        private static readonly Guid Graphics11 = new Guid("8c47866b-7583-450d-f0f0-6bada895af4b");
        /// <summary>DXCore 宣告 D3D12 图形能力的属性。</summary>
        private static readonly Guid Graphics12 = new Guid("0c9ece4d-2f6e-4f01-8c96-e89e331b47b1");
        /// <summary>DXCoreAdapterProperty.HardwareID，对应四个无符号 32 位字段。</summary>
        private const int HardwareIdProperty = 3;
        /// <summary>DXCoreAdapterProperty.IsHardware，屏蔽软件适配器。</summary>
        private const int HardwareProperty = 11;
        /// <summary>DXCoreAdapterProperty.IsIntegrated，读取驱动申报的拓扑。</summary>
        private const int IntegratedProperty = 12;

        /// <summary>Windows SDK 定义的硬件 ID 布局。Unity 只公开其中的供应商和设备 ID。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareId
        {
            /// <summary>PCI 供应商 ID。</summary>
            public uint Vendor;
            /// <summary>PCI 图形设备 ID。</summary>
            public uint Device;
            /// <summary>OEM 子系统 ID，保留 ABI 布局。</summary>
            public uint Subsystem;
            /// <summary>硬件修订 ID，保留 ABI 布局。</summary>
            public uint Revision;
        }

        /// <summary>打开系统 DXCore，不向 Unity 请求改用另一张显卡。</summary>
        [DllImport("dxcore.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        private static extern int DXCoreCreateAdapterFactory(ref Guid id, out IntPtr factory);

        /// <summary>IDXCoreAdapterFactory::CreateAdapterList。属性参数指向一个 GUID。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateList(IntPtr self, uint attributes, ref Guid attribute, ref Guid id, out IntPtr list);
        /// <summary>IDXCoreAdapterList::GetAdapterCount。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint CountAdapters(IntPtr self);
        /// <summary>IDXCoreAdapterList::GetAdapter。成功会获得一份新的 COM 引用。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetAdapter(IntPtr self, uint index, ref Guid id, out IntPtr adapter);
        /// <summary>IDXCoreAdapter::GetProperty。读取完整 HardwareID，长度按本机 size_t 传入。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReadId(IntPtr self, int property, UIntPtr size, out HardwareId id);
        /// <summary>同一 GetProperty 的单字节 C++ bool 输出。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReadFlag(IntPtr self, int property, UIntPtr size, out byte value);
        /// <summary>IUnknown::Release，接口的每份持有对应一次释放。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseReference(IntPtr self);

        /// <summary>只有硬件 ID 唯一匹配才分类。旧版 Windows、软件设备或接口缺失都保留未知。</summary>
        internal static GameBattleGpuKind Identify(int vendor, int device)
        {
            if (vendor <= 0 || device <= 0)
            {
                return GameBattleGpuKind.Unknown;
            }

            IntPtr factory = IntPtr.Zero;
            try
            {
                Guid id = FactoryId;
                if (DXCoreCreateAdapterFactory(ref id, out factory) < 0 || factory == IntPtr.Zero)
                {
                    return GameBattleGpuKind.Unknown;
                }

                var kind = FindInList(factory, Graphics11, (uint)vendor, (uint)device, out bool matched);
                return matched ? kind : FindInList(factory, Graphics12, (uint)vendor, (uint)device, out _);
            }
            catch (DllNotFoundException)
            {
                return GameBattleGpuKind.Unknown;
            }
            catch (EntryPointNotFoundException)
            {
                return GameBattleGpuKind.Unknown;
            }
            finally
            {
                Release(factory);
            }
        }

        /// <summary>找实际设备的唯一供应商与设备 ID 组合。出现多个匹配时不根据内存或名称猜测。</summary>
        private static GameBattleGpuKind FindInList(IntPtr factory, Guid attribute, uint vendor, uint device, out bool matched)
        {
            matched = false;
            var kind = GameBattleGpuKind.Unknown;
            Guid listId = ListId;
            int status = Method<CreateList>(factory, 3)(factory, 1, ref attribute, ref listId, out IntPtr list);
            try
            {
                if (status < 0 || list == IntPtr.Zero)
                {
                    return kind;
                }

                var countAdapters = Method<CountAdapters>(list, 4);
                var getAdapter = Method<GetAdapter>(list, 3);
                uint count = countAdapters(list);
                for (uint index = 0; index < count; index++)
                {
                    Guid adapterId = AdapterId;
                    int gotAdapter = getAdapter(list, index, ref adapterId, out IntPtr adapter);
                    try
                    {
                        if (gotAdapter < 0 || adapter == IntPtr.Zero ||
                            Method<ReadId>(adapter, 6)(adapter, HardwareIdProperty, new UIntPtr(16), out var hardware) < 0 ||
                            hardware.Vendor != vendor || hardware.Device != device)
                        {
                            continue;
                        }
                        if (matched)
                        {
                            return GameBattleGpuKind.Unknown;
                        }

                        matched = true;
                        var read = Method<ReadFlag>(adapter, 6);
                        if (read(adapter, HardwareProperty, new UIntPtr(1), out byte isHardware) >= 0 && isHardware != 0 &&
                            read(adapter, IntegratedProperty, new UIntPtr(1), out byte isIntegrated) >= 0)
                        {
                            kind = isIntegrated != 0 ? GameBattleGpuKind.Integrated : GameBattleGpuKind.Discrete;
                        }
                    }
                    finally
                    {
                        Release(adapter);
                    }
                }
                return kind;
            }
            finally
            {
                Release(list);
            }
        }

        /// <summary>读取公开 COM 接口的方法地址。槽号包含最前面的三个 IUnknown 方法。</summary>
        private static T Method<T>(IntPtr self, int slot) where T : Delegate
        {
            return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));
        }

        /// <summary>只释放有效接口。适配器先于清单，清单先于工厂释放。</summary>
        private static void Release(IntPtr value)
        {
            if (value != IntPtr.Zero)
            {
                Method<ReleaseReference>(value, 2)(value);
            }
        }
    }
}
#endif
