using LightProto;
using MemoryPack;
using System;
using System.Collections.Generic;
using MongoDB.Bson.Serialization.Attributes;
using Fantasy;
using Fantasy.Pool;
using Fantasy.Network.Interface;
using Fantasy.Serialize;

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
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
#pragma warning disable CS8618
#pragma warning disable CS8601 // Possible null reference assignment in generated message copies.
namespace Fantasy
{
    [Serializable]
    [ProtoContract]
    public partial class GoBattleStepInput : AMessage, IDisposable
    {
        public static GoBattleStepInput Create(bool autoReturn = true)
        {
            var goBattleStepInput = MessageObjectPool<GoBattleStepInput>.Rent();
            goBattleStepInput.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                goBattleStepInput.SetIsPool(false);
            }
            
            return goBattleStepInput;
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
            Komi = default;
            Sequence = default;
            Side = default;
            Kind = default;
            X = default;
            Y = default;
            ElapsedMs = default;
            Payload = default;
            PreviousHash = default;
            ResultHash = default;
            MessageObjectPool<GoBattleStepInput>.Return(this);
        }

        public void CopyTo(ref GoBattleStepInput other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.RoomId = RoomId;
            other.Komi = Komi;
            other.Sequence = Sequence;
            other.Side = Side;
            other.Kind = Kind;
            other.X = X;
            other.Y = Y;
            other.ElapsedMs = ElapsedMs;
            other.Payload = Payload;
            other.PreviousHash = PreviousHash;
            other.ResultHash = ResultHash;

        }

        public GoBattleStepInput DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        [ProtoMember(1)]
        public string RoomId { get; set; }
        [ProtoMember(2)]
        public int Komi { get; set; }
        [ProtoMember(3)]
        public int Sequence { get; set; }
        [ProtoMember(4)]
        public int Side { get; set; }
        [ProtoMember(5)]
        public int Kind { get; set; }
        [ProtoMember(6)]
        public int X { get; set; }
        [ProtoMember(7)]
        public int Y { get; set; }
        [ProtoMember(8)]
        public long ElapsedMs { get; set; }
        [ProtoMember(9)]
        public string Payload { get; set; }
        [ProtoMember(10)]
        public string PreviousHash { get; set; }
        [ProtoMember(11)]
        public string ResultHash { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class GoBattleStepResult : AMessage, IDisposable
    {
        public static GoBattleStepResult Create(bool autoReturn = true)
        {
            var goBattleStepResult = MessageObjectPool<GoBattleStepResult>.Rent();
            goBattleStepResult.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                goBattleStepResult.SetIsPool(false);
            }
            
            return goBattleStepResult;
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
            Reason = default;
            Finished = default;
            Winner = default;
            EndReason = default;
            DifferenceHalfPoints = default;
            MessageObjectPool<GoBattleStepResult>.Return(this);
        }

        public void CopyTo(ref GoBattleStepResult other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.Reason = Reason;
            other.Finished = Finished;
            other.Winner = Winner;
            other.EndReason = EndReason;
            other.DifferenceHalfPoints = DifferenceHalfPoints;

        }

        public GoBattleStepResult DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        [ProtoMember(1)]
        public string Reason { get; set; }
        [ProtoMember(2)]
        public bool Finished { get; set; }
        [ProtoMember(3)]
        public int Winner { get; set; }
        [ProtoMember(4)]
        public int EndReason { get; set; }
        [ProtoMember(5)]
        public int DifferenceHalfPoints { get; set; }
    }
    [Serializable]
    [ProtoContract]
    public partial class G2B_GoBatchRequest : AMessage, IAddressRequest
    {
        public static G2B_GoBatchRequest Create(bool autoReturn = true)
        {
            var g2B_GoBatchRequest = MessageObjectPool<G2B_GoBatchRequest>.Rent();
            g2B_GoBatchRequest.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2B_GoBatchRequest.SetIsPool(false);
            }
            
            return g2B_GoBatchRequest;
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
            foreach (var __t in Steps) __t.Dispose();
            Steps.Clear();
            MessageObjectPool<G2B_GoBatchRequest>.Return(this);
        }

        public void CopyTo(ref G2B_GoBatchRequest other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.Steps = new List<GoBattleStepInput>(Steps.Count);
            foreach (var __item in Steps) other.Steps.Add(__item.DeepCopy());

        }

        public G2B_GoBatchRequest DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return InnerOpcode.G2B_GoBatchRequest; } 
        [ProtoIgnore]
        public B2G_GoBatchResponse ResponseType { get; set; }
        [ProtoMember(1)]
        public List<GoBattleStepInput> Steps { get; set; } = new List<GoBattleStepInput>();
    }
    [Serializable]
    [ProtoContract]
    public partial class B2G_GoBatchResponse : AMessage, IAddressResponse
    {
        public static B2G_GoBatchResponse Create(bool autoReturn = true)
        {
            var b2G_GoBatchResponse = MessageObjectPool<B2G_GoBatchResponse>.Rent();
            b2G_GoBatchResponse.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                b2G_GoBatchResponse.SetIsPool(false);
            }
            
            return b2G_GoBatchResponse;
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
            foreach (var __t in Results) __t.Dispose();
            Results.Clear();
            MessageObjectPool<B2G_GoBatchResponse>.Return(this);
        }

        public void CopyTo(ref B2G_GoBatchResponse other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.ErrorCode = ErrorCode;
            other.Results = new List<GoBattleStepResult>(Results.Count);
            foreach (var __item in Results) other.Results.Add(__item.DeepCopy());

        }

        public B2G_GoBatchResponse DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return InnerOpcode.B2G_GoBatchResponse; } 
        [ProtoMember(2)]
        public uint ErrorCode { get; set; }
        [ProtoMember(1)]
        public List<GoBattleStepResult> Results { get; set; } = new List<GoBattleStepResult>();
    }
    [Serializable]
    [ProtoContract]
    public partial class G2B_CloseGoRoom : AMessage, IAddressMessage
    {
        public static G2B_CloseGoRoom Create(bool autoReturn = true)
        {
            var g2B_CloseGoRoom = MessageObjectPool<G2B_CloseGoRoom>.Rent();
            g2B_CloseGoRoom.AutoReturn = autoReturn;
            
            if (!autoReturn)
            {
                g2B_CloseGoRoom.SetIsPool(false);
            }
            
            return g2B_CloseGoRoom;
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
            MessageObjectPool<G2B_CloseGoRoom>.Return(this);
        }

        public void CopyTo(ref G2B_CloseGoRoom other)
        {
            if (other == null)
            {
                other = Create(false);
            }
            other.RoomId = RoomId;

        }

        public G2B_CloseGoRoom DeepCopy()
        {
            var other = Create(false);
            CopyTo(ref other);
            return other;
        }
        public uint OpCode() { return InnerOpcode.G2B_CloseGoRoom; } 
        [ProtoMember(1)]
        public string RoomId { get; set; }
    }
}