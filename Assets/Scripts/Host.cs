/**
 * Copyright (c) 2017-present, Facebook, Inc.
 * All rights reserved.
 *
 * This source code is licensed under the BSD-style license found in the
 * LICENSE file in the Scripts directory of this source tree. An additional grant 
 * of patent rights can be found in the PATENTS file in the same directory.
 */
using System;
using UnityEngine;
using Oculus.Platform;
using Oculus.Platform.Models;
using Network;
using static UnityEngine.Assertions.Assert;
using static UnityEngine.Profiling.Profiler;
using static UnityEngine.Debug;
using static Constants;
using static Host.ClientState;
using static AvatarState;

public class Host : Common {
  public enum ClientState {
    Disconnected,                                   //client is not connected
    Connecting,                                     //client is connecting (joined room, but NAT punched yet)
    Connected                                       //client is fully connected and is sending and receiving packets.
  };

  struct Client {
    public ClientState state;
    public ulong userId;
    public string oculusId;

    public double 
      connectionStarted,
      connected,
      lastPacketSent,
      lastPacketReceived;

    public void Reset() {
      state = Disconnected;
      userId = 0;
      oculusId = "";
      connectionStarted = 0.0;
      connected = 0.0f;
      lastPacketSent = 0.0;
      lastPacketReceived = 0.0;
    }
  };

  public Context context;
  Client[] clients = new Client[MaxClients];
  byte[] buffer = new byte[MaxPacketSize];
  bool isReadyToShutdown = false;
  ulong roomId;                                     //the room id. valid once the host has created a room and enqueued it on the matchmaker.

  bool IsClientConnected(int id) {
    IsTrue(id >= 0);
    IsTrue(id < MaxClients);

    return clients[id].state == Connected;
  }

  new void Awake() {
    Log("*** HOST ***");
    IsNotNull(context);    

    for (int i = 0; i < MaxClients; ++i) //IMPORTANT: the host is *always* client 0
      clients[i].Reset();

    context.Init(0);
    context.resetId = 100;
    InitializePlatformSDK(CheckEntitlement);
    Rooms.SetUpdateNotificationCallback(ConnectClients);
    Net.SetConnectionStateChangedCallback(CheckClientConnection);

    Voip.SetVoipConnectRequestCallback((Message<NetworkingPeer> message) => {
      Voip.Accept(message.Data.ID);
    });

    Voip.SetVoipStateChangeCallback((Message<NetworkingPeer> message) => {
      LogFormat("Voice state changed to {1} for user {0}", message.Data.ID, message.Data.State);
    });
  }

  new void Start() {
    base.Start();
    IsNotNull(context);
    IsNotNull(localAvatar);

    for (int i = 0; i < MaxClients; ++i)
      context.HideAvatar(i);

    localAvatar.GetComponent<Hands>().SetContext(context.GetComponent<Context>());
    localAvatar.transform.position = context.GetAvatar(0).gameObject.transform.position;
    localAvatar.transform.rotation = context.GetAvatar(0).gameObject.transform.rotation;
  }

  new void Update() {
    base.Update();

    for (int i = 1; i < MaxClients; ++i) { //apply host avatar per-remote client at render time with interpolation
      if (clients[i].state != Connected) continue;

      var buffer = context.GetServerData(i).jitterBuffer;
      int count;
      ushort resetSequence;
      if (buffer.GetInterpolatedAvatars(ref interpolatedAvatars, out count, out resetSequence)) continue;

      if (resetSequence == context.resetId)
        context.ApplyAvatarUpdates(count, ref interpolatedAvatars, i, 0);
    }

    for (int i = 1; i < MaxClients; ++i) { //advance jitter buffer time
      if (clients[i].state == Connected)
        context.GetServerData(i).jitterBuffer.AdvanceTime(Time.deltaTime);
    }
    CheckTimeouts(); //check for timeouts
  }

  new void FixedUpdate() {
    var hands = localAvatar.GetComponent<Hands>();

    if (Input.GetKey("space") || (hands.IsPressingIndex() && hands.IsPressingX())) {
      context.Reset();
      context.resetId++;
    }

    context.UpdateSleep();
    ProcessPackets();
    SendPackets();
    context.UpdateSleep();
    base.FixedUpdate();
  }

  void CheckEntitlement(Message message) {
    if (message.IsError) {
      Log("error: You are not entitled to use this app");
      return;
    }

    Log("You are entitled to use this app");
    Users.GetLoggedInUser().OnComplete(CreateRoom);
  }

  void CreateRoom(Message<User> message) {
    if (message.IsError) {
      Log("error: Could not get signed in user");
      return;
    }

    Log("User id is " + message.Data.ID);
    Log("Oculus id is " + message.Data.OculusID);
    clients[0].state = Connected;
    clients[0].userId = message.Data.ID;
    clients[0].oculusId = message.Data.OculusID;

    var options = new MatchmakingOptions();
    options.SetEnqueueQueryKey("quickmatch_query");
    options.SetCreateRoomJoinPolicy(RoomJoinPolicy.Everyone);
    options.SetCreateRoomMaxUsers(MaxClients);
    options.SetEnqueueDataSettings("version", Constants.Version.GetHashCode());

    Matchmaking.CreateAndEnqueueRoom2("quickmatch", options).OnComplete(PrintRoomDetails);
  }

  void PrintRoomDetails(Message<MatchmakingEnqueueResultAndRoom> message) {
    if (message.IsError) {
      Log("error: Failed to create and enqueue room - " + message.GetError());
      return;
    }

    Log("Created and enqueued room");
    PrintRoomDetails(message.Data.Room);
    roomId = message.Data.Room.ID;
  }

  int FindClientId(ulong userId) {
    for (int i = 1; i < MaxClients; ++i) {
      if (clients[i].state != Disconnected && clients[i].userId == userId)
        return i;
    }
    return -1;
  }

  int FindFreeClientId() {
    for (int i = 1; i < MaxClients; ++i) {
      if (clients[i].state == Disconnected)
        return i;
    }
    return -1;
  }

  void ConnectClients(Message<Room> message) {
    var room = message.Data;
    if (room.ID != roomId) return;

    if (message.IsError) {
      Log("error: Room updated error (?!) - " + message.GetError());
      return;
    }

    Log("Room updated");

    foreach (var user in room.Users)
      Log(" + " + user.OculusID + " [" + user.ID + "]");

    for (int i = 1; i < MaxClients; ++i) { //disconnect any clients that are connecting/connected in our state machine, but are no longer in the room
      if (clients[i].state == Disconnected || FindUserById(room.Users, clients[i].userId)) continue;

      Log("Client " + i + " is no longer in the room");
      DisconnectClient(i);
    }
            
    foreach (var user in room.Users) { //connect any clients who are in the room, but aren't connecting/connected in our state machine (excluding the room owner)
      if (user.ID == room.Owner.ID) continue;
      if (FindClientId(user.ID) != -1) continue;

      int id = FindFreeClientId();
      if (id != -1) StartClientConnection(id, user.ID, user.OculusID);
    }
  }

  void StartClientConnection(int id, ulong userId, string oculusId) {
    Log("Starting connection to client " + oculusId + " [" + userId + "]");
    IsTrue(id != 0);

    if (clients[id].state != Disconnected)
      DisconnectClient(id);

    clients[id].state = Connecting;
    clients[id].oculusId = oculusId;
    clients[id].userId = userId;
    clients[id].connectionStarted = renderTime;
    Net.Connect(userId);
  }

  void ConnectClient(int id, ulong userId) {
    IsTrue(id != 0);
    if (clients[id].state != Connecting || clients[id].userId != userId) return;

    clients[id].state = Connected;
    clients[id].connected = renderTime;
    clients[id].lastPacketSent = renderTime;
    clients[id].lastPacketReceived = renderTime;
    ShowAvatar(id);
    BroadcastServerPacket();
  }

  void DisconnectClient(int id) {
    IsTrue(id != 0);
    IsTrue(IsClientConnected(id));
    HideAvatar(id);
    Rooms.KickUser(roomId, clients[id].userId, 0);
    Net.Close(clients[id].userId);
    clients[id].Reset();
    BroadcastServerPacket();
  }

  void ShowAvatar(int id) {
    Log(clients[id].oculusId + " joined the game as client " + id);
    context.ShowAvatar(id);
    Voip.Start(clients[id].userId);

    var head = context.GetAvatarHead(id);
    var audio = head.GetComponent<VoipAudioSourceHiLevel>();

    if (!audio)
      audio = head.AddComponent<VoipAudioSourceHiLevel>();

    audio.senderID = clients[id].userId;
  }

  void HideAvatar(int id) {
    Log(clients[id].oculusId + " left the game");
    var head = context.GetAvatarHead(id);
    var audio = head.GetComponent<VoipAudioSourceHiLevel>();

    if (audio)
      audio.senderID = 0;

    Voip.Stop(clients[id].userId);
    context.HideAvatar(id);
    context.ResetAuthority(id);
    context.GetServerData(id).Reset();
  }

  void CheckClientConnection(Message<NetworkingPeer> message) {
    var userId = message.Data.ID;
    int id = FindClientId(userId);
    if (id == -1) return;

    Log("Connection state changed to " + message.Data.State + " for client " + id);

    if (message.Data.State == PeerConnectionState.Connected) {
      ConnectClient(id, userId);

    } else if (clients[id].state != Disconnected) {
      DisconnectClient(id);
    }
  }

  protected override void OnQuit() {
    if (roomId == 0) {
      isReadyToShutdown = true;
      return;
    }

    for (int i = 1; i < MaxClients; ++i) {
      if (IsClientConnected(i)) DisconnectClient(i);
    }
    LeaveRoom(roomId, StartShutdown);
  }

  protected override bool ReadyToShutdown() => isReadyToShutdown;

  void StartShutdown(Message<Room> msg) {
    if (!msg.IsError) Log("Left room");

    isReadyToShutdown = true;
    roomId = 0;
  }

  void CheckTimeouts() {
    for (int i = 1; i < MaxClients; ++i) {
      if (clients[i].state == Connecting) {
        if (clients[i].connectionStarted + ConnectionTimeout >= renderTime) continue;

        Log("Client " + i + " timed out while connecting");
        DisconnectClient(i);

      } else if (clients[i].state == Connected) {
        if (clients[i].lastPacketReceived + ConnectionTimeout >= renderTime) continue;

        Log("Client " + i + " timed out");
        DisconnectClient(i);
      }
    }
  }

  void ProcessPackets() {
    Packet packet;

    while ((packet = Net.ReadPacket()) != null) {
      int id = FindClientId(packet.SenderID);
      if (id == -1) continue;
      if (!IsClientConnected(id)) continue;

      packet.ReadBytes(buffer);

      if (buffer[0] == (byte)PacketSerializer.PacketType.StateUpdate) {
        if (isJitterBufferEnabled)
          AddUpdatePacket(context, context.GetServerData(id), buffer);
        else
          ProcessUpdatePacket(buffer, id);
      }
      clients[id].lastPacketReceived = renderTime;
    }
    ProcessAcks();

    if (isJitterBufferEnabled) { //process client state update from jitter buffer
      for (int i = 1; i < MaxClients; ++i) {
        if (clients[i].state == Connected)
          ProcessStateUpdateFromJitterBuffer(context, context.GetServerData(i), i, 0, isJitterBufferEnabled);
      }
    }

    for (int i = 1; i < MaxClients; ++i) { //advance remote frame number
      if (clients[i].state != Connected) continue;

      var data = context.GetServerData(i);

      if (!data.isFirstPacket)
        data.frame++;
    }
  }

  void SendPackets() {
    for (int i = 1; i < MaxClients; ++i) {
      if (!IsClientConnected(i)) continue;

      var packet = GenerateUpdatePacket(context.GetServerData(i), i, (float)(physicsTime - renderTime));
      Net.SendPacket(clients[i].userId, packet, SendPolicy.Unreliable);
      clients[i].lastPacketSent = renderTime;
    }
  }

  public void BroadcastServerPacket() {
    for (int i = 1; i < MaxClients; ++i) {
      if (!IsClientConnected(i)) continue;

      Net.SendPacket(clients[i].userId, GenerateServerPacket(), SendPolicy.Unreliable);
      clients[i].lastPacketSent = renderTime;
    }
  }

  public byte[] GenerateServerPacket() {
    for (int i = 0; i < MaxClients; ++i) {
      if (IsClientConnected(i)) {
        info.areConnected[i] = true;
        info.userIds[i] = clients[i].userId;
        info.userNames[i] = clients[i].oculusId;
      } else {
        info.areConnected[i] = false;
        info.userIds[i] = 0;
        info.userNames[i] = "";
      }
    }
    WriteClientsPacket(info.areConnected, info.userIds, info.userNames);

    return writeStream.GetData();
  }

  public byte[] GenerateUpdatePacket(Context.ConnectionData d, int toClientId, float timeOffset) {
    int count = Math.Min(NumCubes, MaxStateUpdates);
    context.UpdateCubePriority();
    context.GetCubeUpdates(d, ref count, ref cubeIds, ref cubes);

    PacketHeader header;
    d.connection.GeneratePacketHeader(out header);
    header.resetSequence = context.resetId;
    header.frame = (uint)frame;
    header.timeOffset = timeOffset;

    DetermineNotChangedAndDeltas(context, d, header.id, count, ref cubeIds, ref notChanged, ref hasDelta, ref baselineIds, ref cubes, ref cubeDeltas);
    DeterminePrediction(context, d, header.id, count, ref cubeIds, ref notChanged, ref hasDelta, ref perfectPrediction, ref hasPredictionDelta, ref baselineIds, ref cubes, ref predictionDelta);
    int id = 0;

    for (int i = 0; i < MaxClients; ++i) {
      if (i == toClientId) continue;

      if (i == 0) {        
        localAvatar.GetComponent<Hands>().GetState(out avatars[id]); //grab state from the local avatar.
        Quantize(ref avatars[id], out avatarsQuantized[id]);
        id++;
      } else {
        var avatar = context.GetAvatar(i); //grab state from a remote avatar.
        if (!avatar) continue;

        avatar.GetAvatarState(out avatars[id]);
        Quantize(ref avatars[id], out avatarsQuantized[id]);
        id++;
      }
    }
    WriteUpdatePacket(ref header, id, ref avatarsQuantized, count, ref cubeIds, ref notChanged, ref hasDelta, ref perfectPrediction, ref hasPredictionDelta, ref baselineIds, ref cubes, ref cubeDeltas, ref predictionDelta);

    var packet = writeStream.GetData(); 
    AddPacket(ref d.sendBuffer, header.id, context.resetId, count, ref cubeIds, ref cubes); //add the sent cube states to the send delta buffer
    context.ResetCubePriority(d, count, cubeIds); //reset cube priority for the cubes that were included in the packet (so other cubes have a chance to be sent...)

    return packet;
  }

  public void ProcessUpdatePacket(byte[] packet, int fromClientId) {
    int avatarCount = 0;
    int updateCount = 0;
    var data = context.GetServerData(fromClientId);
    PacketHeader header;

    if (!ReadUpdatePacket(packet, out header, out avatarCount, ref readAvatarsQuantized, out updateCount, ref readCubeIds, ref readNotChanged, ref readHasDelta, ref readPerfectPrediction, ref readHasPredictionDelta, ref readBaselineIds, ref readCubes, ref readCubeDeltas, ref readPredictionDeltas)
    ) return;      

    for (int i = 0; i < avatarCount; ++i) //unquantize avatar states
      Unquantize(ref readAvatarsQuantized[i], out readAvatars[i]);    

    if (context.resetId != header.resetSequence) return; //ignore any updates from a client with a different reset sequence #
    
    DecodePrediction(data.receiveBuffer, header.id, context.resetId, updateCount, ref readCubeIds, ref readPerfectPrediction, ref readHasPredictionDelta, ref readBaselineIds, ref readCubes, ref readPredictionDeltas); //decode the predicted cube states from baselines
    DecodeNotChangedAndDeltas(data.receiveBuffer, context.resetId, updateCount, ref readCubeIds, ref readNotChanged, ref readHasDelta, ref readBaselineIds, ref readCubes, ref readCubeDeltas); //decode the not changed and delta cube states from baselines
    AddPacket(ref data.receiveBuffer, header.id, context.resetId, updateCount, ref readCubeIds, ref readCubes); //add the cube states to the receive delta buffer
    context.ApplyCubeUpdates(updateCount, ref readCubeIds, ref readCubes, fromClientId, 0, isJitterBufferEnabled); //apply the state updates to cubes
    context.ApplyAvatarUpdates(avatarCount, ref readAvatars, fromClientId, 0); //apply avatar state updates
    data.connection.ProcessPacketHeader(ref header); //process the packet header
  }

  void ProcessAcks() {
    BeginSample("Process Acks");
    for (int _ = 1; _ < MaxClients; ++_) {
      for (int i = 1; i < MaxClients; ++i)
        ProcessAcksForConnection(context, context.GetServerData(i));
    }
    EndSample();
  }
}