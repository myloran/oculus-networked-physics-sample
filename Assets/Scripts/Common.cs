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
using static UnityEngine.Profiling.Profiler;
using static UnityEngine.Assertions.Assert;
using static UnityEngine.Debug;
using static Constants;

public class Common : MonoBehaviour {
  protected class ClientsInfo {
    public bool[] areConnected = new bool[MaxClients];
    public ulong[] userIds = new ulong[MaxClients];
    public string[] userNames = new string[MaxClients];

    public void Clear() {
      for (int i = 0; i < MaxClients; ++i) {
        areConnected[i] = false;
        userIds[i] = 0;
        userNames[i] = "";
      }
    }

    public void CopyFrom(ClientsInfo other) {
      for (int i = 0; i < MaxClients; ++i) {
        areConnected[i] = other.areConnected[i];
        userIds[i] = other.userIds[i];
        userNames[i] = other.userNames[i];
      }
    }

    public int FindClientByUserId(ulong userId) {
      for (int i = 0; i < MaxClients; ++i) {
        if (areConnected[i] && userIds[i] == userId)
          return i;
      }
      return -1;
    }

    public void Print() {
      for (int i = 0; i < MaxClients; ++i) {
        if (areConnected[i])
          Log(i + ": " + userNames[i] + " [" + userIds[i] + "]"); else
          Log(i + ": (not connected)");
      }
    }
  };

  public const int ConnectTimeout = 15;
  public const int ConnectionTimeout = 5;
  public GameObject localAvatar;
  protected ClientsInfo info = new ClientsInfo();
  protected ReadStream readStream = new ReadStream();
  protected WriteStream writeStream = new WriteStream();
  protected PacketSerializer serializer = new PacketSerializer();
  protected Simulator simulator = new Simulator();

  protected AvatarState[] 
    interpolatedAvatars = new AvatarState[MaxClients],
    avatars = new AvatarState[MaxClients],
    readAvatars = new AvatarState[MaxClients];

  protected AvatarStateQuantized[] 
    avatarsQuantized = new AvatarStateQuantized[MaxClients],
    readAvatarsQuantized = new AvatarStateQuantized[MaxClients];

  protected CubeState[]
    cubes = new CubeState[NumCubes],
    readCubes = new CubeState[NumCubes];

  protected CubeDelta[] 
    cubeDeltas = new CubeDelta[NumCubes],
    predictionDelta = new CubeDelta[NumCubes],
    readCubeDeltas = new CubeDelta[NumCubes],
    readPredictionDeltas = new CubeDelta[NumCubes];

  protected uint[] packetBuffer = new uint[MaxPacketSize / 4];

  protected ushort[] 
    baselineSequence = new ushort[NumCubes],
    readBaselineSequence = new ushort[NumCubes],
    acks = new ushort[Connection.MaximumAcks];

  protected int[] 
    cubeIds = new int[NumCubes],
    readCubeIds = new int[NumCubes];

  protected bool[] 
    notChanged = new bool[NumCubes],
    hasDelta = new bool[NumCubes],
    perfectPrediction = new bool[NumCubes],
    hasPredictionDelta = new bool[NumCubes],
    readNotChanged = new bool[NumCubes],
    readHasDelta = new bool[NumCubes],
    readPerfectPrediction = new bool[NumCubes],
    readHasPredictionDelta = new bool[NumCubes];

  protected double 
    renderTime = 0.0,
    physicsTime = 0.0;

  protected long frame = 0;
  protected bool isJitterBufferEnabled = true;
  bool wantsToShutdown = false;


  protected void Start() {
    Log("Running Tests");
    Tests.RunTests();
  }

  protected virtual void OnQuit() { /*override this*/ }
  protected virtual bool ReadyToShutdown() => true;

  protected void Update() {
    if (Input.GetKeyDown("backspace")) {
      if (isJitterBufferEnabled = !isJitterBufferEnabled)
        Log("Enabled jitter buffer"); else
        Log("Disabled jitter buffer");
    }

    if (Input.GetKeyDown(KeyCode.Escape)) {
      Log("User quit the application (ESC)");
      wantsToShutdown = true;
      OnQuit();
    }

    if (wantsToShutdown && ReadyToShutdown()) {
      Log("Shutting down");
      UnityEngine.Application.Quit();
      wantsToShutdown = false;
    }
    renderTime += Time.deltaTime;
  }

  protected void FixedUpdate() {
    physicsTime += 1.0 / PhysicsFrameRate;
    frame++;
  }

  protected void AddUpdatePacket(Context context, Context.ConnectionData d, byte[] packet) {
    long frame;
    if (!d.jitterBuffer.AddUpdatePacket(packet, d.receiveBuffer, context.resetSequence, out frame) || !d.isFirstPacket)
      return;

    d.isFirstPacket = false;
    d.frame = frame - NumJitterBufferFrames;
    d.jitterBuffer.Start(d.frame);
  }

  protected void ProcessStateUpdateFromJitterBuffer(Context context, Context.ConnectionData data, int fromClientIndex, int toClientIndex, bool applySmoothing = true) {
    if (data.frame < 0) return;

    var entry = data.jitterBuffer.GetEntry((uint)data.frame);
    if (entry == null) return;

    if (fromClientIndex == 0) {
      //server -> client      
      if (Util.SequenceGreaterThan(context.resetSequence, entry.packetHeader.resetSequence)) return; //Ignore updates from before the last reset.
      if (Util.SequenceGreaterThan(entry.packetHeader.resetSequence, context.resetSequence)) { //Reset if the server reset sequence is more recent than ours.
        context.Reset();
        context.resetSequence = entry.packetHeader.resetSequence;
      }
    } else {
      //client -> server      
      if (context.resetSequence != entry.packetHeader.resetSequence) return; //Ignore any updates from the client with a different reset sequence #
    }

    AddPacket(ref data.receiveBuffer, entry.packetHeader.sequence, context.resetSequence, entry.numStateUpdates, ref entry.cubeIds, ref entry.cubeState); //add the cube states to the receive delta buffer    
    context.ApplyCubeUpdates(entry.numStateUpdates, ref entry.cubeIds, ref entry.cubeState, fromClientIndex, toClientIndex, applySmoothing); //apply the state updates to cubes    
    data.connection.ProcessPacketHeader(ref entry.packetHeader); //process the packet header (handles acks)
  }

  protected bool WriteClientsPacket(bool[] areConnected, ulong[] userIds, string[] userNames) {
    BeginSample("WriteServerInfoPacket");
    writeStream.Start(packetBuffer);
    var result = true;

    try {
      serializer.WriteClientsPacket(writeStream, areConnected, userIds, userNames);
      writeStream.Finish();
    } catch (SerializeException) {
      Log("error: failed to write server info packet");
      result = false;
    }
    EndSample();

    return result;
  }

  protected bool ReadClientsPacket(byte[] packetData, bool[] clientConnected, ulong[] clientUserId, string[] clientUserName) {
    BeginSample("ReadServerInfoPacket");
    readStream.Start(packetData);
    var result = true;

    try {
      serializer.ReadClientsPacket(readStream, clientConnected, clientUserId, clientUserName);
    } catch (SerializeException) {
      Log("error: failed to read server info packet");
      result = false;
    }
    readStream.Finish();
    EndSample();

    return result;
  }

  protected bool WriteUpdatePacket(ref PacketHeader packetHeader, int numAvatarStates, ref AvatarStateQuantized[] avatarState, int numStateUpdates, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta, ref CubeDelta[] predictionDelta
  ) {
    BeginSample("WriteStateUpdatePacket");
    writeStream.Start(packetBuffer);
    var result = true;

    try {
      serializer.WriteUpdatePacket(writeStream, ref packetHeader, numAvatarStates, avatarState, numStateUpdates, cubeIds, notChanged, hasDelta, perfectPrediction, hasPredictionDelta, baselineSequence, cubeState, cubeDelta, predictionDelta);
      writeStream.Finish();
    } catch (SerializeException) {
      Log("error: failed to write state update packet packet");
      result = false;
    }
    EndSample();

    return result;
  }

  protected bool ReadUpdatePacket(byte[] packetData, out PacketHeader packetHeader, out int numAvatarStates, ref AvatarStateQuantized[] avatarState, out int numStateUpdates, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta, ref CubeDelta[] predictionDelta
  ) {
    BeginSample("ReadStateUpdatePacket");
    readStream.Start(packetData);
    var result = true;

    try {
      serializer.ReadUpdatePacket(readStream, out packetHeader, out numAvatarStates, avatarState, out numStateUpdates, cubeIds, notChanged, hasDelta, perfectPrediction, hasPredictionDelta, baselineSequence, cubeState, cubeDelta, predictionDelta);
    } catch (SerializeException) {
      Log("error: failed to read state update packet");
      packetHeader.sequence = 0;
      packetHeader.ack = 0;
      packetHeader.ackBits = 0;
      packetHeader.frame = 0;
      packetHeader.resetSequence = 0;
      packetHeader.timeOffset = 0.0f;
      numAvatarStates = 0;
      numStateUpdates = 0;
      result = false;
    }
    readStream.Finish();
    EndSample();

    return result;
  }

  protected void AddPacket(ref DeltaBuffer deltaBuffer, ushort sequence, ushort resetSequence, int numCubes, ref int[] cubeIds, ref CubeState[] cubeState
  ) {
    BeginSample("AddPacketToDeltaBuffer");
    deltaBuffer.AddPacket(sequence, resetSequence);

    for (int i = 0; i < numCubes; ++i)
      deltaBuffer.AddCubeState(sequence, cubeIds[i], ref cubeState[i]);

    EndSample();
  }

  protected void DetermineNotChangedAndDeltas(Context context, Context.ConnectionData connectionData, ushort currentSequence, int numCubes, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta
  ) {
    BeginSample("DeterminedNotChangedAndDeltas");
#if !DISABLE_DELTA_COMPRESSION
    CubeState baselineCubeState = CubeState.defaults;
#endif // #if !DISABLE_DELTA_COMPRESSION
    for (int i = 0; i < numCubes; ++i) {
      notChanged[i] = false;
      hasDelta[i] = false;
#if !DISABLE_DELTA_COMPRESSION
#if DEBUG_DELTA_COMPRESSION
            cubeDelta[i].absolute_position_x = cubeState[i].position_x;
            cubeDelta[i].absolute_position_y = cubeState[i].position_y;
            cubeDelta[i].absolute_position_z = cubeState[i].position_z;
#endif // #if DEBUG_DELTA_COMPRESSION
      if (context.GetAck(connectionData, cubeIds[i], ref baselineSequence[i], context.resetSequence, ref baselineCubeState)) {
        if (Util.BaselineDifference(currentSequence, baselineSequence[i]) > MaxBaselineDifference) continue; //baseline is too far behind => send the cube state absolute.
        if (baselineCubeState.Equals(cubeState[i])) {
          notChanged[i] = true;
        } else {
          hasDelta[i] = true;
          cubeDelta[i].positionX = cubeState[i].positionX - baselineCubeState.positionX;
          cubeDelta[i].positionY = cubeState[i].positionY - baselineCubeState.positionY;
          cubeDelta[i].positionZ = cubeState[i].positionZ - baselineCubeState.positionZ;
          cubeDelta[i].linearVelocityX = cubeState[i].linearVelocityX - baselineCubeState.linearVelocityX;
          cubeDelta[i].linearVelocityY = cubeState[i].linearVelocityY - baselineCubeState.linearVelocityY;
          cubeDelta[i].linearVelocityZ = cubeState[i].linearVelocityZ - baselineCubeState.linearVelocityZ;
          cubeDelta[i].angularVelocityX = cubeState[i].angularVelocityX - baselineCubeState.angularVelocityX;
          cubeDelta[i].angularVelocityY = cubeState[i].angularVelocityY - baselineCubeState.angularVelocityY;
          cubeDelta[i].angularVelocityZ = cubeState[i].angularVelocityZ - baselineCubeState.angularVelocityZ;
        }
      }
#endif // #if !DISABLE_DELTA_COMPRESSION
    }
    EndSample();
  }

  protected bool DecodeNotChangedAndDeltas(DeltaBuffer deltaBuffer, ushort resetSequence, int numCubes, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta
  ) {
    BeginSample("DecodeNotChangedAndDeltas");
    bool result = true;
#if !DISABLE_DELTA_COMPRESSION
    var baselineCubeState = CubeState.defaults;

    for (int i = 0; i < numCubes; ++i) {
      if (notChanged[i]) {
        if (deltaBuffer.GetCubeState(baselineSequence[i], resetSequence, cubeIds[i], ref baselineCubeState)) {
#if DEBUG_DELTA_COMPRESSION
          if ( baselineCubeState.position_x != cubeDelta[i].absolute_position_x )
          {
              Log( "expected " + cubeDelta[i].absolute_position_x + ", got " + baselineCubeState.position_x );
          }
          Assert.IsTrue( baselineCubeState.position_x == cubeDelta[i].absolute_position_x );
          Assert.IsTrue( baselineCubeState.position_y == cubeDelta[i].absolute_position_y );
          Assert.IsTrue( baselineCubeState.position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
          cubeState[i] = baselineCubeState;
        } else {
          Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineSequence[i] + " (not changed)");
          result = false;
          break;
        }
      } else if (hasDelta[i]) {
        if (deltaBuffer.GetCubeState(baselineSequence[i], resetSequence, cubeIds[i], ref baselineCubeState)) {
          cubeState[i].positionX = baselineCubeState.positionX + cubeDelta[i].positionX;
          cubeState[i].positionY = baselineCubeState.positionY + cubeDelta[i].positionY;
          cubeState[i].positionZ = baselineCubeState.positionZ + cubeDelta[i].positionZ;
#if DEBUG_DELTA_COMPRESSION
                    Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
                    Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
                    Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
          cubeState[i].linearVelocityX = baselineCubeState.linearVelocityX + cubeDelta[i].linearVelocityX;
          cubeState[i].linearVelocityY = baselineCubeState.linearVelocityY + cubeDelta[i].linearVelocityY;
          cubeState[i].linearVelocityZ = baselineCubeState.linearVelocityZ + cubeDelta[i].linearVelocityZ;
          cubeState[i].angularVelocityX = baselineCubeState.angularVelocityX + cubeDelta[i].angularVelocityX;
          cubeState[i].angularVelocityY = baselineCubeState.angularVelocityY + cubeDelta[i].angularVelocityY;
          cubeState[i].angularVelocityZ = baselineCubeState.angularVelocityZ + cubeDelta[i].angularVelocityZ;
        } else {
          Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineSequence[i] + " (delta)");
          result = false;
          break;
        }
      }
    }
#endif // #if !DISABLE_DELTA_COMPRESSION
    return result;
  }

  protected void DeterminePrediction(Context context, Context.ConnectionData connectionData, ushort currentSequence, int numCubes, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] predictionDeltas
  ) {
    BeginSample("DeterminePrediction");
    var baselineCubeState = CubeState.defaults;

    for (int i = 0; i < numCubes; ++i) {
      perfectPrediction[i] = false;
      hasPredictionDelta[i] = false;
#if !DISABLE_DELTA_ENCODING
      if (notChanged[i]) continue;
      if (!hasDelta[i]) continue;
      if (!cubeState[i].isActive) continue;

      if (context.GetAck(connectionData, cubeIds[i], ref baselineSequence[i], context.resetSequence, ref baselineCubeState)) {
        if (Util.BaselineDifference(currentSequence, baselineSequence[i]) <= MaxBaselineDifference) continue; //baseline is too far behind. send the cube state absolute
        if (!baselineCubeState.isActive) continue; //no point predicting if the cube is at rest.

        int baseline_sequence = baselineSequence[i];
        int current_sequence = currentSequence;

        if (current_sequence < baseline_sequence)
          current_sequence += 65536;

        int baseline_position_x = baselineCubeState.positionX;
        int baseline_position_y = baselineCubeState.positionY;
        int baseline_position_z = baselineCubeState.positionZ;
        int baseline_linear_velocity_x = baselineCubeState.linearVelocityX;
        int baseline_linear_velocity_y = baselineCubeState.linearVelocityY;
        int baseline_linear_velocity_z = baselineCubeState.linearVelocityZ;
        int baseline_angular_velocity_x = baselineCubeState.angularVelocityX;
        int baseline_angular_velocity_y = baselineCubeState.angularVelocityY;
        int baseline_angular_velocity_z = baselineCubeState.angularVelocityZ;

        if (current_sequence < baseline_sequence)
          current_sequence += 65536;

        int numFrames = current_sequence - baseline_sequence;
        int predicted_position_x;
        int predicted_position_y;
        int predicted_position_z;
        int predicted_linear_velocity_x;
        int predicted_linear_velocity_y;
        int predicted_linear_velocity_z;
        int predicted_angular_velocity_x;
        int predicted_angular_velocity_y;
        int predicted_angular_velocity_z;

        Prediction.PredictBallistic(numFrames,
                                     baseline_position_x, baseline_position_y, baseline_position_z,
                                     baseline_linear_velocity_x, baseline_linear_velocity_y, baseline_linear_velocity_z,
                                     baseline_angular_velocity_x, baseline_angular_velocity_y, baseline_angular_velocity_z,
                                     out predicted_position_x, out predicted_position_y, out predicted_position_z,
                                     out predicted_linear_velocity_x, out predicted_linear_velocity_y, out predicted_linear_velocity_z,
                                     out predicted_angular_velocity_x, out predicted_angular_velocity_y, out predicted_angular_velocity_z);

        int current_position_x = cubeState[i].positionX;
        int current_position_y = cubeState[i].positionY;
        int current_position_z = cubeState[i].positionZ;
        int current_linear_velocity_x = cubeState[i].linearVelocityX;
        int current_linear_velocity_y = cubeState[i].linearVelocityY;
        int current_linear_velocity_z = cubeState[i].linearVelocityZ;
        int current_angular_velocity_x = cubeState[i].angularVelocityX;
        int current_angular_velocity_y = cubeState[i].angularVelocityY;
        int current_angular_velocity_z = cubeState[i].angularVelocityZ;
        int position_error_x = current_position_x - predicted_position_x;
        int position_error_y = current_position_y - predicted_position_y;
        int position_error_z = current_position_z - predicted_position_z;
        int linear_velocity_error_x = current_linear_velocity_x - predicted_linear_velocity_x;
        int linear_velocity_error_y = current_linear_velocity_y - predicted_linear_velocity_y;
        int linear_velocity_error_z = current_linear_velocity_z - predicted_linear_velocity_z;
        int angular_velocity_error_x = current_angular_velocity_x - predicted_angular_velocity_x;
        int angular_velocity_error_y = current_angular_velocity_y - predicted_angular_velocity_y;
        int angular_velocity_error_z = current_angular_velocity_z - predicted_angular_velocity_z;

        if (position_error_x == 0 &&
             position_error_y == 0 &&
             position_error_z == 0 &&
             linear_velocity_error_x == 0 &&
             linear_velocity_error_y == 0 &&
             linear_velocity_error_z == 0 &&
             angular_velocity_error_x == 0 &&
             angular_velocity_error_y == 0 &&
             angular_velocity_error_z == 0) {
          perfectPrediction[i] = true;
        } else {
          int abs_position_error_x = Math.Abs(position_error_x);
          int abs_position_error_y = Math.Abs(position_error_y);
          int abs_position_error_z = Math.Abs(position_error_z);
          int abs_linear_velocity_error_x = Math.Abs(linear_velocity_error_x);
          int abs_linear_velocity_error_y = Math.Abs(linear_velocity_error_y);
          int abs_linear_velocity_error_z = Math.Abs(linear_velocity_error_z);
          int abs_angular_velocity_error_x = Math.Abs(angular_velocity_error_x);
          int abs_angular_velocity_error_y = Math.Abs(angular_velocity_error_y);
          int abs_angular_velocity_error_z = Math.Abs(angular_velocity_error_z);

          int total_prediction_error = abs_position_error_x +
                                       abs_position_error_y +
                                       abs_position_error_z +
                                       linear_velocity_error_x +
                                       linear_velocity_error_y +
                                       linear_velocity_error_z +
                                       angular_velocity_error_x +
                                       angular_velocity_error_y +
                                       angular_velocity_error_z;

          int total_absolute_error = Math.Abs(cubeState[i].positionX - baselineCubeState.positionX) +
                                     Math.Abs(cubeState[i].positionY - baselineCubeState.positionY) +
                                     Math.Abs(cubeState[i].positionZ - baselineCubeState.positionZ) +
                                     Math.Abs(cubeState[i].linearVelocityX - baselineCubeState.linearVelocityX) +
                                     Math.Abs(cubeState[i].linearVelocityY - baselineCubeState.linearVelocityY) +
                                     Math.Abs(cubeState[i].linearVelocityZ - baselineCubeState.linearVelocityZ) +
                                     Math.Abs(cubeState[i].angularVelocityX - baselineCubeState.angularVelocityX) +
                                     Math.Abs(cubeState[i].angularVelocityY - baselineCubeState.angularVelocityY) +
                                     Math.Abs(cubeState[i].angularVelocityZ - baselineCubeState.angularVelocityZ);

          if (total_prediction_error < total_absolute_error) {
            int max_position_error = abs_position_error_x;

            if (abs_position_error_y > max_position_error)
              max_position_error = abs_position_error_y;

            if (abs_position_error_z > max_position_error)
              max_position_error = abs_position_error_z;

            int max_linear_velocity_error = abs_linear_velocity_error_x;

            if (abs_linear_velocity_error_y > max_linear_velocity_error)
              max_linear_velocity_error = abs_linear_velocity_error_y;

            if (abs_linear_velocity_error_z > max_linear_velocity_error)
              max_linear_velocity_error = abs_linear_velocity_error_z;

            int max_angular_velocity_error = abs_angular_velocity_error_x;

            if (abs_angular_velocity_error_y > max_angular_velocity_error)
              max_angular_velocity_error = abs_angular_velocity_error_y;

            if (abs_angular_velocity_error_z > max_angular_velocity_error)
              max_angular_velocity_error = abs_angular_velocity_error_z;

            if (max_position_error <= Constants.PositionDeltaMax &&
                 max_linear_velocity_error <= Constants.LinearVelocityDeltaMax &&
                 max_angular_velocity_error <= Constants.AngularVelocityDeltaMax) {
              hasPredictionDelta[i] = true;

              predictionDelta[i].positionX = position_error_x;
              predictionDelta[i].positionY = position_error_y;
              predictionDelta[i].positionZ = position_error_z;

              predictionDelta[i].linearVelocityX = linear_velocity_error_x;
              predictionDelta[i].linearVelocityY = linear_velocity_error_y;
              predictionDelta[i].linearVelocityZ = linear_velocity_error_z;

              predictionDelta[i].angularVelocityX = angular_velocity_error_x;
              predictionDelta[i].angularVelocityY = angular_velocity_error_y;
              predictionDelta[i].angularVelocityZ = angular_velocity_error_z;
            }
          }
        }
      }
    }
#endif // #if !DISABLE_DELTA_ENCODING
    EndSample();
  }

  protected bool DecodePrediction(DeltaBuffer deltaBuffer, ushort currentSequence, ushort resetSequence, int numCubes, ref int[] cubeIds, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] predictionDelta
  ) {
    BeginSample("DecodePrediction");
    var baselineCubeState = CubeState.defaults;
    var result = true;
#if !DISABLE_DELTA_ENCODING

    for (int i = 0; i < numCubes; ++i) {
      if (perfectPrediction[i] || hasPredictionDelta[i]) {
        if (deltaBuffer.GetCubeState(baselineSequence[i], resetSequence, cubeIds[i], ref baselineCubeState)) {
          int baseline_sequence = baselineSequence[i];
          int current_sequence = currentSequence;

          if (current_sequence < baseline_sequence)
            current_sequence += 65536;

          int baseline_position_x = baselineCubeState.positionX;
          int baseline_position_y = baselineCubeState.positionY;
          int baseline_position_z = baselineCubeState.positionZ;
          int baseline_linear_velocity_x = baselineCubeState.linearVelocityX;
          int baseline_linear_velocity_y = baselineCubeState.linearVelocityY;
          int baseline_linear_velocity_z = baselineCubeState.linearVelocityZ;
          int baseline_angular_velocity_x = baselineCubeState.angularVelocityX;
          int baseline_angular_velocity_y = baselineCubeState.angularVelocityY;
          int baseline_angular_velocity_z = baselineCubeState.angularVelocityZ;

          if (current_sequence < baseline_sequence)
            current_sequence += 65536;

          int numFrames = current_sequence - baseline_sequence;
          int predicted_position_x;
          int predicted_position_y;
          int predicted_position_z;
          int predicted_linear_velocity_x;
          int predicted_linear_velocity_y;
          int predicted_linear_velocity_z;
          int predicted_angular_velocity_x;
          int predicted_angular_velocity_y;
          int predicted_angular_velocity_z;

          Prediction.PredictBallistic(numFrames,
                                       baseline_position_x, baseline_position_y, baseline_position_z,
                                       baseline_linear_velocity_x, baseline_linear_velocity_y, baseline_linear_velocity_z,
                                       baseline_angular_velocity_x, baseline_angular_velocity_y, baseline_angular_velocity_z,
                                       out predicted_position_x, out predicted_position_y, out predicted_position_z,
                                       out predicted_linear_velocity_x, out predicted_linear_velocity_y, out predicted_linear_velocity_z,
                                       out predicted_angular_velocity_x, out predicted_angular_velocity_y, out predicted_angular_velocity_z);

          if (perfectPrediction[i]) {
#if DEBUG_DELTA_COMPRESSION
            Assert.IsTrue( predicted_position_x == cubeDelta[i].absolute_position_x );
            Assert.IsTrue( predicted_position_y == cubeDelta[i].absolute_position_y );
            Assert.IsTrue( predicted_position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
            cubeState[i].positionX = predicted_position_x;
            cubeState[i].positionY = predicted_position_y;
            cubeState[i].positionZ = predicted_position_z;
            cubeState[i].linearVelocityX = predicted_linear_velocity_x;
            cubeState[i].linearVelocityY = predicted_linear_velocity_y;
            cubeState[i].linearVelocityZ = predicted_linear_velocity_z;
            cubeState[i].angularVelocityX = predicted_angular_velocity_x;
            cubeState[i].angularVelocityY = predicted_angular_velocity_y;
            cubeState[i].angularVelocityZ = predicted_angular_velocity_z;
          } else {
            cubeState[i].positionX = predicted_position_x + predictionDelta[i].positionX;
            cubeState[i].positionY = predicted_position_y + predictionDelta[i].positionY;
            cubeState[i].positionZ = predicted_position_z + predictionDelta[i].positionZ;
#if DEBUG_DELTA_COMPRESSION
            Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
            Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
            Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
            cubeState[i].linearVelocityX = predicted_linear_velocity_x + predictionDelta[i].linearVelocityX;
            cubeState[i].linearVelocityY = predicted_linear_velocity_y + predictionDelta[i].linearVelocityY;
            cubeState[i].linearVelocityZ = predicted_linear_velocity_z + predictionDelta[i].linearVelocityZ;
            cubeState[i].angularVelocityX = predicted_angular_velocity_x + predictionDelta[i].angularVelocityX;
            cubeState[i].angularVelocityY = predicted_angular_velocity_y + predictionDelta[i].angularVelocityY;
            cubeState[i].angularVelocityZ = predicted_angular_velocity_z + predictionDelta[i].angularVelocityZ;
          }
        } else {
          Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineSequence[i] + " (perfect prediction and prediction delta)");
          result = false;
          break;
        }
      }
    }
#endif // #if !DISABLE_DELTA_COMPRESSION
    EndSample();

    return result;
  }

  protected void ProcessAcksForConnection(Context context, Context.ConnectionData data) {
    BeginSample("ProcessAcksForConnection");
    int numAcks = 0;
    data.connection.GetAcks(ref acks, ref numAcks);

    for (int i = 0; i < numAcks; ++i) {
      int packetNumCubeStates;
      int[] packetCubeIds;
      CubeState[] packetCubeState;

      if (data.sendBuffer.GetPacketData(acks[i], context.resetSequence, out packetNumCubeStates, out packetCubeIds, out packetCubeState)) {
        for (int j = 0; j < packetNumCubeStates; ++j)
          context.UpdateAck(data, packetCubeIds[j], acks[i], context.resetSequence, ref packetCubeState[j]);
      }
    }
    EndSample();
  }

  protected void WriteDeltasToFile(System.IO.StreamWriter file, DeltaBuffer deltaBuffer, ushort sequence, ushort resetSequence, int numCubes, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineSequence, ref CubeState[] cubeState, ref CubeDelta[] cubeDelta
  ) {
    if (file == null) return;

    var baselineCubeState = CubeState.defaults;

    for (int i = 0; i < numCubes; ++i) {
      if (hasDelta[i]) {
        var result = deltaBuffer.GetCubeState(baselineSequence[i], resetSequence, cubeIds[i], ref baselineCubeState);
        IsTrue(result);

        if (result) {
          file.WriteLine(sequence + "," +
            baselineSequence[i] + "," +
            cubeDelta[i].positionX + "," +
            cubeDelta[i].positionY + "," +
            cubeDelta[i].positionZ + "," + ",,," +   // <--- for backwards compatibility.
            cubeDelta[i].linearVelocityX + "," +    //todo: remove this and fix up the indices in "TestPrediction".
            cubeDelta[i].linearVelocityY + "," +
            cubeDelta[i].linearVelocityZ + "," +
            cubeDelta[i].angularVelocityX + "," +
            cubeDelta[i].angularVelocityY + "," +
            cubeDelta[i].angularVelocityZ + "," +
            (baselineCubeState.isActive ? 1 : 0) + "," +
            baselineCubeState.positionX + "," +
            baselineCubeState.positionY + "," +
            baselineCubeState.positionZ + "," +
            baselineCubeState.rotationLargest + "," +
            baselineCubeState.rotationX + "," +
            baselineCubeState.rotationY + "," +
            baselineCubeState.rotationZ + "," +
            baselineCubeState.linearVelocityX + "," +
            baselineCubeState.linearVelocityY + "," +
            baselineCubeState.linearVelocityZ + "," +
            baselineCubeState.angularVelocityX + "," +
            baselineCubeState.angularVelocityY + "," +
            baselineCubeState.angularVelocityZ + "," +
            (cubeState[i].isActive ? 1 : 0) + "," +
            cubeState[i].positionX + "," +
            cubeState[i].positionY + "," +
            cubeState[i].positionZ + "," +
            cubeState[i].rotationLargest + "," +
            cubeState[i].rotationX + "," +
            cubeState[i].rotationY + "," +
            cubeState[i].rotationZ + "," +
            cubeState[i].linearVelocityX + "," +
            cubeState[i].linearVelocityY + "," +
            cubeState[i].linearVelocityZ + "," +
            cubeState[i].angularVelocityX + "," +
            cubeState[i].angularVelocityY + "," +
            cubeState[i].angularVelocityZ);
        }
      }
    }
    file.Flush();
  }

  protected void WritePacketSizeToFile(System.IO.StreamWriter file, int packetBytes) {
    if (file == null) return;

    file.WriteLine(packetBytes);
    file.Flush();
  }

  protected void InitializePlatformSDK(Message.Callback callback) {
    Core.Initialize();
    Entitlements.IsUserEntitledToApplication().OnComplete(callback);
  }

  protected void JoinRoom(ulong roomId, Message<Room>.Callback callback) {
    Log("Joining room " + roomId);
    Rooms.Join(roomId, true).OnComplete(callback);
  }

  protected void LeaveRoom(ulong roomId, Message<Room>.Callback callback) {
    if (roomId == 0) return;

    Log("Leaving room " + roomId);
    Rooms.Leave(roomId).OnComplete(callback);
  }

  protected void PrintRoomDetails(Room room) {
    Log("AppID: " + room.ApplicationID);
    Log("Room ID: " + room.ID);
    Log("Users in room: " + room.Users.Count + " / " + room.MaxUsers);

    if (room.Owner != null)
      Log("Room owner: " + room.Owner.OculusID + " [" + room.Owner.ID + "]");

    Log("Join Policy: " + room.JoinPolicy.ToString());
    Log("Room Type: " + room.Type.ToString());
  }

  protected bool FindUserById(UserList users, ulong userId) {
    foreach (var user in users) {
      if (user.ID == userId)
        return true;
    }
    return false;
  }
}