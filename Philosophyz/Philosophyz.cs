using System;
using System.IO;
using System.Linq;
using System.Reflection;
using OTAPI;
using Philosophyz.Hooks;
using Terraria;
using Terraria.GameContent.Events;
using Terraria.Localization;
using Terraria.Social;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace Philosophyz
{
	[ApiVersion(2, 1)]
	public class Philosophyz : TerrariaPlugin
	{
		const bool DefaultFakeSscStatus = false;
		const double DefaultCheckTime = 1.5d;
		public override string Name => Assembly.GetExecutingAssembly().GetName().Name;
		public override string Author => "MistZZT";
		public override string Description => "Dark";
		public override Version Version => Assembly.GetExecutingAssembly().GetName().Version;

		internal PzRegionManager PzRegions;
		OTAPI.Hooks.Net.SendDataHandler _tsapiHandler;
		DateTime _lastCheck = DateTime.UtcNow;

		public Philosophyz(Main game) : base(game) { Order = 0; }// 最早
		public override void Initialize()
		{
			ServerApi.Hooks.GameInitialize.Register(this, OnInit);
			ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInit);
			ServerApi.Hooks.GameUpdate.Register(this, OnUpdate, 9000);

			RegionHooks.RegionDeleted += OnRegionDeleted;

			_tsapiHandler = OTAPI.Hooks.Net.SendData;
			OTAPI.Hooks.Net.SendData = OnOtapiSendData;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				ServerApi.Hooks.GameInitialize.Deregister(this, OnInit);
				ServerApi.Hooks.GamePostInitialize.Deregister(this, OnPostInit);

				RegionHooks.RegionDeleted -= OnRegionDeleted;

				OTAPI.Hooks.Net.SendData = _tsapiHandler;
			}
			base.Dispose(disposing);
		}

		void OnUpdate(EventArgs args)
		{
			if ((DateTime.UtcNow - _lastCheck).TotalSeconds < DefaultCheckTime) return;

			foreach (var player in TShock.Players.Where(p => p?.Active == true))
			{
				var info = PlayerInfo.GetPlayerInfo(player);
				var oldRegion = info.CurrentRegion;
				info.CurrentRegion = TShock.Regions.GetTopRegion(TShock.Regions.InAreaRegion(player.TileX, player.TileY));

				if (oldRegion == info.CurrentRegion)continue;
				var shouldInvokeLeave = true;

				// 若是pz区域，则更换模式；不需要在离开区域时再次复原或保存备份。
				if (info.CurrentRegion != null)
				{
					var region = PzRegions.GetRegionById(info.CurrentRegion.ID);

					if (region != null && !info.BypassChange)
					{
						info.FakeSscStatus = true;

						if (!info.InSscRegion)
						{
							info.InSscRegion = true;
							info.SetBackupPlayerData();
						}

						if (region.HasDefault)
							info.ChangeCharacter(region.GetDefaultData());

						shouldInvokeLeave = false;
					}
				}
				// 如果从区域出去，且没有进入新pz区域，则恢复
				if (shouldInvokeLeave && oldRegion != null)
				{
					if (!info.InSscRegion || info.FakeSscStatus == DefaultFakeSscStatus)continue;

					info.RestoreCharacter();

					info.InSscRegion = false;
					info.FakeSscStatus = false;
				}
			}

			_lastCheck = DateTime.UtcNow;
		}

		HookResult OnOtapiSendData(ref int bufferId, ref int msgType, ref int remoteClient, ref int ignoreClient, ref NetworkText text, ref int number, ref float number2, ref float number3, ref float number4, ref int number5, ref int number6, ref int number7)
		{
			if (msgType != (int)PacketTypes.WorldInfo)
				return _tsapiHandler(ref bufferId, ref msgType, ref remoteClient, ref ignoreClient, ref text, ref number, ref number2, ref number3, ref number4, ref number5, ref number6, ref number7);

			if (remoteClient == -1)
			{
				var onData = PackInfo(true);
				var offData = PackInfo(false);

				foreach (var tsPlayer in TShock.Players.Where(p => p?.Active == true))
				{
					if (!SendDataHooks.InvokePreSendData(remoteClient, tsPlayer.Index)) continue;
					try
					{
						tsPlayer.SendRawData(PlayerInfo.GetPlayerInfo(tsPlayer).FakeSscStatus ?? DefaultFakeSscStatus ? onData : offData);
					}
					catch
					{
						// ignored
					}
					SendDataHooks.InvokePostSendData(remoteClient, tsPlayer.Index);
				}
			}
			else
			{
				var player = TShock.Players.ElementAtOrDefault(remoteClient);

				if (player != null)
				{
					var info = PlayerInfo.GetPlayerInfo(player);

					/* 如果在区域内，收到了来自别的插件的发送请求
					 * 保持默认 ssc = true 并发送(也就是不需要改什么)
					 * 如果在区域外，收到了来自别的插件的发送请求
					 * 需要 fake ssc = false 并发送
					 */
					SendInfo(remoteClient, info.FakeSscStatus ?? DefaultFakeSscStatus);
				}
			}

			return HookResult.Cancel;
		}

		void OnPostInit(EventArgs args)=>PzRegions.ReloadRegions();
		void OnInit(EventArgs args)
		{
			if (!TShock.ServerSideCharacterConfig.Enabled)
			{
				TShock.Log.ConsoleError("[Pz] 未开启SSC! 你可能选错了插件.");
				Dispose(true);
				throw new NotSupportedException("该插件不支持非SSC模式运行!");
			}

			Commands.ChatCommands.Add(new Command("pz.admin.manage", PzCmd, "pz") { AllowServer = false });
			Commands.ChatCommands.Add(new Command("pz.admin.toggle", ToggleBypass, "pztoggle") { AllowServer = false });
			Commands.ChatCommands.Add(new Command("pz.select", PzSelect, "pzselect") { AllowServer = false });

			PzRegions = new PzRegionManager(TShock.DB);
		}

		void OnRegionDeleted(RegionHooks.RegionDeletedEventArgs args)
		{
			if (!PzRegions.PzRegions.Exists(p => p.Id == args.Region.ID)) return;

			PzRegions.RemoveRegion(args.Region.ID);
		}

		void PzCmd(CommandArgs args)
		{
			var cmd = args.Parameters.Count == 0 ? "HELP" : args.Parameters[0].ToUpperInvariant();

			switch (cmd)
			{
				case "ADD":
					#region add
					if (args.Parameters.Count < 3)
					{
						args.Player.SendErrorMessage("语法无效! 正确语法: /pz add <区域名> <存档名> [玩家名]");
						return;
					}

					var regionName = args.Parameters[1];
					var name = args.Parameters[2];
					var playerName = args.Parameters.ElementAtOrDefault(3);

					if (name.Length > 10)
					{
						args.Player.SendErrorMessage("存档名的长度不能超过10!");
						return;
					}

					var region = TShock.Regions.GetRegionByName(regionName);
					if (region == null)
					{
						args.Player.SendErrorMessage("区域名无效!");
						return;
					}
					TSPlayer player = null;
					if (!string.IsNullOrWhiteSpace(playerName))
					{
						var players = TSPlayer.FindByNameOrID(playerName);
						if (players.Count == 0)
						{
							args.Player.SendErrorMessage("未找到玩家!");
							return;
						}
						if (players.Count > 1)
						{
							args.Player.SendMultipleMatchError(players.Select(p => p.Name));
							return;
						}
						player = players[0];
					}
					player = player ?? args.Player;
					var data = new PlayerData(null);
					data.CopyCharacter(player);

					PzRegions.AddRegion(region.ID);
					PzRegions.AddCharacter(region.ID, name, data);
					args.Player.SendSuccessMessage("添加区域完毕.");
					#endregion
					break;
				case "LIST":
					#region list
					int pageNumber;
					if (!PaginationTools.TryParsePageNumber(args.Parameters, 1, args.Player, out pageNumber))
						return;
					var names = from pz in PzRegions.PzRegions
								select TShock.Regions.GetRegionByID(pz.Id).Name + ": " + string.Join(", ", pz.PlayerDatas.Keys);
					PaginationTools.SendPage(args.Player, pageNumber, PaginationTools.BuildLinesFromTerms(names),
						new PaginationTools.Settings
						{
							HeaderFormat = "应用区域 ({0}/{1}):",
							FooterFormat = "键入 {0}pz list {{0}} 以获取下一页应用区域.".SFormat(Commands.Specifier),
							NothingToDisplayString = "当前没有可用应用区域."
						});
					#endregion
					break;
				case "REMOVE":
					#region remove
					if (args.Parameters.Count == 1)
					{
						args.Player.SendErrorMessage("语法无效! 正确语法: /pz remove <区域名>");
						return;
					}
					regionName = string.Join(" ", args.Parameters.Skip(1));
					region = TShock.Regions.GetRegionByName(regionName);
					if (region == null)
					{
						args.Player.SendErrorMessage("区域名无效!");
						return;
					}

					PzRegions.RemoveRegion(region.ID);
					args.Player.SendSuccessMessage("删除区域及存档完毕.");
					#endregion
					break;
				case "REMOVECHAR":
					#region removeChar
					if (args.Parameters.Count < 3)
					{
						args.Player.SendErrorMessage("语法无效! 正确语法: /pz removechar <区域名> <存档名>");
						return;
					}
					regionName = args.Parameters[1];
					name = args.Parameters[2];
					region = TShock.Regions.GetRegionByName(regionName);
					if (region == null)
					{
						args.Player.SendErrorMessage("区域名无效!");
						return;
					}

					PzRegions.RemoveCharacter(region.ID, name);
					args.Player.SendSuccessMessage("删除存档完毕.");
					#endregion
					break;
				case "DEFAULT":
					#region default
					if (args.Parameters.Count < 3)
					{
						args.Player.SendErrorMessage("语法无效! 正确语法: /pz default <区域名> <存档名>");
						return;
					}
					regionName = args.Parameters[1];
					name = args.Parameters[2];
					region = TShock.Regions.GetRegionByName(regionName);
					if (region == null)
					{
						args.Player.SendErrorMessage("区域名无效!");
						return;
					}

					var pzregion = PzRegions.GetRegionById(region.ID);
					if (pzregion == null)
					{
						args.Player.SendErrorMessage("该区域并卟是Pz区域!");
						return;
					}
					if (!pzregion.PlayerDatas.ContainsKey(name))
					{
						args.Player.SendErrorMessage("区域内未找到符合条件的存档!");
						return;
					}

					PzRegions.SetDefaultCharacter(region.ID, name);
					args.Player.SendSuccessMessage("设定存档完毕.");
					#endregion
					break;
				case "DELDEFAULT":
					#region deldefault
					if (args.Parameters.Count == 1)
					{
						args.Player.SendErrorMessage("语法无效! 正确语法: /pz deldefault <区域名>");
						return;
					}
					regionName = string.Join(" ", args.Parameters.Skip(1));
					region = TShock.Regions.GetRegionByName(regionName);
					if (region == null)
					{
						args.Player.SendErrorMessage("区域名无效!");
						return;
					}

					pzregion = PzRegions.GetRegionById(region.ID);
					if (pzregion == null)
					{
						args.Player.SendErrorMessage("该区域并卟是Pz区域!");
						return;
					}

					PzRegions.SetDefaultCharacter(region.ID, null);
					args.Player.SendSuccessMessage("移除默认存档完毕.");
					#endregion
					break;
				case "SHOW":
				case "RESTORE":
					args.Player.SendErrorMessage("暂不支持该功能.");
					break;
				case "HELP":
					#region help
					if (!PaginationTools.TryParsePageNumber(args.Parameters, 1, args.Player, out pageNumber))
						return;
					var help = new[]
					{
						"add <区域名> <存档名> [玩家名(默认为自己)] - - 增加区域内存档",
						"remove <区域名> - - 删除区域内所有存档",
						"removechar <区域名> <存档名> - - 删除区域内存档",
						"default <区域名> <存档名> - - 设置单一存档默认值",
						"deldefault <区域名> - - 删除单一存档默认值",
						"list [页码] - - 显示所有区域",
						"help [页码] - - 显示子指令帮助"
					};
					PaginationTools.SendPage(args.Player, pageNumber, help,
						new PaginationTools.Settings
						{
							HeaderFormat = "应用区域指令帮助 ({0}/{1}):",
							FooterFormat = "键入 {0}pz help {{0}} 以获取下一页应用区域帮助.".SFormat(Commands.Specifier),
							NothingToDisplayString = "当前没有可用帮助."
						});
					#endregion
					break;
				default:
					args.Player.SendErrorMessage("语法无效! 键入 /pz help 以获取帮助.");
					return;
			}
		}
		static void ToggleBypass(CommandArgs args)
		{
			var info = PlayerInfo.GetPlayerInfo(args.Player);
			info.BypassChange = !info.BypassChange;
			args.Player.SendSuccessMessage("{0}调整跳过装备更换模式。", info.BypassChange ? "关闭" : "开启");
		}
		void PzSelect(CommandArgs args)
		{
			if (args.Parameters.Count == 0)
				args.Player.SendErrorMessage("参数错误！正确用法：/pzselect <存档名>");

			if (args.Player.CurrentRegion == null)
			{
				args.Player.SendInfoMessage("区域无效。");
				return;
			}

			var region = PzRegions.GetRegionById(args.Player.CurrentRegion.ID);
			if (region == null)
			{
				args.Player.SendInfoMessage("区域无效。");
				return;
			}

			var name = string.Join(" ", args.Parameters);
			if (!region.PlayerDatas.TryGetValue(name, out PlayerData data))
			{
				args.Player.SendInfoMessage("未找到对应存档名。");
				return;
			}

			PlayerInfo.GetPlayerInfo(args.Player).ChangeCharacter(data);
			args.Player.SendInfoMessage("你的人物存档被切换为{0}。", name);
		}

		static byte[] PackInfo(bool ssc)
		{
			int s;BitsByte o;
			var memoryStream = new MemoryStream();
			var writer = new BinaryWriter(memoryStream);
			var position = writer.BaseStream.Position;
			writer.BaseStream.Position += 2L;
			writer.Write((byte)PacketTypes.WorldInfo);

			writer.Write((int)Main.time);
			o = 0;
			o[0] = Main.dayTime;
			o[1] = Main.bloodMoon;
			o[2] = Main.eclipse;
			writer.Write(o);
			writer.Write((byte)Main.moonPhase);
			writer.Write((short)Main.maxTilesX);
			writer.Write((short)Main.maxTilesY);
			writer.Write((short)Main.spawnTileX);
			writer.Write((short)Main.spawnTileY);
			writer.Write((short)Main.worldSurface);
			writer.Write((short)Main.rockLayer);
			writer.Write(Main.worldID);
			writer.Write(Main.worldName);
			writer.Write((byte)Main.GameMode);
			writer.Write(Main.ActiveWorldFileData.UniqueId.ToByteArray());
			writer.Write(Main.ActiveWorldFileData.WorldGeneratorVersion);
			writer.Write((byte)Main.moonType);
			writer.Write((byte)WorldGen.treeBG1);
			writer.Write((byte)WorldGen.treeBG2);
			writer.Write((byte)WorldGen.treeBG3);
			writer.Write((byte)WorldGen.treeBG4);
			writer.Write((byte)WorldGen.corruptBG);
			writer.Write((byte)WorldGen.jungleBG);
			writer.Write((byte)WorldGen.snowBG);
			writer.Write((byte)WorldGen.hallowBG);
			writer.Write((byte)WorldGen.crimsonBG);
			writer.Write((byte)WorldGen.desertBG);
			writer.Write((byte)WorldGen.oceanBG);
			writer.Write((byte)WorldGen.mushroomBG);
			writer.Write((byte)WorldGen.underworldBG);
			writer.Write((byte)Main.iceBackStyle);
			writer.Write((byte)Main.jungleBackStyle);
			writer.Write((byte)Main.hellBackStyle);
			writer.Write(Main.windSpeedTarget);
			writer.Write((byte)Main.numClouds);
			for (s = 0; s < 3; s++) writer.Write(Main.treeX[s]);
			for (s = 0; s < 4; s++) writer.Write((byte)Main.treeStyle[s]);
			for (s = 0; s < 3; s++) writer.Write(Main.caveBackX[s]);
			for (s = 0; s < 4; s++) writer.Write((byte)Main.caveBackStyle[s]);
			WorldGen.TreeTops.SyncSend(writer);
			if (!Main.raining)Main.maxRaining = 0f;
			writer.Write(Main.maxRaining);
			o = 0;
			o[0] = WorldGen.shadowOrbSmashed;
			o[1] = NPC.downedBoss1;
			o[2] = NPC.downedBoss2;
			o[3] = NPC.downedBoss3;
			o[4] = Main.hardMode;
			o[5] = NPC.downedClown;
			o[6] = ssc;
			o[7] = NPC.downedPlantBoss;
			writer.Write(o);
			o = 0;
			o[0] = NPC.downedMechBoss1;
			o[1] = NPC.downedMechBoss2;
			o[2] = NPC.downedMechBoss3;
			o[3] = NPC.downedMechBossAny;
			o[4] = Main.cloudBGActive >= 1f;
			o[5] = WorldGen.crimson;
			o[6] = Main.pumpkinMoon;
			o[7] = Main.snowMoon;
			writer.Write(o);
			o = 0;
			o[0] = Main.expertMode;
			o[1] = Main.fastForwardTime;
			o[2] = Main.slimeRain;
			o[3] = NPC.downedSlimeKing;
			o[4] = NPC.downedQueenBee;
			o[5] = NPC.downedFishron;
			o[6] = NPC.downedMartians;
			o[7] = NPC.downedAncientCultist;
			writer.Write(o);
			o = 0;
			o[0] = NPC.downedMoonlord;
			o[1] = NPC.downedHalloweenKing;
			o[2] = NPC.downedHalloweenTree;
			o[3] = NPC.downedChristmasIceQueen;
			o[4] = NPC.downedChristmasSantank;
			o[5] = NPC.downedChristmasTree;
			o[6] = NPC.downedGolemBoss;
			o[7] = BirthdayParty.PartyIsUp;
			writer.Write(o);
			o = 0;
			o[0] = NPC.downedPirates;
			o[1] = NPC.downedFrost;
			o[2] = NPC.downedGoblins;
			o[3] = Sandstorm.Happening;
			o[4] = DD2Event.Ongoing;
			o[5] = DD2Event.DownedInvasionT1;
			o[6] = DD2Event.DownedInvasionT2;
			o[7] = DD2Event.DownedInvasionT3;
			writer.Write(o);
			o = 0;
			o[0] = NPC.combatBookWasUsed;
			o[1] = LanternNight.LanternsUp;
			o[2] = NPC.downedTowerSolar;
			o[3] = NPC.downedTowerVortex;
			o[4] = NPC.downedTowerNebula;
			o[5] = NPC.downedTowerStardust;
			o[6] = Main.forceHalloweenForToday;
			o[7] = Main.forceXMasForToday;
			writer.Write(o);
			o = 0;
			o[0] = NPC.boughtCat;
			o[1] = NPC.boughtDog;
			o[2] = NPC.boughtBunny;
			o[3] = NPC.freeCake;
			o[4] = Main.drunkWorld;
			o[5] = NPC.downedEmpressOfLight;
			o[6] = NPC.downedQueenSlime;
			o[7] = Main.getGoodWorld;
			writer.Write(o);
			writer.Write((short)WorldGen.SavedOreTiers.Copper);
			writer.Write((short)WorldGen.SavedOreTiers.Iron);
			writer.Write((short)WorldGen.SavedOreTiers.Silver);
			writer.Write((short)WorldGen.SavedOreTiers.Gold);
			writer.Write((short)WorldGen.SavedOreTiers.Cobalt);
			writer.Write((short)WorldGen.SavedOreTiers.Mythril);
			writer.Write((short)WorldGen.SavedOreTiers.Adamantite);
			writer.Write((sbyte)Main.invasionType);
			writer.Write(SocialAPI.Network != null ? SocialAPI.Network.GetLobbyId() : 0UL);
			writer.Write(Sandstorm.IntendedSeverity);

			var currentPosition = (int)writer.BaseStream.Position;
			writer.BaseStream.Position = position;
			writer.Write((short)currentPosition);
			writer.BaseStream.Position = currentPosition;
			var data = memoryStream.ToArray();

			writer.Close();

			return data;
		}
		internal static void SendInfo(int remoteClient, bool ssc)
		{
			if (!SendDataHooks.InvokePreSendData(remoteClient, remoteClient))
				return;

			Main.ServerSideCharacter = ssc;

			NetMessage.SendDataDirect((int)PacketTypes.WorldInfo, remoteClient);

			Main.ServerSideCharacter = true;

			SendDataHooks.InvokePostSendData(remoteClient, remoteClient);
		}

	}
}
