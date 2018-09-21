﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO
{
    public partial class MapNetworkManager : BaseGameNetworkManager, IAppServer
    {
        public static MapNetworkManager Singleton { get; protected set; }

        [Header("Central Network Connection")]
        public string centralConnectKey = "SampleConnectKey";
        public string centralNetworkAddress = "127.0.0.1";
        public int centralNetworkPort = 6000;
        public string machineAddress = "127.0.0.1";
        [Header("Database")]
        public float autoSaveDuration = 2f;

        public System.Action<NetPeer> onClientConnected;
        public System.Action<NetPeer, DisconnectInfo> onClientDisconnected;

        private CentralAppServerRegister cacheCentralAppServerRegister;
        public CentralAppServerRegister CentralAppServerRegister
        {
            get
            {
                if (cacheCentralAppServerRegister == null)
                {
                    cacheCentralAppServerRegister = new CentralAppServerRegister(this);
                    cacheCentralAppServerRegister.onAppServerRegistered = OnAppServerRegistered;
                    cacheCentralAppServerRegister.RegisterMessage(MMOMessageTypes.ResponseAppServerAddress, HandleResponseAppServerAddress);
                }
                return cacheCentralAppServerRegister;
            }
        }

        private ChatNetworkManager cacheChatNetworkManager;
        public ChatNetworkManager ChatNetworkManager
        {
            get
            {
                if (cacheChatNetworkManager == null)
                    cacheChatNetworkManager = gameObject.AddComponent<ChatNetworkManager>();
                return cacheChatNetworkManager;
            }
        }

        public BaseDatabase Database
        {
            get { return MMOServerInstance.Singleton.Database; }
        }

        public string CentralNetworkAddress { get { return centralNetworkAddress; } }
        public int CentralNetworkPort { get { return centralNetworkPort; } }
        public string CentralConnectKey { get { return centralConnectKey; } }
        public string AppAddress { get { return machineAddress; } }
        public int AppPort { get { return networkPort; } }
        public string AppConnectKey { get { return connectKey; } }
        public string AppExtra { get { return !string.IsNullOrEmpty(Assets.onlineScene.SceneName) ? Assets.onlineScene.SceneName : SceneManager.GetActiveScene().name; } }
        public CentralServerPeerType PeerType { get { return CentralServerPeerType.MapServer; } }
        private float lastSaveCharacterTime;
        private float lastSaveWorldTime;
        private Task saveCharactersTask;
        private Task saveWorldTask;
        // Listing
        private readonly Dictionary<string, CentralServerPeerInfo> mapServerPeersBySceneName = new Dictionary<string, CentralServerPeerInfo>();
        private readonly Dictionary<long, SimpleUserCharacterData> users = new Dictionary<long, SimpleUserCharacterData>();
        private readonly Dictionary<string, Task> savingCharacters = new Dictionary<string, Task>();
        private readonly Dictionary<int, Task> loadingPartyIds = new Dictionary<int, Task>();

        protected override void Awake()
        {
            Singleton = this;
            doNotDestroyOnSceneChanges = true;
            base.Awake();
        }

        protected override void Update()
        {
            base.Update();
            if (IsServer)
            {
                CentralAppServerRegister.PollEvents();
                tempUnscaledTime = Time.unscaledTime;
                if (tempUnscaledTime - lastSaveCharacterTime > autoSaveDuration)
                {
                    if (saveCharactersTask == null || saveCharactersTask.IsCompleted)
                    {
                        saveCharactersTask = SaveCharacters();
                        lastSaveCharacterTime = tempUnscaledTime;
                    }
                }
                if (tempUnscaledTime - lastSaveWorldTime > autoSaveDuration)
                {
                    if (saveWorldTask == null || saveWorldTask.IsCompleted)
                    {
                        saveWorldTask = SaveWorld();
                        lastSaveWorldTime = tempUnscaledTime;
                    }
                }
            }
        }

        protected override void UpdatePartyMembers()
        {
            var time = Time.unscaledTime;
            tempPartyDataArray = parties.Values.ToArray();
            foreach (var party in tempPartyDataArray)
            {
                tempPartyMemberIdArray = party.GetMemberIds().ToArray();
                foreach (var memberId in tempPartyMemberIdArray)
                {
                    BasePlayerCharacterEntity playerCharacterEntity;
                    if (playerCharactersById.TryGetValue(memberId, out playerCharacterEntity))
                    {
                        party.UpdateMember(playerCharacterEntity);
                        party.NotifyMemberOnline(memberId, time);
                        if (ChatNetworkManager != null && ChatNetworkManager.IsClientConnected)
                            ChatNetworkManager.UpdatePartyMemberOnline(party.id, 
                                playerCharacterEntity.Id, 
                                playerCharacterEntity.CharacterName, 
                                playerCharacterEntity.DataId, 
                                playerCharacterEntity.Level, 
                                playerCharacterEntity.CurrentHp, 
                                playerCharacterEntity.CacheMaxHp, 
                                playerCharacterEntity.CurrentMp, 
                                playerCharacterEntity.CacheMaxMp);
                    }
                    party.UpdateMemberOnline(memberId, time);
                }
            }
        }

        protected override async void OnDestroy()
        {
            CentralAppServerRegister.Stop();
            // Wait old save character task to be completed
            if (saveCharactersTask != null && !saveCharactersTask.IsCompleted)
                await Task.WhenAll(saveCharactersTask, SaveCharacters());
            else
                await SaveCharacters();
            if (saveWorldTask != null && !saveWorldTask.IsCompleted)
                await Task.WhenAll(saveWorldTask, SaveWorld());
            else
                await SaveWorld();
            base.OnDestroy();
        }

        public override void UnregisterPlayerCharacter(NetPeer peer)
        {
            var connectId = peer.ConnectId;
            // Send remove character from map server
            SimpleUserCharacterData userData;
            if (users.TryGetValue(connectId, out userData))
            {
                users.Remove(connectId);
                // Remove map user from central server and chat server
                UpdateMapUser(CentralAppServerRegister.Peer, UpdateMapUserMessage.UpdateType.Remove, userData);
                if (ChatNetworkManager.IsClientConnected)
                    UpdateMapUser(ChatNetworkManager.Client.Peer, UpdateMapUserMessage.UpdateType.Remove, userData);
            }
            base.UnregisterPlayerCharacter(peer);
        }

        public override async void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            var connectId = peer.ConnectId;
            // Save player character data
            BasePlayerCharacterEntity playerCharacterEntity;
            if (playerCharacters.TryGetValue(connectId, out playerCharacterEntity))
                await SaveCharacter(playerCharacterEntity);
            UnregisterPlayerCharacter(peer);
            base.OnPeerDisconnected(peer, disconnectInfo);
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            CentralAppServerRegister.OnStartServer();
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            CentralAppServerRegister.OnStopServer();
            if (ChatNetworkManager.IsClientConnected)
                ChatNetworkManager.StopClient();
            mapServerPeersBySceneName.Clear();
        }

        public override void OnClientConnected(NetPeer peer)
        {
            base.OnClientConnected(peer);
            if (onClientConnected != null)
                onClientConnected(peer);
        }

        public override void OnClientDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            base.OnClientDisconnected(peer, disconnectInfo);
            if (onClientDisconnected != null)
                onClientDisconnected(peer, disconnectInfo);
        }

        #region Character spawn function
        public override void SerializeClientReadyExtra(NetDataWriter writer)
        {
            writer.Put(MMOClientInstance.UserId);
            writer.Put(MMOClientInstance.AccessToken);
            writer.Put(MMOClientInstance.SelectCharacterId);
        }

        public override async void DeserializeClientReadyExtra(LiteNetLibIdentity playerIdentity, NetPeer peer, NetDataReader reader)
        {
            var userId = reader.GetString();
            var accessToken = reader.GetString();
            var selectCharacterId = reader.GetString();
            // Validate access token
            if (playerCharacters.ContainsKey(peer.ConnectId))
            {
                Debug.LogError("[Map Server] User trying to hack: " + userId);
                Server.NetManager.DisconnectPeer(peer);
            }
            else if (!await Database.ValidateAccessToken(userId, accessToken))
            {
                Debug.LogError("[Map Server] Invalid access token for user: " + userId);
                Server.NetManager.DisconnectPeer(peer);
            }
            else
            {
                var playerCharacterData = await Database.ReadCharacter(userId, selectCharacterId);
                // If data is empty / cannot find character, disconnect user
                if (playerCharacterData == null)
                {
                    Debug.LogError("[Map Server] Cannot find select character: " + selectCharacterId + " for user: " + userId);
                    Server.NetManager.DisconnectPeer(peer);
                    return;
                }
                // Load party data, if this map-server does not have party data
                if (playerCharacterData.PartyId > 0 && !parties.ContainsKey(playerCharacterData.PartyId))
                    await LoadPartyDataFromDatabase(playerCharacterData.PartyId);
                // If it is not allow this character data, disconnect user
                var dataId = playerCharacterData.DataId;
                PlayerCharacter playerCharacter;
                if (!GameInstance.PlayerCharacters.TryGetValue(dataId, out playerCharacter) || playerCharacter.entityPrefab == null)
                {
                    Debug.LogError("[Map Server] Cannot find player character with data Id: " + dataId);
                    return;
                }
                // Spawn character entity and set its data
                var playerCharacterPrefab = playerCharacter.entityPrefab;
                var identity = Assets.NetworkSpawn(playerCharacterPrefab.Identity.HashAssetId, playerCharacterData.CurrentPosition, Quaternion.identity, 0, peer.ConnectId);
                var playerCharacterEntity = identity.GetComponent<BasePlayerCharacterEntity>();
                playerCharacterData.CloneTo(playerCharacterEntity);
                // Notify clients that this character is spawn or dead
                if (!playerCharacterEntity.IsDead())
                    playerCharacterEntity.RequestOnRespawn();
                else
                    playerCharacterEntity.RequestOnDead();
                RegisterPlayerCharacter(peer, playerCharacterEntity);
                var userData = new SimpleUserCharacterData(userId, playerCharacterEntity.Id, playerCharacterEntity.CharacterName);
                users[peer.ConnectId] = userData;
                // Add map user to central server and chat server
                UpdateMapUser(CentralAppServerRegister.Peer, UpdateMapUserMessage.UpdateType.Add, userData);
                if (ChatNetworkManager.IsClientConnected)
                    UpdateMapUser(ChatNetworkManager.Client.Peer, UpdateMapUserMessage.UpdateType.Add, userData);
            }
        }
        #endregion

        #region Network message handlers
        protected override void HandleWarpAtClient(LiteNetLibMessageHandler messageHandler)
        {
            var message = messageHandler.ReadMessage<MMOWarpMessage>();
            Assets.offlineScene.SceneName = string.Empty;
            StopClient();
            Assets.onlineScene.SceneName = message.sceneName;
            StartClient(message.networkAddress, message.networkPort, message.connectKey);
        }

        protected override void HandleChatAtServer(LiteNetLibMessageHandler messageHandler)
        {
            // Send chat message to chat server, for MMO mode chat message handling by chat server
            var message = messageHandler.ReadMessage<ChatMessage>();
            if (ChatNetworkManager.IsClientConnected)
                ChatNetworkManager.EnterChat(message.channel, message.message, message.sender, message.receiver);
        }

        protected override async void HandleRequestCashShopInfo(LiteNetLibMessageHandler messageHandler)
        {
            var peer = messageHandler.peer;
            var message = messageHandler.ReadMessage<BaseAckMessage>();
            var error = ResponseCashShopInfoMessage.Error.None;
            var cash = 0;
            var cashShopItemIds = new List<int>();
            SimpleUserCharacterData user;
            if (!users.TryGetValue(peer.ConnectId, out user))
                error = ResponseCashShopInfoMessage.Error.UserNotFound;
            else
            {
                cash = await Database.GetCash(user.userId);
                foreach (var cashShopItemId in GameInstance.CashShopItems.Keys)
                {
                    cashShopItemIds.Add(cashShopItemId);
                }
            }
            
            var responseMessage = new ResponseCashShopInfoMessage();
            responseMessage.ackId = message.ackId;
            responseMessage.responseCode = error == ResponseCashShopInfoMessage.Error.None ? AckResponseCode.Success : AckResponseCode.Error;
            responseMessage.error = error;
            responseMessage.cash = cash;
            responseMessage.cashShopItemIds = cashShopItemIds.ToArray();
            LiteNetLibPacketSender.SendPacket(SendOptions.ReliableUnordered, peer, MsgTypes.CashShopInfo, responseMessage);
        }

        protected override async void HandleRequestCashShopBuy(LiteNetLibMessageHandler messageHandler)
        {
            var peer = messageHandler.peer;
            var message = messageHandler.ReadMessage<RequestCashShopBuyMessage>();
            var error = ResponseCashShopBuyMessage.Error.None;
            var dataId = message.dataId;
            var cash = 0;
            SimpleUserCharacterData user;
            if (!users.TryGetValue(peer.ConnectId, out user))
                error = ResponseCashShopBuyMessage.Error.UserNotFound;
            else
            {
                // Request cash, reduce, send item info messages to map server
                cash = await Database.GetCash(user.userId);
                BasePlayerCharacterEntity playerCharacter;
                CashShopItem cashShopItem;
                if (!playerCharacters.TryGetValue(peer.ConnectId, out playerCharacter))
                    error = ResponseCashShopBuyMessage.Error.CharacterNotFound;
                else if (!GameInstance.CashShopItems.TryGetValue(dataId, out cashShopItem))
                    error = ResponseCashShopBuyMessage.Error.ItemNotFound;
                else if (cash < cashShopItem.sellPrice)
                    error = ResponseCashShopBuyMessage.Error.NotEnoughCash;
                else
                {
                    cash = await Database.DecreaseCash(user.userId, cashShopItem.sellPrice);
                    playerCharacter.Gold += cashShopItem.receiveGold;
                    foreach (var receiveItem in cashShopItem.receiveItems)
                    {
                        if (receiveItem.item == null) continue;
                        var characterItem = CharacterItem.Create(receiveItem.item, 1, receiveItem.amount);
                        playerCharacter.NonEquipItems.Add(characterItem);
                    }
                }
            }
            var responseMessage = new ResponseCashShopBuyMessage();
            responseMessage.ackId = message.ackId;
            responseMessage.responseCode = error == ResponseCashShopBuyMessage.Error.None ? AckResponseCode.Success : AckResponseCode.Error;
            responseMessage.error = error;
            responseMessage.cash = cash;
            LiteNetLibPacketSender.SendPacket(SendOptions.ReliableUnordered, peer, MsgTypes.CashShopBuy, responseMessage);
        }

        protected override async void HandleRequestCashPackageInfo(LiteNetLibMessageHandler messageHandler)
        {
            var peer = messageHandler.peer;
            var message = messageHandler.ReadMessage<BaseAckMessage>();
            var error = ResponseCashPackageInfoMessage.Error.None;
            var cash = 0;
            var cashPackageIds = new List<int>();
            SimpleUserCharacterData user;
            if (!users.TryGetValue(peer.ConnectId, out user))
                error = ResponseCashPackageInfoMessage.Error.UserNotFound;
            else
            {
                cash = await Database.GetCash(user.userId);
                foreach (var cashShopItemId in GameInstance.CashPackages.Keys)
                {
                    cashPackageIds.Add(cashShopItemId);
                }
            }

            var responseMessage = new ResponseCashPackageInfoMessage();
            responseMessage.ackId = message.ackId;
            responseMessage.responseCode = error == ResponseCashPackageInfoMessage.Error.None ? AckResponseCode.Success : AckResponseCode.Error;
            responseMessage.error = error;
            responseMessage.cash = cash;
            responseMessage.cashPackageIds = cashPackageIds.ToArray();
            LiteNetLibPacketSender.SendPacket(SendOptions.ReliableUnordered, peer, MsgTypes.CashPackageInfo, responseMessage);
        }

        protected override async void HandleRequestCashPackageBuyValidation(LiteNetLibMessageHandler messageHandler)
        {
            var peer = messageHandler.peer;
            var message = messageHandler.ReadMessage<RequestCashPackageBuyValidationMessage>();
            var error = ResponseCashPackageBuyValidationMessage.Error.None;
            var dataId = message.dataId;
            var cash = 0;
            SimpleUserCharacterData user;
            if (!users.TryGetValue(peer.ConnectId, out user))
                error = ResponseCashPackageBuyValidationMessage.Error.UserNotFound;
            else
            {
                // Get current cash will return this in case it cannot increase cash
                cash = await Database.GetCash(user.userId);
                // TODO: Validate purchasing at server side
                BasePlayerCharacterEntity playerCharacter;
                CashPackage cashPackage;
                if (!playerCharacters.TryGetValue(peer.ConnectId, out playerCharacter))
                    error = ResponseCashPackageBuyValidationMessage.Error.CharacterNotFound;
                else if (!GameInstance.CashPackages.TryGetValue(dataId, out cashPackage))
                    error = ResponseCashPackageBuyValidationMessage.Error.PackageNotFound;
                else
                    cash = await Database.IncreaseCash(user.userId, cashPackage.cashAmount);
            }
            var responseMessage = new ResponseCashPackageBuyValidationMessage();
            responseMessage.ackId = message.ackId;
            responseMessage.responseCode = error == ResponseCashPackageBuyValidationMessage.Error.None ? AckResponseCode.Success : AckResponseCode.Error;
            responseMessage.error = error;
            responseMessage.dataId = dataId;
            responseMessage.cash = cash;
            LiteNetLibPacketSender.SendPacket(SendOptions.ReliableUnordered, peer, MsgTypes.CashPackageBuyValidation, responseMessage);
        }        

        private void HandleResponseAppServerAddress(LiteNetLibMessageHandler messageHandler)
        {
            var peer = messageHandler.peer;
            var message = messageHandler.ReadMessage<ResponseAppServerAddressMessage>();
            if (message.responseCode == AckResponseCode.Success)
            {
                var peerInfo = message.peerInfo;
                switch (peerInfo.peerType)
                {
                    case CentralServerPeerType.MapServer:
                        if (!string.IsNullOrEmpty(peerInfo.extra))
                        {
                            Debug.Log("Register map server: " + peerInfo.extra);
                            mapServerPeersBySceneName[peerInfo.extra] = peerInfo;
                        }
                        break;
                    case CentralServerPeerType.Chat:
                        if (!ChatNetworkManager.IsClientConnected)
                        {
                            Debug.Log("Connecting to chat server");
                            ChatNetworkManager.StartClient(this, peerInfo.networkAddress, peerInfo.networkPort, peerInfo.connectKey);
                        }
                        break;
                }
            }
        }

        private void OnAppServerRegistered(AckResponseCode responseCode, BaseAckMessage message)
        {
            if (responseCode == AckResponseCode.Success)
                UpdateMapUsers(CentralAppServerRegister.Peer, UpdateMapUserMessage.UpdateType.Add);
        }
        #endregion

        #region Connect to chat server
        public void OnChatServerConnected()
        {
            Debug.Log("Connected to chat server");
            UpdateMapUsers(ChatNetworkManager.Client.Peer, UpdateMapUserMessage.UpdateType.Add);
        }

        public void OnChatMessageReceive(ChatMessage message)
        {
            ReadChatMessage(message);
        }

        public void OnUpdatePartyMember(UpdatePartyMemberMessage message)
        {
            PartyData party;
            BasePlayerCharacterEntity playerCharacter;
            SocialCharacterData partyMember;
            if (parties.TryGetValue(message.id, out party))
            {
                switch (message.type)
                {
                    case UpdatePartyMemberMessage.UpdateType.Add:
                        partyMember = new SocialCharacterData();
                        partyMember.id = message.characterId;
                        partyMember.characterName = message.characterName;
                        partyMember.dataId = message.dataId;
                        partyMember.level = message.level;
                        if (playerCharactersById.TryGetValue(message.characterId, out playerCharacter))
                            playerCharacter.PartyId = message.id;
                        party.AddMember(partyMember);
                        break;
                    case UpdatePartyMemberMessage.UpdateType.Remove:
                        if (playerCharactersById.TryGetValue(message.characterId, out playerCharacter))
                            playerCharacter.PartyId = 0;
                        party.RemoveMember(message.characterId);
                        break;
                    case UpdatePartyMemberMessage.UpdateType.Online:
                        partyMember = new SocialCharacterData();
                        partyMember.id = message.characterId;
                        partyMember.characterName = message.characterName;
                        partyMember.dataId = message.dataId;
                        partyMember.level = message.level;
                        partyMember.currentHp = message.currentHp;
                        partyMember.maxHp = message.maxHp;
                        partyMember.currentMp = message.currentMp;
                        partyMember.maxMp = message.maxMp;
                        party.UpdateMember(partyMember);
                        party.NotifyMemberOnline(message.characterId, Time.unscaledTime);
                        break;
                }
            }
        }

        public void OnUpdateParty(UpdatePartyMessage message)
        {
            PartyData party;
            if (parties.TryGetValue(message.id, out party))
            {
                switch (message.type)
                {
                    case UpdatePartyMessage.UpdateType.Setting:
                        party.Setting(message.shareExp, message.shareItem);
                        parties[message.id] = party;
                        break;
                    case UpdatePartyMessage.UpdateType.Terminate:
                        parties.Remove(message.id);
                        break;
                }
            }
        }
        #endregion

        #region Update map user functions
        private void UpdateMapUsers(NetPeer peer, UpdateMapUserMessage.UpdateType updateType)
        {
            foreach (var user in users.Values)
            {
                UpdateMapUser(peer, updateType, user);
            }
        }

        private void UpdateMapUser(NetPeer peer, UpdateMapUserMessage.UpdateType updateType, SimpleUserCharacterData userData)
        {
            var updateMapUserMessage = new UpdateMapUserMessage();
            updateMapUserMessage.type = updateType;
            updateMapUserMessage.userData = userData;
            LiteNetLibPacketSender.SendPacket(SendOptions.ReliableOrdered, peer, MMOMessageTypes.UpdateMapUser, updateMapUserMessage);
        }
        #endregion

        #region Load Functions
        private async Task LoadPartyDataFromDatabase(int partyId)
        {
            // If there are other party loading which is not completed, it will not load again
            if (partyId <= 0 || loadingPartyIds.ContainsKey(partyId))
                return;
            var task = Database.ReadParty(partyId);
            loadingPartyIds.Add(partyId, task);
            var party = await task;
            if (party != null)
                parties[partyId] = party;
            else
                parties.Remove(partyId);
            loadingPartyIds.Remove(partyId);
        }
        #endregion

        #region Save functions
        private async Task SaveCharacter(IPlayerCharacterData playerCharacterData)
        {
            if (playerCharacterData == null)
                return;
            Task task;
            if (savingCharacters.TryGetValue(playerCharacterData.Id, out task) && !task.IsCompleted)
                await task;
            task = Database.UpdateCharacter(playerCharacterData);
            savingCharacters[playerCharacterData.Id] = task;
            Debug.Log("Character [" + playerCharacterData.Id + "] Saved");
            await task;
        }

        private async Task SaveCharacters()
        {
            if (saveCharactersTask != null && !saveCharactersTask.IsCompleted)
                await saveCharactersTask;
            var tasks = new List<Task>();
            foreach (var playerCharacter in playerCharacters.Values)
            {
                tasks.Add(SaveCharacter(playerCharacter));
            }
            await Task.WhenAll(tasks);
            Debug.Log("Characters Saved " + tasks.Count + " character(s)");
        }

        private async Task SaveWorld()
        {
            if (saveWorldTask != null && !saveWorldTask.IsCompleted)
                await saveWorldTask;
            var tasks = new List<Task>();
            foreach (var buildingEntity in buildingEntities.Values)
            {
                tasks.Add(Database.UpdateBuilding(Assets.onlineScene.SceneName, buildingEntity));
            }
            await Task.WhenAll(tasks);
            Debug.Log("World Saved " + tasks.Count + " building(s)");
        }

        public override async void CreateBuildingEntity(BuildingSaveData saveData, bool initialize)
        {
            base.CreateBuildingEntity(saveData, initialize);
            if (!initialize)
                await Database.CreateBuilding(Assets.onlineScene.SceneName, saveData);
        }

        public override async void DestroyBuildingEntity(string id)
        {
            base.DestroyBuildingEntity(id);
            await Database.DeleteBuilding(Assets.onlineScene.SceneName, id);
        }

        public override async void OnServerOnlineSceneLoaded()
        {
            base.OnServerOnlineSceneLoaded();
            // Spawn buildings
            var buildings = await Database.ReadBuildings(Assets.onlineScene.SceneName);
            foreach (var building in buildings)
            {
                CreateBuildingEntity(building, true);
            }
            // Spawn harvestables
            var harvestableSpawnAreas = FindObjectsOfType<HarvestableSpawnArea>();
            foreach (var harvestableSpawnArea in harvestableSpawnAreas)
            {
                harvestableSpawnArea.SpawnAll();
            }
        }
        #endregion

        #region Implement Abstract Functions
        public override async void WarpCharacter(BasePlayerCharacterEntity playerCharacterEntity, string mapName, Vector3 position)
        {
            if (playerCharacterEntity == null || !IsServer)
                return;
            // If warping to same map player does not have to reload new map data
            if (string.IsNullOrEmpty(mapName) || mapName.Equals(playerCharacterEntity.CurrentMapName))
            {
                playerCharacterEntity.CacheNetTransform.Teleport(position, Quaternion.identity);
                return;
            }
            // If warping to different map
            long connectId = playerCharacterEntity.ConnectId;
            NetPeer peer;
            CentralServerPeerInfo peerInfo;
            if (!string.IsNullOrEmpty(mapName) &&
                !mapName.Equals(playerCharacterEntity.CurrentMapName) &&
                playerCharacters.ContainsKey(connectId) &&
                Peers.TryGetValue(connectId, out peer) &&
                mapServerPeersBySceneName.TryGetValue(mapName, out peerInfo))
            {
                // Unregister player character
                UnregisterPlayerCharacter(peer);
                // Clone character data to save
                var savingCharacterData = new PlayerCharacterData();
                playerCharacterEntity.CloneTo(savingCharacterData);
                // Save character current map / position
                savingCharacterData.CurrentMapName = mapName;
                savingCharacterData.CurrentPosition = position;
                await SaveCharacter(savingCharacterData);
                // Destroy character from server
                playerCharacterEntity.NetworkDestroy();
                // Send message to client to warp
                var message = new MMOWarpMessage();
                message.sceneName = mapName;
                message.networkAddress = peerInfo.networkAddress;
                message.networkPort = peerInfo.networkPort;
                message.connectKey = peerInfo.connectKey;
                LiteNetLibPacketSender.SendPacket(SendOptions.ReliableOrdered, peer, MsgTypes.Warp, message);
            }
        }

        public override async void CreateParty(BasePlayerCharacterEntity playerCharacterEntity, bool shareExp, bool shareItem)
        {
            if (playerCharacterEntity == null || !IsServer)
                return;
            var partyId = await Database.CreateParty(shareExp, shareItem, playerCharacterEntity.Id);
            var party = new PartyData(partyId, shareExp, shareItem, playerCharacterEntity);
            await Database.SetCharacterParty(playerCharacterEntity.Id, partyId);
            parties[partyId] = party;
            playerCharacterEntity.PartyId = partyId;
        }

        public override async void PartySetting(BasePlayerCharacterEntity playerCharacterEntity, bool shareExp, bool shareItem)
        {
            if (playerCharacterEntity == null || !IsServer)
                return;
            var partyId = playerCharacterEntity.PartyId;
            PartyData party;
            if (!parties.TryGetValue(partyId, out party))
                return;
            if (!party.IsLeader(playerCharacterEntity))
            {
                // TODO: May warn that it's not party leader
                return;
            }
            await Database.UpdateParty(playerCharacterEntity.PartyId, shareExp, shareItem);
            if (ChatNetworkManager.IsClientConnected)
                ChatNetworkManager.UpdatePartySetting(partyId, shareExp, shareItem);
        }

        public override async void AddPartyMember(BasePlayerCharacterEntity inviteCharacterEntity, BasePlayerCharacterEntity acceptCharacterEntity)
        {
            if (inviteCharacterEntity == null || acceptCharacterEntity == null || !IsServer)
                return;
            var partyId = inviteCharacterEntity.PartyId;
            PartyData party;
            if (!parties.TryGetValue(partyId, out party))
                return;
            if (!party.IsLeader(inviteCharacterEntity))
            {
                // TODO: May warn that it's not party leader
                return;
            }
            if (party.CountMember() == gameInstance.maxPartyMember)
            {
                // TODO: May warn that it's exceeds limit max party member
                return;
            }
            await Database.SetCharacterParty(acceptCharacterEntity.Id, partyId);
            if (ChatNetworkManager.IsClientConnected)
                ChatNetworkManager.UpdatePartyMemberAdd(partyId, acceptCharacterEntity.Id, acceptCharacterEntity.CharacterName, acceptCharacterEntity.DataId, acceptCharacterEntity.Level);
        }

        public override async void KickFromParty(BasePlayerCharacterEntity playerCharacterEntity, string characterId)
        {
            if (playerCharacterEntity == null || !IsServer)
                return;
            var partyId = playerCharacterEntity.PartyId;
            PartyData party;
            if (!parties.TryGetValue(partyId, out party))
                return;
            if (!party.IsLeader(playerCharacterEntity))
            {
                // TODO: May warn that it's not party leader
                return;
            }
            await Database.SetCharacterParty(characterId, 0);
            if (ChatNetworkManager.IsClientConnected)
                ChatNetworkManager.UpdatePartyMemberRemove(partyId, characterId);
        }

        public override async void LeaveParty(BasePlayerCharacterEntity playerCharacterEntity)
        {
            if (playerCharacterEntity == null || !IsServer)
                return;
            var partyId = playerCharacterEntity.PartyId;
            PartyData party;
            if (!parties.TryGetValue(partyId, out party))
                return;
            // If it is leader kick all members and terminate party
            if (party.IsLeader(playerCharacterEntity))
            {
                var tasks = new List<Task>();
                foreach (var memberId in party.GetMemberIds())
                {
                    tasks.Add(Database.SetCharacterParty(memberId, 0));
                    if (ChatNetworkManager.IsClientConnected)
                        ChatNetworkManager.UpdatePartyMemberRemove(partyId, memberId);
                }
                tasks.Add(Database.DeleteParty(partyId));
                await Task.WhenAll(tasks);
                if (ChatNetworkManager.IsClientConnected)
                    ChatNetworkManager.UpdatePartyTerminate(partyId);
            }
            else
            {
                await Database.SetCharacterParty(playerCharacterEntity.Id, 0);
                if (ChatNetworkManager.IsClientConnected)
                    ChatNetworkManager.UpdatePartyMemberRemove(partyId, playerCharacterEntity.Id);
            }
        }
        #endregion
    }
}
