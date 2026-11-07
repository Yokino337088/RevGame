using System.Runtime.CompilerServices;
using Fantasy;
using Fantasy.Async;
using Fantasy.Network;
using System.Collections.Generic;
#pragma warning disable CS8618
namespace Fantasy
{
   public static class NetworkProtocolHelper
   {
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoHelloResponse> C2G_GoHelloRequest(this Session session, C2G_GoHelloRequest C2G_GoHelloRequest_request)
		{
			return (G2C_GoHelloResponse)await session.Call(C2G_GoHelloRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoHelloResponse> C2G_GoHelloRequest(this Session session, string coreVersion, string nickname, string resumeToken, string rulesJson, string matchKind, bool supportsDelta)
		{
			using var C2G_GoHelloRequest_request = Fantasy.C2G_GoHelloRequest.Create();
			C2G_GoHelloRequest_request.CoreVersion = coreVersion;
			C2G_GoHelloRequest_request.Nickname = nickname;
			C2G_GoHelloRequest_request.ResumeToken = resumeToken;
			C2G_GoHelloRequest_request.RulesJson = rulesJson;
			C2G_GoHelloRequest_request.MatchKind = matchKind;
			C2G_GoHelloRequest_request.SupportsDelta = supportsDelta;
			return (G2C_GoHelloResponse)await session.Call(C2G_GoHelloRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoMatchResponse> C2G_GoMatchRequest(this Session session, C2G_GoMatchRequest C2G_GoMatchRequest_request)
		{
			return (G2C_GoMatchResponse)await session.Call(C2G_GoMatchRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoMatchResponse> C2G_GoMatchRequest(this Session session, bool cancel, int localRating)
		{
			using var C2G_GoMatchRequest_request = Fantasy.C2G_GoMatchRequest.Create();
			C2G_GoMatchRequest_request.Cancel = cancel;
			C2G_GoMatchRequest_request.LocalRating = localRating;
			return (G2C_GoMatchResponse)await session.Call(C2G_GoMatchRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoCommandResponse> C2G_GoCommandRequest(this Session session, C2G_GoCommandRequest C2G_GoCommandRequest_request)
		{
			return (G2C_GoCommandResponse)await session.Call(C2G_GoCommandRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoCommandResponse> C2G_GoCommandRequest(this Session session, string roomId, GoRelayCommand command)
		{
			using var C2G_GoCommandRequest_request = Fantasy.C2G_GoCommandRequest.Create();
			C2G_GoCommandRequest_request.RoomId = roomId;
			C2G_GoCommandRequest_request.Command = command;
			return (G2C_GoCommandResponse)await session.Call(C2G_GoCommandRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoSyncResponse> C2G_GoSyncRequest(this Session session, C2G_GoSyncRequest C2G_GoSyncRequest_request)
		{
			return (G2C_GoSyncResponse)await session.Call(C2G_GoSyncRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoSyncResponse> C2G_GoSyncRequest(this Session session)
		{
			using var C2G_GoSyncRequest_request = Fantasy.C2G_GoSyncRequest.Create();
			return (G2C_GoSyncResponse)await session.Call(C2G_GoSyncRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoLeaveResponse> C2G_GoLeaveRequest(this Session session, C2G_GoLeaveRequest C2G_GoLeaveRequest_request)
		{
			return (G2C_GoLeaveResponse)await session.Call(C2G_GoLeaveRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoLeaveResponse> C2G_GoLeaveRequest(this Session session)
		{
			using var C2G_GoLeaveRequest_request = Fantasy.C2G_GoLeaveRequest.Create();
			return (G2C_GoLeaveResponse)await session.Call(C2G_GoLeaveRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoHeartbeatResponse> C2G_GoHeartbeatRequest(this Session session, C2G_GoHeartbeatRequest C2G_GoHeartbeatRequest_request)
		{
			return (G2C_GoHeartbeatResponse)await session.Call(C2G_GoHeartbeatRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_GoHeartbeatResponse> C2G_GoHeartbeatRequest(this Session session)
		{
			using var C2G_GoHeartbeatRequest_request = Fantasy.C2G_GoHeartbeatRequest.Create();
			return (G2C_GoHeartbeatResponse)await session.Call(C2G_GoHeartbeatRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_GoState(this Session session, G2C_GoState G2C_GoState_message)
		{
			session.Send(G2C_GoState_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void G2C_GoState(this Session session, GoRelayState state)
		{
			using var G2C_GoState_message = Fantasy.G2C_GoState.Create();
			G2C_GoState_message.State = state;
			session.Send(G2C_GoState_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_TestMessage(this Session session, C2G_TestMessage C2G_TestMessage_message)
		{
			session.Send(C2G_TestMessage_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void C2G_TestMessage(this Session session, string tag)
		{
			using var C2G_TestMessage_message = Fantasy.C2G_TestMessage.Create();
			C2G_TestMessage_message.Tag = tag;
			session.Send(C2G_TestMessage_message);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_TestResponse> C2G_TestRequest(this Session session, C2G_TestRequest C2G_TestRequest_request)
		{
			return (G2C_TestResponse)await session.Call(C2G_TestRequest_request);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static async FTask<G2C_TestResponse> C2G_TestRequest(this Session session, string tag)
		{
			using var C2G_TestRequest_request = Fantasy.C2G_TestRequest.Create();
			C2G_TestRequest_request.Tag = tag;
			return (G2C_TestResponse)await session.Call(C2G_TestRequest_request);
		}

   }
}