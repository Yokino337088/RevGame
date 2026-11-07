using LightProto;
using System;
using MemoryPack;
using System.Collections.Generic;
using Fantasy;
using Fantasy.Pool;
using Fantasy.Network.Interface;
using Fantasy.Serialize;

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
#pragma warning disable CS8618
#pragma warning disable CS8601 // Possible null reference assignment in generated message copies.
// ReSharper disable InconsistentNaming
// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable RedundantTypeArgumentsOfMethod
// ReSharper disable PartialTypeWithSinglePart
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable PreferConcreteValueOverDefault
// ReSharper disable RedundantNameQualifier
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable CheckNamespace
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable RedundantUsingDirective
// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
namespace Fantasy
{
    /// <summary>
    /// 一条经 Battle 校验并排序的围棋操作。
    /// </summary>
    [Serializable]
    [ProtoContract]
    public partial class GoRelayCommand : AMessage, IDisposable
    {
        public static GoRelayCommand Create(bool autoReturn = true)
        {
            var goRelayCommand = MessageObjectPool<GoRelayCommand>.Rent();
            goRelayCommand.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                goRelayCommand.SetIsPool(false);
            }
            
            return goRelayCommand;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            Sequence = default;
            Side = default;
            Kind = default;
            X = default;
            Y = default;
            Payload = default;
            ElapsedMs = default;
            PreviousHash = default;
            ResultHash = default;
            MessageObjectPool<GoRelayCommand>.Return(this);
        }

        public void CopyTo(ref GoRelayCommand other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.Sequence = Sequence;
            other.Side = Side;
            other.Kind = Kind;
            other.X = X;
            other.Y = Y;
            other.Payload = Payload;
            other.ElapsedMs = ElapsedMs;
            other.PreviousHash = PreviousHash;
            other.ResultHash = ResultHash;

        }

        public GoRelayCommand DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        [ProtoMember(1)]
        public int Sequence { get; set; }
        [ProtoMember(2)]
        public int Side { get; set; }
        [ProtoMember(3)]
        public int Kind { get; set; }
        [ProtoMember(4)]
        public int X { get; set; }
        [ProtoMember(5)]
        public int Y { get; set; }
        [ProtoMember(6)]
        public string Payload { get; set; }
        [ProtoMember(7)]
        public long ElapsedMs { get; set; }
        [ProtoMember(8)]
        public string PreviousHash { get; set; }
        [ProtoMember(9)]
        public string ResultHash { get; set; }
    }
    /// <summary>
    /// 首次同步和重连携带完整操作序列，协商后普通操作可只带最新一手。
    /// </summary>
    [Serializable]
    [ProtoContract]
    public partial class GoRelayState : AMessage, IDisposable
    {
        public static GoRelayState Create(bool autoReturn = true)
        {
            var goRelayState = MessageObjectPool<GoRelayState>.Rent();
            goRelayState.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                goRelayState.SetIsPool(false);
            }
            
            return goRelayState;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            RoomId = default;
            Phase = default;
            MySide = default;
            BlackName = default;
            WhiteName = default;
            CoreVersion = default;
            RulesJson = default;
            foreach (var __t in Commands) __t.Dispose();
            Commands.Clear();
            StartUtcMs = default;
            ServerUtcMs = default;
            ReconnectDeadlineUtcMs = default;
            Reason = default;
            MatchKind = default;
            BaseSequence = default;
            MessageObjectPool<GoRelayState>.Return(this);
        }

        public void CopyTo(ref GoRelayState other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.RoomId = RoomId;
            other.Phase = Phase;
            other.MySide = MySide;
            other.BlackName = BlackName;
            other.WhiteName = WhiteName;
            other.CoreVersion = CoreVersion;
            other.RulesJson = RulesJson;
            other.Commands = new List<GoRelayCommand>(Commands.Count);
            foreach (var __item in Commands) other.Commands.Add(__item.DeepCopy());
            other.StartUtcMs = StartUtcMs;
            other.ServerUtcMs = ServerUtcMs;
            other.ReconnectDeadlineUtcMs = ReconnectDeadlineUtcMs;
            other.Reason = Reason;
            other.MatchKind = MatchKind;
            other.BaseSequence = BaseSequence;

        }

        public GoRelayState DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        [ProtoMember(1)]
        public string RoomId { get; set; }
        [ProtoMember(2)]
        public string Phase { get; set; }
        [ProtoMember(3)]
        public int MySide { get; set; }
        [ProtoMember(4)]
        public string BlackName { get; set; }
        [ProtoMember(5)]
        public string WhiteName { get; set; }
        [ProtoMember(6)]
        public string CoreVersion { get; set; }
        [ProtoMember(7)]
        public string RulesJson { get; set; }
        [ProtoMember(8)]
        public List<GoRelayCommand> Commands { get; set; } = new List<GoRelayCommand>();
        [ProtoMember(9)]
        public long StartUtcMs { get; set; }
        [ProtoMember(10)]
        public long ServerUtcMs { get; set; }
        [ProtoMember(11)]
        public long ReconnectDeadlineUtcMs { get; set; }
        [ProtoMember(12)]
        public string Reason { get; set; }
        [ProtoMember(13)]
        public string MatchKind { get; set; }
        [ProtoMember(14)]
        public int BaseSequence { get; set; }
    }
    /// <summary>
    /// 匿名身份只用于本次休闲联机；恢复令牌不可作为日志或公开昵称。
    /// </summary>
    [Serializable]
    [ProtoContract]
    public partial class C2G_GoHelloRequest : AMessage, IRequest
    {
        public static C2G_GoHelloRequest Create(bool autoReturn = true)
        {
            var c2G_GoHelloRequest = MessageObjectPool<C2G_GoHelloRequest>.Rent();
            c2G_GoHelloRequest.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_GoHelloRequest.SetIsPool(false);
            }
            
            return c2G_GoHelloRequest;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            CoreVersion = default;
            Nickname = default;
            ResumeToken = default;
            RulesJson = default;
            MatchKind = default;
            SupportsDelta = default;
            MessageObjectPool<C2G_GoHelloRequest>.Return(this);
        }

        public void CopyTo(ref C2G_GoHelloRequest other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.CoreVersion = CoreVersion;
            other.Nickname = Nickname;
            other.ResumeToken = ResumeToken;
            other.RulesJson = RulesJson;
            other.MatchKind = MatchKind;
            other.SupportsDelta = SupportsDelta;

        }

        public C2G_GoHelloRequest DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_GoHelloRequest; } 
        [ProtoIgnore]
        public G2C_GoHelloResponse ResponseType { get; set; }
        [ProtoMember(1)]
        public string CoreVersion { get; set; }
        [ProtoMember(2)]
        public string Nickname { get; set; }
        [ProtoMember(3)]
        public string ResumeToken { get; set; }
        [ProtoMember(4)]
        public string RulesJson { get; set; }
        [ProtoMember(5)]
        public string MatchKind { get; set; }
        [ProtoMember(6)]
        public bool SupportsDelta { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_GoHelloResponse : AMessage, IResponse
    {
        public static G2C_GoHelloResponse Create(bool autoReturn = true)
        {
            var g2C_GoHelloResponse = MessageObjectPool<G2C_GoHelloResponse>.Rent();
            g2C_GoHelloResponse.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_GoHelloResponse.SetIsPool(false);
            }
            
            return g2C_GoHelloResponse;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            ErrorCode = 0;
            Token = default;
            if (State != null)
            {
                State.Dispose();
                State = null;
            }
            Reason = default;
            MessageObjectPool<G2C_GoHelloResponse>.Return(this);
        }

        public void CopyTo(ref G2C_GoHelloResponse other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.ErrorCode = ErrorCode;
            other.Token = Token;
            other.State = State?.DeepCopy();
            other.Reason = Reason;

        }

        public G2C_GoHelloResponse DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_GoHelloResponse; } 
        [ProtoMember(4)]
        public uint ErrorCode { get; set; }
        [ProtoMember(1)]
        public string Token { get; set; }
        [ProtoMember(2)]
        public GoRelayState State { get; set; }
        [ProtoMember(3)]
        public string Reason { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_GoMatchRequest : AMessage, IRequest
    {
        public static C2G_GoMatchRequest Create(bool autoReturn = true)
        {
            var c2G_GoMatchRequest = MessageObjectPool<C2G_GoMatchRequest>.Rent();
            c2G_GoMatchRequest.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_GoMatchRequest.SetIsPool(false);
            }
            
            return c2G_GoMatchRequest;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            Cancel = default;
            MessageObjectPool<C2G_GoMatchRequest>.Return(this);
        }

        public void CopyTo(ref C2G_GoMatchRequest other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.Cancel = Cancel;

        }

        public C2G_GoMatchRequest DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_GoMatchRequest; } 
        [ProtoIgnore]
        public G2C_GoMatchResponse ResponseType { get; set; }
        [ProtoMember(1)]
        public bool Cancel { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_GoMatchResponse : AMessage, IResponse
    {
        public static G2C_GoMatchResponse Create(bool autoReturn = true)
        {
            var g2C_GoMatchResponse = MessageObjectPool<G2C_GoMatchResponse>.Rent();
            g2C_GoMatchResponse.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_GoMatchResponse.SetIsPool(false);
            }
            
            return g2C_GoMatchResponse;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            ErrorCode = 0;
            if (State != null)
            {
                State.Dispose();
                State = null;
            }
            Reason = default;
            MessageObjectPool<G2C_GoMatchResponse>.Return(this);
        }

        public void CopyTo(ref G2C_GoMatchResponse other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.ErrorCode = ErrorCode;
            other.State = State?.DeepCopy();
            other.Reason = Reason;

        }

        public G2C_GoMatchResponse DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_GoMatchResponse; } 
        [ProtoMember(3)]
        public uint ErrorCode { get; set; }
        [ProtoMember(1)]
        public GoRelayState State { get; set; }
        [ProtoMember(2)]
        public string Reason { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_GoCommandRequest : AMessage, IRequest
    {
        public static C2G_GoCommandRequest Create(bool autoReturn = true)
        {
            var c2G_GoCommandRequest = MessageObjectPool<C2G_GoCommandRequest>.Rent();
            c2G_GoCommandRequest.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_GoCommandRequest.SetIsPool(false);
            }
            
            return c2G_GoCommandRequest;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            RoomId = default;
            if (Command != null)
            {
                Command.Dispose();
                Command = null;
            }
            MessageObjectPool<C2G_GoCommandRequest>.Return(this);
        }

        public void CopyTo(ref C2G_GoCommandRequest other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.RoomId = RoomId;
            other.Command = Command?.DeepCopy();

        }

        public C2G_GoCommandRequest DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_GoCommandRequest; } 
        [ProtoIgnore]
        public G2C_GoCommandResponse ResponseType { get; set; }
        [ProtoMember(1)]
        public string RoomId { get; set; }
        [ProtoMember(2)]
        public GoRelayCommand Command { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_GoCommandResponse : AMessage, IResponse
    {
        public static G2C_GoCommandResponse Create(bool autoReturn = true)
        {
            var g2C_GoCommandResponse = MessageObjectPool<G2C_GoCommandResponse>.Rent();
            g2C_GoCommandResponse.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_GoCommandResponse.SetIsPool(false);
            }
            
            return g2C_GoCommandResponse;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            ErrorCode = 0;
            if (State != null)
            {
                State.Dispose();
                State = null;
            }
            Reason = default;
            MessageObjectPool<G2C_GoCommandResponse>.Return(this);
        }

        public void CopyTo(ref G2C_GoCommandResponse other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.ErrorCode = ErrorCode;
            other.State = State?.DeepCopy();
            other.Reason = Reason;

        }

        public G2C_GoCommandResponse DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_GoCommandResponse; } 
        [ProtoMember(3)]
        public uint ErrorCode { get; set; }
        [ProtoMember(1)]
        public GoRelayState State { get; set; }
        [ProtoMember(2)]
        public string Reason { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_GoSyncRequest : AMessage, IRequest
    {
        public static C2G_GoSyncRequest Create(bool autoReturn = true)
        {
            var c2G_GoSyncRequest = MessageObjectPool<C2G_GoSyncRequest>.Rent();
            c2G_GoSyncRequest.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_GoSyncRequest.SetIsPool(false);
            }
            
            return c2G_GoSyncRequest;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            MessageObjectPool<C2G_GoSyncRequest>.Return(this);
        }

        public void CopyTo(ref C2G_GoSyncRequest other)
        {
            if (other == null)
            {
                other = Create(false);
            }

        }

        public C2G_GoSyncRequest DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_GoSyncRequest; } 
        [ProtoIgnore]
        public G2C_GoSyncResponse ResponseType { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_GoSyncResponse : AMessage, IResponse
    {
        public static G2C_GoSyncResponse Create(bool autoReturn = true)
        {
            var g2C_GoSyncResponse = MessageObjectPool<G2C_GoSyncResponse>.Rent();
            g2C_GoSyncResponse.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_GoSyncResponse.SetIsPool(false);
            }
            
            return g2C_GoSyncResponse;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            ErrorCode = 0;
            if (State != null)
            {
                State.Dispose();
                State = null;
            }
            Reason = default;
            MessageObjectPool<G2C_GoSyncResponse>.Return(this);
        }

        public void CopyTo(ref G2C_GoSyncResponse other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.ErrorCode = ErrorCode;
            other.State = State?.DeepCopy();
            other.Reason = Reason;

        }

        public G2C_GoSyncResponse DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_GoSyncResponse; } 
        [ProtoMember(3)]
        public uint ErrorCode { get; set; }
        [ProtoMember(1)]
        public GoRelayState State { get; set; }
        [ProtoMember(2)]
        public string Reason { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_GoLeaveRequest : AMessage, IRequest
    {
        public static C2G_GoLeaveRequest Create(bool autoReturn = true)
        {
            var c2G_GoLeaveRequest = MessageObjectPool<C2G_GoLeaveRequest>.Rent();
            c2G_GoLeaveRequest.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_GoLeaveRequest.SetIsPool(false);
            }
            
            return c2G_GoLeaveRequest;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            MessageObjectPool<C2G_GoLeaveRequest>.Return(this);
        }

        public void CopyTo(ref C2G_GoLeaveRequest other)
        {
            if (other == null)
            {
                other = Create(false);
            }

        }

        public C2G_GoLeaveRequest DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_GoLeaveRequest; } 
        [ProtoIgnore]
        public G2C_GoLeaveResponse ResponseType { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_GoLeaveResponse : AMessage, IResponse
    {
        public static G2C_GoLeaveResponse Create(bool autoReturn = true)
        {
            var g2C_GoLeaveResponse = MessageObjectPool<G2C_GoLeaveResponse>.Rent();
            g2C_GoLeaveResponse.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_GoLeaveResponse.SetIsPool(false);
            }
            
            return g2C_GoLeaveResponse;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            ErrorCode = 0;
            if (State != null)
            {
                State.Dispose();
                State = null;
            }
            Reason = default;
            MessageObjectPool<G2C_GoLeaveResponse>.Return(this);
        }

        public void CopyTo(ref G2C_GoLeaveResponse other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.ErrorCode = ErrorCode;
            other.State = State?.DeepCopy();
            other.Reason = Reason;

        }

        public G2C_GoLeaveResponse DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_GoLeaveResponse; } 
        [ProtoMember(3)]
        public uint ErrorCode { get; set; }
        [ProtoMember(1)]
        public GoRelayState State { get; set; }
        [ProtoMember(2)]
        public string Reason { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_GoHeartbeatRequest : AMessage, IRequest
    {
        public static C2G_GoHeartbeatRequest Create(bool autoReturn = true)
        {
            var c2G_GoHeartbeatRequest = MessageObjectPool<C2G_GoHeartbeatRequest>.Rent();
            c2G_GoHeartbeatRequest.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_GoHeartbeatRequest.SetIsPool(false);
            }
            
            return c2G_GoHeartbeatRequest;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            MessageObjectPool<C2G_GoHeartbeatRequest>.Return(this);
        }

        public void CopyTo(ref C2G_GoHeartbeatRequest other)
        {
            if (other == null)
            {
                other = Create(false);
            }

        }

        public C2G_GoHeartbeatRequest DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_GoHeartbeatRequest; } 
        [ProtoIgnore]
        public G2C_GoHeartbeatResponse ResponseType { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_GoHeartbeatResponse : AMessage, IResponse
    {
        public static G2C_GoHeartbeatResponse Create(bool autoReturn = true)
        {
            var g2C_GoHeartbeatResponse = MessageObjectPool<G2C_GoHeartbeatResponse>.Rent();
            g2C_GoHeartbeatResponse.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_GoHeartbeatResponse.SetIsPool(false);
            }
            
            return g2C_GoHeartbeatResponse;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            ErrorCode = 0;
            ServerUtcMs = default;
            Reason = default;
            MessageObjectPool<G2C_GoHeartbeatResponse>.Return(this);
        }

        public void CopyTo(ref G2C_GoHeartbeatResponse other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.ErrorCode = ErrorCode;
            other.ServerUtcMs = ServerUtcMs;
            other.Reason = Reason;

        }

        public G2C_GoHeartbeatResponse DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_GoHeartbeatResponse; } 
        [ProtoMember(3)]
        public uint ErrorCode { get; set; }
        [ProtoMember(1)]
        public long ServerUtcMs { get; set; }
        [ProtoMember(2)]
        public string Reason { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_GoState : AMessage, IMessage
    {
        public static G2C_GoState Create(bool autoReturn = true)
        {
            var g2C_GoState = MessageObjectPool<G2C_GoState>.Rent();
            g2C_GoState.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_GoState.SetIsPool(false);
            }
            
            return g2C_GoState;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            if (State != null)
            {
                State.Dispose();
                State = null;
            }
            MessageObjectPool<G2C_GoState>.Return(this);
        }

        public void CopyTo(ref G2C_GoState other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.State = State?.DeepCopy();

        }

        public G2C_GoState DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_GoState; } 
        [ProtoMember(1)]
        public GoRelayState State { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_TestMessage : AMessage, IMessage
    {
        public static C2G_TestMessage Create(bool autoReturn = true)
        {
            var c2G_TestMessage = MessageObjectPool<C2G_TestMessage>.Rent();
            c2G_TestMessage.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_TestMessage.SetIsPool(false);
            }
            
            return c2G_TestMessage;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            Tag = default;
            MessageObjectPool<C2G_TestMessage>.Return(this);
        }

        public void CopyTo(ref C2G_TestMessage other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.Tag = Tag;

        }

        public C2G_TestMessage DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_TestMessage; } 
        [ProtoMember(1)]
        public string Tag { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class C2G_TestRequest : AMessage, IRequest
    {
        public static C2G_TestRequest Create(bool autoReturn = true)
        {
            var c2G_TestRequest = MessageObjectPool<C2G_TestRequest>.Rent();
            c2G_TestRequest.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                c2G_TestRequest.SetIsPool(false);
            }
            
            return c2G_TestRequest;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            Tag = default;
            MessageObjectPool<C2G_TestRequest>.Return(this);
        }

        public void CopyTo(ref C2G_TestRequest other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.Tag = Tag;

        }

        public C2G_TestRequest DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.C2G_TestRequest; } 
        [ProtoIgnore]
        public G2C_TestResponse ResponseType { get; set; }
        [ProtoMember(1)]
        public string Tag { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2C_TestResponse : AMessage, IResponse
    {
        public static G2C_TestResponse Create(bool autoReturn = true)
        {
            var g2C_TestResponse = MessageObjectPool<G2C_TestResponse>.Rent();
            g2C_TestResponse.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2C_TestResponse.SetIsPool(false);
            }
            
            return g2C_TestResponse;
        }
        
        public void Return()
        {
            if (!AutoReturn)
            {
                SetIsPool(true);
                AutoReturn = true;
            }
            else if (!IsPool())
            {
                return;
            }
            Dispose();
        }

        public void Dispose()
        {
            if (!IsPool()) return; 
            ErrorCode = 0;
            Tag = default;
            MessageObjectPool<G2C_TestResponse>.Return(this);
        }

        public void CopyTo(ref G2C_TestResponse other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.ErrorCode = ErrorCode;
            other.Tag = Tag;

        }

        public G2C_TestResponse DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return OuterOpcode.G2C_TestResponse; } 
        [ProtoMember(2)]
        public uint ErrorCode { get; set; }
        [ProtoMember(1)]
        public string Tag { get; set; }
    }
}