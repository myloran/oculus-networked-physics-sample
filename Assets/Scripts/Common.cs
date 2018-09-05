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
using System.IO;

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

    public void CopyFrom(ClientsInfo c) {
      for (int i = 0; i < MaxClients; ++i) {
        areConnected[i] = c.areConnected[i];
        userIds[i] = c.userIds[i];
        userNames[i] = c.userNames[i];
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
    cubes = new CubeState[MaxCubes],
    readCubes = new CubeState[MaxCubes];

  protected CubeDelta[] 
    cubeDeltas = new CubeDelta[MaxCubes],
    cubePredictions = new CubeDelta[MaxCubes],
    readCubeDeltas = new CubeDelta[MaxCubes],
    readPredictionDeltas = new CubeDelta[MaxCubes];

  protected uint[] packetBuffer = new uint[MaxPacketSize / 4];

  protected ushort[] 
    baselineIds = new ushort[MaxCubes],
    readBaselineIds = new ushort[MaxCubes],
    acks = new ushort[Connection.MaximumAcks];

  protected int[] 
    cubeIds = new int[MaxCubes],
    readCubeIds = new int[MaxCubes];

  protected bool[] 
    notChanged = new bool[MaxCubes],
    hasDelta = new bool[MaxCubes],
    perfectPrediction = new bool[MaxCubes],
    hasPredictionDelta = new bool[MaxCubes],
    readNotChanged = new bool[MaxCubes],
    readHasDelta = new bool[MaxCubes],
    readPerfectPrediction = new bool[MaxCubes],
    readHasPredictionDelta = new bool[MaxCubes];

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

  protected void ProcessStateUpdateFromJitterBuffer(Context context, Context.ConnectionData data, int fromClientId, int toClientId, bool isSmooth = true) {
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
    context.ApplyCubeUpdates(entry.cubeCount, ref entry.cubeIds, ref entry.cubes, fromClientId, toClientId, isSmooth); //apply the state updates to cubes    
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

  protected bool WriteUpdatePacket(ref PacketHeader header, int avatarCount, ref AvatarStateQuantized[] avatars, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] hasPerfectPrediction, ref bool[] hasPrediction, ref ushort[] baselineIds, ref CubeState[] cubes, ref CubeDelta[] deltas, ref CubeDelta[] predictions
  ) {
    BeginSample("WriteStateUpdatePacket");
    writeStream.Start(packetBuffer);
    var result = true;

    try {
      serializer.WriteUpdatePacket(writeStream, ref header, avatarCount, avatars, cubeCount, cubeIds, notChanged, hasDelta, hasPerfectPrediction, hasPrediction, baselineIds, cubes, deltas, predictions);
      writeStream.Finish();
    } catch (SerializeException) {
      Log("error: failed to write state update packet packet");
      result = false;
    }
    EndSample();

    return result;
  }

  protected bool ReadUpdatePacket(byte[] packet, out PacketHeader header, out int avatarCount, ref AvatarStateQuantized[] avatars, out int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] hasPerfectPrediction, ref bool[] hasPrediction, ref ushort[] baselineIds, ref CubeState[] cubes, ref CubeDelta[] deltas, ref CubeDelta[] predictions
  ) {
    BeginSample("ReadStateUpdatePacket");
    readStream.Start(packet);
    var result = true;

    try {
      serializer.ReadUpdatePacket(readStream, out header, out avatarCount, avatars, out cubeCount, cubeIds, notChanged, hasDelta, hasPerfectPrediction, hasPrediction, baselineIds, cubes, deltas, predictions);
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

  protected void AddPacket(ref DeltaBuffer buffer, ushort packetId, ushort resetId, int count, ref int[] cubeIds, ref CubeState[] states) {
    BeginSample("AddPacketToDeltaBuffer");
    buffer.AddPacket(packetId, resetId);

    for (int i = 0; i < count; ++i)
      buffer.AddCube(packetId, cubeIds[i], ref states[i]);

    EndSample();
  }

  protected void DetermineNotChangedAndDeltas(Context context, Context.ConnectionData data, ushort currentId, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineIds, ref CubeState[] cubes, ref CubeDelta[] deltas
  ) {
    BeginSample("DeterminedNotChangedAndDeltas");
#if !DISABLE_DELTA_COMPRESSION
    var baseline = CubeState.defaults;
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
      if (context.GetAck(data, cubeIds[i], ref baselineIds[i], context.resetId, ref baseline)) {
        if (Util.BaselineDifference(currentId, baselineIds[i]) > MaxBaselineDifference) continue; //baseline is too far behind => send the cube state absolute.
        if (baseline.Equals(cubes[i])) {
          notChanged[i] = true;
        } else {
          hasDelta[i] = true;
          deltas[i].positionX = cubes[i].positionX - baseline.positionX;
          deltas[i].positionY = cubes[i].positionY - baseline.positionY;
          deltas[i].positionZ = cubes[i].positionZ - baseline.positionZ;
          deltas[i].linearVelocityX = cubes[i].linearVelocityX - baseline.linearVelocityX;
          deltas[i].linearVelocityY = cubes[i].linearVelocityY - baseline.linearVelocityY;
          deltas[i].linearVelocityZ = cubes[i].linearVelocityZ - baseline.linearVelocityZ;
          deltas[i].angularVelocityX = cubes[i].angularVelocityX - baseline.angularVelocityX;
          deltas[i].angularVelocityY = cubes[i].angularVelocityY - baseline.angularVelocityY;
          deltas[i].angularVelocityZ = cubes[i].angularVelocityZ - baseline.angularVelocityZ;
        }
      }
#endif // #if !DISABLE_DELTA_COMPRESSION
    }
    EndSample();
  }

  protected bool DecodeNotChangedAndDeltas(DeltaBuffer buffer, ushort resetId, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineId, ref CubeState[] cubes, ref CubeDelta[] deltas
  ) {
    BeginSample("DecodeNotChangedAndDeltas");
    bool result = true;
#if !DISABLE_DELTA_COMPRESSION
    var baseline = CubeState.defaults;

    for (int i = 0; i < cubeCount; ++i) {
      if (notChanged[i]) {
        if (buffer.GetCube(baselineId[i], resetId, cubeIds[i], ref baseline)) {
#if DEBUG_DELTA_COMPRESSION
          if ( baselineCubeState.position_x != cubeDelta[i].absolute_position_x )
          {
              Log( "expected " + cubeDelta[i].absolute_position_x + ", got " + baselineCubeState.position_x );
          }
          Assert.IsTrue( baselineCubeState.position_x == cubeDelta[i].absolute_position_x );
          Assert.IsTrue( baselineCubeState.position_y == cubeDelta[i].absolute_position_y );
          Assert.IsTrue( baselineCubeState.position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
          cubes[i] = baseline;
        } else {
          Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineId[i] + " (not changed)");
          result = false;
          break;
        }
      } else if (hasDelta[i]) {
        if (buffer.GetCube(baselineId[i], resetId, cubeIds[i], ref baseline)) {
          cubes[i].positionX = baseline.positionX + deltas[i].positionX;
          cubes[i].positionY = baseline.positionY + deltas[i].positionY;
          cubes[i].positionZ = baseline.positionZ + deltas[i].positionZ;
#if DEBUG_DELTA_COMPRESSION
                    Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
                    Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
                    Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
          cubes[i].linearVelocityX = baseline.linearVelocityX + deltas[i].linearVelocityX;
          cubes[i].linearVelocityY = baseline.linearVelocityY + deltas[i].linearVelocityY;
          cubes[i].linearVelocityZ = baseline.linearVelocityZ + deltas[i].linearVelocityZ;
          cubes[i].angularVelocityX = baseline.angularVelocityX + deltas[i].angularVelocityX;
          cubes[i].angularVelocityY = baseline.angularVelocityY + deltas[i].angularVelocityY;
          cubes[i].angularVelocityZ = baseline.angularVelocityZ + deltas[i].angularVelocityZ;
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

  protected void DeterminePrediction(Context context, Context.ConnectionData data, ushort currentId, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref bool[] hasPerfectPrediction, ref bool[] hasPrediction, ref ushort[] baselineIds, ref CubeState[] cubes, ref CubeDelta[] predictions
  ) {
    BeginSample("DeterminePrediction");
    var baseline = CubeState.defaults;

    for (int i = 0; i < cubeCount; ++i) {
      hasPerfectPrediction[i] = false;
      hasPrediction[i] = false;
#if !DISABLE_DELTA_ENCODING
      if (notChanged[i]) continue;
      if (!hasDelta[i]) continue;
      if (!cubes[i].isActive) continue;

      if (context.GetAck(data, cubeIds[i], ref baselineIds[i], context.resetId, ref baseline)) {
        if (Util.BaselineDifference(currentId, baselineIds[i]) <= MaxBaselineDifference) continue; //baseline is too far behind. send the cube state absolute
        if (!baseline.isActive) continue; //no point predicting if the cube is at rest.

        int id = currentId;

        if (id < baselineIds[i])
          id += 65536;

        if (id < baselineIds[i])
          id += 65536;

        int positionX;
        int positionY;
        int positionZ;
        int linearVelocityX;
        int linearVelocityY;
        int linearVelocityZ;
        int angularVelocityX;
        int angularVelocityY;
        int angularVelocityZ;

        Prediction.PredictBallistic(id - baselineIds[i],
          baseline.positionX, baseline.positionY, baseline.positionZ,
          baseline.linearVelocityX, baseline.linearVelocityY, baseline.linearVelocityZ,
          baseline.angularVelocityX, baseline.angularVelocityY, baseline.angularVelocityZ,
          out positionX, out positionY, out positionZ,
          out linearVelocityX, out linearVelocityY, out linearVelocityZ,
          out angularVelocityX, out angularVelocityY, out angularVelocityZ);

        int positionLagX = cubes[i].positionX - positionX;
        int positionLagY = cubes[i].positionY - positionY;
        int positionLagZ = cubes[i].positionZ - positionZ;
        int linearVelocityLagX = cubes[i].linearVelocityX - linearVelocityX;
        int linearVelocityLagY = cubes[i].linearVelocityY - linearVelocityY;
        int linearVelocityLagZ = cubes[i].linearVelocityZ - linearVelocityZ;
        int angularVelocityLagX = cubes[i].angularVelocityX - angularVelocityX;
        int angularVelocityLagY = cubes[i].angularVelocityY - angularVelocityY;
        int angularVelocityLagZ = cubes[i].angularVelocityZ - angularVelocityZ;

        if (positionLagX == 0 && positionLagY == 0 && positionLagZ == 0
          && linearVelocityLagX == 0 && linearVelocityLagY == 0 && linearVelocityLagZ == 0
          && angularVelocityLagX == 0 && angularVelocityLagY == 0 && angularVelocityLagZ == 0
        ) {
          hasPerfectPrediction[i] = true;
          continue;
        }

        int absPositionX = Math.Abs(positionLagX);
        int absPositionY = Math.Abs(positionLagY);
        int absPositionZ = Math.Abs(positionLagZ);
        int absLinearVelocityX = Math.Abs(linearVelocityLagX);
        int absLinearVelocityY = Math.Abs(linearVelocityLagY);
        int absLinearVelocityZ = Math.Abs(linearVelocityLagZ);
        int absAngularVelocityX = Math.Abs(angularVelocityLagX);
        int absAngularVelocityY = Math.Abs(angularVelocityLagY);
        int absAngularVelocityZ = Math.Abs(angularVelocityLagZ);

        int predictedLag = absPositionX + absPositionY + absPositionZ
          + linearVelocityLagX + linearVelocityLagY + linearVelocityLagZ
          + angularVelocityLagX + angularVelocityLagY + angularVelocityLagZ;

        int absoluteLag = Math.Abs(cubes[i].positionX - baseline.positionX)
          + Math.Abs(cubes[i].positionY - baseline.positionY)
          + Math.Abs(cubes[i].positionZ - baseline.positionZ)
          + Math.Abs(cubes[i].linearVelocityX - baseline.linearVelocityX)
          + Math.Abs(cubes[i].linearVelocityY - baseline.linearVelocityY)
          + Math.Abs(cubes[i].linearVelocityZ - baseline.linearVelocityZ) 
          + Math.Abs(cubes[i].angularVelocityX - baseline.angularVelocityX) 
          + Math.Abs(cubes[i].angularVelocityY - baseline.angularVelocityY) 
          + Math.Abs(cubes[i].angularVelocityZ - baseline.angularVelocityZ);

        if (predictedLag < absoluteLag) {
          int maxPositionLag = absPositionX;

          if (absPositionY > maxPositionLag)
            maxPositionLag = absPositionY;

          if (absPositionZ > maxPositionLag)
            maxPositionLag = absPositionZ;

          int maxLinearVelocityLag = absLinearVelocityX;

          if (absLinearVelocityY > maxLinearVelocityLag)
            maxLinearVelocityLag = absLinearVelocityY;

          if (absLinearVelocityZ > maxLinearVelocityLag)
            maxLinearVelocityLag = absLinearVelocityZ;

          int maxAngularVelocityLag = absAngularVelocityX;

          if (absAngularVelocityY > maxAngularVelocityLag)
            maxAngularVelocityLag = absAngularVelocityY;

          if (absAngularVelocityZ > maxAngularVelocityLag)
            maxAngularVelocityLag = absAngularVelocityZ;

          if (maxPositionLag > PositionDeltaMax
            || maxLinearVelocityLag > LinearVelocityDeltaMax
            || maxAngularVelocityLag > AngularVelocityDeltaMax
          ) continue;

          hasPrediction[i] = true;
          cubePredictions[i].positionX = positionLagX;
          cubePredictions[i].positionY = positionLagY;
          cubePredictions[i].positionZ = positionLagZ;
          cubePredictions[i].linearVelocityX = linearVelocityLagX;
          cubePredictions[i].linearVelocityY = linearVelocityLagY;
          cubePredictions[i].linearVelocityZ = linearVelocityLagZ;
          cubePredictions[i].angularVelocityX = angularVelocityLagX;
          cubePredictions[i].angularVelocityY = angularVelocityLagY;
          cubePredictions[i].angularVelocityZ = angularVelocityLagZ;
        }
      }
    }
#endif // #if !DISABLE_DELTA_ENCODING
    EndSample();
  }

  protected bool DecodePrediction(DeltaBuffer buffer, ushort currentId, ushort resetId, int cubeCount, ref int[] cubeIds, ref bool[] hasPerfectPrediction, ref bool[] hasPrediction, ref ushort[] baselineIds, ref CubeState[] cubes, ref CubeDelta[] predictions
  ) {
    BeginSample("DecodePrediction");
    var baseline = CubeState.defaults;
    var result = true;
#if !DISABLE_DELTA_ENCODING

    for (int i = 0; i < cubeCount; ++i) {
      if (!hasPerfectPrediction[i] && !hasPrediction[i]) continue;

      if (!buffer.GetCube(baselineIds[i], resetId, cubeIds[i], ref baseline)) {
        Log("error: missing baseline for cube " + cubeIds[i] + " at sequence " + baselineIds[i] + " (perfect prediction and prediction delta)");
        result = false;
        break;
      }

      int id = currentId;

      if (id < baselineIds[i])
        id += 65536;

      if (id < baselineIds[i])
        id += 65536;

      int positionX;
      int positionY;
      int positionZ;
      int linearVelocityX;
      int linearVelocityY;
      int linearVelocityZ;
      int angularVelocityX;
      int angularVelocityY;
      int angularVelocityZ;

      Prediction.PredictBallistic(id - baselineIds[i],
        baseline.positionX, baseline.positionY, baseline.positionZ,
        baseline.linearVelocityX, baseline.linearVelocityY, baseline.linearVelocityZ,
        baseline.angularVelocityX, baseline.angularVelocityY, baseline.angularVelocityZ,
        out positionX, out positionY, out positionZ,
        out linearVelocityX, out linearVelocityY, out linearVelocityZ,
        out angularVelocityX, out angularVelocityY, out angularVelocityZ);

      if (hasPerfectPrediction[i]) {
#if DEBUG_DELTA_COMPRESSION
        Assert.IsTrue( predicted_position_x == cubeDelta[i].absolute_position_x );
        Assert.IsTrue( predicted_position_y == cubeDelta[i].absolute_position_y );
        Assert.IsTrue( predicted_position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
        cubes[i].positionX = positionX;
        cubes[i].positionY = positionY;
        cubes[i].positionZ = positionZ;
        cubes[i].linearVelocityX = linearVelocityX;
        cubes[i].linearVelocityY = linearVelocityY;
        cubes[i].linearVelocityZ = linearVelocityZ;
        cubes[i].angularVelocityX = angularVelocityX;
        cubes[i].angularVelocityY = angularVelocityY;
        cubes[i].angularVelocityZ = angularVelocityZ;
        continue;
      } 

      cubes[i].positionX = positionX + predictions[i].positionX;
      cubes[i].positionY = positionY + predictions[i].positionY;
      cubes[i].positionZ = positionZ + predictions[i].positionZ;
#if DEBUG_DELTA_COMPRESSION
      Assert.IsTrue( cubeState[i].position_x == cubeDelta[i].absolute_position_x );
      Assert.IsTrue( cubeState[i].position_y == cubeDelta[i].absolute_position_y );
      Assert.IsTrue( cubeState[i].position_z == cubeDelta[i].absolute_position_z );
#endif // #if DEBUG_DELTA_COMPRESSION
      cubes[i].linearVelocityX = linearVelocityX + predictions[i].linearVelocityX;
      cubes[i].linearVelocityY = linearVelocityY + predictions[i].linearVelocityY;
      cubes[i].linearVelocityZ = linearVelocityZ + predictions[i].linearVelocityZ;
      cubes[i].angularVelocityX = angularVelocityX + predictions[i].angularVelocityX;
      cubes[i].angularVelocityY = angularVelocityY + predictions[i].angularVelocityY;
      cubes[i].angularVelocityZ = angularVelocityZ + predictions[i].angularVelocityZ;
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

  protected void WriteDeltasToFile(StreamWriter file, DeltaBuffer buffer, ushort id, ushort resetId, int cubeCount, ref int[] cubeIds, ref bool[] notChanged, ref bool[] hasDelta, ref ushort[] baselineIds, ref CubeState[] cubes, ref CubeDelta[] deltas
  ) {
    if (file == null) return;

    var baseline = CubeState.defaults;

    for (int i = 0; i < cubeCount; ++i) {
      if (!hasDelta[i]) continue;

        var result = buffer.GetCube(baselineIds[i], resetId, cubeIds[i], ref baseline);
        IsTrue(result);

      if (result) {
        file.WriteLine(id + "," +
          baselineIds[i] + "," +
          deltas[i].positionX + "," +
          deltas[i].positionY + "," +
          deltas[i].positionZ + "," + ",,," +   // <--- for backwards compatibility.
          deltas[i].linearVelocityX + "," +    //todo: remove this and fix up the indices in "TestPrediction".
          deltas[i].linearVelocityY + "," +
          deltas[i].linearVelocityZ + "," +
          deltas[i].angularVelocityX + "," +
          deltas[i].angularVelocityY + "," +
          deltas[i].angularVelocityZ + "," +
          (baseline.isActive ? 1 : 0) + "," +
          baseline.positionX + "," +
          baseline.positionY + "," +
          baseline.positionZ + "," +
          baseline.rotationLargest + "," +
          baseline.rotationX + "," +
          baseline.rotationY + "," +
          baseline.rotationZ + "," +
          baseline.linearVelocityX + "," +
          baseline.linearVelocityY + "," +
          baseline.linearVelocityZ + "," +
          baseline.angularVelocityX + "," +
          baseline.angularVelocityY + "," +
          baseline.angularVelocityZ + "," +
          (cubes[i].isActive ? 1 : 0) + "," +
          cubes[i].positionX + "," +
          cubes[i].positionY + "," +
          cubes[i].positionZ + "," +
          cubes[i].rotationLargest + "," +
          cubes[i].rotationX + "," +
          cubes[i].rotationY + "," +
          cubes[i].rotationZ + "," +
          cubes[i].linearVelocityX + "," +
          cubes[i].linearVelocityY + "," +
          cubes[i].linearVelocityZ + "," +
          cubes[i].angularVelocityX + "," +
          cubes[i].angularVelocityY + "," +
          cubes[i].angularVelocityZ);
      }
    }
    file.Flush();
  }

  protected void WritePacketSizeToFile(StreamWriter file, int packetBytes) {
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