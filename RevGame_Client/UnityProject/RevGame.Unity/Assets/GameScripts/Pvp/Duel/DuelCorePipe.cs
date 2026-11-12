#nullable enable annotations
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RevGame.Pvp.Duel
{
    // 一帧已执行结果的值副本。HUD 和 AI 读取它，改副本不会改原生世界。
    public struct DuelSnapshot
    {
        // 最后执行完的逻辑帧。开局是 0，每帧的运动和伤害都已结算后才发布这个值。
        public int Frame;
        // 0 进行中、1 黑胜、2 白胜、3 平局。预测输出不对外提供这个值。
        public int Result;
        // 原生完整状态的同帧指纹，不能拿不同帧的指纹比较。
        public int Hash;
        // 本帧黑方打出的伤害点数。0 表示没命中，开发态免伤也会保留命中事件。
        public int FirstHit;
        // 本帧白方打出的伤害点数。同一个已确认帧的特效只消费一次。
        public int SecondHit;
        // 黑方离地高度，单位千分格，0 表示在地面。
        public int FirstY;
        // 白方离地高度，单位千分格。恢复时还需要快照未包含的垂直速度。
        public int SecondY;
        // 黑方横坐标，单位千分格，负数在场地中心左侧。
        public int FirstX;
        // 黑方当前生命点数，由原生规则结算，0 表示倒地。
        public int FirstHealth;
        // 黑方当前能量点数，用于招式消耗和 HUD。
        public int FirstEnergy;
        // 黑方当前已执行动作。5/6 是原生选出的普攻二段和三段。
        public int FirstAction;
        // 黑方当前招式已执行的逻辑帧数，出招首帧为 0。
        public int FirstAge;
        // 黑方面向，-1 左、1 右，命中特效使用已确认的朝向。
        public int FirstFacing;
        // 白方横坐标，单位千分格。
        public int SecondX;
        // 白方当前生命点数，由同一份原生规则结算。
        public int SecondHealth;
        // 白方当前能量点数。
        public int SecondEnergy;
        // 白方当前已执行动作，编号与黑方相同。
        public int SecondAction;
        // 白方当前招式已执行的逻辑帧数。
        public int SecondAge;
        // 白方面向，-1 左、1 右。
        public int SecondFacing;
        // 黑方下一逻辑帧允许接纳的四个输入槽掩码。
        public int FirstOptions;
        // 白方下一逻辑帧允许接纳的输入槽掩码。
        public int SecondOptions;
        // 黑方当前连段的权威命中数。
        public int FirstCombo;
        // 白方当前连段的权威命中数。
        public int SecondCombo;
        // 黑方剩余受击硬直，单位为逻辑帧。
        public int FirstStun;
        // 白方剩余受击硬直，单位为逻辑帧。
        public int SecondStun;

        // 帧零也允许开招。沙盒和封帧器均读取同一掩码，避免托管层重复实现取消规则。
        internal bool CanTakeAction(bool black, int action)
        {
            return action >= 1 && action <= DuelCorePipe.MaxActionInput &&
                ((black ? FirstOptions : SecondOptions) & (1 << (action - 1))) != 0;
        }
    }

    /// <summary>托管宿主只写整帧消息、读结果。任何地方都不能通过 P/Invoke 直接改血量或位移。</summary>
    public sealed class DuelCorePipe : IDisposable
    {
        // P/Invoke 库名，由 Unity 或服务进程的原生搜索路径解析。
        private const string Library = "RevGameCore";
        // 每席占六位：两位方向、三位招式、一位跳跃。
        public const int InputBits = 6;
        // 截取一席的输入，防止把另一席的方向和招式读进来。
        public const int InputMask = (1 << InputBits) - 1;
        // 跳跃占第 5 位（从 0 数起），可与方向及招式同时存在。
        public const int Jump = 1 << 5;
        // 玩家只发送四个输入槽，派生段 5/6 由原生世界选出。
        public const int MaxActionInput = 4;
        // 完整帧第 12 位由本地测试权威设置。
        internal const int NoDamageFrame = 1 << (2 * InputBits);
        // 防止错误 DLL 返回超大长度。当前检查点为固定的小端数据包。
        internal const int MaxCheckpointBytes = 4096;
        // 当前 C# 实例独占的原生句柄，释放后不能继续写入。
        private readonly Handle _handle;
        // 复用的整帧写缓冲，五项各占四字节。
        private readonly byte[] _frame = new byte[20];
        // 复用的快照读取缓冲，共 25 个小端整数。
        private readonly byte[] _output = new byte[100];
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
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        // 空缓冲查询所需字节数，实际保存要求两侧 FIFO 都已消费完。
        private static extern int GameCore_SaveCheckpoint(Handle handle, [Out] byte[]? buffer, int capacity);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        // 校验同局检查点后一次提交，并写回恢复帧的显示基线。
        private static extern int GameCore_RestoreCheckpoint(Handle handle, byte[] buffer, int size, [Out] byte[] snapshot, int capacity);

        // 开局身份，录像和恢复必须使用同一个 ID。
        public string DuelId { get; }
        // 原生开局配置中使用的角色，独立于黑白座位；预测和录像必须沿用。
        public DuelRole FirstRole { get; }
        public DuelRole SecondRole { get; }
        // 本端最后一帧的值副本；只在完整结果校验成功后替换。
        public DuelSnapshot Snapshot { get; private set; }

        // 创建句柄、提交版本化开局配置并准备初始显示值；配置失败时释放句柄。
        public DuelCorePipe(string duelId, DuelRole firstRole = DuelRole.Ember, DuelRole secondRole = DuelRole.Yaya)
        {
            if (string.IsNullOrEmpty(duelId))
            {
                throw new ArgumentException("决斗需要统一的对局标识。", nameof(duelId));
            }
            if (!DuelPreparation.ValidRole(firstRole) || !DuelPreparation.ValidRole(secondRole))
            {
                throw new ArgumentException("决斗角色不存在。");
            }
            ulong seed = SeedFor(duelId);
            DuelId = duelId;
            FirstRole = firstRole;
            SecondRole = secondRole;
            _handle = GameCore_Create();
            int[] settings = DuelConfig.CreateCoreWords(firstRole == DuelRole.Yaya, secondRole == DuelRole.Yaya);
            byte[] start = new byte[(settings.Length + 4) * 4];
            // 开局消息写协议版本、操作号、完整配置与种子，队列两端不能依赖托管类型的内存排版。
            Write(start, 0, DuelConfig.CoreProtocol);
            Write(start, 1, 1);
            for (int i = 0; i < settings.Length; i++)
            {
                Write(start, i + 2, settings[i]);
            }
            Write(start, settings.Length + 2, unchecked((int)seed));
            Write(start, settings.Length + 3, unchecked((int)(seed >> 32)));
            if (_handle.IsInvalid || GameCore_Write(_handle, start, start.Length) != 1)
            {
                _handle.Dispose();
                throw new InvalidOperationException("原生决斗配置校验失败。");
            }
            Snapshot = new DuelSnapshot { FirstX = -DuelConfig.SpawnOffset, SecondX = DuelConfig.SpawnOffset,
                FirstHealth = DuelConfig.Health, SecondHealth = DuelConfig.Health, FirstFacing = 1, SecondFacing = -1,
                FirstOptions = 15, SecondOptions = 15 };
        }

        // 权威和回放副本从同一个对局 ID 派生种子。明确按 UTF-16 小端字节处理，避免本机哈希和编码差异。
        private static ulong SeedFor(string duelId)
        {
            const ulong prime = 1099511628211UL;
            ulong hash = 14695981039346656037UL;
            foreach (char codeUnit in duelId)
            {
                hash = unchecked((hash ^ (uint)(codeUnit & 0xff)) * prime);
                hash = unchecked((hash ^ (uint)(codeUnit >> 8)) * prime);
            }
            return hash;
        }

        // 提取三位动作槽，回执和输入缓冲都使用同一布局。
        internal static int ReadActionInput(int packed)
        {
            return (packed >> 2) & 7;
        }

        // 验证单席六位输入。两方向同时置位或发送派生普攻都属于无效输入。
        public static bool ValidInput(int input)
        {
            return input >= 0 && input <= InputMask && (input & 3) != 3 && ReadActionInput(input) <= MaxActionInput;
        }

        // 写入完整帧后取回同号快照。只有权威、确认副本或隔离沙盒能调用，原生失败时停止使用该实例。
        public void Append(int frame, int packed)
        {
            if ((packed & ~(NoDamageFrame | (NoDamageFrame - 1))) != 0)
            {
                throw new InvalidOperationException("封帧包含未知标记。");
            }
            Write(_frame, 0, DuelConfig.CoreProtocol);
            Write(_frame, 1, 2);
            Write(_frame, 2, frame);
            // 原生 FIFO 用黑方字的第 6 位承载双席免伤标记。
            Write(_frame, 3, (packed & InputMask) | ((packed & NoDamageFrame) >> InputBits));
            Write(_frame, 4, (packed >> InputBits) & InputMask);
            // 一次送一整帧并及时消费结果，输入和输出 FIFO 都有固定容量。
            if (GameCore_Write(_handle, _frame, _frame.Length) != 1 ||
                GameCore_Read(_handle, _output, _output.Length) != _output.Length || Read(0) != frame)
            {
                throw new InvalidOperationException("原生 FIFO 拒绝封帧或输出序号分歧。");
            }
            ReadSnapshot();
        }

        // 保存的是原生权威状态。托管显示快照缺少随机状态、垂直速度等字段，不能用来恢复。
        public byte[] SaveCheckpoint()
        {
            int size = GameCore_SaveCheckpoint(_handle, null, 0);
            if (size <= 0 || size > MaxCheckpointBytes)
            {
                throw new InvalidOperationException("决斗检查点大小无效或仍有待消费帧。");
            }
            var checkpoint = new byte[size];
            if (SaveCheckpoint(checkpoint) != size)
            {
                throw new InvalidOperationException("决斗检查点保存失败。");
            }
            return checkpoint;
        }

        // 热路径使用调用方独占的缓冲区。原生返回实际字节数，缓冲尾部没有状态，不能一起送去恢复。
        internal int SaveCheckpoint(byte[] buffer)
        {
            int size = GameCore_SaveCheckpoint(_handle, buffer, buffer.Length);
            if (size <= 0 || size > buffer.Length || size > MaxCheckpointBytes)
            {
                throw new InvalidOperationException("决斗检查点未保存，可能还有待消费的帧。");
            }
            return size;
        }

        // 失败时保留原生实例和当前快照。成功后只返回显示基线，恢复帧的旧命中不会再发一次。
        public bool TryRestoreCheckpoint(byte[]? checkpoint)
        {
            return checkpoint != null && TryRestoreCheckpoint(checkpoint, checkpoint.Length);
        }

        // 可复用缓冲的恢复入口，只读有效长度。失败时预测者可以放弃该轨迹，权威副本不会被修改。
        internal bool TryRestoreCheckpoint(byte[] checkpoint, int size)
        {
            if (size <= 0 || size > checkpoint.Length || size > MaxCheckpointBytes ||
                GameCore_RestoreCheckpoint(_handle, checkpoint, size, _output, _output.Length) != 1)
            {
                return false;
            }
            ReadSnapshot();
            return true;
        }

        // 执行帧与恢复基线共享同一个显示快照解码顺序。
        private void ReadSnapshot()
        {
            Snapshot = new DuelSnapshot { Frame = Read(0), Result = Read(1), FirstX = Read(2), FirstHealth = Read(3),
                FirstEnergy = Read(4), FirstAction = Read(5), FirstAge = Read(6), FirstFacing = Read(7),
                SecondX = Read(8), SecondHealth = Read(9), SecondEnergy = Read(10), SecondAction = Read(11),
                SecondAge = Read(12), SecondFacing = Read(13), FirstHit = Read(14), SecondHit = Read(15), Hash = Read(16),
                FirstY = Read(17), SecondY = Read(18), FirstOptions = Read(19), SecondOptions = Read(20),
                FirstCombo = Read(21), SecondCombo = Read(22), FirstStun = Read(23), SecondStun = Read(24) };
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
