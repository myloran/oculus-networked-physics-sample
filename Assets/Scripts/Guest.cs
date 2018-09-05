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
    InMatchmaking,                                                // searching for a match
    Connecting,                                                 // connecting to the server
    Connected,                                                  // connected to server. we can send and receive packets.
    Disconnected,                                               // not connected (terminal state).
    WaitingForRetry,                                            // waiting for retry. sit in this state for a few seconds before starting matchmaking again.
  };

  public Context context;
  public double timeMatchmakingStarted;                           // time matchmaking started
  public double timeConnectionStarted;                            // time the client connection started (used to timeout due to NAT)
  public double timeConnected;                                    // time the client connected to the server
  public double timeLastPacketSent;                               // time the last packet was sent to the server
  public double timeLastPacketReceived;                           // time the last packet was received from the server (used for post-connect timeouts)
  public double timeRetryStarted;                                 // time the retry state started. used to delay in waiting for retry state before retrying matchmaking from scratch.
  ClientsInfo packetServerInfo = new ClientsInfo();
  HashSet<ulong> connections = new HashSet<ulong>();       // set of connection request ids we have received. used to fix race condition between connection request and room join.
  GuestState state = LoggingIn;
  const double RetryTime = 0.0;//5.0;                                   // time between retry attempts.
  byte[] readBuffer = new byte[MaxPacketSize];
  string oculusId;                                                // this is our user name.

  ulong 
    userId,                                                   // user id that is signed in
    hostUserId,                                               // the user id of the room owner (host).
    roomId;                                                   // the id of the room that we have joined.

  int clientId = -1;                                           // while connected to server in [1,Constants.MaxClients-1]. -1 if not connected.

  bool 
    isConnectionAccepted,                                 // true if we have accepted the connection request from the host.
    isConnected,                                     // true if we have ever successfully connected to a server.
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

    if (state == Connecting && !isConnectionAccepted
      && hostUserId != 0 && connections.Contains(hostUserId)
    ) {
      Debug.Log("Accepting connection request from host");
      Net.Accept(hostUserId);
      isConnectionAccepted = true;
    }

    if (state == Connected) {
      var data = context.GetClientData(); //apply guest avatar state at render time with interpolation
      int numInterpolatedAvatarStates;
      ushort avatarResetSequence;

      if (data.jitterBuffer.GetInterpolatedAvatars(ref interpolatedAvatars, out numInterpolatedAvatarStates, out avatarResetSequence)
        && avatarResetSequence == context.resetSequence
      ) context.ApplyAvatarUpdates(numInterpolatedAvatarStates, ref interpolatedAvatars, 0, clientId);

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

  void ConnectToServer(int clientId) {
    Assert.IsTrue(clientId >= 1);
    Assert.IsTrue(clientId < MaxClients);
    localAvatar.transform.position = context.GetAvatar(clientId).gameObject.transform.position;
    localAvatar.transform.rotation = context.GetAvatar(clientId).gameObject.transform.rotation;
    state = Connected;
    this.clientId = clientId;
    context.Init(clientId);
    OnConnectToServer(clientId);
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

  void OnConnectToServer(int clientId) {
    Debug.Log("Connected to server as client " + clientId);
    timeConnected = renderTime;
    context.Activate();

    for (int i = 0; i < MaxClients; ++i)
      context.HideAvatar(i);

    isConnected = true;
  }

  void ResetContext() {
    Debug.Log("Disconnected from server");
    context.GetClientData().Reset();
    context.resetSequence = 0;
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
    if (!message.IsError) {
      Debug.Log("Left room");
    }

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
    var packet = GenerateStateUpdatePacket(data, (float)(physicsTime - renderTime));
    Net.SendPacket(hostUserId, packet, SendPolicy.Unreliable);
    timeLastPacketSent = renderTime;
  }

  void ProcessPacketsFromServer() {
    Packet packet;

    while ((packet = Net.ReadPacket()) != null) {
      if (packet.SenderID != hostUserId) continue;

      packet.ReadBytes(readBuffer);
      var packetType = readBuffer[0];

      if ((state == Connecting || state == Connected) && packetType == (byte)PacketSerializer.PacketType.ClientsInfo)
        ProcessServerInfoPacket(readBuffer);

      if (!IsConnectedToServer()) continue;

      if (packetType == (byte)PacketSerializer.PacketType.StateUpdate) {
        if (isJitterBufferEnabled)
          AddUpdatePacket(context, context.GetClientData(), readBuffer);
        else
          ProcessStateUpdatePacket(context.GetClientData(), readBuffer);
      }
      timeLastPacketReceived = renderTime;
    }    

    if (isJitterBufferEnabled && IsConnectedToServer()) { //process state update from jitter buffer
      ProcessStateUpdateFromJitterBuffer(context, context.GetClientData(), 0, clientId, isJitterBufferEnabled && renderTime > timeConnected + 0.25);
    }    

    if (IsConnectedToServer()) { //advance remote frame number
      var data = context.GetClientData();

      if (!data.isFirstPacket)
        data.frame++;
    }
  }

  public byte[] GenerateStateUpdatePacket(Context.ConnectionData data, float timeOffset) {
    Profiler.BeginSample("GenerateStateUpdatePacket");
    int maxStateUpdates = Math.Min(NumCubes, MaxStateUpdates);
    int numStateUpdates = maxStateUpdates;

    context.UpdateCubePriority();
    context.GetCubeUpdates(data, ref numStateUpdates, ref cubeIds, ref cubes);
    PacketHeader writePacketHeader;
    data.connection.GeneratePacketHeader(out writePacketHeader);
    writePacketHeader.resetSequence = context.resetSequence;
    writePacketHeader.frame = (uint)frame;
    writePacketHeader.timeOffset = timeOffset;

    DetermineNotChangedAndDeltas(context, data, writePacketHeader.sequence, numStateUpdates, ref cubeIds, ref notChanged, ref hasDelta, ref baselineSequence, ref cubes, ref cubeDeltas);
    DeterminePrediction(context, data, writePacketHeader.sequence, numStateUpdates, ref cubeIds, ref notChanged, ref hasDelta, ref perfectPrediction, ref hasPredictionDelta, ref baselineSequence, ref cubes, ref predictionDelta);

    int numAvatarStates = 1;
    localAvatar.GetComponent<Hands>().GetState(out avatars[0]);
    AvatarState.Quantize(ref avatars[0], out avatarsQuantized[0]);
    WriteUpdatePacket(ref writePacketHeader, numAvatarStates, ref avatarsQuantized, numStateUpdates, ref cubeIds, ref notChanged, ref hasDelta, ref perfectPrediction, ref hasPredictionDelta, ref baselineSequence, ref cubes, ref cubeDeltas, ref predictionDelta);

    var packet = writeStream.GetData();
    AddPacket(ref data.sendBuffer, writePacketHeader.sequence, context.resetSequence, numStateUpdates, ref cubeIds, ref cubes);
    context.ResetCubePriority(data, numStateUpdates, cubeIds);
    Profiler.EndSample();

    return packet;
  }  

  public void ProcessServerInfoPacket(byte[] packet) {
    Profiler.BeginSample("ProcessServerInfoPacket");

    if (ReadClientsPacket(packet, packetServerInfo.areConnected, packetServerInfo.userIds, packetServerInfo.userNames)) {
      Debug.Log("Received server info:");
      packetServerInfo.Print();      

      if (state == Connecting) { //client searches for its own user id in the first server info. this is how the client knows what client slot it has been assigned.
        int clientId = packetServerInfo.FindClientByUserId(userId);

        if (clientId != -1) {
          ConnectToServer(clientId);
        } else {
          Debug.Log("error: Could not find our user id " + userId + " in server info? Something is horribly wrong!");
          DisconnectFromServer();
          return;
        }
      }      

      for (int i = 0; i < MaxClients; ++i) { //track remote clients joining and leaving by detecting edge triggers on the server info.
        if (i == clientId) continue;

        if (!info.areConnected[i] && packetServerInfo.areConnected[i])
          OnRemoteClientConnected(i, packetServerInfo.userIds[i], packetServerInfo.userNames[i]);

        else if (info.areConnected[i] && !packetServerInfo.areConnected[i])
          OnRemoteClientDisconnected(i, info.userIds[i], info.userNames[i]);
      }     
      info.CopyFrom(packetServerInfo); //copy across the packet server info to our current server info
    }
    Profiler.EndSample();
  }

  void OnRemoteClientConnected(int clientId, ulong userId, string userName) {
    Debug.Log(userName + " connected as client " + clientId);
    context.ShowAvatar(clientId);
    Voip.Start(userId);
    var headGameObject = context.GetAvatarHead(clientId);
    var audioSource = headGameObject.GetComponent<VoipAudioSourceHiLevel>();

    if (!audioSource)
      audioSource = headGameObject.AddComponent<VoipAudioSourceHiLevel>();

    audioSource.senderID = userId;
  }

  void OnRemoteClientDisconnected(int clientId, ulong userId, string userName) {
    Debug.Log(userName + " disconnected");
    var headGameObject = context.GetAvatarHead(clientId);
    var audioSource = headGameObject.GetComponent<VoipAudioSourceHiLevel>();

    if (audioSource)
      audioSource.senderID = 0;

    Voip.Stop(userId);
    context.HideAvatar(clientId);
  }

  public void ProcessStateUpdatePacket(Context.ConnectionData data, byte[] packet) {
    Profiler.BeginSample("ProcessStateUpdatePacket");
    int readNumAvatarStates = 0;
    int readNumStateUpdates = 0;
    PacketHeader readPacketHeader;

    if (ReadUpdatePacket(packet, out readPacketHeader, out readNumAvatarStates, ref readAvatarsQuantized, out readNumStateUpdates, ref readCubeIds, ref readNotChanged, ref readHasDelta, ref readPerfectPrediction, ref readHasPredictionDelta, ref readBaselineSequence, ref readCubes, ref readCubeDeltas, ref readPredictionDeltas)
    ) {
      for (int i = 0; i < readNumAvatarStates; ++i) //unquantize avatar states
        AvatarState.Unquantize(ref readAvatarsQuantized[i], out readAvatars[i]);      

      if (Util.SequenceGreaterThan(context.resetSequence, readPacketHeader.resetSequence)) return; //ignore updates from before the last server reset      

      if (Util.SequenceGreaterThan(readPacketHeader.resetSequence, context.resetSequence)) { //reset if the server reset sequence is more recent than ours
        context.Reset();
        context.resetSequence = readPacketHeader.resetSequence;
      }      

      DecodePrediction(data.receiveBuffer, readPacketHeader.sequence, context.resetSequence, readNumStateUpdates, ref readCubeIds, ref readPerfectPrediction, ref readHasPredictionDelta, ref readBaselineSequence, ref readCubes, ref readPredictionDeltas); //decode the predicted cube states from baselines      
      DecodeNotChangedAndDeltas(data.receiveBuffer, context.resetSequence, readNumStateUpdates, ref readCubeIds, ref readNotChanged, ref readHasDelta, ref readBaselineSequence, ref readCubes, ref readCubeDeltas); //decode the not changed and delta cube states from baselines
      AddPacket(ref data.receiveBuffer, readPacketHeader.sequence, context.resetSequence, readNumStateUpdates, ref readCubeIds, ref readCubes); //add the cube states to the receive delta buffer      

      int fromClientId = 0; //apply the state updates to cubes
      int toClientId = clientId;
      context.ApplyCubeUpdates(readNumStateUpdates, ref readCubeIds, ref readCubes, fromClientId, toClientId, isJitterBufferEnabled && renderTime > timeConnected + 0.25);
      context.ApplyAvatarUpdates(readNumAvatarStates, ref readAvatars, fromClientId, toClientId); //apply avatar state updates      
      data.connection.ProcessPacketHeader(ref readPacketHeader); //process the packet header
    }
    Profiler.EndSample();
  }

  void ProcessAcks() {
    Profiler.BeginSample("Process Acks");
    ProcessAcksForConnection(context, context.GetClientData());
    Profiler.EndSample();
  }
}