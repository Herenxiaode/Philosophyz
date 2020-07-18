﻿using OTAPI;
namespace Philosophyz.Hooks
{
	public class SendDataHooks
	{
		public delegate HookResult PzSendData(int remoteClient, int index);
		public static PzSendData PreSendData;
		public static PzSendData PostSendData;
		internal static bool InvokePreSendData(int remoteClient, int index)
			=>PreSendData?.Invoke(remoteClient, index) != HookResult.Cancel;
		internal static void InvokePostSendData(int remoteClient, int index)
			=>PostSendData?.Invoke(remoteClient, index);
	}
}
