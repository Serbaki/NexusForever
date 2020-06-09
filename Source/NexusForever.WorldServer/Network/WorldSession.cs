using System;
using System.Collections.Generic;
using System.Net.Sockets;
using NexusForever.Shared.Cryptography;
using NexusForever.Shared.Database.Auth.Model;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.Shared.Network.Message.Model;
using NexusForever.Shared.Network.Packet;
using NexusForever.WorldServer.Database.Character.Model;
using NexusForever.WorldServer.Game.Account;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Network.Message.Model;
using NLog;

namespace NexusForever.WorldServer.Network
{
    public class WorldSession : GameSession
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();
        public Account Account { get; private set; }
        public List<Character> Characters { get; } = new List<Character>();

        public Player Player { get; set; }

        public GenericUnlockManager GenericUnlockManager { get; set; }
        public AccountCurrencyManager AccountCurrencyManager { get; set; }
        public EntitlementManager EntitlementManager { get; set; }

        public TimeSpan Uptime
        {
            get
            {
                return DateTime.UtcNow.Subtract(sessionCreated);
            }
        }
        private DateTime sessionCreated;

        public override void OnAccept(Socket newSocket)
        {
            base.OnAccept(newSocket);

            EnqueueMessageEncrypted(new ServerHello
            {
                AuthVersion    = 16042,
                RealmId        = WorldServer.RealmId,
                RealmGroupId   = 21,
                AuthMessage    = 0x97998A0,
                ConnectionType = 11
            });
        }

        protected override IWritable BuildEncryptedMessage(byte[] data)
        {
            return new ServerRealmEncrypted
            {
                Data = data
            };
        }

        protected override void OnDisconnect()
        {
            base.OnDisconnect();
            //log.Info($"OnDisconnect triggered");
            Player?.CleanUp(); //being triggered while leaving housing map
        }

        /// <summary>
        /// Initialise <see cref="WorldSession"/> from an existing <see cref="Account"/> database model.
        /// </summary>
        public void Initialise(Account account)
        {
            if (Account != null)
                throw new InvalidOperationException();

            Account = account;

            GenericUnlockManager   = new GenericUnlockManager(this, account);
            AccountCurrencyManager = new AccountCurrencyManager(this, account);
            EntitlementManager     = new EntitlementManager(this, account);

            sessionCreated = DateTime.UtcNow;
        }

        public void SetEncryptionKey(byte[] sessionKey)
        {
            ulong key = PacketCrypt.GetKeyFromTicket(sessionKey);
            encryption = new PacketCrypt(key);
        }

        [MessageHandler(GameMessageOpcode.ClientPackedWorld)]
        public void HandlePackedWorld(ClientPackedWorld packedWorld)
        {
            var packet = new ClientGamePacket(packedWorld.Data);
            HandlePacket(packet);
        }
    }
}
