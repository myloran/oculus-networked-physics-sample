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
  HashSet<ulong> connectionRequests = new HashSet<ulong>();       // set of connection request ids we have received. used to fix race condition between connection request and room join.
  GuestState state = LoggingIn;
  const double RetryTime = 0.0;//5.0;                                   // time between retry attempts.
  byte[] readBuffer = new byte[Constants.MaxPacketSize];
  string oculusId;                                                // this is our user name.

  ulong 
    userId,                                                   // user id that is signed in
    hostUserId,                                               // the user id of the room owner (host).
    roomId;                                                   // the id of the room that we have joined.

  int clientIndex = -1;                                           // while connected to server in [1,Constants.MaxClients-1]. -1 if not connected.

  bool 
    acceptedConnectionRequest,                                 // true if we have accepted the connection request from the host.
    successfullyConnected,                                     // true if we have ever successfully connected to a server.
    readyToShutdown = false;


  bool IsConnectedToServer() => state == Connected;

  new void Awake() {
    Debug.Log("*** GUEST ***");
    Assert.IsNotNull(context);
    state = LoggingIn;
    InitializePlatformSDK(GetEntitlementCallback);
    Matchmaking.SetMatchFoundNotificationCallback(MatchFoundCallback);
    Rooms.SetUpdateNotificationCallback(RoomUpdatedCallback);
    Users.GetLoggedInUser().OnComplete(GetLoggedInUserCallback);
    Net.SetPeerConnectRequestCallback(PeerConnectRequestCallback);
    Net.SetConnectionStateChangedCallback(ConnectionStateChangedCallback);

    Voip.SetVoipConnectRequestCallback((Message<NetworkingPeer> msg) => {
      Debug.Log("Accepting voice connection from " + msg.Data.ID);
      Voip.Accept(msg.Data.ID);
    });

    Voip.SetVoipStateChangeCallback((Message<NetworkingPeer> msg) => {
      Debug.LogFormat("Voice state changed to {1} for user {0}", msg.Data.ID, msg.Data.State);
    });
  }

  new void Start() {
    base.Start();
    Assert.IsNotNull(context);
    Assert.IsNotNull(localAvatar);
    context.Deactivate();
    localAvatar.GetComponent<Hands>().SetContext(context.GetComponent<Context>());
  }

  void RetryUntilConnectedToServer() {
    Matchmaking.Cancel();
    DisconnectFromServer();
    if (successfullyConnected) return;

    Debug.Log("Retrying in " + RetryTime + " seconds...");
    timeRetryStarted = renderTime;
    state = WaitingForRetry;
  }

  void GetEntitlementCallback(Message msg) {
    if (msg.IsError)
      Debug.Log("error: You are not entitled to use this app");
    else
      Debug.Log("You are entitled to use this app");
  }

  void GetLoggedInUserCallback(Message<User> msg) {
    if (msg.IsError) {
      Debug.Log("error: Could not get signed in user");
      return;
    }

    Debug.Log("User successfully logged in");
    userId = msg.Data.ID;
    oculusId = msg.Data.OculusID;
    Debug.Log("User id is " + userId);
    Debug.Log("Oculus id is " + oculusId);
    StartMatchmaking();
  }

  void StartMatchmaking() {
    var options = new MatchmakingOptions();
    options.SetEnqueueQueryKey("quickmatch_query");
    options.SetCreateRoomJoinPolicy(RoomJoinPolicy.Everyone);
    options.SetCreateRoomMaxUsers(Constants.MaxClients);
    options.SetEnqueueDataSettings("version", Constants.Version.GetHashCode());
    Matchmaking.Enqueue2("quickmatch", options).OnComplete(MatchmakingEnqueueCallback);
    timeMatchmakingStarted = renderTime;
    state = InMatchmaking;
  }

  void MatchmakingEnqueueCallback(Message msg) {
    if (msg.IsError) {
      Debug.Log("error: matchmaking error - " + msg.GetError());
      RetryUntilConnectedToServer();
      return;
    }

    Debug.Log("Started matchmaking...");
  }

  void MatchFoundCallback(Message<Room> msg) {
    Debug.Log("Found match. Room id = " + msg.Data.ID);
    roomId = msg.Data.ID;
    Matchmaking.JoinRoom(msg.Data.ID, true).OnComplete(JoinRoomCallback);
  }

  void JoinRoomCallback(Message<Room> msg) {
    if (msg.IsError) {
      Debug.Log("error: Failed to join room - " + msg.GetError());
      RetryUntilConnectedToServer();
      return;
    }

    Debug.Log("Joined room");
    hostUserId = msg.Data.Owner.ID;
    PrintRoomDetails(msg.Data);
    StartConnectionToServer();
  }

  void RoomUpdatedCallback(Message<Room> msg) {
    var room = msg.Data;
    if (room.ID != roomId) return;
    if (msg.IsError) {
      Debug.Log("error: Room updated error (?!) - " + msg.GetError());
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

  void LeaveRoomCallback(Message<Room> msg) {
    if (msg.IsError)
      Debug.Log("error: Failed to leave room - " + msg.GetError());
    else
      Debug.Log("Left room");
  }

  void PeerConnectRequestCallback(Message<NetworkingPeer> msg) {
    Debug.Log("Received connection request from " + msg.Data.ID);
    connectionRequests.Add(msg.Data.ID);
  }

  void ConnectionStateChangedCallback(Message<NetworkingPeer> msg) {
    if (msg.Data.ID != hostUserId) return;

    Debug.Log("Connection state changed to " + msg.Data.State);

    if (msg.Data.State != PeerConnectionState.Connected)
      DisconnectFromServer();
  }

  void StartConnectionToServer() {
    state = Connecting;
    timeConnectionStarted = renderTime;
  }

  void ConnectToServer(int clientIndex) {
    Assert.IsTrue(clientIndex >= 1);
    Assert.IsTrue(clientIndex < Constants.MaxClients);
    localAvatar.transform.position = context.GetAvatar(clientIndex).gameObject.transform.position;
    localAvatar.transform.rotation = context.GetAvatar(clientIndex).gameObject.transform.rotation;
    state = Connected;
    this.clientIndex = clientIndex;
    context.Init(clientIndex);
    OnConnectToServer(clientIndex);
  }

  void DisconnectFromServer() {
    if (IsConnectedToServer())
      OnDisconnectFromServer();

    Net.Close(hostUserId);
    LeaveRoom(roomId, LeaveRoomCallback);
    roomId = 0;
    hostUserId = 0;
    state = Disconnected;
    info.Clear();
    connectionRequests.Clear();
    acceptedConnectionRequest = false;
  }

  void OnConnectToServer(int clientIndex) {
    Debug.Log("Connected to server as client " + clientIndex);
    timeConnected = renderTime;
    context.Activate();

    for (int i = 0; i < Constants.MaxClients; ++i)
      context.HideAvatar(i);

    successfullyConnected = true;
  }

  void OnDisconnectFromServer() {
    Debug.Log("Disconnected from server");
    context.GetClientData().Reset();
    context.SetResetSequence(0);
    context.Reset();
    context.Deactivate();
  }


  protected override void OnQuit() {
    Matchmaking.Cancel();

    if (IsConnectedToServer())
      DisconnectFromServer();

    if (roomId == 0)
      readyToShutdown = true;
    else
      LeaveRoom(roomId, LeaveRoomOnQuitCallback);
  }

  protected override bool ReadyToShutdown() => readyToShutdown;

  void LeaveRoomOnQuitCallback(Message<Room> msg) {
    if (!msg.IsError) {
      Debug.Log("Left room");
    }

    readyToShutdown = true;
    roomId = 0;
  }

  new void Update() {
    base.Update();

    if (Input.GetKeyDown("space")) {
      Debug.Log("Forcing reconnect");
      successfullyConnected = false;
      RetryUntilConnectedToServer();
    }

    if (state == InMatchmaking && timeMatchmakingStarted + 30.0 < renderTime) {
      Debug.Log("No result from matchmaker");
      RetryUntilConnectedToServer();
      return;
    }

    if (state == Connecting && !acceptedConnectionRequest
      && hostUserId != 0 && connectionRequests.Contains(hostUserId)
    ) {
      Debug.Log("Accepting connection request from host");
      Net.Accept(hostUserId);
      acceptedConnectionRequest = true;
    }

    if (state == Connected) {      
      var data = context.GetClientData(); //apply guest avatar state at render time with interpolation
      int numInterpolatedAvatarStates;
      ushort avatarResetSequence;

      if (data.jitterBuffer.GetInterpolatedAvatar(ref interpolatedAvatars, out numInterpolatedAvatarStates, out avatarResetSequence)
        && avatarResetSequence == context.GetResetSequence()
      ) context.ApplyAvatarUpdates(numInterpolatedAvatarStates, ref interpolatedAvatars, 0, clientIndex);      

      context.GetClientData().jitterBuffer.AdvanceTime(Time.deltaTime); //advance jitter buffer time
    }

    if (state == WaitingForRetry && timeRetryStarted + RetryTime < renderTime) {
      StartMatchmaking();
      return;
    }
    CheckForTimeouts();
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

      if ((state == Connecting || state == Connected) && packetType == (byte)PacketSerializer.PacketType.ServerInfo)
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
      ProcessStateUpdateFromJitterBuffer(context, context.GetClientData(), 0, clientIndex, isJitterBufferEnabled && renderTime > timeConnected + 0.25);
    }    

    if (IsConnectedToServer()) { //advance remote frame number
      var data = context.GetClientData();

      if (!data.isFirstPacket)
        data.frame++;
    }
  }

  public byte[] GenerateStateUpdatePacket(Context.ConnectionData data, float timeOffset) {
    Profiler.BeginSample("GenerateStateUpdatePacket");
    int maxStateUpdates = Math.Min(Constants.NumCubes, Constants.MaxStateUpdates);
    int numStateUpdates = maxStateUpdates;

    context.UpdateCubePriority();
    context.GetCubeUpdates(data, ref numStateUpdates, ref cubeIds, ref cubes);
    PacketHeader writePacketHeader;
    data.connection.GeneratePacketHeader(out writePacketHeader);
    writePacketHeader.resetSequence = context.GetResetSequence();
    writePacketHeader.frameNumber = (uint)frame;
    writePacketHeader.timeOffset = timeOffset;

    DetermineNotChangedAndDeltas(context, data, writePacketHeader.sequence, numStateUpdates, ref cubeIds, ref notChanged, ref hasDelta, ref baselineSequence, ref cubes, ref cubeDeltas);
    DeterminePrediction(context, data, writePacketHeader.sequence, numStateUpdates, ref cubeIds, ref notChanged, ref hasDelta, ref perfectPrediction, ref hasPredictionDelta, ref baselineSequence, ref cubes, ref predictionDelta);

    int numAvatarStates = 1;
    localAvatar.GetComponent<Hands>().GetState(out avatars[0]);
    AvatarState.Quantize(ref avatars[0], out avatarsQuantized[0]);
    WriteUpdatePacket(ref writePacketHeader, numAvatarStates, ref avatarsQuantized, numStateUpdates, ref cubeIds, ref notChanged, ref hasDelta, ref perfectPrediction, ref hasPredictionDelta, ref baselineSequence, ref cubes, ref cubeDeltas, ref predictionDelta);

    var packet = writeStream.GetData();
    AddPacket(ref data.sendBuffer, writePacketHeader.sequence, context.GetResetSequence(), numStateUpdates, ref cubeIds, ref cubes);
    context.ResetCubePriority(data, numStateUpdates, cubeIds);
    Profiler.EndSample();

    return packet;
  }  

  public void ProcessServerInfoPacket(byte[] packet) {
    Profiler.BeginSample("ProcessServerInfoPacket");

    if (ReadServerPacket(packet, packetServerInfo.areConnected, packetServerInfo.userIds, packetServerInfo.userNames)) {
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

      for (int i = 0; i < Constants.MaxClients; ++i) { //track remote clients joining and leaving by detecting edge triggers on the server info.
        if (i == clientIndex) continue;

        if (!info.areConnected[i] && packetServerInfo.areConnected[i])
          OnRemoteClientConnected(i, packetServerInfo.userIds[i], packetServerInfo.userNames[i]);

        else if (info.areConnected[i] && !packetServerInfo.areConnected[i])
          OnRemoteClientDisconnected(i, info.userIds[i], info.userNames[i]);
      }     
      info.CopyFrom(packetServerInfo); //copy across the packet server info to our current server info
    }
    Profiler.EndSample();
  }

  void OnRemoteClientConnected(int clientIndex, ulong userId, string userName) {
    Debug.Log(userName + " connected as client " + clientIndex);
    context.ShowAvatar(clientIndex);
    Voip.Start(userId);
    var headGameObject = context.GetAvatarHead(clientIndex);
    var audioSource = headGameObject.GetComponent<VoipAudioSourceHiLevel>();

    if (!audioSource)
      audioSource = headGameObject.AddComponent<VoipAudioSourceHiLevel>();

    audioSource.senderID = userId;
  }

  void OnRemoteClientDisconnected(int clientIndex, ulong userId, string userName) {
    Debug.Log(userName + " disconnected");
    var headGameObject = context.GetAvatarHead(clientIndex);
    var audioSource = headGameObject.GetComponent<VoipAudioSourceHiLevel>();

    if (audioSource)
      audioSource.senderID = 0;

    Voip.Stop(userId);
    context.HideAvatar(clientIndex);
  }

  public void ProcessStateUpdatePacket(Context.ConnectionData connectionData, byte[] packetData) {
    Profiler.BeginSample("ProcessStateUpdatePacket");
    int readNumAvatarStates = 0;
    int readNumStateUpdates = 0;
    PacketHeader readPacketHeader;

    if (ReadUpdatePacket(packetData, out readPacketHeader, out readNumAvatarStates, ref readAvatarsQuantized, out readNumStateUpdates, ref readCubeIds, ref readNotChanged, ref readHasDelta, ref readPerfectPrediction, ref readHasPredictionDelta, ref readBaselineSequence, ref readCubes, ref readCubeDeltas, ref readPredictionDeltas)
    ) {
      for (int i = 0; i < readNumAvatarStates; ++i) //unquantize avatar states
        AvatarState.Unquantize(ref readAvatarsQuantized[i], out readAvatars[i]);      

      if (Util.SequenceGreaterThan(context.GetResetSequence(), readPacketHeader.resetSequence)) return; //ignore updates from before the last server reset      

      if (Util.SequenceGreaterThan(readPacketHeader.resetSequence, context.GetResetSequence())) { //reset if the server reset sequence is more recent than ours
        context.Reset();
        context.SetResetSequence(readPacketHeader.resetSequence);
      }      

      DecodePrediction(connectionData.receiveBuffer, readPacketHeader.sequence, context.GetResetSequence(), readNumStateUpdates, ref readCubeIds, ref readPerfectPrediction, ref readHasPredictionDelta, ref readBaselineSequence, ref readCubes, ref readPredictionDeltas); //decode the predicted cube states from baselines      
      DecodeNotChangedAndDeltas(connectionData.receiveBuffer, context.GetResetSequence(), readNumStateUpdates, ref readCubeIds, ref readNotChanged, ref readHasDelta, ref readBaselineSequence, ref readCubes, ref readCubeDeltas); //decode the not changed and delta cube states from baselines
      AddPacket(ref connectionData.receiveBuffer, readPacketHeader.sequence, context.GetResetSequence(), readNumStateUpdates, ref readCubeIds, ref readCubes); //add the cube states to the receive delta buffer      

      int fromClientIndex = 0; //apply the state updates to cubes
      int toClientIndex = clientIndex;
      context.ApplyCubeUpdates(readNumStateUpdates, ref readCubeIds, ref readCubes, fromClientIndex, toClientIndex, isJitterBufferEnabled && renderTime > timeConnected + 0.25);
      context.ApplyAvatarUpdates(readNumAvatarStates, ref readAvatars, fromClientIndex, toClientIndex); //apply avatar state updates      
      connectionData.connection.ProcessPacketHeader(ref readPacketHeader); //process the packet header
    }
    Profiler.EndSample();
  }

  void ProcessAcks() {
    Profiler.BeginSample("Process Acks");
    Context.ConnectionData connectionData = context.GetClientData();
    ProcessAcksForConnection(context, connectionData);
    Profiler.EndSample();
  }
}