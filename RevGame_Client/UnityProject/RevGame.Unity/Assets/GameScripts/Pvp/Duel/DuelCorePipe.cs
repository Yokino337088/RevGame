using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RevGame.Pvp.Duel
{
    // 一帧已执行结果的值副本。HUD 和 AI 读取它，改副本不会改原生世界。
    public struct DuelSnapshot
    {
        // Frame 为已执行帧号；Result 为 0 进行中、1 黑胜、2 白胜、3 平局；Hash 用于同帧对账。
        // FirstHit、SecondHit 是黑、白命中的原始伤害值，0 表示没有命中。免伤帧也保留此值供表现读取。
        public int Frame, Result, Hash, FirstHit, SecondHit;
        // FirstY 和 SecondY 是黑白两席的离地高度，单位千分格。
        public int FirstY, SecondY;
        // 黑方的水平位置（千分格）、生命、能量、招式槽、招式已过帧数、朝向（±1）。
        public int FirstX, FirstHealth, FirstEnergy, FirstAction, FirstAge, FirstFacing;
        // 白方对应字段，单位与黑方相同。招式槽 0 表示无招式。
        public int SecondX, SecondHealth, SecondEnergy, SecondAction, SecondAge, SecondFacing;
    }

    /// <summary>托管宿主只写整帧消息、读结果。任何地方都不能通过 P/Invoke 直接改血量或位移。</summary>
    public sealed class DuelCorePipe : IDisposable
    {
        // P/Invoke 库名，由 Unity 或服务进程的原生搜索路径解析。
        private const string Library = "RevGameCore";
        // 小端消息布局版本，不同版本的 DLL 与宿主不能混用。
        private const int Protocol = 2;
        // 每席占五位：两位方向、两位招式、一位跳跃。
        public const int InputBits = 5;
        // 截取打包字的低五位，防止把另一席的输入读进来。
        public const int InputMask = (1 << InputBits) - 1;
        // 跳跃占第 4 位（从 0 数起），可与方向及招式同时存在。
        public const int Jump = 1 << 4;
        // 完整帧第 10 位由本地测试权威设置，不属于黑白任一席的输入。
        internal const int NoDamageFrame = 1 << (2 * InputBits);
        // 当前 C# 实例独占的原生句柄，释放后不能继续写入。
        private readonly Handle _handle;
        // 复用的整帧写缓冲，五项各占四字节。
        private readonly byte[] _frame = new byte[20];
        // 复用的快照读取缓冲，共 19 个小端整数。
        private readonly byte[] _output = new byte[76];
        // SafeHandle 管理原生指针生命周期，避免重复释放。
        private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
        {
            // 声明托管端拥有返回句柄，供 P/Invoke 封装原生指针。
            public Handle() : base(true)
            {
            }

            // SafeHandle 收尾时调用原生销毁入口。
            protected override bool ReleaseHandle()
            {
                GameCore_Destroy(handle);
                return true;
            }
        }
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        // 原生分配一局实例；无效返回值由构造函数检查。
        private static extern Handle GameCore_Create();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        // 原生释放实例及双向队列，由 SafeHandle 调用。
        private static extern void GameCore_Destroy(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        // 同步传入包地址和长度，原生返回接纳、背压或非法。
        private static extern int GameCore_Write(Handle handle, byte[] buffer, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        // 原生同步写入托管缓冲并返回实际字节数，缓冲所有权仍在 C#。
        private static extern int GameCore_Read(Handle handle, [Out] byte[] buffer, int size);

        // 本端最后一帧的值副本；只在完整结果校验成功后替换。
        public DuelSnapshot Snapshot { get; private set; }

        // 创建句柄、提交版本化开局配置并准备初始显示值；配置失败时释放句柄。
        public DuelCorePipe()
        {
            _handle = GameCore_Create();
            int[] settings = DuelConfig.CreateCoreWords();
            byte[] start = new byte[(settings.Length + 2) * 4];
            Write(start, 0, Protocol);
            Write(start, 1, 1);
            for (int i = 0; i < settings.Length; i++)
            {
                Write(start, i + 2, settings[i]);
            }
            if (_handle.IsInvalid || GameCore_Write(_handle, start, start.Length) != 1)
            {
                _handle.Dispose();
                throw new InvalidOperationException("原生决斗配置校验失败。");
            }
            Snapshot = new DuelSnapshot { FirstX = -DuelConfig.SpawnOffset, SecondX = DuelConfig.SpawnOffset,
                FirstHealth = DuelConfig.Health, SecondHealth = DuelConfig.Health, FirstFacing = 1, SecondFacing = -1 };
        }

        // 验证单席五位输入；两个方向同时置位属于无效输入。
        public static bool ValidInput(int input)
        {
            return input >= 0 && input <= InputMask && (input & 3) != 3;
        }

        // 写入完整帧后取回相同帧号的快照。桥接失败时抛错，由上层停止继续播放。
        public void Append(int frame, int packed)
        {
            if ((packed & ~(NoDamageFrame | (NoDamageFrame - 1))) != 0)
            {
                throw new InvalidOperationException("封帧包含未知标记。");
            }
            Write(_frame, 0, Protocol);
            Write(_frame, 1, 2);
            Write(_frame, 2, frame);
            // 原生 FIFO 用黑方字的第 5 位承载双席免伤标记，白方字仍只承载输入。
            Write(_frame, 3, (packed & InputMask) | ((packed & NoDamageFrame) >> InputBits));
            Write(_frame, 4, (packed >> InputBits) & InputMask);
            // 一次送一整帧并及时消费结果，输入和输出 FIFO 都有固定容量。
            if (GameCore_Write(_handle, _frame, _frame.Length) != 1 ||
                GameCore_Read(_handle, _output, _output.Length) != _output.Length || Read(0) != frame)
            {
                throw new InvalidOperationException("原生 FIFO 拒绝封帧或输出序号分歧。");
            }
            Snapshot = new DuelSnapshot { Frame = Read(0), Result = Read(1), FirstX = Read(2), FirstHealth = Read(3),
                FirstEnergy = Read(4), FirstAction = Read(5), FirstAge = Read(6), FirstFacing = Read(7),
                SecondX = Read(8), SecondHealth = Read(9), SecondEnergy = Read(10), SecondAction = Read(11),
                SecondAge = Read(12), SecondFacing = Read(13), FirstHit = Read(14), SecondHit = Read(15), Hash = Read(16),
                FirstY = Read(17), SecondY = Read(18) };
        }

        // 把指定整数槽拆为四个低位在前的字节；index 是整数下标。
        private static void Write(byte[] bytes, int index, int value)
        {
            int offset = index * 4;
            for (int i = 0; i < 4; i++)
            {
                bytes[offset + i] = (byte)(value >> (i * 8));
            }
        }

        // 把输出中的四字节重组为 32 位整数，保留负位置或朝向。
        private int Read(int index)
        {
            int offset = index * 4;
            return _output[offset] | (_output[offset + 1] << 8) | (_output[offset + 2] << 16) | (_output[offset + 3] << 24);
        }

        // 结束该宿主副本的生命周期，释放它独占的原生句柄。
        public void Dispose()
        {
            _handle.Dispose();
        }
    }
}
