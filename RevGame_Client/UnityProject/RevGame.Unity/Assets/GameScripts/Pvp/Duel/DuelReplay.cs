#nullable enable annotations
using System;
using System.IO;
using System.Text;

namespace RevGame.Pvp.Duel
{
    // 单条录像记录来自已执行的权威帧。命中值是该帧的一次性输出。
    internal readonly struct DuelReplayFrame
    {
        // 已执行的连续帧号，第一条记录为 1。
        public readonly int Frame;
        // 同帧黑白输入字与本地免伤位，回放将原样送给 C++。
        public readonly int Packed;
        // 执行完成后的完整状态指纹。
        public readonly int Hash;
        // 黑方在本帧的命中点数，和状态指纹分开核对。
        public readonly int FirstHit;
        // 白方在本帧的命中点数。
        public readonly int SecondHit;

        // 封口时从实际原生输出构造录像条目，不读取预测。
        public DuelReplayFrame(int packed, DuelSnapshot snapshot)
            : this(snapshot.Frame, packed, snapshot.Hash, snapshot.FirstHit, snapshot.SecondHit)
        {
        }

        // 反序列化用显式字段构造值副本，调用方还需核对范围和帧号。
        public DuelReplayFrame(int frame, int packed, int hash, int firstHit, int secondHit)
        {
            Frame = frame;
            Packed = packed;
            Hash = hash;
            FirstHit = firstHit;
            SecondHit = secondHit;
        }
    }

    /// <summary>只包含已封口的逻辑录像。电脑输入已经在其中，播放时无需再次运行电脑决策。</summary>
    public sealed class DuelReplay
    {
        // 小端文件标记 RVDL。与原生检查点的 RGCP 标记不同。
        private const int Magic = 0x4C445652;
        // 录像容器版本，规则指纹和原生版本另行存储。
        private const int Version = 2;
        // v2 在原头部之后增加两个角色编号；v1 仍可按旧固定角色读取。
        private const int LegacyHeaderBytes = 7 * sizeof(int);
        private const int HeaderBytes = 9 * sizeof(int);
        // 每条逻辑帧五个 32 位整数，字段顺序与条目一致。
        private const int FrameBytes = 5 * sizeof(int);
        // 局号按 UTF-8 编码后的字节上限。
        private const int MaxIdBytes = 256;
        // 文件末尾的 32 位校验和长度。
        private const int FooterBytes = sizeof(uint);
        // 一次录像导入最多一小时的帧记录，限制文件分配。对局本身可继续执行。
        private const int MaxRecordedFrames = 60 * 60 * 1000 / DuelConfig.FrameMs;
        // 不加 BOM，并拒绝非法 UTF-8，保证同一局号能产生同一开局种子。
        private static readonly Encoding IdEncoding = new UTF8Encoding(false, true);
        // 由创建者转移所有权。类不暴露数组，存档和回放可读同一份数据。
        private readonly DuelReplayFrame[] _frames;

        // 封帧器或校验后的加载器移交数组所有权，构造后不再追加。
        internal DuelReplay(string duelId, DuelReplayFrame[] frames, DuelRole firstRole, DuelRole secondRole)
        {
            DuelId = duelId;
            _frames = frames;
            FirstRole = firstRole;
            SecondRole = secondRole;
        }

        // 同时决定开局种子与回放所属对局。
        public string DuelId { get; }
        // 两席锁定的角色，决定重放使用哪一组招式距离。
        public DuelRole FirstRole { get; }
        public DuelRole SecondRole { get; }
        // 录像记录条数，也等于最后一个连续帧号。
        public int FrameCount => _frames.Length;

        // 导出独立的小端录像字节。调用方决定是否落盘，内核不访问文件系统。
        public byte[] Serialize()
        {
            byte[] identity = IdEncoding.GetBytes(DuelId);
            if (identity.Length == 0 || identity.Length > MaxIdBytes || FrameCount > MaxRecordedFrames)
            {
                throw new InvalidDataException("决斗录像超出格式限制。");
            }
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(DuelConfig.CoreProtocol);
            writer.Write(DuelConfig.Fingerprint);
            writer.Write(DuelConfig.FrameMs);
            writer.Write(identity.Length);
            writer.Write(FrameCount);
            writer.Write((int)FirstRole);
            writer.Write((int)SecondRole);
            writer.Write(identity);
            foreach (DuelReplayFrame frame in _frames)
            {
                writer.Write(frame.Frame);
                writer.Write(frame.Packed);
                writer.Write(frame.Hash);
                writer.Write(frame.FirstHit);
                writer.Write(frame.SecondHit);
            }
            writer.Flush();
            writer.Write(Checksum(output.GetBuffer(), (int)output.Length));
            writer.Flush();
            return output.ToArray();
        }

        // 先验证格式、长度、顺序和输入范围，再构造可播放的录像。错误字节不会进入原生。
        public static DuelReplay Deserialize(byte[]? data)
        {
            if (data == null || data.Length < LegacyHeaderBytes + 1 + FooterBytes ||
                data.Length > HeaderBytes + MaxIdBytes + MaxRecordedFrames * FrameBytes + FooterBytes)
            {
                throw new InvalidDataException("决斗录像长度无效。");
            }
            using var input = new MemoryStream(data, false);
            using var reader = new BinaryReader(input);
            int magic = reader.ReadInt32();
            int version = reader.ReadInt32();
            if (magic != Magic || (version != 1 && version != Version) ||
                reader.ReadInt32() != DuelConfig.CoreProtocol || reader.ReadInt32() != DuelConfig.Fingerprint ||
                reader.ReadInt32() != DuelConfig.FrameMs)
            {
                throw new InvalidDataException("决斗录像版本或规则不匹配。");
            }
            int idBytes = reader.ReadInt32();
            int frames = reader.ReadInt32();
            int header = version == 1 ? LegacyHeaderBytes : HeaderBytes;
            if (idBytes <= 0 || idBytes > MaxIdBytes || frames < 0 || frames > MaxRecordedFrames ||
                data.Length != header + idBytes + frames * FrameBytes + FooterBytes)
            {
                throw new InvalidDataException("决斗录像内容长度无效。");
            }
            DuelRole first = version == 1 ? DuelRole.Ember : (DuelRole)reader.ReadInt32();
            DuelRole second = version == 1 ? DuelRole.Yaya : (DuelRole)reader.ReadInt32();
            if (!DuelPreparation.ValidRole(first) || !DuelPreparation.ValidRole(second))
            {
                throw new InvalidDataException("录像的角色编号无效。");
            }
            int payloadBytes = data.Length - FooterBytes;
            // 先验整个文件，再按经过边界校验的帧数分配，避免损坏长度把无效字节送进原生。
            input.Position = payloadBytes;
            if (reader.ReadUInt32() != Checksum(data, payloadBytes))
            {
                throw new InvalidDataException("决斗录像校验失败。");
            }
            input.Position = header;
            string duelId;
            try
            {
                duelId = IdEncoding.GetString(reader.ReadBytes(idBytes));
            }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException("决斗录像标识编码无效。", error);
            }
            var history = new DuelReplayFrame[frames];
            for (int i = 0; i < frames; i++)
            {
                var entry = new DuelReplayFrame(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
                    reader.ReadInt32(), reader.ReadInt32());
                if (entry.Frame != i + 1 || !ValidPackedInput(entry.Packed) || entry.Hash < 0 ||
                    entry.FirstHit < 0 || entry.SecondHit < 0)
                {
                    throw new InvalidDataException("决斗录像帧号、输入或结果无效。");
                }
                history[i] = entry;
            }
            return new DuelReplay(duelId, history, first, second);
        }

        // 用隔离的原生实例重放。可从开局或已恢复的同局帧开始，回调仅收到新执行的帧。
        // 若逐帧对账失败，调用方应丢弃这个播放实例。
        public void ReplayInto(DuelCorePipe target, Action<DuelSnapshot>? onFrame = null)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            int cursor = target.Snapshot.Frame;
            if (!string.Equals(target.DuelId, DuelId, StringComparison.Ordinal) || target.FirstRole != FirstRole ||
                target.SecondRole != SecondRole || cursor < 0 || cursor > FrameCount ||
                (cursor > 0 && target.Snapshot.Hash != _frames[cursor - 1].Hash))
            {
                throw new InvalidDataException("决斗录像与恢复基线不一致。");
            }
            for (int i = cursor; i < FrameCount; i++)
            {
                // 从恢复帧的下一帧开始。重新执行输入才能验证命中，直接复制显示快照不能证明恢复正确。
                DuelReplayFrame expected = _frames[i];
                target.Append(expected.Frame, expected.Packed);
                DuelSnapshot actual = target.Snapshot;
                if (actual.Hash != expected.Hash || actual.FirstHit != expected.FirstHit || actual.SecondHit != expected.SecondHit)
                {
                    throw new InvalidDataException("决斗录像在逻辑帧 " + expected.Frame + " 分歧。");
                }
                onFrame?.Invoke(actual);
            }
        }

        // 两席输入与本地测试位均来自现有整帧格式，坏值在播放前拒绝。
        private static bool ValidPackedInput(int packed)
        {
            return (packed & ~(DuelCorePipe.NoDamageFrame | (DuelCorePipe.NoDamageFrame - 1))) == 0 &&
                DuelCorePipe.ValidInput(packed & DuelCorePipe.InputMask) &&
                DuelCorePipe.ValidInput((packed >> DuelCorePipe.InputBits) & DuelCorePipe.InputMask);
        }

        // 固定顺序的小端文件校验，只用于识别损坏，逐帧逻辑另用内核指纹和命中字段对账。
        private static uint Checksum(byte[] data, int length)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < length; i++)
            {
                hash = unchecked((hash ^ data[i]) * 16777619u);
            }
            return hash;
        }
    }
}
