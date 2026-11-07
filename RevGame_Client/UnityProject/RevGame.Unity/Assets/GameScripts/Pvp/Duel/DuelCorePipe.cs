using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RevGame.Pvp.Duel
{
    public struct DuelSnapshot
    {
        public int Frame, Result, Hash, FirstHit, SecondHit;
        public int FirstY, SecondY;
        public int FirstX, FirstHealth, FirstEnergy, FirstAction, FirstAge, FirstFacing;
        public int SecondX, SecondHealth, SecondEnergy, SecondAction, SecondAge, SecondFacing;
    }

    /// <summary>托管宿主只写整帧消息、读结果。任何地方都不能通过 P/Invoke 直接改血量或位移。</summary>
    public sealed class DuelCorePipe : IDisposable
    {
        private const string Library = "RevGameCore";
        private const int Protocol = 2;
        public const int InputBits = 5;
        public const int InputMask = (1 << InputBits) - 1;
        public const int Jump = 1 << 4;
        private readonly Handle _handle;
        private readonly byte[] _frame = new byte[20];
        private readonly byte[] _output = new byte[76];
        private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public Handle() : base(true)
            {
            }

            protected override bool ReleaseHandle()
            {
                GameCore_Destroy(handle);
                return true;
            }
        }
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern Handle GameCore_Create();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void GameCore_Destroy(IntPtr handle);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GameCore_Write(Handle handle, byte[] buffer, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int GameCore_Read(Handle handle, [Out] byte[] buffer, int size);

        public DuelSnapshot Snapshot { get; private set; }

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

        public static bool ValidInput(int input)
        {
            return input >= 0 && input <= InputMask && (input & 3) != 3;
        }

        public void Append(int frame, int packed)
        {
            Write(_frame, 0, Protocol);
            Write(_frame, 1, 2);
            Write(_frame, 2, frame);
            Write(_frame, 3, packed & InputMask);
            Write(_frame, 4, packed >> InputBits);
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

        private static void Write(byte[] bytes, int index, int value)
        {
            int offset = index * 4;
            for (int i = 0; i < 4; i++)
            {
                bytes[offset + i] = (byte)(value >> (i * 8));
            }
        }

        private int Read(int index)
        {
            int offset = index * 4;
            return _output[offset] | (_output[offset + 1] << 8) | (_output[offset + 2] << 16) | (_output[offset + 3] << 24);
        }

        public void Dispose()
        {
            _handle.Dispose();
        }
    }
}
