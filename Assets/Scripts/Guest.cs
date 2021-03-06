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
using UnityEngine.Profiling;
using UnityEngine.Assertions;
using Oculus.Platform;
using Oculus.Platform.Models;
using System.Collections.Generic;
using Network;
using static Guest.GuestState;
using static Constants;

public class Guest : Common {
  public enum GuestState {
    LoggingIn,                                                  // logging in to oculus platform SDK.
    InMatchmaking,                                              // searching for a match
    Connecting,                                                 // connecting to the server
    Connected,                                                  // connected to server. we can send and receive packets.
    Disconnected,                                               // not connected (terminal state).
    WaitingForRetry,                                            // waiting for retry. sit in this state for a few seconds before starting matchmaking again.
  };

  public Context context;
  public double timeMatchmakingStarted;                         // time matchmaking started
  public double timeConnectionStarted;                          // time the client connection started (used to timeout due to NAT)
  public double timeConnected;                                  // time the client connected to the server
  public double timeLastPacketSent;                             // time the last packet was sent to the server
  public double timeLastPacketReceived;                         // time the last packet was received from the server (used for post-connect timeouts)
  public double timeRetryStarted;                               // time the retry state started. used to delay in waiting for retry state before retrying matchmaking from scratch.
  HashSet<ulong> connections = new HashSet<ulong>();            // set of connection request ids we have received. used to fix race condition between connection request and room join.
  GuestState state = LoggingIn;
  const double RetryTime = 0.0;//5.0;                           // time between retry attempts.
  byte[] buffer = new byte[MaxPacketSize];
  string oculusId;                                              // this is our user name.

  ulong
    userId,                                                     // user id that is signed in
    hostUserId,                                                 // the user id of the room owner (host).
    roomId;                                                     // the id of the room that we have joined.

  int clientId = -1;                                            // while connected to server in [1,Constants.MaxClients-1]. -1 if not connected.

  bool
    isConnectionAccepted,                                       // true if we have accepted the connection request from the host.
    isConnected,                                                // true if we have ever successfully connected to a server.
    isReadyToShutdown = false;


  bool IsConnectedToServer() => state == Connected;

  new void Awake() {
    Debug.Log("*** GUEST ***");
    Assert.IsNotNull(context);
    state = LoggingIn;
    InitializePlatformSDK(CheckEntitlement);
    Matchmaking.SetMatchFoundNotificationCallback(JoinRoom);
    Rooms.SetUpdateNotificationCallback(CheckRoomConnection);
    Users.GetLoggedInUser().OnComplete(StartMatchmaking);
    Net.SetPeerConnectRequestCallback(AddConnection);
    Net.SetConnectionStateChangedCallback(CheckConnection);

    Voip.SetVoipConnectRequestCallback((Message<NetworkingPeer> message) => {
      Debug.Log("Accepting voice connection from " + message.Data.ID);
      Voip.Accept(message.Data.ID);
    });

    Voip.SetVoipStateChangeCallback((Message<NetworkingPeer> message) => {
      Debug.LogFormat("Voice state changed to {1} for user {0}", message.Data.ID, message.Data.State);
    });
  }

  new void Start() {
    base.Start();
    Assert.IsNotNull(context);
    Assert.IsNotNull(localAvatar);
    context.Deactivate();
    localAvatar.GetComponent<Hands>().SetContext(context.GetComponent<Context>());
  }

  new void Update() {
    base.Update();

    if (Input.GetKeyDown("space")) {
      Debug.Log("Forcing reconnect");
      isConnected = false;
      RetryUntilConnectedToServer();
    }

    if (state == InMatchmaking && timeMatchmakingStarted + 30.0 < renderTime) {
      Debug.Log("No result from matchmaker");
      RetryUntilConnectedToServer();
      return;
    }

    if (state == Connecting && !isConnectionAccepted && hostUserId != 0 && connections.Contains(hostUserId)) {
      Debug.Log("Accepting connection request from host");
      Net.Accept(hostUserId);
      isConnectionAccepted = true;
    }

    if (state == Connected) {
      var data = context.GetClientData(); //apply guest avatar state at render time with interpolation
      int count;
      ushort resetId;

      if (data.jitterBuffer.GetInterpolatedAvatars(ref interpolatedAvatars, out count, out resetId) && resetId == context.resetId)
        context.ApplyAvatarUpdates(count, ref interpolatedAvatars, 0, clientId);

      context.GetClientData().jitterBuffer.AdvanceTime(Time.deltaTime); //advance jitter buffer time
    }

    if (state == WaitingForRetry && timeRetryStarted + RetryTime < renderTime) {
      StartMatchmaking();
      return;
    }
    CheckForTimeouts();
  }

  new void FixedUpdate() {
    if (IsConnectedToServer())
      context.UpdateSleep();

    ProcessPacketsFromServer();

    if (IsConnectedToServer()) {
      ProcessAcks();
      SendPacketToServer();
      context.UpdateSleep();
    }
    base.FixedUpdate();
  }

  void RetryUntilConnectedToServer() {
    Matchmaking.Cancel();
    DisconnectFromServer();
    if (isConnected) return;

    Debug.Log("Retrying in " + RetryTime + " seconds...");
    timeRetryStarted = renderTime;
    state = WaitingForRetry;
  }

  void CheckEntitlement(Message message) {
    if (message.IsError)
      Debug.Log("error: You are not entitled to use this app");
    else
      Debug.Log("You are entitled to use this app");
  }

  void StartMatchmaking(Message<User> message) {
    if (message.IsError) {
      Debug.Log("error: Could not get signed in user");
      return;
    }

    Debug.Log("User successfully logged in");
    userId = message.Data.ID;
    oculusId = message.Data.OculusID;
    Debug.Log("User id is " + userId);
    Debug.Log("Oculus id is " + oculusId);
    StartMatchmaking();
  }

  void StartMatchmaking() {
    var options = new MatchmakingOptions();
    options.SetEnqueueQueryKey("quickmatch_query");
    options.SetCreateRoomJoinPolicy(RoomJoinPolicy.Everyone);
    options.SetCreateRoomMaxUsers(MaxClients);
    options.SetEnqueueDataSettings("version", Constants.Version.GetHashCode());

    Matchmaking.Enqueue2("quickmatch", options).OnComplete(MatchmakingEnqueueCallback);
    timeMatchmakingStarted = renderTime;
    state = InMatchmaking;
  }

  void MatchmakingEnqueueCallback(Message message) {
    if (message.IsError) {
      Debug.Log("error: matchmaking error - " + message.GetError());
      RetryUntilConnectedToServer();
      return;
    }

    Debug.Log("Started matchmaking...");
  }

  void JoinRoom(Message<Room> message) {
    Debug.Log("Found match. Room id = " + message.Data.ID);
    roomId = message.Data.ID;
    Matchmaking.JoinRoom(message.Data.ID, true).OnComplete(StartConnectionToServer);
  }

  void StartConnectionToServer(Message<Room> message) {
    if (message.IsError) {
      Debug.Log("error: Failed to join room - " + message.GetError());
      RetryUntilConnectedToServer();
      return;
    }

    Debug.Log("Joined room");
    hostUserId = message.Data.Owner.ID;
    PrintRoomDetails(message.Data);
    StartConnectionToServer();
  }

  void CheckRoomConnection(Message<Room> message) {
    var room = message.Data;
    if (room.ID != roomId) return;
    if (message.IsError) {
      Debug.Log("error: Room updated error (?!) - " + message.GetError());
      return;
    }
    Debug.Log("Room updated");

    foreach (var user in room.Users)
      Debug.Log(" + " + user.OculusID + " [" + user.ID + "]");

    if (state == Connected && !FindUserById(room.Users, userId)) {
      Debug.Log("Looks like we got kicked from the room");
      RetryUntilConnectedToServer();
    }
  }

  void LogLeaveRoomResult(Message<Room> message) {
    if (message.IsError)
      Debug.Log("error: Failed to leave room - " + message.GetError());
    else
      Debug.Log("Left room");
  }

  void AddConnection(Message<NetworkingPeer> message) {
    Debug.Log("Received connection request from " + message.Data.ID);
    connections.Add(message.Data.ID);
  }

  void CheckConnection(Message<NetworkingPeer> message) {
    if (message.Data.ID != hostUserId) return;

    Debug.Log("Connection state changed to " + message.Data.State);

    if (message.Data.State != PeerConnectionState.Connected)
      DisconnectFromServer();
  }

  void StartConnectionToServer() {
    state = Connecting;
    timeConnectionStarted = renderTime;
  }

  void ConnectToServer(int id) {
    Assert.IsTrue(id >= 1);
    Assert.IsTrue(id < MaxClients);
    localAvatar.transform.position = context.GetAvatar(id).gameObject.transform.position;
    localAvatar.transform.rotation = context.GetAvatar(id).gameObject.transform.rotation;
    state = Connected;
    clientId = id;
    context.Init(id);
    OnConnectToServer(id);
  }

  void DisconnectFromServer() {
    if (IsConnectedToServer())
      ResetContext();

    Net.Close(hostUserId);
    LeaveRoom(roomId, LogLeaveRoomResult);
    roomId = 0;
    hostUserId = 0;
    state = Disconnected;
    info.Clear();
    connections.Clear();
    isConnectionAccepted = false;
  }

  void OnConnectToServer(int id) {
    Debug.Log("Connected to server as client " + id);
    timeConnected = renderTime;
    context.Activate();

    for (int i = 0; i < MaxClients; ++i)
      context.HideAvatar(i);

    isConnected = true;
  }

  void ResetContext() {
    Debug.Log("Disconnected from server");
    context.GetClientData().Reset();
    context.resetId = 0;
    context.Reset();
    context.Deactivate();
  }


  protected override void OnQuit() {
    Matchmaking.Cancel();

    if (IsConnectedToServer())
      DisconnectFromServer();

    if (roomId == 0)
      isReadyToShutdown = true;
    else
      LeaveRoom(roomId, LeaveRoomOnQuitCallback);
  }

  protected override bool ReadyToShutdown() => isReadyToShutdown;

  void LeaveRoomOnQuitCallback(Message<Room> message) {
    if (!message.IsError)
      Debug.Log("Left room");

    isReadyToShutdown = true;
    roomId = 0;
  }

  void CheckForTimeouts() {
    if (state == Connecting) {
      if (timeConnectionStarted + ConnectTimeout < renderTime) {
        Debug.Log("Timed out while trying to connect to server");
        RetryUntilConnectedToServer();
      }
    } else if (state == Connected) {
      if (timeLastPacketReceived + ConnectionTimeout < renderTime) {
        Debug.Log("Connection to server timed out");
        DisconnectFromServer();
      }
    }
  }

  void SendPacketToServer() {
    if (!IsConnectedToServer()) return;

    var data = context.GetClientData();
    var packet = GenerateUpdatePacket(data, (float)(physicsTime - renderTime));
    Net.SendPacket(hostUserId, packet, SendPolicy.Unreliable);
    timeLastPacketSent = renderTime;
  }

  void ProcessPacketsFromServer() {
    Packet packet;

    while ((packet = Net.ReadPacket()) != null) {
      if (packet.SenderID != hostUserId) continue;

      packet.ReadBytes(buffer);
      var packetType = buffer[0];

      if ((state == Connecting || state == Connected) && packetType == (byte)PacketSerializer.PacketType.ClientsInfo)
        ProcessServerInfoPacket(buffer);

      if (!IsConnectedToServer()) continue;

      if (packetType == (byte)PacketSerializer.PacketType.StateUpdate) {
        if (isJitterBufferEnabled)
          AddUpdatePacketToJitterBuffer(context, context.GetClientData(), buffer);
        else
          ProcessStateUpdatePacket(context.GetClientData(), buffer);
      }
      timeLastPacketReceived = renderTime;
    }    

    if (isJitterBufferEnabled && IsConnectedToServer()) { //process state update from jitter buffer
      ProcessUpdateFromJitterBuffer(context, context.GetClientData(), 0, clientId, isJitterBufferEnabled && renderTime > timeConnected + 0.25);
    }    

    if (IsConnectedToServer()) { //advance remote frame number
      var data = context.GetClientData();

      if (!data.isFirstPacket)
        data.frame++;
    }
  }

  public byte[] GenerateUpdatePacket(Context.NetworkData data, float timeOffset) {
    Profiler.BeginSample("GenerateStateUpdatePacket");
    int cubeCount = Math.Min(MaxCubes, MaxStateUpdates);
    context.UpdateCubePriorities();
    context.GetCubeUpdates(data, ref cubeCount, ref cubeIds, ref cubes);

    var header = new PacketHeader {
      frame = (uint)frame,
      resetId = context.resetId,
      timeOffset = timeOffset
    };
    data.acking.AddUnackedPackets(ref header);
    DetermineNotChangedAndDeltas(context, data, header.id, cubeCount, ref cubeIds, ref notChanged, ref hasDelta, ref baselineIds, ref cubes, ref cubeDeltas);
    DeterminePrediction(context, data, header.id, cubeCount, ref cubeIds, ref notChanged, ref hasDelta, ref perfectPrediction, ref hasPredictionDelta, ref baselineIds, ref cubes, ref cubePredictions);

    int avatarCount = 1;
    localAvatar.GetComponent<Hands>().GetState(out avatars[0]);
    AvatarState.Quantize(ref avatars[0], out avatarsQuantized[0]);
    WriteUpdatePacket(ref header, avatarCount, ref avatarsQuantized, cubeCount, ref cubeIds, ref notChanged, ref hasDelta, ref perfectPrediction, ref hasPredictionDelta, ref baselineIds, ref cubes, ref cubeDeltas, ref cubePredictions);

    var packet = writeStream.GetData();
    AddPacketToDeltaBuffer(ref data.sendBuffer, header.id, context.resetId, cubeCount, ref cubeIds, ref cubes);
    context.ResetCubePriority(data, cubeCount, cubeIds);
    Profiler.EndSample();

    return packet;
  }  

  public void ProcessServerInfoPacket(byte[] packet) {
    Profiler.BeginSample("ProcessServerInfoPacket");
    var clients = new ClientsInfo();

    if (ReadClientsPacket(packet, clients.areConnected, clients.userIds, clients.userNames)) {
      Debug.Log("Received server info:");
      clients.Print();      

      if (state == Connecting) { //client searches for its own user id in the first server info. this is how the client knows what client slot it has been assigned.
        int id = clients.FindClientByUserId(userId);

        if (id == Nobody) {
          Debug.Log("error: Could not find our user id " + userId + " in server info? Something is horribly wrong!");
          DisconnectFromServer();
          return;
        }

        ConnectToServer(id);
      }

      for (int i = 0; i < MaxClients; ++i) { //track remote clients joining and leaving by detecting edge triggers on the server info.
        if (i == clientId) continue;

        if (!info.areConnected[i] && clients.areConnected[i])
          OnRemoteClientConnected(i, clients.userIds[i], clients.userNames[i]);

        else if (info.areConnected[i] && !clients.areConnected[i])
          OnRemoteClientDisconnected(i, info.userIds[i], info.userNames[i]);
      }     
      info.CopyFrom(clients); //copy across the packet server info to our current server info
    }
    Profiler.EndSample();
  }

  void OnRemoteClientConnected(int clientId, ulong userId, string userName) {
    Debug.Log(userName + " connected as client " + clientId);
    context.ShowAvatar(clientId);
    Voip.Start(userId);
    var head = context.GetAvatarHead(clientId);
    var audio = head.GetComponent<VoipAudioSourceHiLevel>();

    if (!audio)
      audio = head.AddComponent<VoipAudioSourceHiLevel>();

    audio.senderID = userId;
  }

  void OnRemoteClientDisconnected(int clientId, ulong userId, string userName) {
    Debug.Log(userName + " disconnected");
    var head = context.GetAvatarHead(clientId);
    var audio = head.GetComponent<VoipAudioSourceHiLevel>();

    if (audio)
      audio.senderID = 0;

    Voip.Stop(userId);
    context.HideAvatar(clientId);
  }

  public void ProcessStateUpdatePacket(Context.NetworkData data, byte[] packet) {
    Profiler.BeginSample("ProcessStateUpdatePacket");
    int avatarCount = 0;
    int cubeCount = 0;
    PacketHeader header;

    if (ReadUpdatePacket(packet, out header, out avatarCount, ref readAvatarsQuantized, out cubeCount, ref readCubeIds, ref readNotChanged, ref readHasDelta, ref readPerfectPrediction, ref readHasPredictionDelta, ref readBaselineIds, ref readCubes, ref readCubeDeltas, ref readPredictionDeltas)
    ) {
      for (int i = 0; i < avatarCount; ++i) //unquantize avatar states
        AvatarState.Unquantize(ref readAvatarsQuantized[i], out readAvatars[i]);      

      if (Util.IdGreaterThan(context.resetId, header.resetId)) return; //ignore updates from before the last server reset      

      if (Util.IdGreaterThan(header.resetId, context.resetId)) { //reset if the server reset sequence is more recent than ours
        context.Reset();
        context.resetId = header.resetId;
      }      

      DecodePrediction(data.receiveBuffer, header.id, context.resetId, cubeCount, ref readCubeIds, ref readPerfectPrediction, ref readHasPredictionDelta, ref readBaselineIds, ref readCubes, ref readPredictionDeltas); //decode the predicted cube states from baselines      
      DecodeNotChangedAndDeltas(data.receiveBuffer, context.resetId, cubeCount, ref readCubeIds, ref readNotChanged, ref readHasDelta, ref readBaselineIds, ref readCubes, ref readCubeDeltas); //decode the not changed and delta cube states from baselines
      AddPacketToDeltaBuffer(ref data.receiveBuffer, header.id, context.resetId, cubeCount, ref readCubeIds, ref readCubes); //add the cube states to the receive delta buffer      

      int fromClientId = 0; //apply the state updates to cubes
      int toClientId = clientId;
      context.ApplyCubeUpdates(cubeCount, ref readCubeIds, ref readCubes, fromClientId, toClientId, isJitterBufferEnabled && renderTime > timeConnected + 0.25);
      context.ApplyAvatarUpdates(avatarCount, ref readAvatars, fromClientId, toClientId); //apply avatar state updates      
      data.acking.AckPackets(ref header); //process the packet header
    }
    Profiler.EndSample();
  }

  void ProcessAcks() {
    Profiler.BeginSample("Process Acks");
    ProcessAcksForConnection(context, context.GetClientData());
    Profiler.EndSample();
  }
}