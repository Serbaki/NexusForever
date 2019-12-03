﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Map.Search;
using NexusForever.WorldServer.Game.Social.Model;
using NexusForever.WorldServer.Game.Social.Static;
using NexusForever.WorldServer.Network;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Network.Message.Model.Shared;
using NLog;
using Item = NexusForever.WorldServer.Game.Entity.Item;

namespace NexusForever.WorldServer.Game.Social
{
    public sealed class SocialManager : Singleton<SocialManager>
    {
        private const float LocalChatDistace = 155f;

        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        private readonly Dictionary<ChatChannel, ChatChannelHandler> chatChannelHandlers
            = new Dictionary<ChatChannel, ChatChannelHandler>();
        private readonly Dictionary<ChatFormatType, ChatFormatFactoryDelegate> chatFormatFactories
            = new Dictionary<ChatFormatType, ChatFormatFactoryDelegate>();

        private delegate IChatFormat ChatFormatFactoryDelegate();
        private delegate void ChatChannelHandler(WorldSession session, ClientChat chat);

        private SocialManager()
        {
        }

        public void Initialise()
        {
            InitialiseChatHandlers();
            InitialiseChatFormatFactories();
        }

        private void InitialiseChatHandlers()
        {
            foreach (MethodInfo method in Assembly.GetExecutingAssembly().GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)))
            {
                IEnumerable<ChatChannelHandlerAttribute> attributes = method.GetCustomAttributes<ChatChannelHandlerAttribute>();
                foreach (ChatChannelHandlerAttribute attribute in attributes)
                {
                    #region Debug
                    ParameterInfo[] parameterInfo = method.GetParameters();
                    Debug.Assert(parameterInfo.Length == 2);
                    Debug.Assert(typeof(WorldSession) == parameterInfo[0].ParameterType);
                    Debug.Assert(typeof(ClientChat) == parameterInfo[1].ParameterType);
                    #endregion

                    ChatChannelHandler @delegate = (ChatChannelHandler)Delegate.CreateDelegate(typeof(ChatChannelHandler), this, method);
                    chatChannelHandlers.Add(attribute.ChatChannel, @delegate);
                }
            }
        }

        private void InitialiseChatFormatFactories()
        {
            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
            {
                ChatFormatAttribute attribute = type.GetCustomAttribute<ChatFormatAttribute>();
                if (attribute == null)
                    continue;

                NewExpression @new = Expression.New(type.GetConstructor(Type.EmptyTypes));
                ChatFormatFactoryDelegate factory = Expression.Lambda<ChatFormatFactoryDelegate>(@new).Compile();
                chatFormatFactories.Add(attribute.Type, factory);
            }
        }

        /// <summary>
        /// Returns a new <see cref="IChatFormat"/> model for supplied <see cref="ChatFormatType"/> type.
        /// </summary>
        public IChatFormat GetChatFormatModel(ChatFormatType type)
        {
            if (!chatFormatFactories.TryGetValue(type, out ChatFormatFactoryDelegate factory))
                return null;
            return factory.Invoke();
        }

        /// <summary>
        /// Process and delegate a <see cref="ClientChat"/> message from <see cref="WorldSession"/>, this is called directly from a packet hander.
        /// </summary>
        public void HandleClientChat(WorldSession session, ClientChat chat)
        {
            if (chatChannelHandlers.ContainsKey(chat.Channel))
                chatChannelHandlers[chat.Channel](session, chat);
            else
            {
                log.Info($"ChatChannel {chat.Channel} has no handler implemented.");

                session.EnqueueMessageEncrypted(new ServerChat
                {
                    Channel = ChatChannel.Debug,
                    Name    = "SocialManager",
                    Text    = "Currently not implemented",
                });
            }
        }

        private void SendChatAccept(WorldSession session)
        {
            session.EnqueueMessageEncrypted(new ServerChatAccept
            {
                Name = session.Player.Name,
                Guid = session.Player.Guid
            });
        }

        [ChatChannelHandler(ChatChannel.Say)]
        [ChatChannelHandler(ChatChannel.Yell)]
        [ChatChannelHandler(ChatChannel.Emote)]
        private void HandleLocalChat(WorldSession session, ClientChat chat)
        {
            var serverChat = new ServerChat
            {
                Guid    = session.Player.Guid,
                Channel = chat.Channel,
                Name    = session.Player.Name,
                Text    = chat.Message,
                Formats = ParseChatLinks(session, chat.Formats).ToList(),
            };

            session.Player.Map.Search(
                session.Player.Position,
                LocalChatDistace,
                new SearchCheckRangePlayerOnly(session.Player.Position, LocalChatDistace, session.Player),
                out List<GridEntity> intersectedEntities
            );

            // Remove all session in range who are currently ignoring this person
            intersectedEntities.RemoveAll(e => ((Player)e).IsIgnoring(session.Player.CharacterId)); 
            // TODO: Could probably be cleaner by not grabbing users who are ignoring in the first place

            intersectedEntities.ForEach(e => ((Player)e).Session.EnqueueMessageEncrypted(serverChat));
            SendChatAccept(session);            
        }

        /// <summary>
        /// Parses chat links from <see cref="ChatFormat"/> delivered by <see cref="ClientChat"/>
        /// </summary>
        /// <param name="session"></param>
        /// <param name="chat"></param>
        /// <returns></returns>
        public IEnumerable<ChatFormat> ParseChatLinks(WorldSession session, List<ChatFormat> chatFormats)
        {
            foreach (ChatFormat format in chatFormats)
            {
                yield return ParseChatFormat(session, format);
            }
        }

        /// <summary>
        /// Parses a <see cref="ChatFormat"/> to return a formatted <see cref="ChatFormat"/>
        /// </summary>
        /// <param name="session"></param>
        /// <param name="format"></param>
        /// <returns></returns>
        private static ChatFormat ParseChatFormat(WorldSession session, ChatFormat format)
        {
            switch (format.FormatModel)
            {
                case ChatFormatItemId chatFormatItemId:
                    {
                        Item2Entry item = GameTableManager.Instance.Item.GetEntry(chatFormatItemId.ItemId);

                        return new ChatFormat
                        {
                            Type = ChatFormatType.ItemItemId,
                            StartIndex = format.StartIndex,
                            StopIndex = format.StopIndex,
                            FormatModel = new ChatFormatItemId
                            {
                                ItemId = item.Id
                            }
                        };
                    }
                case ChatFormatItemGuid chatFormatItemGuid:
                    {
                        Item item = session.Player.Inventory.GetItem(chatFormatItemGuid.Guid);

                        // TODO: this probably needs to be a full item response
                        return new ChatFormat
                        {
                            Type = ChatFormatType.ItemItemId,
                            StartIndex = format.StartIndex,
                            StopIndex = format.StopIndex,
                            FormatModel = new ChatFormatItemId
                            {
                                ItemId = item.Entry.Id
                            }
                        };
                    }
                default:
                    return format;
            }
        }

        public void SendMessage(WorldSession session, string message, string name = "", ChatChannel channel = ChatChannel.System)
        {
            session.EnqueueMessageEncrypted(new ServerChat
            {
                Channel = channel,
                Name = name,
                Text = message,
            });
        }
    }
}
