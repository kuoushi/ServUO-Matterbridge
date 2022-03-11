using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.IO;
using Refit;
using Server.Engines.Chat;

namespace Server.Custom
{
	public interface IMatterbridgeClient
	{
		[Get("/api/messages")]
		Task<List<MatterbridgeMessage>> GetNextMessages([Authorize("Bearer")] string token);

		[Post("/api/message")]
		Task PostMessage([Authorize("Bearer")] string token, [Refit.Body] MatterbridgePostMessage message);
	}
	
	public class MatterbridgeMessage : MatterbridgePostMessage
	{
		[JsonPropertyName("channel")]
		public string Channel { get; set; }
		[JsonPropertyName("userid")]
		public string UserId { get; set; }
		[JsonPropertyName("account")]
		public string Account { get; set; }
		[JsonPropertyName("protocol")]
		public string Protocol { get; set; }
		[JsonPropertyName("parent_id")]
		public string ParentId { get; set; }
		[JsonPropertyName("timestamp")]
		public string Timestamp { get; set; }
		[JsonPropertyName("id")]
		public string Id { get; set; }
		[JsonPropertyName("extra")]
		public object Extra { get; set; }
		public MatterbridgeMessage(string gateway, string username, string text) : base(gateway, username, text)
		{
			
		}
		public override string ToString()
		{
			return "[" + Protocol + "] " + Channel + " | " + Username + ": " + Text;
		}
	}

	public class MatterbridgePostMessage
	{
		[JsonPropertyName("text")]
		public string Text { get; set; }
		[JsonPropertyName("username")]
		public string Username { get; set; }
		[JsonPropertyName("avatar")]
		public string Avatar { get; set; }
		[JsonPropertyName("event")]
		public string Event { get; set; }
		[JsonPropertyName("gateway")]
		public string Gateway { get; set; }

		public MatterbridgePostMessage(string gateway, string username, string text)
		{
			Gateway = gateway;
			Username = username;
			Text = text;
		}
		public override string ToString()
		{
			return Gateway + " | " + Event + " | " + Username + " | " + Text;
		}
	}

	public class MatterbridgeConfig
	{
		public string TargetToken => m_Vars["TargetToken"];
		public string TargetGateway => m_Vars["TargetGateway"];
		public string TargetAddress => m_Vars["TargetAddress"];
		public int TargetPort => Int32.Parse(m_Vars["TargetPort"]);
		public string TargetUri => "http://" + m_Vars["TargetAddress"] + ":" + m_Vars["TargetPort"];
		public string ChatChannel => m_Vars["ChatChannel"];
		public string CustomFormat => m_Vars["MessageFormat"];
		public bool AutoJoinChatChannel => IsBooleanKeyEnabled("AutoJoinChatChannel");
		public bool IncludeWorldChat => IsBooleanKeyEnabled("IncludeWorldChat");

		private Dictionary<string, string> m_Vars;

		public MatterbridgeConfig(string filename)
		{
			m_Vars = new Dictionary<string, string>();
			var path = Path.Combine("Scripts/Custom", filename);
			FileInfo cfg = new FileInfo(path);
			if (cfg.Exists)
			{
				using (StreamReader stream = new StreamReader(cfg.FullName))
				{
					String line;
					while ((line = stream.ReadLine()) != null)
					{
						if (!line.StartsWith("#"))
						{
							var parts = line.Split('=');
							if (parts.Length == 2)
							{
								var key = parts[0];
								var value = parts[1];
								m_Vars.Add(key, value);
							}
						}
					}
				}
			}
			else
			{
				throw new Exception("MatterbridgeConfig.cfg file is missing.");
			}
		}

		private bool IsBooleanKeyEnabled(string key)
		{
			if (m_Vars.ContainsKey(key) && m_Vars[key].ToLower() == "true")
				return true;
			return false;
		}
	}

	public static class Matterbridge
	{

		private static IMatterbridgeClient matterbridgeClient;
		private static MatterbridgeConfig matterbridgeConfig;
		// private static Dictionary<string, string> m_Tags;

		public static void Configure()
		{
			matterbridgeConfig = new MatterbridgeConfig("MatterbridgeConfig.cfg");
			// m_Tags = new Dictionary<string, string>();
			// m_Tags.Add();

			var options = new JsonSerializerOptions()
			{
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
				WriteIndented = true,
			};

			matterbridgeClient = RestService.For<IMatterbridgeClient>(matterbridgeConfig.TargetUri, new RefitSettings { ContentSerializer = new SystemTextJsonContentSerializer(options) });

			if (matterbridgeConfig.IncludeWorldChat)
			{
				EventSink.Speech += EventSink_Speech;
			}

			if (!IsRconPacketHandlersEnabled())
            {
				if (matterbridgeConfig.ChatChannel != "*")
				{
					Channel.AddStaticChannel(matterbridgeConfig.ChatChannel);
					if (matterbridgeConfig.AutoJoinChatChannel)
					{
						EventSink.Login += EventSink_JoinDefaultChannelAtLogin;

						// quietly ignore General channel join attempt on player login for 5 seconds
						// ClassicUO client sends a join request when entering the game, but we want players in our channel
						ChatActionHandlers.Register(0x62, false, new OnChatAction(BlockGeneralAtLogin));
					}
				}
			}

			ChatActionHandlers.Register(0x61, true, new OnChatAction(OnServUOChatReceived));
			Listen();
		}

		private static void Listen()
		{
			Task.Run(async () =>
			{
				while (true)
				{
					try
					{
						var nextmessages = await matterbridgeClient.GetNextMessages(matterbridgeConfig.TargetToken);

						foreach (var x in nextmessages)
						{
							OnMatterbridgeMessageReceived(x);
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine(ex.Message);
					}

					Task.Delay(200).Wait();
				}
			});
		}

		private static void EventSink_Speech(SpeechEventArgs e)
		{
			Mobile from = e.Mobile;
			if (from is Mobiles.PlayerMobile)
			{
				matterbridgeClient.PostMessage(matterbridgeConfig.TargetToken, FormatMatterbridgeMessage(null, from, e.Speech));
			}
		}

		private static void EventSink_JoinDefaultChannelAtLogin(LoginEventArgs e)
		{
			var from = e.Mobile;
			var defaultChannel = Channel.FindChannelByName(matterbridgeConfig.ChatChannel);
			var chatUser = ChatUser.AddChatUser(from);
			defaultChannel.AddUser(chatUser);
		}

		public static void BlockGeneralAtLogin(ChatUser from, Channel channel, string param)
		{
			if (param.Contains("General") && from.Mobile.NetState.ConnectedFor.TotalSeconds < 5)
				return;

			ChatActionHandlers.JoinChannel(from, channel, param);
		}

		public static void OnMatterbridgeMessageReceived(MatterbridgeMessage message)
		{
			if (matterbridgeConfig.ChatChannel != "*")
			{
				Channel c = Channel.FindChannelByName(matterbridgeConfig.ChatChannel);
				if (c != null)
				{
					foreach (ChatUser user in c.Users)
					{
						user.Mobile.SendMessage(0, message.ToString());
					}
				}
			}
			else
			{
				World.Broadcast(0, false, message.ToString());
			}
		}

		private static void OnServUOChatReceived(ChatUser from, Channel channel, string param)
		{
			if (IsRconPacketHandlersEnabled())
			{
				RconPacketHandlersRelayChatPacket(from, channel, param);
			}
			else
			{
				ChatActionHandlers.ChannelMessage(from, channel, param);
			}

			if (channel.Name == matterbridgeConfig.ChatChannel || matterbridgeConfig.ChatChannel == "*")
			{
				matterbridgeClient.PostMessage(matterbridgeConfig.TargetToken, FormatMatterbridgeMessage(null, from.Mobile, param, channel));
			}
		}

		private static bool IsRconPacketHandlersEnabled()
		{
			var rconPacketHandlersClass = Type.GetType("Server.RemoteAdmin.RconPacketHandlers");
			if (rconPacketHandlersClass != null)
				return true;
			return false;
		}

		private static void RconPacketHandlersRelayChatPacket(ChatUser from, Channel channel, string param)
		{
			var rconPacketHandlersClass = Type.GetType("Server.RemoteAdmin.RconPacketHandlers");
			var m = rconPacketHandlersClass.GetMethod("RelayChatPacket");
			object[] parameters = {from, channel, param};
			m.Invoke(rconPacketHandlersClass, parameters);
		}

		public static MatterbridgePostMessage FormatMatterbridgeMessage(string type, Mobile from, string message = null, Channel channel = null)
		{
			var t = matterbridgeConfig.CustomFormat;
			if (message != null)
			{
				t = t.Replace("{name}", from.Name).Replace("{message}", message).Replace("{account}", from.Account.Username).Replace("{serial}", from.Serial.Value.ToString()).Replace("{region}", from.Region.Name).Replace("{coords.x}", from.X.ToString()).Replace("{coords.y}", from.Y.ToString()).Replace("{coords.z}", from.Z.ToString()).Replace("{ip}", from.NetState.Address.ToString());
				if (from.Guild != null)
					t = t.Replace("{guild}", " (" + from.Guild.Name + ")");
				else
					t = t.Replace("{guild}", string.Empty);
				if (!from.Alive)
					t = t.Replace("{dead}", " <Dead>");
				else
					t = t.Replace("{dead}", string.Empty);
				if (from.Account.Young)
					t = t.Replace("{young}", " <Young>");
				else
					t = t.Replace("{young}", string.Empty);
				if (from.Criminal)
					t = t.Replace("{criminal}", " <Criminal>");
				else
					t = t.Replace("{criminal}", string.Empty);
				if (from.Murderer)
					t = t.Replace(" {murderer}", " <Murderer>");
				else
					t = t.Replace("{murderer}", string.Empty);
				if (channel == null)
					t = t.Replace("{channel}", "World");
				else
					t = t.Replace("{channel}", channel.Name);
				if (t.Contains("{coords}"))
					t = t.Replace("{coords}", from.X + "," + from.Y + "," + from.Z);
			}
			return new MatterbridgePostMessage(matterbridgeConfig.TargetGateway, from.Name, t);
		}
	}
}
