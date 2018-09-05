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
    baselineIds = new ushort[NumCubes],
    readBaselineIds = new ushort[NumCubes],
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
    if (!d.jitterBuffer.AddUpdatePacket(packet, d.receiveBuffer, context.resetId, out frame) || !d.isFirstPacket)
      return;

    d.isFirstPacket = false;
    d.frame = frame - NumJitterBufferFrames;
    d.jitterBuffer.Start(d.frame);
  }

  protected void ProcessStateUpdateFromJitterBuffer(Context context, Context.ConnectionData data, int fromClientId, int toClientId, bool applySmoothing = true) {
    if (data.frame < 0) return;

    var entry = data.jitterBuffer.GetEntry((uint)data.frame);
    if (entry == null) return;

    if (fromClientId == 0) {
      //server -> client      
      if (Util.SequenceGreaterThan(context.resetId, entry.header.resetSequence)) return; //Ignore updates from before the last reset.
      if (Util.SequenceGreaterThan(entry.header.resetSequence, context.resetId)) { //Reset if the server reset sequence is more recent than ours.
        context.Reset();
        context.resetId = entry.header.resetSequence;
      }
    } else {
      //client -> server      
      if (context.resetId != entry.header.resetSequence) return; //Ignore any updates from the client with a different reset sequence #
    }

    AddPacket(ref data.receiveBuffer, entry.header.id, context.resetId, entry.cubeCount, ref entry.cubeIds, ref entry.cubes); //add the cube states to the receive delta buffer    
    context.ApplyCubeUpdates(entry.cubeCount, ref entry.cubeIds, ref entry.cubes, fromClientId, toClientId, applySmoothing); //apply the state updates to cubes    
    data.connection.ProcessPacketHeader(ref entry.header); //process the packet header (handles acks)
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

  protected bool ReadClientsPacket(byte[] packet, bool[] areConnected, ulong[] userIds, string[] userNames) {
    BeginSample("ReadServerInfoPacket");
    readStream.Start(packet);
    var result = true;

    try {
      serializer.ReadClientsPacket(readStream, areConnected, userIds, userNames);
    } catch (SerializeException) {
      Log("error: failed to read server info packet");
      result = false;
    }
    readStream.Finish();
    EndSample();

    return result;
  }

  protected bool WriteUpdatePacket(ref PacketHeader header, int avatarCount, ref AvatarStateQuantized[] avatarState, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineIds, ref CubeState[] cubeStates, ref CubeDelta[] cubeDeltas, ref CubeDelta[] predictionDeltas
  ) {
    BeginSample("WriteStateUpdatePacket");
    writeStream.Start(packetBuffer);
    var result = true;

    try {
      serializer.WriteUpdatePacket(writeStream, ref header, avatarCount, avatarState, cubeCount, cubeIds, notChanged, hasDelta, perfectPrediction, hasPredictionDelta, baselineIds, cubeStates, cubeDeltas, predictionDeltas);
      writeStream.Finish();
    } catch (SerializeException) {
      Log("error: failed to write state update packet packet");
      result = false;
    }
    EndSample();

    return result;
  }

  protected bool ReadUpdatePacket(byte[] packet, out PacketHeader header, out int avatarCount, ref AvatarStateQuantized[] avatarStates, out int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineIds, ref CubeState[] cubeStates, ref CubeDelta[] cubeDeltas, ref CubeDelta[] predictionDeltas
  ) {
    BeginSample("ReadStateUpdatePacket");
    readStream.Start(packet);
    var result = true;

    try {
      serializer.ReadUpdatePacket(readStream, out header, out avatarCount, avatarStates, out cubeCount, cubeIds, notChanged, hasDelta, perfectPrediction, hasPredictionDelta, baselineIds, cubeStates, cubeDeltas, predictionDeltas);
    } catch (SerializeException) {
      Log("error: failed to read state update packet");
      header.id = 0;
      header.ack = 0;
      header.ackBits = 0;
      header.frame = 0;
      header.resetSequence = 0;
      header.timeOffset = 0.0f;
      avatarCount = 0;
      cubeCount = 0;
      result = false;
    }
    readStream.Finish();
    EndSample();

    return result;
  }

  protected void AddPacket(ref DeltaBuffer buffer, ushort id, ushort resetId, int count, ref int[] cubeIds, ref CubeState[] states) {
    BeginSample("AddPacketToDeltaBuffer");
    buffer.AddPacket(id, resetId);

    for (int i = 0; i < count; ++i)
      buffer.AddCube(id, cubeIds[i], ref states[i]);

    EndSample();
  }

  protected void DetermineNotChangedAndDeltas(Context context, Context.ConnectionData data, ushort currentId, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineIds, ref CubeState[] cubeStates, ref CubeDelta[] cubeDeltas
  ) {
    BeginSample("DeterminedNotChangedAndDeltas");
#if !DISABLE_DELTA_COMPRESSION
    var baselineState = CubeState.defaults;
#endif // #if !DISABLE_DELTA_COMPRESSION
    for (int i = 0; i < cubeCount; ++i) {
      notChanged[i] = false;
      hasDelta[i] = false;
#if !DISABLE_DELTA_COMPRESSION
#if DEBUG_DELTA_COMPRESSION
            cubeDelta[i].absolute_position_x = cubeState[i].position_x;
            cubeDelta[i].absolute_position_y = cubeState[i].position_y;
            cubeDelta[i].absolute_position_z = cubeState[i].position_z;
#endif // #if DEBUG_DELTA_COMPRESSION
      if (context.GetAck(data, cubeIds[i], ref baselineIds[i], context.resetId, ref baselineState)) {
        if (Util.BaselineDifference(currentId, baselineIds[i]) > MaxBaselineDifference) continue; //baseline is too far behind => send the cube state absolute.
        if (baselineState.Equals(cubeStates[i])) {
          notChanged[i] = true;
        } else {
          hasDelta[i] = true;
          cubeDeltas[i].positionX = cubeStates[i].positionX - baselineState.positionX;
          cubeDeltas[i].positionY = cubeStates[i].positionY - baselineState.positionY;
          cubeDeltas[i].positionZ = cubeStates[i].positionZ - baselineState.positionZ;
          cubeDeltas[i].linearVelocityX = cubeStates[i].linearVelocityX - baselineState.linearVelocityX;
          cubeDeltas[i].linearVelocityY = cubeStates[i].linearVelocityY - baselineState.linearVelocityY;
          cubeDeltas[i].linearVelocityZ = cubeStates[i].linearVelocityZ - baselineState.linearVelocityZ;
          cubeDeltas[i].angularVelocityX = cubeStates[i].angularVelocityX - baselineState.angularVelocityX;
          cubeDeltas[i].angularVelocityY = cubeStates[i].angularVelocityY - baselineState.angularVelocityY;
          cubeDeltas[i].angularVelocityZ = cubeStates[i].angularVelocityZ - baselineState.angularVelocityZ;
        }
      }
#endif // #if !DISABLE_DELTA_COMPRESSION
    }
    EndSample();
  }

  protected bool DecodeNotChangedAndDeltas(DeltaBuffer buffer, ushort resetId, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineId, ref CubeState[] cubeStates, ref CubeDelta[] cubeDeltas
  ) {
    BeginSample("DecodeNotChangedAndDeltas");
    bool result = true;
#if !DISABLE_DELTA_COMPRESSION
    var baselineCubeState = CubeState.defaults;

    for (int i = 0; i < cubeCount; ++i) {
      if (notChanged[i]) {
        if (buffer.GetCube(baselineId[i], resetId, cubeIds[i], ref baselineCubeState)) {
#if DEBUG_DELTA_COMPRESSION
          if ( baselineCubeState.position_x != cubeDelta[i].absolute_position_x )
          {
              Log( "expected " + cubeDelta[i].absolute_position_x + ", got " + baselineCubeState.position_x );
          }
          Assert.IsTrue( baselineCubeState.position_x == cubeDelta[i].absolute_position_x );
          Assert.IsTrue( baselineCubeState.position_y == cubeDelta[i].absolute_position_y );
          Assert.IsTrue( baselineCubeState.position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
          cubeStates[i] = baselineCubeState;
        } else {
          Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineId[i] + " (not changed)");
          result = false;
          break;
        }
      } else if (hasDelta[i]) {
        if (buffer.GetCube(baselineId[i], resetId, cubeIds[i], ref baselineCubeState)) {
          cubeStates[i].positionX = baselineCubeState.positionX + cubeDeltas[i].positionX;
          cubeStates[i].positionY = baselineCubeState.positionY + cubeDeltas[i].positionY;
          cubeStates[i].positionZ = baselineCubeState.positionZ + cubeDeltas[i].positionZ;
#if DEBUG_DELTA_COMPRESSION
                    Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
                    Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
                    Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
          cubeStates[i].linearVelocityX = baselineCubeState.linearVelocityX + cubeDeltas[i].linearVelocityX;
          cubeStates[i].linearVelocityY = baselineCubeState.linearVelocityY + cubeDeltas[i].linearVelocityY;
          cubeStates[i].linearVelocityZ = baselineCubeState.linearVelocityZ + cubeDeltas[i].linearVelocityZ;
          cubeStates[i].angularVelocityX = baselineCubeState.angularVelocityX + cubeDeltas[i].angularVelocityX;
          cubeStates[i].angularVelocityY = baselineCubeState.angularVelocityY + cubeDeltas[i].angularVelocityY;
          cubeStates[i].angularVelocityZ = baselineCubeState.angularVelocityZ + cubeDeltas[i].angularVelocityZ;
        } else {
          Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineId[i] + " (delta)");
          result = false;
          break;
        }
      }
    }
#endif // #if !DISABLE_DELTA_COMPRESSION
    return result;
  }

  protected void DeterminePrediction(Context context, Context.ConnectionData data, ushort currentId, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineIds, ref CubeState[] cubeStates, ref CubeDelta[] predictionDeltas
  ) {
    BeginSample("DeterminePrediction");
    var baselineState = CubeState.defaults;

    for (int i = 0; i < cubeCount; ++i) {
      perfectPrediction[i] = false;
      hasPredictionDelta[i] = false;
#if !DISABLE_DELTA_ENCODING
      if (notChanged[i]) continue;
      if (!hasDelta[i]) continue;
      if (!cubeStates[i].isActive) continue;

      if (context.GetAck(data, cubeIds[i], ref baselineIds[i], context.resetId, ref baselineState)) {
        if (Util.BaselineDifference(currentId, baselineIds[i]) <= MaxBaselineDifference) continue; //baseline is too far behind. send the cube state absolute
        if (!baselineState.isActive) continue; //no point predicting if the cube is at rest.

        int baseline_sequence = baselineIds[i];
        int current_sequence = currentId;

        if (current_sequence < baseline_sequence)
          current_sequence += 65536;

        int baseline_position_x = baselineState.positionX;
        int baseline_position_y = baselineState.positionY;
        int baseline_position_z = baselineState.positionZ;
        int baseline_linear_velocity_x = baselineState.linearVelocityX;
        int baseline_linear_velocity_y = baselineState.linearVelocityY;
        int baseline_linear_velocity_z = baselineState.linearVelocityZ;
        int baseline_angular_velocity_x = baselineState.angularVelocityX;
        int baseline_angular_velocity_y = baselineState.angularVelocityY;
        int baseline_angular_velocity_z = baselineState.angularVelocityZ;

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

        int current_position_x = cubeStates[i].positionX;
        int current_position_y = cubeStates[i].positionY;
        int current_position_z = cubeStates[i].positionZ;
        int current_linear_velocity_x = cubeStates[i].linearVelocityX;
        int current_linear_velocity_y = cubeStates[i].linearVelocityY;
        int current_linear_velocity_z = cubeStates[i].linearVelocityZ;
        int current_angular_velocity_x = cubeStates[i].angularVelocityX;
        int current_angular_velocity_y = cubeStates[i].angularVelocityY;
        int current_angular_velocity_z = cubeStates[i].angularVelocityZ;
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

          int total_absolute_error = Math.Abs(cubeStates[i].positionX - baselineState.positionX) +
                                     Math.Abs(cubeStates[i].positionY - baselineState.positionY) +
                                     Math.Abs(cubeStates[i].positionZ - baselineState.positionZ) +
                                     Math.Abs(cubeStates[i].linearVelocityX - baselineState.linearVelocityX) +
                                     Math.Abs(cubeStates[i].linearVelocityY - baselineState.linearVelocityY) +
                                     Math.Abs(cubeStates[i].linearVelocityZ - baselineState.linearVelocityZ) +
                                     Math.Abs(cubeStates[i].angularVelocityX - baselineState.angularVelocityX) +
                                     Math.Abs(cubeStates[i].angularVelocityY - baselineState.angularVelocityY) +
                                     Math.Abs(cubeStates[i].angularVelocityZ - baselineState.angularVelocityZ);

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

  protected bool DecodePrediction(DeltaBuffer buffer, ushort currentId, ushort resetId, int cubeCount, ref int[] cubeIds, ref bool[] perfectPrediction, ref bool[] hasPredictionDelta, ref ushort[] baselineIds, ref CubeState[] cubeStates, ref CubeDelta[] predictionDeltas
  ) {
    BeginSample("DecodePrediction");
    var baselineState = CubeState.defaults;
    var result = true;
#if !DISABLE_DELTA_ENCODING

    for (int i = 0; i < cubeCount; ++i) {
      if (perfectPrediction[i] || hasPredictionDelta[i]) {
        if (buffer.GetCube(baselineIds[i], resetId, cubeIds[i], ref baselineState)) {
          int baseline_sequence = baselineIds[i];
          int current_sequence = currentId;

          if (current_sequence < baseline_sequence)
            current_sequence += 65536;

          int baseline_position_x = baselineState.positionX;
          int baseline_position_y = baselineState.positionY;
          int baseline_position_z = baselineState.positionZ;
          int baseline_linear_velocity_x = baselineState.linearVelocityX;
          int baseline_linear_velocity_y = baselineState.linearVelocityY;
          int baseline_linear_velocity_z = baselineState.linearVelocityZ;
          int baseline_angular_velocity_x = baselineState.angularVelocityX;
          int baseline_angular_velocity_y = baselineState.angularVelocityY;
          int baseline_angular_velocity_z = baselineState.angularVelocityZ;

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
            cubeStates[i].positionX = predicted_position_x;
            cubeStates[i].positionY = predicted_position_y;
            cubeStates[i].positionZ = predicted_position_z;
            cubeStates[i].linearVelocityX = predicted_linear_velocity_x;
            cubeStates[i].linearVelocityY = predicted_linear_velocity_y;
            cubeStates[i].linearVelocityZ = predicted_linear_velocity_z;
            cubeStates[i].angularVelocityX = predicted_angular_velocity_x;
            cubeStates[i].angularVelocityY = predicted_angular_velocity_y;
            cubeStates[i].angularVelocityZ = predicted_angular_velocity_z;
          } else {
            cubeStates[i].positionX = predicted_position_x + predictionDeltas[i].positionX;
            cubeStates[i].positionY = predicted_position_y + predictionDeltas[i].positionY;
            cubeStates[i].positionZ = predicted_position_z + predictionDeltas[i].positionZ;
#if DEBUG_DELTA_COMPRESSION
            Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
            Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
            Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
            cubeStates[i].linearVelocityX = predicted_linear_velocity_x + predictionDeltas[i].linearVelocityX;
            cubeStates[i].linearVelocityY = predicted_linear_velocity_y + predictionDeltas[i].linearVelocityY;
            cubeStates[i].linearVelocityZ = predicted_linear_velocity_z + predictionDeltas[i].linearVelocityZ;
            cubeStates[i].angularVelocityX = predicted_angular_velocity_x + predictionDeltas[i].angularVelocityX;
            cubeStates[i].angularVelocityY = predicted_angular_velocity_y + predictionDeltas[i].angularVelocityY;
            cubeStates[i].angularVelocityZ = predicted_angular_velocity_z + predictionDeltas[i].angularVelocityZ;
          }
        } else {
          Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineIds[i] + " (perfect prediction and prediction delta)");
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
      int cubeCount;
      int[] cubeIds;
      CubeState[] states;

      if (data.sendBuffer.GetPacket(acks[i], context.resetId, out cubeCount, out cubeIds, out states)) {
        for (int j = 0; j < cubeCount; ++j)
          context.UpdateAck(data, cubeIds[j], acks[i], context.resetId, ref states[j]);
      }
    }
    EndSample();
  }

  protected void WriteDeltasToFile(System.IO.StreamWriter file, DeltaBuffer buffer, ushort id, ushort resetId, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineIds, ref CubeState[] cubeStates, ref CubeDelta[] cubeDeltas
  ) {
    if (file == null) return;

    var baselineCubeState = CubeState.defaults;

    for (int i = 0; i < cubeCount; ++i) {
      if (hasDelta[i]) {
        var result = buffer.GetCube(baselineIds[i], resetId, cubeIds[i], ref baselineCubeState);
        IsTrue(result);

        if (result) {
          file.WriteLine(id + "," +
            baselineIds[i] + "," +
            cubeDeltas[i].positionX + "," +
            cubeDeltas[i].positionY + "," +
            cubeDeltas[i].positionZ + "," + ",,," +   // <--- for backwards compatibility.
            cubeDeltas[i].linearVelocityX + "," +    //todo: remove this and fix up the indices in "TestPrediction".
            cubeDeltas[i].linearVelocityY + "," +
            cubeDeltas[i].linearVelocityZ + "," +
            cubeDeltas[i].angularVelocityX + "," +
            cubeDeltas[i].angularVelocityY + "," +
            cubeDeltas[i].angularVelocityZ + "," +
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
            (cubeStates[i].isActive ? 1 : 0) + "," +
            cubeStates[i].positionX + "," +
            cubeStates[i].positionY + "," +
            cubeStates[i].positionZ + "," +
            cubeStates[i].rotationLargest + "," +
            cubeStates[i].rotationX + "," +
            cubeStates[i].rotationY + "," +
            cubeStates[i].rotationZ + "," +
            cubeStates[i].linearVelocityX + "," +
            cubeStates[i].linearVelocityY + "," +
            cubeStates[i].linearVelocityZ + "," +
            cubeStates[i].angularVelocityX + "," +
            cubeStates[i].angularVelocityY + "," +
            cubeStates[i].angularVelocityZ);
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